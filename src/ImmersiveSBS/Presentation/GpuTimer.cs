using System.Text;
using Vortice.Direct3D11;

namespace ImmersiveSBS.Presentation;

/// <summary>
/// GPU time per pass (improvement-plan.md M5): a timestamp query after each pass of a frame,
/// bracketed by a disjoint query, read a few frames later and never waited on. A frame whose
/// results are not in by the time its slot comes round again is dropped, not waited for. The
/// time of a pass is from the previous mark to its own, so work queued between marks counts to
/// the next one. Render thread only.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    private const int Slots = 4;
    private const int MaxMarks = 24;

    private readonly ID3D11DeviceContext context;
    private readonly Slot[] slots;
    private readonly Dictionary<string, (double Ms, int Frames)> totals = [];
    private readonly List<string> order = [];
    private int next;
    private Slot? open;
    private int frames, dropped;

    private sealed class Slot
    {
        public required ID3D11Query Disjoint;
        public required ID3D11Query[] Stamps;
        public readonly string?[] Names = new string?[MaxMarks];
        public int Count;
        public bool Pending;
    }

    public GpuTimer(ID3D11Device device)
    {
        context = device.ImmediateContext;
        slots = new Slot[Slots];
        for (int i = 0; i < Slots; i++)
        {
            var stamps = new ID3D11Query[MaxMarks];
            for (int k = 0; k < MaxMarks; k++) stamps[k] = device.CreateQuery(new QueryDescription(QueryType.Timestamp, QueryFlags.None));
            slots[i] = new Slot { Disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint, QueryFlags.None)), Stamps = stamps };
        }
    }

    /// <summary>Starts a frame's measurement; a frame already open is ended first.</summary>
    public void BeginFrame()
    {
        if (open != null) EndFrame();
        Slot slot = slots[next];
        next = (next + 1) % Slots;
        if (slot.Pending && !TryRead(slot))
        {
            slot.Pending = false;
            dropped++;
        }
        context.Begin(slot.Disjoint);
        context.End(slot.Stamps[0]);
        slot.Names[0] = null;
        slot.Count = 1;
        open = slot;
    }

    /// <summary>Marks the end of a pass named <paramref name="stage"/>; nothing while no frame is open.</summary>
    public void Mark(string stage)
    {
        if (open == null || open.Count == MaxMarks) return;
        context.End(open.Stamps[open.Count]);
        open.Names[open.Count++] = stage;
    }

    /// <summary>Ends the open frame; its results are read by <see cref="Collect"/> once the GPU has them.</summary>
    public void EndFrame()
    {
        if (open == null) return;
        context.End(open.Disjoint);
        open.Pending = open.Count > 1;
        open = null;
    }

    /// <summary>Frames ended whose results are not read yet.</summary>
    public int PendingFrames => slots.Count(s => s.Pending && s != open);

    /// <summary>Reads every finished frame without waiting.</summary>
    public void Collect()
    {
        foreach (Slot slot in slots)
            if (slot.Pending && slot != open && TryRead(slot)) slot.Pending = false;
    }

    private unsafe bool TryRead(Slot slot)
    {
        QueryDataTimestampDisjoint disjoint;
        if (context.GetData(slot.Disjoint, (IntPtr)(&disjoint), (uint)sizeof(QueryDataTimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0)
            return false;
        Span<ulong> stamps = stackalloc ulong[MaxMarks];
        for (int i = 0; i < slot.Count; i++)
        {
            ulong t;
            if (context.GetData(slot.Stamps[i], (IntPtr)(&t), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0) return false;
            stamps[i] = t;
        }
        slot.Pending = false;
        // The GPU's clock changed speed meanwhile (power state): these numbers mean nothing.
        if (disjoint.Disjoint || disjoint.Frequency == 0) return true;

        frames++;
        for (int i = 1; i < slot.Count; i++)
        {
            string name = slot.Names[i]!;
            double ms = (stamps[i] - stamps[i - 1]) * 1000.0 / disjoint.Frequency;
            if (!totals.TryGetValue(name, out var t))
            {
                t = (0, 0);
                order.Add(name);
            }
            totals[name] = (t.Ms + ms, t.Frames + 1);
        }
        return true;
    }

    /// <summary>Each pass's mean time over the frames it ran in since the last call, ms; then starts over.</summary>
    public Dictionary<string, double> TakeAverages()
    {
        var result = new Dictionary<string, double>();
        foreach (string name in order)
        {
            (double ms, int n) = totals[name];
            if (n > 0) result[name] = ms / n;
            totals[name] = (0, 0);
        }
        frames = dropped = 0;
        return result;
    }

    /// <summary>
    /// "GPU ms per frame: upsample 0.31 (60), splat 0.52 (60); 60 frames" since the last call:
    /// each pass's mean over the frames it ran in (how many in brackets); then starts over.
    /// </summary>
    public string TakeSummary()
    {
        if (frames == 0) return dropped > 0 ? $"GPU timing: {dropped} frames not measured" : "GPU timing: no frames";
        var text = new StringBuilder("GPU ms per frame:");
        double sum = 0;
        foreach (string name in order)
        {
            (double ms, int n) = totals[name];
            if (n == 0) continue;
            text.Append($" {name} {ms / n:0.00}{(n != frames ? $" ({n})" : "")},");
            sum += ms;
        }
        text.Length--;
        text.Append($"; total {sum / frames:0.00}; {frames} frames");
        if (dropped > 0) text.Append($", {dropped} not measured");
        foreach (string name in order) totals[name] = (0, 0);
        frames = dropped = 0;
        return text.ToString();
    }

    public void Dispose()
    {
        foreach (Slot slot in slots)
        {
            slot.Disjoint.Dispose();
            foreach (ID3D11Query q in slot.Stamps) q.Dispose();
        }
    }
}
