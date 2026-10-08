using System.Numerics;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Displays;
using SharpGen.Runtime;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ImmersiveSBS.Presentation;

/// <summary>
/// Draws the captured desktop into the glasses' swap chain: once per eye when the glasses show
/// a side-by-side mode (3840×1080), once when they are in 2D (1920×1080), pixel for pixel when
/// the sizes match. The cursor is drawn last, in the same place in both eyes (zero depth).
/// </summary>
public sealed class Renderer : IDisposable
{
    private const string Hlsl = """
        cbuffer Quad : register(b0) { float4 rect; }   // left, top, right, bottom in NDC
        Texture2D tex : register(t0);
        SamplerState samp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint id : SV_VertexID)
        {
            float2 uv = float2(id & 1, id >> 1);       // strip: TL, TR, BL, BR
            VSOut o;
            o.pos = float4(lerp(rect.x, rect.z, uv.x), lerp(rect.y, rect.w, uv.y), 0, 1);
            o.uv = uv;
            return o;
        }

        float4 PSOpaque(VSOut i) : SV_Target { return float4(tex.Sample(samp, i.uv).rgb, 1); }
        float4 PSSample(VSOut i) : SV_Target { return tex.Sample(samp, i.uv); }
        """;

    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain2 swapChain;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader opaqueShader;
    private readonly ID3D11PixelShader sampleShader;
    private readonly ID3D11Buffer fullQuad;
    private readonly ID3D11Buffer cursorQuad;
    private readonly ID3D11SamplerState pointSampler;
    private readonly ID3D11SamplerState linearSampler;
    private readonly ID3D11BlendState alphaBlend;
    private readonly ID3D11BlendState invertBlend;
    private readonly ID3D11RasterizerState rasterizer;
    private ID3D11RenderTargetView? target;

    private IntPtr sourceKey;
    private ID3D11ShaderResourceView? sourceView;
    private int cursorVersion = -1;
    private ID3D11ShaderResourceView? cursorColor;
    private ID3D11ShaderResourceView? cursorXor;

    public Renderer(ID3D11Device device, IntPtr hwnd, int width, int height)
    {
        this.device = device;
        context = device.ImmediateContext;

        using (IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
        using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = Flags,
            };
            using IDXGISwapChain1 sc = factory.CreateSwapChainForHwnd(device, hwnd, desc);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            swapChain = sc.QueryInterface<IDXGISwapChain2>();
        }

        // One queued frame: the next frame is drawn from the freshest capture.
        swapChain.MaximumFrameLatency = 1;
        Waitable = swapChain.FrameLatencyWaitableObject;
        Width = width;
        Height = height;
        CreateTarget();

        ReadOnlyMemory<byte> vs = Compiler.Compile(Hlsl, "VS", "renderer.hlsl", "vs_5_0");
        vertexShader = device.CreateVertexShader(vs.Span);
        opaqueShader = device.CreatePixelShader(Compiler.Compile(Hlsl, "PSOpaque", "renderer.hlsl", "ps_5_0").Span);
        sampleShader = device.CreatePixelShader(Compiler.Compile(Hlsl, "PSSample", "renderer.hlsl", "ps_5_0").Span);

        Vector4 full = new(-1, 1, 1, -1);
        fullQuad = device.CreateBuffer(in full, new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Immutable));
        cursorQuad = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        pointSampler = device.CreateSamplerState(SamplerDescription.PointClamp);
        linearSampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        alphaBlend = device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));
        // src·(1−dst) + dst·(1−src): XOR for 0/1 values, which is what an inverting cursor means.
        // Alpha blend factors may not be *_COLOR; the alpha channel keeps the destination.
        invertBlend = device.CreateBlendState(new BlendDescription(
            Blend.InverseDestinationColor, Blend.InverseSourceColor, Blend.Zero, Blend.One));
        rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Signalled when the swap chain can take another frame.</summary>
    public IntPtr Waitable { get; }

    /// <summary>False for the mirror window, which is always drawn once.</summary>
    public bool AllowSideBySide { get; init; } = true;

    public bool SideBySide => AllowSideBySide && GlassesStateEvaluator.ClassifyFrame(Width, Height) == StereoFrameShape.SideBySide;

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;

        context.UnsetRenderTargets();
        target?.Dispose();
        target = null;
        swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, Flags).CheckError();
        Width = width;
        Height = height;
        CreateTarget();
    }

    public void Render(ID3D11Texture2D? source, PointerState? pointer)
    {
        context.ClearRenderTargetView(target!, new Color4(0, 0, 0, 1));
        if (source == null) return;

        if (source.NativePointer != sourceKey)
        {
            sourceView?.Dispose();
            sourceView = device.CreateShaderResourceView(source);
            sourceKey = source.NativePointer;
        }

        var sd = source.Description;
        int srcW = (int)sd.Width, srcH = (int)sd.Height;
        int eyes = SideBySide ? 2 : 1;
        int eyeW = Width / eyes;

        // Fit the source into each eye; integer offsets keep a 1:1 picture sharp.
        double scale = Math.Min((double)eyeW / srcW, (double)Height / srcH);
        int vw = (int)Math.Round(srcW * scale), vh = (int)Math.Round(srcH * scale);
        bool oneToOne = vw == srcW && vh == srcH;
        ID3D11SamplerState sampler = oneToOne ? pointSampler : linearSampler;

        bool drawCursor = pointer is { Visible: true, Width: > 0 } && UpdateCursor(pointer, srcW, srcH);

        context.OMSetRenderTargets(target!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetState(rasterizer);
        context.VSSetShader(vertexShader);
        context.PSSetSampler(0, sampler);

        for (int eye = 0; eye < eyes; eye++)
        {
            context.RSSetViewport(eye * eyeW + (eyeW - vw) / 2, (Height - vh) / 2, vw, vh);

            context.OMSetBlendState(null);
            context.VSSetConstantBuffer(0, fullQuad);
            context.PSSetShader(opaqueShader);
            context.PSSetShaderResource(0, sourceView!);
            context.Draw(4, 0);

            if (drawCursor)
            {
                context.VSSetConstantBuffer(0, cursorQuad);
                context.PSSetShader(sampleShader);
                context.OMSetBlendState(alphaBlend);
                context.PSSetShaderResource(0, cursorColor!);
                context.Draw(4, 0);

                if (pointer!.HasXor)
                {
                    context.OMSetBlendState(invertBlend);
                    context.PSSetShaderResource(0, cursorXor!);
                    context.Draw(4, 0);
                }
            }
        }

        context.PSSetShaderResource(0, null!);
    }

    /// <summary>Presents on the next vertical blank. Returns the present count for statistics.</summary>
    public uint Present()
    {
        Result r = swapChain.Present(1, PresentFlags.None);
        if (r.Failure) r.CheckError();
        return swapChain.LastPresentCount;
    }

    public bool TryGetFrameStatistics(out FrameStatistics stats) =>
        swapChain.GetFrameStatistics(out stats).Success;

    private unsafe bool UpdateCursor(PointerState pointer, int srcW, int srcH)
    {
        if (pointer.ShapeVersion != cursorVersion)
        {
            cursorColor?.Dispose();
            cursorXor?.Dispose();
            cursorColor = CreateImage(pointer.Color, pointer.Width, pointer.Height);
            cursorXor = CreateImage(pointer.Xor, pointer.Width, pointer.Height);
            cursorVersion = pointer.ShapeVersion;
        }

        // Pixel rectangle of the cursor in the source, as NDC of the eye viewport.
        var rect = new Vector4(
            pointer.X * 2f / srcW - 1,
            1 - pointer.Y * 2f / srcH,
            (pointer.X + pointer.Width) * 2f / srcW - 1,
            1 - (pointer.Y + pointer.Height) * 2f / srcH);

        MappedSubresource m = context.Map(cursorQuad, MapMode.WriteDiscard);
        *(Vector4*)m.DataPointer = rect;
        context.Unmap(cursorQuad, 0);
        return true;
    }

    private unsafe ID3D11ShaderResourceView CreateImage(byte[] bgra, int width, int height)
    {
        fixed (byte* p = bgra)
        {
            using ID3D11Texture2D tex = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, [new DataRectangle((IntPtr)p, (uint)width * 4)]);
            return device.CreateShaderResourceView(tex);
        }
    }

    private void CreateTarget()
    {
        using ID3D11Texture2D buffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        target = device.CreateRenderTargetView(buffer);
    }

    public void Dispose()
    {
        context.ClearState();
        cursorColor?.Dispose();
        cursorXor?.Dispose();
        sourceView?.Dispose();
        target?.Dispose();
        rasterizer.Dispose();
        invertBlend.Dispose();
        alphaBlend.Dispose();
        linearSampler.Dispose();
        pointSampler.Dispose();
        cursorQuad.Dispose();
        fullQuad.Dispose();
        sampleShader.Dispose();
        opaqueShader.Dispose();
        vertexShader.Dispose();
        swapChain.Dispose();
    }
}
