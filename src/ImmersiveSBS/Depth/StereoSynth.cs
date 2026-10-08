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
/// the luma grid for <see cref="RegionTracker"/>, the model input tensor, and from a nearness
/// map the two eye images of the video region: guided upsample (§6.3), foreground dilation
/// (§6.4), forward splat with a depth test, hole fill toward the background and gather (§7.3).
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
            float rangeSigma;   // luma sigma of the guided upsample
            int layout;         // native split: 0 the same picture in both eyes, 1 side-by-side, 2 top-bottom
            float bandLeft;     // floating window: black band on the left eye's left edge, px
            float bandRight;    // and on the right eye's right edge
        };

        Texture2D<float4> Source : register(t0);
        Texture2D<float> Depth : register(t1);
        Texture2D<float> NearIn : register(t2);
        SamplerState Linear : register(s0);

        static const float3 LumaWeights = float3(0.299, 0.587, 0.114);

        float LumaAt(float2 sourcePx) { return dot(Source.SampleLevel(Linear, sourcePx / sourceSize, 0).rgb, LumaWeights); }

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
            c = (c * 0.25 - float3(0.485, 0.456, 0.406)) / float3(0.229, 0.224, 0.225);
            uint plane = modelSize.x * modelSize.y;
            uint i = id.y * modelSize.x + id.x;
            Tensor[i] = c.r;
            Tensor[plane + i] = c.g;
            Tensor[2 * plane + i] = c.b;
        }

        // --- Mean luma per 8×8 cell of the whole frame, 0..255.
        RWStructuredBuffer<uint> Grid : register(u0);
        [numthreads(8, 8, 1)]
        void LumaGrid(uint3 id : SV_DispatchThreadID)
        {
            uint2 cells = (uint2(sourceSize) + 7) / 8;
            if (id.x >= cells.x || id.y >= cells.y) return;
            float sum = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int2 p = min(int2(id.xy * 8) + int2(x, y), sourceSize - 1);
                    sum += dot(Source.Load(int3(p, 0)).rgb, LumaWeights);
                }
            Grid[id.y * cells.x + id.x] = (uint)round(sum / 64 * 255);
        }

        // --- Joint bilateral upsample of nearness to region resolution, guided by luma (§6.3).
        RWTexture2D<float> NearOut : register(u0);
        [numthreads(8, 8, 1)]
        void Upsample(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)rect.z || id.y >= (uint)rect.w) return;
            float2 toModel = float2(modelSize) / float2(rect.zw);
            float2 m = (id.xy + 0.5) * toModel - 0.5;
            int2 m0 = (int2)floor(m + 0.5);
            float yc = LumaAt(rect.xy + id.xy + 0.5);
            float wsum = 0, nsum = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int2 q = clamp(m0 + int2(dx, dy), int2(0, 0), modelSize - 1);
                    float2 d = float2(q) - m;
                    float yq = LumaAt(rect.xy + (q + 0.5) / toModel);
                    float dyq = yc - yq;
                    float w = exp(-0.5 * dot(d, d)) * exp(-dyq * dyq / (2 * rangeSigma * rangeSigma)) + 1e-6;
                    nsum += w * Depth.Load(int3(q, 0));
                    wsum += w;
                }
            NearOut[id.xy] = nsum / wsum;
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
                    if (t < 0 || t >= (int)w) continue;
                    float xs = id.x + (t - xt);
                    uint fx = (uint)clamp(round((xs + 2) * 16), 16.0, 65535.0);
                    InterlockedMax(Keys[eye * plane + id.y * w + t], near16 | fx);
                }
            }
        }

        // --- Gather; a hole is background revealed behind a foreground edge, so it continues
        // the farther of its two nearest neighbours.
        RWTexture2D<unorm float4> Eyes : register(u1);
        [numthreads(8, 8, 1)]
        void Resolve(uint3 id : SV_DispatchThreadID)
        {
            uint w = rect.z, plane = rect.z * rect.w;
            if (id.x >= 2 * w || id.y >= (uint)rect.w) return;
            uint eye = id.x / w;
            int t = id.x % w;
            uint row = eye * plane + id.y * w;
            uint key = Keys[row + t];
            float xs;
            if (key != 0)
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
                else if (r < 0 || (l >= 0 && (kl >> 16) <= (kr >> 16))) xs = (kl & 0xFFFF) / 16.0 - 2 + (t - l);
                else xs = (kr & 0xFFFF) / 16.0 - 2 + (t - r);
            }
            xs = clamp(xs, 0, w - 1);
            float2 p = float2(rect.x + xs + 0.5, rect.y + id.y + 0.5);
            float3 c = Source.SampleLevel(Linear, p / sourceSize, 0).rgb;

            // Floating window (§7.3 step 5): where something in front of the screen touches the
            // video's side edge, a black band moves that edge forward too.
            if ((eye == 0 && t < bandLeft) || (eye == 1 && t >= (int)w - bandRight)) c = 0;
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
    }

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11ComputeShader preprocess, lumaGrid, upsample, dilate, splat, resolve, nativeSplit;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState linear;

    private IntPtr sourceKey;
    private ID3D11ShaderResourceView? sourceView;

    private GpuBuffer? tensor, grid, keys;
    private ID3D11Texture2D? depthTexture;
    private ID3D11ShaderResourceView? depthView;
    private Size depthSize;
    private Texture? nearA, nearB, eyes;
    private Size regionSize;

    public StereoSynth(ID3D11Device device)
    {
        this.device = device;
        context = device.ImmediateContext;
        preprocess = Compile("Preprocess");
        lumaGrid = Compile("LumaGrid");
        upsample = Compile("Upsample");
        dilate = Compile("Dilate");
        splat = Compile("Splat");
        resolve = Compile("Resolve");
        nativeSplit = Compile("NativeSplit");
        constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        linear = device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    /// <summary>Foreground dilation radius in source px.</summary>
    public int DilateRadius { get; set; } = 3;

    /// <summary>Luma sigma of the guided upsample (luma in 0..1).</summary>
    public float RangeSigma { get; set; } = 0.1f;

    private ID3D11ComputeShader Compile(string entry) =>
        device.CreateComputeShader(Compiler.Compile(Hlsl, entry, "stereo.hlsl", "cs_5_0").Span);

    /// <summary>Mean luma per 8×8 cell of the frame, 0..255; <paramref name="cells"/> gets the grid size.</summary>
    public void ReadLumaGrid(ID3D11Texture2D source, byte[] destination, out Size cells)
    {
        Size src = BindSource(source);
        cells = new Size((src.Width + 7) / 8, (src.Height + 7) / 8);
        int count = cells.Width * cells.Height;
        if (destination.Length < count) throw new ArgumentException("Grid buffer too small.");

        EnsureBuffer(ref grid, count, 4);
        SetParams(Rectangle.Empty, Size.Empty, src, 0, 0, 0);
        Dispatch(lumaGrid, grid!.Uav, (cells.Width + 7) / 8, (cells.Height + 7) / 8);

        var values = new uint[count];
        grid.Read(context, MemoryMarshal.AsBytes(values.AsSpan()));
        for (int i = 0; i < count; i++) destination[i] = (byte)Math.Min(values[i], 255u);
    }

    /// <summary>The model input for <paramref name="region"/>: planar RGB, ImageNet-normalised.</summary>
    public void ReadModelInput(ID3D11Texture2D source, Rectangle region, Size modelSize, float[] destination)
    {
        Size src = BindSource(source);
        int count = 3 * modelSize.Width * modelSize.Height;
        EnsureBuffer(ref tensor, count, 4);
        SetParams(region, modelSize, src, 0, 0, 0);
        Dispatch(preprocess, tensor!.Uav, (modelSize.Width + 7) / 8, (modelSize.Height + 7) / 8);
        tensor.Read(context, MemoryMarshal.AsBytes(destination.AsSpan(0, count)));
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

        context.CSSetShaderResource(2, nearA.Srv);
        Dispatch(dilate, nearB!.Uav, (region.Width + 63) / 64, region.Height);
        context.CSSetShaderResource(2, null!);

        context.ClearUnorderedAccessView(keys!.Uav, new Int4(0, 0, 0, 0));
        context.CSSetShaderResource(2, nearB.Srv);
        Dispatch(splat, keys.Uav, gx, gy);
        context.CSSetShaderResource(2, null!);

        context.CSSetUnorderedAccessView(1, eyes!.Uav);
        Dispatch(resolve, keys.Uav, (2 * region.Width + 7) / 8, gy);
        context.CSSetUnorderedAccessView(1, null!);

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
        };
        MappedSubresource m = context.Map(constants, MapMode.WriteDiscard);
        *(Params*)m.DataPointer = p;
        context.Unmap(constants, 0);
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
        grid?.Dispose();
        keys?.Dispose();
        depthView?.Dispose();
        depthTexture?.Dispose();
        nearA?.Dispose();
        nearB?.Dispose();
        eyes?.Dispose();
        linear.Dispose();
        constants.Dispose();
        foreach (var s in new[] { preprocess, lumaGrid, upsample, dilate, splat, resolve, nativeSplit }) s.Dispose();
    }

    /// <summary>A structured buffer with a UAV and a staging copy for reading back.</summary>
    private sealed class GpuBuffer : IDisposable
    {
        private readonly ID3D11Buffer buffer;
        private readonly ID3D11Buffer staging;

        public GpuBuffer(ID3D11Device device, int elements, int stride)
        {
            Elements = elements;
            uint size = (uint)(elements * stride);
            buffer = device.CreateBuffer(new BufferDescription(size, BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));
            staging = device.CreateBuffer(new BufferDescription(size, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            Uav = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements },
            });
        }

        public int Elements { get; }

        public ID3D11UnorderedAccessView Uav { get; }

        /// <summary>Copies to staging and reads; waits for the GPU (small buffers only).</summary>
        public unsafe void Read(ID3D11DeviceContext context, Span<byte> destination)
        {
            context.CopyResource(staging, buffer);
            MappedSubresource m = context.Map(staging, MapMode.Read);
            new ReadOnlySpan<byte>((void*)m.DataPointer, destination.Length).CopyTo(destination);
            context.Unmap(staging, 0);
        }

        public void Dispose()
        {
            Uav.Dispose();
            staging.Dispose();
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
