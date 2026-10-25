using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Capture;

/// <summary>The texture a capture copies each frame into, so its own frame can be released at once.</summary>
internal static class CaptureTexture
{
    /// <summary>Makes <paramref name="texture"/> a shader-readable texture of this size and format; false when it already was.</summary>
    public static bool Ensure(ID3D11Device device, ref ID3D11Texture2D? texture, uint width, uint height, Format format)
    {
        if (texture != null)
        {
            var d = texture.Description;
            if (d.Width == width && d.Height == height && d.Format == format) return false;
            texture.Dispose();
        }

        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        return true;
    }
}
