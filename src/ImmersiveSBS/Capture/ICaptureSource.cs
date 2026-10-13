using Vortice.Direct3D11;

namespace ImmersiveSBS.Capture;

public enum CaptureMethod
{
    /// <summary>DXGI Desktop Duplication: frame and cursor separately (plan D3).</summary>
    DesktopDuplication,

    /// <summary>Windows.Graphics.Capture, cursor drawn into the frame by Windows.</summary>
    GraphicsCapture,
}

/// <summary>What one <see cref="ICaptureSource.Acquire"/> call changed.</summary>
/// <param name="ContentQpc">The newest change's QPC time, the frame's or the cursor's.</param>
/// <param name="FrameQpc">When the desktop image was made, QPC; 0 without a new one.</param>
public readonly record struct CaptureUpdate(bool Frame, bool Pointer, long ContentQpc, long FrameQpc = 0)
{
    public bool Any => Frame || Pointer;
}

/// <summary>
/// A virtual monitor being captured into <see cref="Texture"/>, a texture on the shared device
/// that stays valid until the next <see cref="ICaptureSource.Acquire"/>.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>The latest desktop image, or null before the first frame.</summary>
    ID3D11Texture2D? Texture { get; }

    /// <summary>Separately captured cursor; never visible when the method draws it into the frame.</summary>
    PointerState Pointer { get; }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new desktop image or cursor change and
    /// copies it in. Recovers from lost access (mode change, secure desktop) by itself.
    /// </summary>
    CaptureUpdate Acquire(int timeoutMs);
}
