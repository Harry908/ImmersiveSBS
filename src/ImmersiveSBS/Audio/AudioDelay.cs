using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ImmersiveSBS.Audio;

/// <summary>
/// Plays an app's sound late by a fixed delay, so it stays with a delayed picture
/// (video-3d-plan.md D5): process loopback captures the app's whole process tree, its live
/// sessions are turned down to an inaudible level (<see cref="SessionAttenuator"/>; a mute would
/// silence the capture too), and the capture, boosted back, is played on the output the app plays
/// on exactly the delay after it was captured. Capture and playback run on the same device clock,
/// so they cannot drift; jitter is absorbed by <see cref="AudioDelayLine"/>.
/// </summary>
public sealed class AudioDelay : IDisposable
{
    /// <summary>
    /// A boosted packet louder than this was captured before its session was turned down (a tab
    /// that just started); it is passed at unity gain instead of being blown up by the boost.
    /// </summary>
    private const float ImplausiblePeak = 4;

    private readonly WasapiRecorder recorder;
    private readonly WasapiPlayer player;
    private readonly SessionAttenuator attenuator;
    private readonly AudioDelayLine line;
    private readonly Timer refresh;
    /// <summary>The output the player was opened on; null when it follows the default output.</summary>
    private readonly string? playerDevice;
    private float[] scratch = new float[4096];
    private long capturedFrames, silentFrames, playedFrames, loudPackets;
    private float peak;
    private long statsStart = Stopwatch.GetTimestamp();
    private long elsewhereSince;
    private volatile bool needsRestart;
    private bool disposed;

    private AudioDelay(uint processId, TimeSpan delay, WasapiRecorder recorder, WasapiPlayer player, AudioDelayLine line,
        SessionAttenuator attenuator, string? playerDevice)
    {
        ProcessId = processId;
        Delay = delay;
        this.recorder = recorder;
        this.player = player;
        this.line = line;
        this.attenuator = attenuator;
        this.playerDevice = playerDevice;
        // An output that goes away (the glasses' sound resets when they switch 2D/3D) stops the
        // player; the controller then starts over.
        player.PlaybackStopped += (_, e) =>
        {
            if (disposed) return;
            needsRestart = true;
            Log.Write("Audio: the delayed sound's output stopped" + (e.Exception != null ? ": " + e.Exception.Message : "") + ".");
        };
        // Tabs and players open new sessions at any time; turn those down quickly too.
        refresh = new Timer(_ => RefreshSessions(), null, 250, 250);
    }

    public uint ProcessId { get; }

    public TimeSpan Delay { get; }

    /// <summary>Lip-sync trim: positive plays the sound later.</summary>
    public TimeSpan Offset { get; set; }

    public int Resyncs => line.Resyncs;

    /// <summary>
    /// The player's output stopped, or the app now plays on another output: this instance is to be
    /// replaced by a new one.
    /// </summary>
    public bool NeedsRestart => needsRestart;

    /// <summary>
    /// Starts capturing <paramref name="processId"/>'s tree, silencing its live sound and playing it
    /// <paramref name="delay"/> late. The first <paramref name="delay"/> after the start is silent.
    /// </summary>
    public static async Task<AudioDelay> StartAsync(uint processId, TimeSpan delay)
    {
        // Turn the live sound down first, before anything is captured (so the boost only meets
        // attenuated sound), and to learn which output the app plays on: it may be routed away
        // from the default output (App volume and device preferences), and the delayed copy must
        // be heard where the app was. While it plays nothing, follow the default output, as the
        // app itself does unless told otherwise.
        var attenuator = new SessionAttenuator(ProcessTree.Of(processId));
        attenuator.Refresh();

        var builder = new WasapiPlayerBuilder().WithSharedMode().WithLatency(40);
        string? playerDevice = attenuator.PlayingDeviceId;
        if (playerDevice != null && playerDevice != attenuator.DefaultDeviceId)
        {
            using var enumerator = new MMDeviceEnumerator();
            builder = builder.WithDevice(enumerator.GetDevice(playerDevice));
        }
        else
        {
            playerDevice = null;
            builder = builder.WithDefaultDeviceStreamRouting();
        }

        WasapiPlayer player;
        try
        {
            player = await builder.BuildAsync();
        }
        catch
        {
            attenuator.Dispose();
            throw;
        }

        WasapiRecorder? recorder = null;
        try
        {
            WaveFormat mix = player.DeviceMixFormat;
            int channels = Math.Min(mix.Channels, 8);
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, channels);
            var line = new AudioDelayLine(format.SampleRate, channels, delay);

            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(format)
                .BuildAsync();
            var result = new AudioDelay(processId, delay, recorder, player, line, attenuator, playerDevice);
            recorder.DataAvailable += result.OnCaptured;

            // The delayed copy plays at the app's own session volume (the same v² curve).
            player.Init(new DelayedProvider(line, format, result));
            player.SessionVolume.Volume = attenuator.OriginalVolume;
            recorder.StartRecording();
            player.Play();
            Log.Write($"Audio: delaying process {processId}'s sound by {delay.TotalSeconds:0.0#} s " +
                      $"({format.SampleRate} Hz, {channels} ch, output latency {player.LatencyMilliseconds} ms, " +
                      $"{(playerDevice == null ? "following the default output, " : "")}{player.DeviceFriendlyName}).");
            return result;
        }
        catch
        {
            try { recorder?.StopRecording(); } catch { }
            player.Dispose();
            attenuator.Dispose();
            throw;
        }
    }

    /// <summary>Capture thread: boost the attenuated sound back and file it by capture time.</summary>
    private void OnCaptured(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpc)
    {
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(data);
        if (scratch.Length < samples.Length) scratch = new float[samples.Length];
        Span<float> boosted = scratch.AsSpan(0, samples.Length);

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            boosted.Clear();
        }
        else
        {
            float packetPeak = 0;
            foreach (float s in samples) packetPeak = Math.Max(packetPeak, Math.Abs(s));
            float gain = packetPeak * SessionAttenuator.CaptureGain > ImplausiblePeak ? 1 : SessionAttenuator.CaptureGain;
            if (gain == 1) loudPackets++;
            for (int i = 0; i < samples.Length; i++) boosted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
            peak = Math.Max(peak, Math.Min(1, packetPeak * gain));
        }
        line.Write(boosted, qpc);
        CountCaptured(samples.Length / Math.Max(1, line.Channels), (flags & AudioClientBufferFlags.Silent) != 0);
    }

    /// <summary>
    /// Capture thread: every 10 s the log gets what was captured (silent or not, how loud, how many
    /// packets came from a session not yet turned down) and played, to tell a stalled or quiet app
    /// from a stalled capture or playback.
    /// </summary>
    private void CountCaptured(int frames, bool silent)
    {
        capturedFrames += frames;
        if (silent) silentFrames += frames;
        double seconds = Stopwatch.GetElapsedTime(statsStart).TotalSeconds;
        if (seconds < 10) return;

        int rate = line.SampleRate;
        string level = peak > 0 ? $"peak {20 * Math.Log10(peak):0} dBFS" : "no signal";
        (float Peak, float Volume, string? Device) own;
        lock (attenuator) own = attenuator.TakeOwnPeak();
        // What Windows itself metered from this app: the proof that the copy is really heard.
        string heard = own.Device == null
            ? "Windows sees no sound from this app"
            : $"Windows meters this app at {(own.Peak > 0 ? $"{20 * Math.Log10(own.Peak):0} dBFS" : "silence")} " +
              $"(volume {own.Volume:0.00}) on {own.Device}";
        Log.Write($"Audio: last {seconds:0} s: captured {capturedFrames / (double)rate:0.0} s of sound " +
                  $"({silentFrames / (double)rate:0.0} s silent, {level}, {loudPackets} unattenuated packets), " +
                  $"played {Interlocked.Exchange(ref playedFrames, 0) / (double)rate:0.0} s on {player.DeviceFriendlyName}; {heard}; resyncs {line.Resyncs}.");
        capturedFrames = silentFrames = loudPackets = 0;
        peak = 0;
        statsStart = Stopwatch.GetTimestamp();
    }

    private void RefreshSessions()
    {
        try
        {
            lock (attenuator)
            {
                if (disposed) return;
                attenuator.Refresh();
                FollowApp();
            }
        }
        catch (Exception e)
        {
            Log.Write("Audio: session refresh failed: " + e.Message);
        }
    }

    /// <summary>
    /// The app moved to another output (the user switched it, or the glasses came and went): after
    /// a second there, ask for a restart on it.
    /// </summary>
    private void FollowApp()
    {
        string? playing = attenuator.PlayingDeviceId;
        bool elsewhere = playing != null && playing != (playerDevice ?? attenuator.DefaultDeviceId);
        if (!elsewhere)
        {
            elsewhereSince = 0;
            return;
        }
        if (elsewhereSince == 0)
        {
            elsewhereSince = Environment.TickCount64;
        }
        else if (Environment.TickCount64 - elsewhereSince > 1000 && !needsRestart)
        {
            needsRestart = true;
            Log.Write($"Audio: the app now plays on {attenuator.PlayingDeviceName}; moving the delayed sound there.");
        }
    }

    /// <summary>The time the next block handed to the device will be heard, 100 ns QPC units.</summary>
    private long PlayTime() =>
        (long)(Stopwatch.GetTimestamp() * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)
        + TimeSpan.FromMilliseconds(player.LatencyMilliseconds).Ticks
        - Offset.Ticks;

    public void Dispose()
    {
        lock (attenuator)
        {
            if (disposed) return;
            disposed = true;
        }
        refresh.Dispose();
        try { player.Stop(); } catch { }
        try { recorder.StopRecording(); } catch { }
        // Its capture thread and audio client go with it, not at process end.
        try { recorder.Dispose(); } catch { }
        player.Dispose();
        // Restore the live sound last, once the delayed copy has stopped, so nothing plays twice.
        attenuator.Dispose();
        Log.Write($"Audio: stopped delaying process {ProcessId}'s sound (resyncs: {line.Resyncs}).");
    }

    /// <summary>Feeds the player from the delay line.</summary>
    private sealed class DelayedProvider(AudioDelayLine line, WaveFormat format, AudioDelay owner) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> buffer)
        {
            int bytes = buffer.Length - buffer.Length % format.BlockAlign;
            line.Read(MemoryMarshal.Cast<byte, float>(buffer[..bytes]), owner.PlayTime());
            Interlocked.Add(ref owner.playedFrames, bytes / format.BlockAlign);
            return bytes;
        }
    }
}
