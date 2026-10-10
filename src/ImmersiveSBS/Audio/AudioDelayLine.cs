namespace ImmersiveSBS.Audio;

/// <summary>
/// A fixed delay for interleaved float audio, placed on a clock rather than counted: each
/// captured packet is written where its capture time says it belongs, and playback reads the
/// samples captured exactly <see cref="Delay"/> before the time they will be heard. Gaps (a
/// silent app sends no packets) play as silence, packet jitter within a few milliseconds is
/// absorbed by writing contiguously, and playback reads contiguously, following the render
/// clock's smoothed error one frame at a time: the clock reading in each render callback jitters
/// by milliseconds, and jumping to every reading (as the first version did, 13 to 17 times a
/// session) is a click each time. Only a real jump of the clock resynchronises. Times are in
/// 100 ns units on the QPC clock, as WASAPI reports them. Thread-safe: one writer (the capture
/// thread) and one reader (the render thread). Pure: no audio devices.
/// </summary>
public sealed class AudioDelayLine
{
    private readonly object gate = new();
    private readonly float[] ring;
    private readonly long capacityFrames;
    private readonly long toleranceFrames;
    private readonly long resyncFrames;
    private readonly double correctionFrames;
    private long origin = long.MinValue;  // time of frame 0, 100 ns
    private long writeHead;               // next frame to write
    private long oldest;                  // oldest frame still in the ring
    private long readHead = long.MinValue;
    private double readError;             // smoothed (where reading should be − where it is), frames

    /// <param name="sampleRate">Frames per second.</param>
    /// <param name="channels">Interleaved channels per frame.</param>
    /// <param name="delay">The delay; the ring holds it plus two seconds.</param>
    public AudioDelayLine(int sampleRate, int channels, TimeSpan delay)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Delay = delay;
        capacityFrames = (long)((delay.TotalSeconds + 2) * sampleRate);
        ring = new float[capacityFrames * channels];
        toleranceFrames = sampleRate * 5 / 1000;
        resyncFrames = sampleRate * 40 / 1000;
        correctionFrames = sampleRate * 1.5 / 1000;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public TimeSpan Delay { get; }

    /// <summary>How many times the read side jumped to stay on time; for diagnostics.</summary>
    public int Resyncs { get; private set; }

    /// <summary>Frames skipped or repeated to follow the render clock smoothly; for diagnostics.</summary>
    public int Corrections { get; private set; }

    /// <summary>
    /// Makes the next read start exactly where the clock says, in one jump: for a deliberate
    /// change of timing (the lip-sync trim), which following a frame at a time would take seconds.
    /// </summary>
    public void Restart()
    {
        lock (gate) readHead = long.MinValue;
    }

    /// <summary>Writes a packet captured at <paramref name="time100ns"/>.</summary>
    public void Write(ReadOnlySpan<float> samples, long time100ns)
    {
        int frames = samples.Length / Channels;
        if (frames == 0) return;

        lock (gate)
        {
            if (origin == long.MinValue)
            {
                origin = time100ns;
                writeHead = oldest = 0;
            }

            long at = ToFrame(time100ns);
            // Within tolerance of where the last packet ended: continue seamlessly.
            if (Math.Abs(at - writeHead) <= toleranceFrames) at = writeHead;
            if (at > writeHead) Clear(writeHead, at); // a gap is silence
            if (at < oldest) return;                   // too old to matter

            for (int f = 0; f < frames; f++)
            {
                long slot = (at + f) % capacityFrames * Channels;
                samples.Slice(f * Channels, Channels).CopyTo(ring.AsSpan((int)slot, Channels));
            }
            writeHead = Math.Max(writeHead, at + frames);
            oldest = Math.Max(oldest, writeHead - capacityFrames);
        }
    }

    /// <summary>
    /// Fills <paramref name="output"/> with what was captured <see cref="Delay"/> before
    /// <paramref name="playTime100ns"/>, the time its first frame will be heard.
    /// </summary>
    public void Read(Span<float> output, long playTime100ns)
    {
        int frames = output.Length / Channels;
        lock (gate)
        {
            if (origin == long.MinValue)
            {
                output.Clear();
                return;
            }

            long ideal = ToFrame(playTime100ns - Delay.Ticks);
            if (readHead == long.MinValue || Math.Abs(ideal - readHead) > resyncFrames)
            {
                if (readHead != long.MinValue) Resyncs++;
                readHead = ideal;
                readError = 0;
            }
            else
            {
                // Capture and playback share the device clock, so the error is reading jitter
                // around a constant: follow its average by a frame per block (at most 0.2 % speed,
                // inaudible) instead of jumping.
                readError += (ideal - readHead - readError) * 0.1;
                if (Math.Abs(readError) > correctionFrames)
                {
                    int step = Math.Sign(readError);
                    readHead += step;   // skip a frame, or play one twice
                    readError -= step;
                    Corrections++;
                }
            }

            for (int f = 0; f < frames; f++)
            {
                long frame = readHead + f;
                Span<float> dst = output.Slice(f * Channels, Channels);
                if (frame < oldest || frame >= writeHead) dst.Clear();
                else ring.AsSpan((int)(frame % capacityFrames * Channels), Channels).CopyTo(dst);
            }
            readHead += frames;
        }
    }

    private long ToFrame(long time100ns) => (long)Math.Round((time100ns - origin) * (double)SampleRate / TimeSpan.TicksPerSecond);

    private void Clear(long from, long to)
    {
        long count = Math.Min(to - from, capacityFrames);
        for (long f = to - count; f < to; f++) ring.AsSpan((int)(f % capacityFrames * Channels), Channels).Clear();
    }
}
