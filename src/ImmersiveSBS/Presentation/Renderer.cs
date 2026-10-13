using System.Drawing;
using System.Numerics;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Theater;
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
        cbuffer Quad : register(b0)
        {
            float4 rect;     // left, top, right, bottom in NDC
            float4 uvRect;   // left, top, right, bottom in texture coordinates
        };
        Texture2D tex : register(t0);
        SamplerState samp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint id : SV_VertexID)
        {
            float2 uv = float2(id & 1, id >> 1);       // strip: TL, TR, BL, BR
            VSOut o;
            o.pos = float4(lerp(rect.x, rect.z, uv.x), lerp(rect.y, rect.w, uv.y), 0, 1);
            o.uv = lerp(uvRect.xy, uvRect.zw, uv);
            return o;
        }

        float4 PSOpaque(VSOut i) : SV_Target { return float4(tex.Sample(samp, i.uv).rgb, 1); }
        float4 PSSample(VSOut i) : SV_Target { return tex.Sample(samp, i.uv); }
        """;

    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    /// <summary>The Quad constant buffer: where to draw (NDC) and which part of the texture.</summary>
    private readonly record struct Quad(Vector4 Rect, Vector4 UvRect);

    private const uint QuadSize = 32;

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain2 swapChain;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader opaqueShader;
    private readonly ID3D11PixelShader sampleShader;
    private readonly ID3D11Buffer fullQuad;
    private readonly ID3D11Buffer cursorQuad;
    private readonly ID3D11Buffer stereoQuad;
    private readonly ID3D11Buffer uiQuad;
    private readonly Dictionary<UiImage, ID3D11ShaderResourceView> uiViews = [];
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

        Quad full = new(new Vector4(-1, 1, 1, -1), new Vector4(0, 0, 1, 1));
        fullQuad = device.CreateBuffer(in full, new BufferDescription(QuadSize, BindFlags.ConstantBuffer, ResourceUsage.Immutable));
        cursorQuad = device.CreateBuffer(new BufferDescription(QuadSize, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        stereoQuad = device.CreateBuffer(new BufferDescription(QuadSize, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        uiQuad = device.CreateBuffer(new BufferDescription(QuadSize, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

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

    /// <param name="stereo">AI 3D eye images of a region; drawn over the desktop in side-by-side only.</param>
    /// <param name="ui">The model badge, in side-by-side only.</param>
    /// <param name="live">Parts of the source (source px) drawn over <paramref name="stereo"/> as
    /// they are now, the same in both eyes: the theater player's controls and 3D panel.</param>
    public void Render(ID3D11Texture2D? source, PointerState? pointer, StereoOverlay? stereo = null, UiLayer? ui = null,
        IReadOnlyList<Rectangle>? live = null)
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

        bool haveCursor = pointer is { Visible: true, Width: > 0 } && UpdateCursor(pointer);

        context.OMSetRenderTargets(target!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetState(rasterizer);
        context.VSSetShader(vertexShader);

        for (int eye = 0; eye < eyes; eye++)
        {
            int eyeX = eye * eyeW;
            var eyeHalf = new Vector4(eye * 0.5f, 0, eye * 0.5f + 0.5f, 1);

            {
                context.RSSetViewport(eyeX + (eyeW - vw) / 2, (Height - vh) / 2, vw, vh);
                context.PSSetSampler(0, sampler);

                context.OMSetBlendState(null);
                context.VSSetConstantBuffer(0, fullQuad);
                context.PSSetShader(opaqueShader);
                context.PSSetShaderResource(0, sourceView!);
                context.Draw(4, 0);

                if (stereo is StereoOverlay s && eyes == 2)
                {
                    // The eyes texture holds the left eye in its left half, the right eye in its right.
                    WriteQuad(stereoQuad, SourceNdc(s.Region, srcW, srcH), eyeHalf);
                    context.VSSetConstantBuffer(0, stereoQuad);
                    context.PSSetShaderResource(0, s.Eyes);
                    context.Draw(4, 0);

                    // The page's own controls over the 3D picture: live and flat, as the page draws them.
                    if (live is { Count: > 0 })
                    {
                        context.PSSetShaderResource(0, sourceView!);
                        var bounds = new Rectangle(0, 0, srcW, srcH);
                        foreach (Rectangle box in live)
                        {
                            Rectangle r = Rectangle.Intersect(box, bounds);
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            WriteQuad(stereoQuad, SourceNdc(r, srcW, srcH),
                                new Vector4((float)r.Left / srcW, (float)r.Top / srcH, (float)r.Right / srcW, (float)r.Bottom / srcH));
                            context.VSSetConstantBuffer(0, stereoQuad);
                            context.Draw(4, 0);
                        }
                    }
                }

                if (haveCursor)
                {
                    WriteQuad(cursorQuad, SourceNdc(new Rectangle(pointer!.X, pointer.Y, pointer.Width, pointer.Height), srcW, srcH),
                        new Vector4(0, 0, 1, 1));
                    DrawCursor(pointer);
                }
            }

            // On-screen buttons, in eye px, on top of everything.
            if (ui != null && eyes == 2 && ui.Images.Count > 0)
            {
                context.RSSetViewport(eyeX, 0, eyeW, Height);
                context.PSSetSampler(0, pointSampler);
                context.PSSetShader(sampleShader);
                context.OMSetBlendState(alphaBlend);
                foreach (var (image, rect) in ui.Images)
                {
                    WriteQuad(uiQuad, EyeNdc(rect, eyeW), new Vector4(0, 0, 1, 1));
                    context.VSSetConstantBuffer(0, uiQuad);
                    context.PSSetShaderResource(0, ViewOf(image));
                    context.Draw(4, 0);
                }
                ForgetUnusedImages(ui.Images);
            }
        }

        context.PSSetShaderResource(0, null!);
    }

    private void DrawCursor(PointerState pointer)
    {
        context.VSSetConstantBuffer(0, cursorQuad);
        context.PSSetShader(sampleShader);
        context.OMSetBlendState(alphaBlend);
        context.PSSetShaderResource(0, cursorColor!);
        context.Draw(4, 0);

        if (pointer.HasXor)
        {
            context.OMSetBlendState(invertBlend);
            context.PSSetShaderResource(0, cursorXor!);
            context.Draw(4, 0);
        }
    }

    /// <summary>A rectangle in source px as NDC of the source viewport.</summary>
    private static Vector4 SourceNdc(Rectangle r, int srcW, int srcH) => new(
        r.Left * 2f / srcW - 1, 1 - r.Top * 2f / srcH, r.Right * 2f / srcW - 1, 1 - r.Bottom * 2f / srcH);

    /// <summary>A rectangle in eye px as NDC of the whole-eye viewport.</summary>
    private Vector4 EyeNdc(Rectangle r, int eyeW) => new(
        r.Left * 2f / eyeW - 1, 1 - r.Top * 2f / Height, r.Right * 2f / eyeW - 1, 1 - r.Bottom * 2f / Height);

    /// <summary>
    /// Frees the textures of UI images not drawn this frame once there are many: an image made
    /// anew for changing text would otherwise keep its texture for good.
    /// </summary>
    private void ForgetUnusedImages(IReadOnlyList<(UiImage Image, Rectangle EyeRect)> drawn)
    {
        if (uiViews.Count <= 256) return;
        var keep = new HashSet<UiImage>(drawn.Select(d => d.Image));
        foreach (UiImage image in uiViews.Keys.Where(i => !keep.Contains(i)).ToList())
        {
            uiViews[image].Dispose();
            uiViews.Remove(image);
        }
    }

    /// <summary>The uploaded texture of a UI image (uploaded on first use, kept).</summary>
    private ID3D11ShaderResourceView ViewOf(UiImage image)
    {
        if (!uiViews.TryGetValue(image, out ID3D11ShaderResourceView? view))
            uiViews[image] = view = CreateImage(image.Bgra, image.Width, image.Height);
        return view;
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

    /// <summary>Uploads the cursor's images when its shape changed.</summary>
    private bool UpdateCursor(PointerState pointer)
    {
        if (pointer.ShapeVersion != cursorVersion)
        {
            cursorColor?.Dispose();
            cursorXor?.Dispose();
            cursorColor = CreateImage(pointer.Color, pointer.Width, pointer.Height);
            cursorXor = CreateImage(pointer.Xor, pointer.Width, pointer.Height);
            cursorVersion = pointer.ShapeVersion;
        }
        return true;
    }

    /// <summary>Writes a quad: where to draw (NDC of the current viewport) and which part of the texture.</summary>
    private unsafe void WriteQuad(ID3D11Buffer buffer, Vector4 ndc, Vector4 uv)
    {
        MappedSubresource m = context.Map(buffer, MapMode.WriteDiscard);
        *(Quad*)m.DataPointer = new Quad(ndc, uv);
        context.Unmap(buffer, 0);
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
        stereoQuad.Dispose();
        uiQuad.Dispose();
        foreach (var view in uiViews.Values) view.Dispose();
        fullQuad.Dispose();
        sampleShader.Dispose();
        opaqueShader.Dispose();
        vertexShader.Dispose();
        swapChain.Dispose();
    }
}

/// <summary>
/// AI 3D eye images (left half, right half) drawn over <paramref name="Region"/> of the source (source px).
/// </summary>
public readonly record struct StereoOverlay(ID3D11ShaderResourceView Eyes, Rectangle Region);

/// <summary>What is drawn on top in side-by-side (the model badge), in eye px.</summary>
public sealed class UiLayer
{
    public IReadOnlyList<(UiImage Image, Rectangle EyeRect)> Images { get; init; } = [];
}
