using System.Runtime.InteropServices;
using ImmersiveSBS.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>
/// How process loopback relates to an app's session volume and mute (video-3d-plan.md open
/// question 4), which the delayed sound (<see cref="AudioDelay"/>) is built on. Measured on
/// Windows 11 build 26300: loopback is taken after the session mute (a muted session captures
/// silence) and after the session volume, which acts as amplitude v² down to v = 0.001 (−120 dB;
/// 0.0001 is silent). So the live sound is turned down to v = 0.01 rather than muted, and the
/// capture is boosted by (1/v)². The tests play quiet tones from this process; without an audio
/// output they report why and return.
/// </summary>
public class AudioLoopbackTests(ITestOutputHelper output)
{
    private sealed class Tone(WaveFormat format, float amplitude = 0.05f) : IWaveProvider
    {
        private long frame;

        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> buffer)
        {
            Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
            int frames = samples.Length / format.Channels;
            for (int f = 0; f < frames; f++, frame++)
            {
                float v = amplitude * MathF.Sin(2 * MathF.PI * 440 * frame / format.SampleRate);
                for (int c = 0; c < format.Channels; c++) samples[f * format.Channels + c] = v;
            }
            return frames * format.BlockAlign;
        }
    }

    /// <summary>The control: unmuted, a barely audible tone (−46 dBFS, half a second) is captured.</summary>
    [Fact]
    public async Task LoopbackCapturesThisProcess()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            output.WriteLine("Skipped: no audio output.");
            return;
        }

        uint self = (uint)Environment.ProcessId;
        WasapiPlayer player = await new WasapiPlayerBuilder().WithSharedMode().WithLatency(40).BuildAsync();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(player.DeviceMixFormat.SampleRate, 2);
        player.Init(new Tone(format, 0.005f));

        double sumSquares = 0;
        long count = 0;
        WasapiRecorder recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(self, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .BuildAsync();
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) return;
            foreach (float s in MemoryMarshal.Cast<byte, float>(data))
            {
                sumSquares += s * s;
                count++;
            }
        };

        try
        {
            recorder.StartRecording();
            player.Play();
            await Task.Delay(500);
        }
        finally
        {
            player.Stop();
            recorder.StopRecording();
            player.Dispose();
        }

        double rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        output.WriteLine($"Captured {count} samples unmuted, RMS {rms:0.00000} (tone RMS 0.00354).");
        Assert.True(rms > 0.001, $"loopback of this process captured RMS {rms:0.00000}");
    }

    /// <summary>At session volume v the capture is v² × the tone, all the way down to −120 dB.</summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.03f)]
    [InlineData(0.01f)]
    [InlineData(0.001f)]
    public async Task LoopbackFollowsSessionVolumeSquared(float volume)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            output.WriteLine("Skipped: no audio output.");
            return;
        }

        uint self = (uint)Environment.ProcessId;
        WasapiPlayer player = await new WasapiPlayerBuilder().WithSharedMode().WithLatency(40).BuildAsync();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(player.DeviceMixFormat.SampleRate, 2);
        player.Init(new Tone(format, 0.05f));
        float before = player.SessionVolume.Volume;
        player.SessionVolume.Volume = volume;

        double sumSquares = 0;
        long count = 0;
        WasapiRecorder recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(self, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .BuildAsync();
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) return;
            foreach (float s in MemoryMarshal.Cast<byte, float>(data))
            {
                sumSquares += s * s;
                count++;
            }
        };

        try
        {
            recorder.StartRecording();
            player.Play();
            await Task.Delay(500);
        }
        finally
        {
            player.Stop();
            recorder.StopRecording();
            player.SessionVolume.Volume = before;
            player.Dispose();
        }

        double rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        double ratio = rms / 0.0354;
        output.WriteLine($"Session volume {volume}: captured RMS {rms:0.0000000}, {ratio:0.0000000} of the tone.");
        Assert.InRange(ratio, 0.8 * volume * volume, 1.25 * volume * volume);
    }

    [Fact]
    public async Task LoopbackOfAMutedSessionIsSilent()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            output.WriteLine("Skipped: no audio output.");
            return;
        }

        uint self = (uint)Environment.ProcessId;
        WasapiPlayer player = await new WasapiPlayerBuilder().WithSharedMode().WithLatency(40).BuildAsync();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(player.DeviceMixFormat.SampleRate, 2);
        player.Init(new Tone(format));

        // The session exists once the stream is initialised: mute it before anything plays.
        player.SessionVolume.Mute = true;

        double sumSquares = 0;
        long count = 0;
        WasapiRecorder recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(self, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .BuildAsync();
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) return;
            foreach (float s in MemoryMarshal.Cast<byte, float>(data))
            {
                sumSquares += s * s;
                count++;
            }
        };

        try
        {
            recorder.StartRecording();
            player.Play();
            await Task.Delay(1000);
        }
        finally
        {
            player.Stop();
            recorder.StopRecording();
            player.SessionVolume.Mute = false;
            player.Dispose();
        }

        double rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        output.WriteLine($"Captured {count} samples from the muted session, RMS {rms:0.0000} (tone RMS 0.0354).");
        Assert.True(rms < 1e-6, $"loopback captured RMS {rms:0.0000} from a muted session: muting would leave the live sound audible");
    }
}
