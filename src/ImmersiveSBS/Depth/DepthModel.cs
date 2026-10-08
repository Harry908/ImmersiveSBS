using System.IO;

namespace ImmersiveSBS.Depth;

/// <summary>The model ladder (video-3d-plan.md D3/D4).</summary>
public enum DepthTier
{
    /// <summary>Depth Anything V2 Small (Apache-2.0): fastest, smoother, loses thin structure.</summary>
    Small,

    /// <summary>Distill-Any-Depth Base (MIT): the default; close to Large at a fraction of its cost.</summary>
    Base,
}

/// <summary>Model input size, 16:9, each side a multiple of the ViT patch (14).</summary>
public enum DepthPreset
{
    /// <summary>392×224.</summary>
    Fast,

    /// <summary>518×294, the default.</summary>
    Balanced,

    /// <summary>756×420.</summary>
    Quality,
}

/// <summary>
/// One static-shape fp16 ONNX export (float32 input NCHW with ImageNet normalisation, output
/// [1, H, W] relative inverse depth, larger = nearer). TensorRT for RTX needs static shapes for
/// these exports, so every tier and size is its own file:
/// %LOCALAPPDATA%\ImmersiveSBS\models\{da2-small|dad-base}_{H}x{W}.onnx.
/// </summary>
public sealed record DepthModel(DepthTier Tier, DepthPreset Preset)
{
    public static string ModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "models");

    public int Width => Preset switch { DepthPreset.Fast => 392, DepthPreset.Quality => 756, _ => 518 };

    public int Height => Preset switch { DepthPreset.Fast => 224, DepthPreset.Quality => 420, _ => 294 };

    /// <summary>File name without extension, also the engine cache key.</summary>
    public string Stem => (Tier == DepthTier.Small ? "da2-small" : "dad-base") + $"_{Height}x{Width}";

    public string FilePath => Path.Combine(ModelsDirectory, Stem + ".onnx");

    public bool Exists => File.Exists(FilePath);

    public override string ToString() =>
        (Tier == DepthTier.Small ? "Depth Anything V2 Small" : "Distill-Any-Depth Base") + $" {Width}×{Height}";
}
