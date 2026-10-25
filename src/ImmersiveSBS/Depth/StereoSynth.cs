using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The GPU half of AI 3D, on the render device (video-3d-plan.md §4.1 stages 2–3 and 5–6):
/// the model input tensor, and from a nearness map the two eye images of the video region: guided upsample (§6.3), edge snapping,
/// foreground dilation (§6.4), forward splat with a depth test, hole fill from the background
/// and gather (§7.3).
/// Render thread only.
/// </summary>
public sealed class StereoSynth : IDisposable
{
    private const string Hlsl = """
        cbuffer Params : register(b0)
        {
            int4 rect;          // video region in source px: x, y, w, h
            int2 modelSize;     // depth map / model input size
            int2 sourceSize;    // whole captured frame
            float strength;     // S in source px (total disparity per unit of nearness)
            float convergence;  // c: the nearness that stays at the screen plane
            int maxHole;        // hole search radius, px
            int dilateRadius;   // foreground dilation radius, px
            float rangeSigma;   // colour sigma of the guided upsample
            int layout;         // native split: 0 the same picture in both eyes, 1 side-by-side, 2 top-bottom
            float bandLeft;     // floating window: black band on the left eye's left edge, px
            float bandRight;    // and on the right eye's right edge
            int snapRadius;     // edge snapping search radius, px; 0 = off
            float snapStep;     // nearness range within the radius that counts as a depth edge
            int lookupSkip;     // lookup warp: jump spans that cannot hold the root
            int pad;
        };

        Texture2D<float4> Source : register(t0);
        Texture2D<float> Depth : register(t1);
        Texture2D<float> NearIn : register(t2);
        Texture2D<float> BlockMax : register(t3);
        SamplerState Linear : register(s0);

        float3 ColourAt(float2 sourcePx) { return Source.SampleLevel(Linear, sourcePx / sourceSize, 0).rgb; }

        // --- Model input: crop, 4-tap box downsample, ImageNet normalisation, planar NCHW.
        RWStructuredBuffer<float> Tensor : register(u0);
        [numthreads(8, 8, 1)]
        void Preprocess(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)modelSize.x || id.y >= (uint)modelSize.y) return;
            float2 scale = float2(rect.zw) / float2(modelSize);
            float2 p = rect.xy + (id.xy + 0.5) * scale;
            float2 q = scale * 0.25;
            float3 c = Source.SampleLevel(Linear, (p + float2(-q.x, -q.y)) / sourceSize, 0).rgb
                     + Source.SampleLevel(Linear, (p + float2( q.x, -q.y)) / sourceSize, 0).rgb
                     + Source.SampleLevel(Linear, (p + float2(-q.x,  q.y)) / sourceSize, 0).rgb
                     + Source.SampleLevel(Linear, (p + float2( q.x,  q.y)) / sourceSize, 0).rgb;
            c = (c * 0.25 - float3(0.485, 0.456, 0.406)) / float3(0.229, 0.224, 0.225);   // ImageNet.cs
            uint plane = modelSize.x * modelSize.y;
            uint i = id.y * modelSize.x + id.x;
            Tensor[i] = c.r;
            Tensor[plane + i] = c.g;
            Tensor[2 * plane + i] = c.b;
        }

        // --- Joint bilateral upsample of nearness to region resolution, guided by colour (§6.3;
        // colour rather than luma keeps hair and skin apart from a background of the same brightness).
        RWTexture2D<float> NearOut : register(u0);
        [numthreads(8, 8, 1)]
        void Upsample(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float2 toModel = float2(modelSize) / float2(rect.zw);
            float2 m = (id.xy + 0.5) * toModel - 0.5;
            int2 m0 = (int2)floor(m + 0.5);
            float3 cc = ColourAt(rect.xy + id.xy + 0.5);
            float wsum = 0, nsum = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int2 q = clamp(m0 + int2(dx, dy), int2(0, 0), modelSize - 1);
                    float2 d = float2(q) - m;
                    float3 dc = cc - ColourAt(rect.xy + (q + 0.5) / toModel);
                    float w = exp(-0.5 * dot(d, d)) * exp(-dot(dc, dc) / (6 * rangeSigma * rangeSigma)) + 1e-6;
                    nsum += w * Depth.Load(int3(q, 0));
                    wsum += w;
                }
            NearOut[id.xy] = nsum / wsum;
        }

        // --- Edge snapping at full resolution. The model's depth edges are a few model pixels
        // soft, so an outline or hair gets an in-between depth and smears. Where the nearness
        // spans a depth edge within the radius, the pixel takes the near or the far side's depth
        // (the extremes: the means still hold the soft ramp), whichever side's colour it
        // resembles; a pixel like neither (hair over the background) gets a blend, a softer edge
        // where the picture itself is uncertain.
        [numthreads(64, 1, 1)]
        void Snap(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float n0 = NearIn.Load(int3(id.xy, 0));
            float lo = n0, hi = n0;
            for (int d = -snapRadius; d <= snapRadius; d++)
            {
                float n = NearIn.Load(int3(clamp((int)id.x + d, 0, rect.z - 1), id.y, 0));
                lo = min(lo, n);
                hi = max(hi, n);
            }
            if (hi - lo < snapStep) { NearOut[id.xy] = n0; return; }

            float mid = 0.5 * (lo + hi);
            float sigma2 = 2 * max(1.0, snapRadius * 0.5) * max(1.0, snapRadius * 0.5);
            float3 cNear = 0, cFar = 0;
            float wNear = 0, wFar = 0;
            for (int k = -snapRadius; k <= snapRadius; k++)
            {
                int x = clamp((int)id.x + k, 0, rect.z - 1);
                float n = NearIn.Load(int3(x, id.y, 0));
                float3 c = ColourAt(rect.xy + float2(x, id.y) + 0.5);
                float w = exp(-k * k / sigma2);
                if (n > mid) { cNear += w * c; wNear += w; }
                else { cFar += w * c; wFar += w; }
            }
            if (wNear == 0 || wFar == 0) { NearOut[id.xy] = n0; return; }
            cNear /= wNear; cFar /= wFar;

            float3 c0 = ColourAt(rect.xy + id.xy + 0.5);
            float dNear = dot(c0 - cNear, c0 - cNear), dFar = dot(c0 - cFar, c0 - cFar);
            // +1 like the near side, −1 like the far side; steep, but a pixel like neither blends.
            float like = (dFar - dNear) / (dNear + dFar + 1e-4);
            NearOut[id.xy] = lerp(lo, hi, saturate(0.5 + 2 * like));
        }

        // --- Horizontal max filter: object borders err onto the background (§6.4).
        [numthreads(64, 1, 1)]
        void Dilate(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float n = 0;
            for (int d = -dilateRadius; d <= dilateRadius; d++)
            {
                int x = clamp((int)id.x + d, 0, rect.z - 1);
                n = max(n, NearIn.Load(int3(x, id.y, 0)));
            }
            NearOut[id.xy] = n;
        }

        // --- Slope limit (improvement-plan.md §2.1): the forward splat leaves a hole wherever
        // nearness changes by more than 2/S per px, so every step becomes a ramp no steeper than
        // that: n'(x) = max over d of n(x + d) − g·|d|, with g just under 2/S, a dilation by a
        // cone. The ramp lies on the far side (the background beside an object is stretched over
        // it, as iw3 and Depth3D do), and depth that is already gentle is left as it is. Beyond
        // 1/g px nothing can win (nearness spans at most 1).
        #define RAMP_GROUP 256
        #define MAX_RAMP 64
        groupshared float rampRow[RAMP_GROUP + 2 * MAX_RAMP];
        [numthreads(RAMP_GROUP, 1, 1)]
        void SlopeLimit(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
        {
            int start = (int)(gid.x * RAMP_GROUP) - MAX_RAMP;
            int row = min((int)id.y, rect.w - 1);
            for (uint i = gi; i < RAMP_GROUP + 2 * MAX_RAMP; i += RAMP_GROUP)
                rampRow[i] = NearIn.Load(int3(clamp(start + (int)i, 0, rect.z - 1), row, 0));
            GroupMemoryBarrierWithGroupSync();
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float g = 1.9 / max(strength, 1e-3);
            int reach = min((int)ceil(1 / g), MAX_RAMP);
            int c = gi + MAX_RAMP;
            float n = rampRow[c];
            for (int d = 1; d <= reach; d++)
                n = max(n, max(rampRow[c - d], rampRow[c + d]) - g * d);
            NearOut[id.xy] = n;
        }

        // --- Forward splat with a depth test (§7.3): key = nearness << 16 | (source x + 2) in 12.4.
        RWStructuredBuffer<uint> Keys : register(u0);
        [numthreads(8, 8, 1)]
        void Splat(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float n = saturate(NearIn.Load(int3(id.xy, 0)));
            float s = 0.5 * strength * (n - convergence);
            uint near16 = (uint)(n * 65535.0) << 16;
            uint w = rect.z, plane = rect.z * rect.w;
            for (uint eye = 0; eye < 2; eye++)
            {
                float xt = id.x + (eye == 0 ? s : -s);   // left eye +s, right eye −s
                int t0 = (int)floor(xt);
                for (int k = 0; k < 2; k++)
                {
                    int t = t0 + k;
                    // Only columns this pixel really covers (one landing a hair before a column
                    // would otherwise claim it and widen an object by a pixel).
                    if (t < 0 || t >= (int)w || abs(t - xt) > 0.99) continue;
                    float xs = id.x + (t - xt);
                    // The sample point reaches up to a pixel into a neighbour. Across a depth edge
                    // that neighbour is another surface (an object's edge pixel when this is the
                    // background beside it), and reading it draws a 1 px copy of the outline.
                    int nb = clamp(xs > id.x ? (int)id.x + 1 : (int)id.x - 1, 0, (int)w - 1);
                    if (abs(NearIn.Load(int3(nb, id.y, 0)) - n) > 0.02) xs = id.x;
                    uint fx = (uint)clamp(round((xs + 2) * 16), 16.0, 65535.0);
                    InterlockedMax(Keys[eye * plane + id.y * w + t], near16 | fx);
                }
            }
        }

        // --- Gather. A hole is background revealed behind a foreground edge: what was there is
        // unknown, so it is filled from the farther of its two nearest neighbours, mirrored away
        // from the edge and softly blurred. (Continuing past the neighbour instead reads the
        // foreground's own edge pixels and draws a second outline beside it.)
        RWTexture2D<unorm float4> Eyes : register(u1);
        bool ResolvePixel(uint3 id)
        {
            uint w = rect.z, plane = rect.z * rect.w;
            uint eye = id.x / w;
            int t = id.x % w;
            uint row = eye * plane + id.y * w;
            uint key = Keys[row + t];
            float xs;
            bool hole = key == 0;
            if (!hole)
            {
                xs = (key & 0xFFFF) / 16.0 - 2;
            }
            else
            {
                int l = -1, r = -1;
                uint kl = 0, kr = 0;
                for (int d = 1; d <= maxHole && (l < 0 || r < 0); d++)
                {
                    if (l < 0 && t - d >= 0 && Keys[row + t - d] != 0) { l = t - d; kl = Keys[row + l]; }
                    if (r < 0 && t + d < (int)w && Keys[row + t + d] != 0) { r = t + d; kr = Keys[row + r]; }
                }
                if (l < 0 && r < 0) xs = t;
                else if (r < 0 || (l >= 0 && (kl >> 16) <= (kr >> 16))) xs = (kl & 0xFFFF) / 16.0 - 2 - (t - l);
                else xs = (kr & 0xFFFF) / 16.0 - 2 + (r - t);
            }
            xs = clamp(xs, 0, w - 1);
            float2 p = float2(rect.x + xs + 0.5, rect.y + id.y + 0.5);
            float3 c = Source.SampleLevel(Linear, p / sourceSize, 0).rgb;
            if (hole)
            {
                // Four bilinear taps around the centre: a 3×3 tent, so no streak or texture copy shows.
                c = 0.4 * c + 0.15 * (ColourAt(p + float2(-1, -1)) + ColourAt(p + float2(1, -1))
                                    + ColourAt(p + float2(-1, 1)) + ColourAt(p + float2(1, 1)));
            }

            // Floating window (§7.3 step 5): where something in front of the screen touches the
            // video's side edge, a black band moves that edge forward too.
            if ((eye == 0 && t < bandLeft) || (eye == 1 && t >= (int)w - bandRight)) c = 0;
            Eyes[id.xy] = float4(c, 1);
            // Holes beside outlines count; those at the side edges (picture shifted in from
            // outside the frame, which the floating window covers) do not.
            int border = (int)ceil(0.5 * strength) + 1;
            return hole && t >= border && t < (int)w - border;
        }

        // Hole pixels counted for the statistics (improvement-plan.md M2): per group first, so
        // the counter in memory sees one atomic per 64 pixels.
        RWStructuredBuffer<uint> Holes : register(u2);
        groupshared uint groupHoles;
        [numthreads(8, 8, 1)]
        void Resolve(uint3 id : SV_DispatchThreadID, uint gi : SV_GroupIndex)
        {
            if (gi == 0) groupHoles = 0;
            GroupMemoryBarrierWithGroupSync();
            // (HLSL's && does not short-circuit.)
            bool hole = false;
            if (id.x < 2 * (uint)rect.z && id.y < (uint)rect.w) hole = ResolvePixel(id);
            if (hole) InterlockedAdd(groupHoles, 1);
            GroupMemoryBarrierWithGroupSync();
            if (gi == 0 && groupHoles > 0) InterlockedAdd(Holes[0], groupHoles);
        }

        // --- Lookup warp (improvement-plan.md §8): for every output pixel, the source pixel that
        // lands on it, so nothing is left empty. Left eye: the smallest root of
        // g(x) = x + s(n(x)) − u on [u − s_max, u − s_min] (of two source pixels landing on u
        // the left one is nearer and hides the other); right eye: g(x) = x − s(n(x)) − u, the
        // largest root. With x_t = u ∓ s_max ± t·h both read G(t) = t·h − s_max + S/2·(n(x_t) − c),
        // rising from S/2·(n − 1) ≤ 0 to S/2·n ≥ 0: marched at a sample per px, refined linearly.
        // Where the two samples around the root differ by more than τ = 0.1 the root is at a
        // depth edge and the pixel shows what was hidden behind it: the farther sample is taken
        // (the background stretches, not the object), blended in over τ..2τ so it never flickers.
        // Optionally, spans that cannot hold the root are jumped (Depth3D's "nearest depth chain",
        // exact): in a block of 8 px whose nearest depth is M, G ≤ (distance to the block's far
        // end) + S/2·(M − c); below 0 the whole block is passed by.
        #define LOOKUP_BLOCK 8
        [numthreads(64, 1, 1)]
        void BlockMaxima(uint3 id : SV_DispatchThreadID)
        {
            uint blocks = (rect.z + LOOKUP_BLOCK - 1) / LOOKUP_BLOCK;
            if (id.x >= blocks || id.y >= (uint)rect.w) return;
            float m = 0;
            // Through the first pixel of the next block: a sample between the two reads it too.
            for (int k = 0; k <= LOOKUP_BLOCK; k++)
                m = max(m, NearIn.Load(int3(min((int)id.x * LOOKUP_BLOCK + k, rect.z - 1), id.y, 0)));
            NearOut[id.xy] = saturate(m);
        }

        float NearAt(float x, int y)
        {
            x = clamp(x, 0, rect.z - 1);
            int x0 = (int)x;
            int x1 = min(x0 + 1, rect.z - 1);
            return saturate(lerp(NearIn.Load(int3(x0, y, 0)), NearIn.Load(int3(x1, y, 0)), x - x0));
        }

        [numthreads(8, 8, 1)]
        void Lookup(uint3 id : SV_DispatchThreadID)
        {
            uint w = rect.z;
            if (id.x >= 2 * w || id.y >= (uint)rect.w) return;
            uint eye = id.x / w;
            int column = id.x % w;
            int y = id.y;
            float u = column;
            float half = 0.5 * strength;
            float sMax = half * (1 - convergence);
            float dir = eye == 0 ? 1 : -1;
            int steps = max(1, (int)ceil(half));
            float h = half / steps;
            float start = u - dir * sMax;

            float xPrev = start, nPrev = NearAt(start, y);
            float gPrev = -sMax + half * (nPrev - convergence);
            float xs = start;
            if (gPrev < 0)
            {
                xs = start + dir * half;   // G(end) = S/2·n ≥ 0, so only rounding ends here
                int lastBlock = -1;
                for (int k = 1; k <= steps; k++)
                {
                    float x = start + dir * k * h;
                    if (lookupSkip != 0 && x >= 0 && x <= w - 1)
                    {
                        int j = (int)x / LOOKUP_BLOCK;
                        if (j != lastBlock)
                        {
                            lastBlock = j;
                            float far = dir > 0 ? (j + 1) * LOOKUP_BLOCK : j * LOOKUP_BLOCK;
                            if (dir * (far - u) + half * (BlockMax.Load(int3(j, y, 0)) - convergence) < 0)
                            {
                                // No root here: on to the last sample inside the block.
                                int last = dir > 0 ? (int)ceil((far - start) / h) - 1 : (int)floor((start - far) / h);
                                if (last > k && last <= steps)
                                {
                                    k = last;
                                    x = start + dir * k * h;
                                }
                                xPrev = x;
                                nPrev = NearAt(x, y);
                                gPrev = k * h - sMax + half * (nPrev - convergence);
                                continue;
                            }
                        }
                    }
                    float n = NearAt(x, y);
                    float g = k * h - sMax + half * (n - convergence);
                    if (g >= 0)
                    {
                        float root = xPrev + dir * h * (-gPrev / max(g - gPrev, 1e-6));
                        // At a depth edge G jumps, and the line through the two samples misses.
                        // Each side's own root (as if its depth went on) is exact where that side
                        // really is: the nearer side if it reaches this pixel, else the farther;
                        // if neither, the pixel is in the gap and the farther sample stretches.
                        bool nearIsNext = n > nPrev;
                        float nNear = max(n, nPrev), nFar = min(n, nPrev);
                        float xNear = start + dir * (sMax - half * (nNear - convergence));
                        float xFar = start + dir * (sMax - half * (nFar - convergence));
                        float edge = abs(NearAt(xNear, y) - nNear) < 0.05 ? xNear
                                   : abs(NearAt(xFar, y) - nFar) < 0.05 ? xFar
                                   : nearIsNext ? xPrev : x;
                        xs = lerp(root, edge, smoothstep(0.1, 0.2, abs(n - nPrev)));
                        break;
                    }
                    xPrev = x;
                    nPrev = n;
                    gPrev = g;
                }
            }
            xs = clamp(xs, 0, w - 1);
            float3 c = Source.SampleLevel(Linear, float2(rect.x + xs + 0.5, rect.y + y + 0.5) / sourceSize, 0).rgb;
            if ((eye == 0 && column < bandLeft) || (eye == 1 && column >= (int)w - bandRight)) c = 0;
            Eyes[id.xy] = float4(c, 1);
        }

        // --- Video that is already 3D (§7.4): each half stretched to the whole region.
        [numthreads(8, 8, 1)]
        void NativeSplit(uint3 id : SV_DispatchThreadID)
        {
            uint w = rect.z;
            if (id.x >= 2 * w || id.y >= (uint)rect.w) return;
            uint eye = id.x / w;
            float t = id.x % w + 0.5, y = id.y + 0.5;
            float2 p = layout == 1
                ? float2(rect.x + eye * w * 0.5 + t * 0.5, rect.y + y)
                : layout == 2
                    ? float2(rect.x + t, rect.y + eye * rect.w * 0.5 + y * 0.5)
                    : float2(rect.x + t, rect.y + y);
            Eyes[id.xy] = float4(Source.SampleLevel(Linear, p / sourceSize, 0).rgb, 1);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Int4 Rect;
        public Int2 ModelSize;
        public Int2 SourceSize;
        public float Strength;
        public float Convergence;
        public int MaxHole;
        public int DilateRadius;
        public float RangeSigma;
        public int Layout;
        public float BandLeft;
        public float BandRight;
        public int SnapRadius;
        public float SnapStep;
        public int LookupSkip;
        public int Pad;
    }

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11ComputeShader preprocess, upsample, snap, dilate, slopeLimit, splat, resolve, blockMaxima, lookup, nativeSplit;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState linear;
    /// <summary>Signalled when the GPU has made the queued model input's copy.</summary>
    private readonly ID3D11Query inputDone;

    private IntPtr sourceKey;
    private ID3D11ShaderResourceView? sourceView;

    private GpuBuffer? tensor, keys, holes;
    private ID3D11Texture2D? depthTexture;
    private ID3D11ShaderResourceView? depthView;
    private Size depthSize;
    private Texture? nearA, nearB, eyes, blocks;
    private Size regionSize;
    private int tensorCount;
    private long holePixels, queuedHolePixels;

    public StereoSynth(ID3D11Device device)
    {
        this.device = device;
        context = device.ImmediateContext;
        preprocess = Compile("Preprocess");
        upsample = Compile("Upsample");
        snap = Compile("Snap");
        dilate = Compile("Dilate");
        slopeLimit = Compile("SlopeLimit");
        splat = Compile("Splat");
        resolve = Compile("Resolve");
        blockMaxima = Compile("BlockMaxima");
        lookup = Compile("Lookup");
        nativeSplit = Compile("NativeSplit");
        constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        linear = device.CreateSamplerState(SamplerDescription.LinearClamp);
        inputDone = device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
    }

    /// <summary>Times each pass on the GPU when set (improvement-plan.md M5).</summary>
    public Presentation.GpuTimer? Timer { get; set; }

    /// <summary>Foreground dilation radius in source px; 0 skips the pass.</summary>
    public int DilateRadius { get; set; } = 3;

    /// <summary>Colour sigma of the guided upsample (per channel, 0..1).</summary>
    public float RangeSigma { get; set; } = 0.1f;

    /// <summary>Snaps depth edges to the picture's edges at full resolution (see the Snap shader).</summary>
    public bool EdgeSnap { get; set; } = true;

    /// <summary>
    /// Turns every depth step into a ramp gentle enough for the forward warp to stretch over
    /// instead of tearing a hole (the SlopeLimit shader, improvement-plan.md §2.1). The lookup
    /// warp needs none.
    /// </summary>
    public bool LimitSlope { get; set; } = true;

    /// <summary>The lookup warp (the Lookup shader, improvement-plan.md §8) instead of the forward splat.</summary>
    public bool Lookup { get; set; }

    /// <summary>
    /// The lookup warp jumps 8-px spans that cannot hold the source pixel (exact). Off: at the
    /// default strength its search is only S/2 ≈ 17 px, and the extra reads and diverging threads
    /// cost more than the steps saved (on this RTX 3060 Laptop, 1.09 ms a frame against 0.84 ms
    /// at 1920×1080; the splat takes 0.90 ms).
    /// </summary>
    public bool LookupSkip { get; set; }

    /// <summary>A nearness range at least this wide within the snapping radius is a depth edge.</summary>
    public const float SnapStep = 0.1f;

    private ID3D11ComputeShader Compile(string entry) =>
        device.CreateComputeShader(Compiler.Compile(Hlsl, entry, "stereo.hlsl", "cs_5_0").Span);

    // Read-backs are split in two: the work and a copy to a staging buffer are queued while the
    // frame is built, and read after it is presented, when the GPU has long finished the copy.
    // Reading at once made the render thread wait for the GPU twice a frame, in front of the
    // present, and the GPU is shared with the depth model.

    /// <summary>Queues the model input for <paramref name="region"/> (planar RGB, ImageNet-normalised).</summary>
    public void QueueModelInput(ID3D11Texture2D source, Rectangle region, Size modelSize)
    {
        Size src = BindSource(source);
        tensorCount = 3 * modelSize.Width * modelSize.Height;
        EnsureBuffer(ref tensor, tensorCount, 4);
        SetParams(region, modelSize, src, 0, 0, 0);
        Dispatch(preprocess, tensor!.Uav, (modelSize.Width + 7) / 8, (modelSize.Height + 7) / 8);
        tensor.QueueRead(context);
        context.End(inputDone);
        Timer?.Mark("input");
    }

    /// <summary>The queued model input's copy is made: reading it now does not wait. Never waits itself.</summary>
    public unsafe bool QueuedModelInputReady
    {
        get
        {
            if (tensor is not { Pending: true }) return false;
            int done = 0;
            return context.GetData(inputDone, (IntPtr)(&done), sizeof(int), AsyncGetDataFlags.DoNotFlush).Code == 0 && done != 0;
        }
    }

    /// <summary>Sends the work queued so far to the GPU now, rather than at the next present.</summary>
    public void Flush() => context.Flush();

    /// <summary>The queued model input; false when none is queued. Waits for the GPU if it is not done.</summary>
    public bool ReadQueuedModelInput(float[] destination)
    {
        if (tensor is not { Pending: true }) return false;
        tensor.ReadQueued(context, MemoryMarshal.AsBytes(destination.AsSpan(0, tensorCount)));
        return true;
    }

    /// <summary>The queued model input's length in floats.</summary>
    public int QueuedModelInputLength => tensorCount;

    /// <summary>
    /// Queues the count of hole pixels (filled, not warped) since the last call, for
    /// <see cref="ReadQueuedHoles"/>, and starts counting again (improvement-plan.md M2).
    /// </summary>
    public void QueueHoleCount()
    {
        if (holes == null || holePixels == 0) return;
        holes.QueueRead(context);
        context.ClearUnorderedAccessView(holes.Uav, new Int4(0, 0, 0, 0));
        queuedHolePixels = holePixels;
        holePixels = 0;
    }

    /// <summary>The queued hole count as a share of the eye pixels built meanwhile; false when none is queued.</summary>
    public bool ReadQueuedHoles(out double fraction)
    {
        fraction = 0;
        if (holes is not { Pending: true }) return false;
        Span<uint> count = stackalloc uint[1];
        holes.ReadQueued(context, MemoryMarshal.AsBytes(count));
        fraction = queuedHolePixels > 0 ? count[0] / (double)queuedHolePixels : 0;
        return true;
    }

    /// <summary>Uploads a post-processed nearness map (model resolution).</summary>
    public unsafe void UploadNearness(DepthFrame frame)
    {
        var size = new Size(frame.Width, frame.Height);
        if (depthTexture == null || depthSize != size)
        {
            depthView?.Dispose();
            depthTexture?.Dispose();
            depthTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            depthView = device.CreateShaderResourceView(depthTexture);
            depthSize = size;
        }

        fixed (float* p = frame.Nearness)
        {
            context.UpdateSubresource(depthTexture, 0, null, (IntPtr)p, (uint)(size.Width * 4), 0);
        }
    }

    /// <summary>
    /// Builds both eye images of <paramref name="region"/> from the last uploaded nearness: a
    /// texture twice the region's width, left eye left. <paramref name="strength"/> is S in
    /// source px.
    /// </summary>
    public ID3D11ShaderResourceView Synthesize(ID3D11Texture2D source, Rectangle region, float strength, float convergence,
        float bandLeft = 0, float bandRight = 0)
    {
        if (depthView == null) throw new InvalidOperationException("No nearness uploaded.");

        Size src = BindSource(source);
        EnsureRegion(region.Size);
        int maxHole = (int)Math.Ceiling(strength) + 2;
        SetParams(region, depthSize, src, strength, convergence, maxHole, bandLeft: bandLeft, bandRight: bandRight);

        int gx = (region.Width + 7) / 8, gy = (region.Height + 7) / 8;

        context.CSSetShaderResource(1, depthView);
        Dispatch(upsample, nearA!.Uav, gx, gy);
        context.CSSetShaderResource(1, null!);
        Timer?.Mark("upsample");

        // Ping-pong: each step reads whichever texture holds the nearness and writes the other.
        Texture near = nearA!;
        if (EdgeSnap) near = NearPass(near, snap, (region.Width + 63) / 64, region.Height, "snap");
        if (DilateRadius > 0) near = NearPass(near, dilate, (region.Width + 63) / 64, region.Height, "dilate");

        if (holes == null)
        {
            holes = new GpuBuffer(device, 1, 4);
            context.ClearUnorderedAccessView(holes.Uav, new Int4(0, 0, 0, 0));
        }

        if (Lookup)
        {
            // Nothing is left empty: its pixels count toward M2 with no holes.
            int blockCount = (region.Width + 7) / 8;
            context.CSSetShaderResource(2, near.Srv);
            Dispatch(blockMaxima, blocks!.Uav, (blockCount + 63) / 64, region.Height);
            context.CSSetShaderResource(3, blocks.Srv);
            context.CSSetUnorderedAccessView(1, eyes!.Uav);
            Dispatch(lookup, keys!.Uav, (2 * region.Width + 7) / 8, gy);
            context.CSSetUnorderedAccessView(1, null!);
            context.CSSetShaderResource(3, null!);
            context.CSSetShaderResource(2, null!);
            holePixels += 2L * region.Width * region.Height;
            Timer?.Mark("lookup");
            return eyes.Srv;
        }

        if (LimitSlope && strength > 0) near = NearPass(near, slopeLimit, (region.Width + 255) / 256, region.Height, "slope");

        context.ClearUnorderedAccessView(keys!.Uav, new Int4(0, 0, 0, 0));
        context.CSSetShaderResource(2, near.Srv);
        Dispatch(splat, keys.Uav, gx, gy);
        context.CSSetShaderResource(2, null!);
        Timer?.Mark("splat");

        context.CSSetUnorderedAccessView(1, eyes!.Uav);
        context.CSSetUnorderedAccessView(2, holes.Uav);
        Dispatch(resolve, keys.Uav, (2 * region.Width + 7) / 8, gy);
        context.CSSetUnorderedAccessView(2, null!);
        context.CSSetUnorderedAccessView(1, null!);
        holePixels += 2L * region.Width * region.Height;
        Timer?.Mark("resolve");

        return eyes.Srv;
    }

    /// <summary>
    /// Eye images of video that is already 3D, each half stretched to the region (§7.4); with
    /// <see cref="Native3DLayout.None"/> the same flat picture in both eyes.
    /// </summary>
    public ID3D11ShaderResourceView SplitNative(ID3D11Texture2D source, Rectangle region, Native3DLayout layout)
    {
        Size src = BindSource(source);
        EnsureRegion(region.Size);
        SetParams(region, Size.Empty, src, 0, 0, 0, layout: (int)layout);
        context.CSSetUnorderedAccessView(1, eyes!.Uav);
        Dispatch(nativeSplit, keys!.Uav, (2 * region.Width + 7) / 8, (region.Height + 7) / 8);
        context.CSSetUnorderedAccessView(1, null!);
        Timer?.Mark("split");
        return eyes.Srv;
    }

    private Size BindSource(ID3D11Texture2D source)
    {
        if (source.NativePointer != sourceKey)
        {
            sourceView?.Dispose();
            sourceView = device.CreateShaderResourceView(source);
            sourceKey = source.NativePointer;
        }
        var d = source.Description;
        return new Size((int)d.Width, (int)d.Height);
    }

    private unsafe void SetParams(Rectangle region, Size model, Size source, float strength, float convergence, int maxHole,
        int layout = 0, float bandLeft = 0, float bandRight = 0)
    {
        var p = new Params
        {
            Rect = new Int4(region.X, region.Y, region.Width, region.Height),
            ModelSize = new Int2(model.Width, model.Height),
            SourceSize = new Int2(source.Width, source.Height),
            Strength = strength,
            Convergence = convergence,
            MaxHole = maxHole,
            DilateRadius = DilateRadius,
            RangeSigma = RangeSigma,
            Layout = layout,
            BandLeft = bandLeft,
            BandRight = bandRight,
            // The model's soft edges span about two model pixels each way.
            SnapRadius = model.Width > 0 ? Math.Clamp((int)Math.Round(2.5 * region.Width / model.Width), 2, 16) : 0,
            SnapStep = SnapStep,
            LookupSkip = LookupSkip ? 1 : 0,
        };
        MappedSubresource m = context.Map(constants, MapMode.WriteDiscard);
        *(Params*)m.DataPointer = p;
        context.Unmap(constants, 0);
    }

    /// <summary>One ping-pong step over the nearness: reads <paramref name="near"/> (t2), writes the other texture, and returns it.</summary>
    private Texture NearPass(Texture near, ID3D11ComputeShader shader, int x, int y, string mark)
    {
        Texture other = near == nearA ? nearB! : nearA!;
        context.CSSetShaderResource(2, near.Srv);
        Dispatch(shader, other.Uav, x, y);
        context.CSSetShaderResource(2, null!);
        Timer?.Mark(mark);
        return other;
    }

    private void Dispatch(ID3D11ComputeShader shader, ID3D11UnorderedAccessView output, int x, int y)
    {
        context.CSSetShader(shader);
        context.CSSetConstantBuffer(0, constants);
        context.CSSetSampler(0, linear);
        context.CSSetShaderResource(0, sourceView!);
        context.CSSetUnorderedAccessView(0, output);
        context.Dispatch((uint)Math.Max(1, x), (uint)Math.Max(1, y), 1);
        context.CSSetUnorderedAccessView(0, null!);
    }

    private void EnsureRegion(Size size)
    {
        if (size == regionSize && eyes != null) return;
        nearA?.Dispose();
        nearB?.Dispose();
        eyes?.Dispose();
        blocks?.Dispose();
        blocks = new Texture(device, (size.Width + 7) / 8, size.Height, Format.R16_Float);
        nearA = new Texture(device, size.Width, size.Height, Format.R16_Float);
        nearB = new Texture(device, size.Width, size.Height, Format.R16_Float);
        eyes = new Texture(device, 2 * size.Width, size.Height, Format.R8G8B8A8_UNorm);
        EnsureBuffer(ref keys, 2 * size.Width * size.Height, 4);
        regionSize = size;
    }

    private void EnsureBuffer(ref GpuBuffer? buffer, int elements, int stride)
    {
        if (buffer != null && buffer.Elements >= elements) return;
        buffer?.Dispose();
        buffer = new GpuBuffer(device, elements, stride);
    }

    public void Dispose()
    {
        sourceView?.Dispose();
        tensor?.Dispose();
        keys?.Dispose();
        holes?.Dispose();
        depthView?.Dispose();
        depthTexture?.Dispose();
        nearA?.Dispose();
        nearB?.Dispose();
        eyes?.Dispose();
        blocks?.Dispose();
        linear.Dispose();
        inputDone.Dispose();
        constants.Dispose();
        foreach (var s in new[] { preprocess, upsample, snap, dilate, slopeLimit, splat, resolve, blockMaxima, lookup, nativeSplit }) s.Dispose();
    }

    /// <summary>
    /// A structured buffer with a UAV, and a staging copy for reading back made on the first read
    /// (the splat's 16 MB key buffer is never read).
    /// </summary>
    private sealed class GpuBuffer : IDisposable
    {
        private readonly ID3D11Device device;
        private readonly ID3D11Buffer buffer;
        private readonly uint size;
        private ID3D11Buffer? staging;

        public GpuBuffer(ID3D11Device device, int elements, int stride)
        {
            this.device = device;
            Elements = elements;
            size = (uint)(elements * stride);
            buffer = device.CreateBuffer(new BufferDescription(size, BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));
            Uav = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements },
            });
        }

        public int Elements { get; }

        public ID3D11UnorderedAccessView Uav { get; }

        /// <summary>A copy queued by <see cref="QueueRead"/> waits to be read.</summary>
        public bool Pending { get; private set; }

        /// <summary>Queues a copy of the buffer as it will be after the work queued so far.</summary>
        public void QueueRead(ID3D11DeviceContext context)
        {
            staging ??= device.CreateBuffer(new BufferDescription(size, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            context.CopyResource(staging, buffer);
            Pending = true;
        }

        /// <summary>Reads the queued copy, waiting for the GPU only if it has not made it yet.</summary>
        public unsafe void ReadQueued(ID3D11DeviceContext context, Span<byte> destination)
        {
            MappedSubresource m = context.Map(staging!, MapMode.Read);
            new ReadOnlySpan<byte>((void*)m.DataPointer, destination.Length).CopyTo(destination);
            context.Unmap(staging!, 0);
            Pending = false;
        }

        public void Dispose()
        {
            Uav.Dispose();
            staging?.Dispose();
            buffer.Dispose();
        }
    }

    /// <summary>A 2D texture with a UAV and an SRV.</summary>
    private sealed class Texture : IDisposable
    {
        private readonly ID3D11Texture2D texture;

        public Texture(ID3D11Device device, int width, int height, Format format)
        {
            texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            });
            Uav = device.CreateUnorderedAccessView(texture);
            Srv = device.CreateShaderResourceView(texture);
        }

        public ID3D11UnorderedAccessView Uav { get; }

        public ID3D11ShaderResourceView Srv { get; }

        public void Dispose()
        {
            Srv.Dispose();
            Uav.Dispose();
            texture.Dispose();
        }
    }
}
