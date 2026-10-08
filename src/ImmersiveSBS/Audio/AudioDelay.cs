using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ImmersiveSBS.Audio;

/// <summary>
/// Plays an app's sound late by a fixed delay, so it stays with a delayed picture
/// (video-3d-plan.md D5): process loopback captures the app's whole process tree, its live
/// sessions are turned down to an inaudible level (<see cref="SessionAttenuator"/>; a mute would
/// silence the capture too), and the capture, boosted back, is played on the default output
/// exactly the delay after it was captured. Capture and playback run on the same device clock,
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
    private float[] scratch = new float[4096];
    private bool disposed;

    private AudioDelay(uint processId, TimeSpan delay, WasapiRecorder recorder, WasapiPlayer player, AudioDelayLine line,
        SessionAttenuator attenuator)
    {
        ProcessId = processId;
        Delay = delay;
        this.recorder = recorder;
        this.player = player;
        this.line = line;
        this.attenuator = attenuator;
        // Tabs and players open new sessions at any time; turn those down quickly too.
        refresh = new Timer(_ => RefreshSessions(), null, 250, 250);
    }

    public uint ProcessId { get; }

    public TimeSpan Delay { get; }

    /// <summary>Lip-sync trim: positive plays the sound later.</summary>
    public TimeSpan Offset { get; set; }

    public int Resyncs => line.Resyncs;

    /// <summary>
    /// Starts capturing <paramref name="processId"/>'s tree, silencing its live sound and playing it
    /// <paramref name="delay"/> late. The first <paramref name="delay"/> after the start is silent.
    /// </summary>
    public static async Task<AudioDelay> StartAsync(uint processId, TimeSpan delay)
    {
        WasapiPlayer player = await new WasapiPlayerBuilder().WithSharedMode().WithLatency(40).BuildAsync();
        WasapiRecorder? recorder = null;
        var attenuator = new SessionAttenuator(ProcessTree.Of(processId));
        try
        {
            WaveFormat mix = player.DeviceMixFormat;
            int channels = Math.Min(mix.Channels, 8);
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, channels);
            var line = new AudioDelayLine(format.SampleRate, channels, delay);

            // Turn the live sound down before capturing, so the boost only meets attenuated sound.
            attenuator.Refresh();

            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(format)
                .BuildAsync();
            var result = new AudioDelay(processId, delay, recorder, player, line, attenuator);
            recorder.DataAvailable += result.OnCaptured;

            // The delayed copy plays at the app's own session volume (the same v² curve).
            player.Init(new DelayedProvider(line, format, result));
            player.SessionVolume.Volume = attenuator.OriginalVolume;
            recorder.StartRecording();
            player.Play();
            Log.Write($"Audio: delaying process {processId}'s sound by {delay.TotalSeconds:0.0#} s " +
                      $"({format.SampleRate} Hz, {channels} ch, output latency {player.LatencyMilliseconds} ms, {player.DeviceFriendlyName}).");
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
            float peak = 0;
            foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
            float gain = peak * SessionAttenuator.CaptureGain > ImplausiblePeak ? 1 : SessionAttenuator.CaptureGain;
            for (int i = 0; i < samples.Length; i++) boosted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }
        line.Write(boosted, qpc);
    }

    private void RefreshSessions()
    {
        try
        {
            lock (attenuator) if (!disposed) attenuator.Refresh();
        }
        catch (Exception e)
        {
            Log.Write("Audio: session refresh failed: " + e.Message);
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
            return bytes;
        }
    }
}
