using System.IO;

namespace ImmersiveSBS.Depth;

/// <summary>The model ladder (video-3d-plan.md D3/D4), fastest first within each family.</summary>
public enum DepthTier
{
    /// <summary>Depth Anything V2 Small (Apache-2.0): fastest, smoother, loses thin structure.</summary>
    Small,

    /// <summary>Distill-Any-Depth Base (MIT): the default; close to Large at a fraction of its cost.</summary>
    Base,

    /// <summary>
    /// Distill-Any-Depth Large (MIT): the sharpest. The available export only takes 518×518 (its
    /// position embeddings are baked in), so a 16:9 frame is squeezed into it; ~40 ms per frame.
    /// </summary>
    Large,

    /// <summary>Depth Anything V2 Base (CC-BY-NC-4.0: personal use only, not to be shipped).</summary>
    V2Base,

    /// <summary>Depth Anything V2 Large (CC-BY-NC-4.0: personal use only, not to be shipped).</summary>
    V2Large,

    /// <summary>
    /// Depth Anything 3 Small (Apache-2.0), the 2025 generation. Exported any-view; used here with
    /// one image and its depth output only (the camera head is cut off), and its output is depth,
    /// not inverse depth (<see cref="DepthModel.OutputIsDepth"/>).
    /// </summary>
    DA3Small,

    /// <summary>Depth Anything 3 Base (Apache-2.0); as <see cref="DA3Small"/>.</summary>
    DA3Base,
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
/// relative inverse depth, larger = nearer). TensorRT for RTX needs static shapes for these
/// exports, so every tier and size is its own file:
/// %LOCALAPPDATA%\ImmersiveSBS\models\{stem}_{H}x{W}.onnx.
/// </summary>
public sealed record DepthModel(DepthTier Tier, DepthPreset Preset)
{
    public static string ModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "models");

    /// <summary>True for a model that exists in one fixed size only, whatever the preset.</summary>
    public bool FixedSize => Tier == DepthTier.Large;

    /// <summary>The model outputs depth (larger = farther), not relative inverse depth: the estimator inverts it.</summary>
    public bool OutputIsDepth => Tier is DepthTier.DA3Small or DepthTier.DA3Base;

    public int Width => FixedSize ? 518 : Preset switch { DepthPreset.Fast => 392, DepthPreset.Quality => 756, _ => 518 };

    public int Height => FixedSize ? 518 : Preset switch { DepthPreset.Fast => 224, DepthPreset.Quality => 420, _ => 294 };

    /// <summary>File name without extension, also the engine cache key.</summary>
    public string Stem => Family + $"_{Height}x{Width}";

    private string Family => Tier switch
    {
        DepthTier.Small => "da2-small",
        DepthTier.Large => "dad-large",
        DepthTier.V2Base => "da2-base",
        DepthTier.V2Large => "da2-large",
        DepthTier.DA3Small => "da3-small",
        DepthTier.DA3Base => "da3-base",
        _ => "dad-base",
    };

    public string FilePath => Path.Combine(ModelsDirectory, Stem + ".onnx");

    public bool Exists => File.Exists(FilePath);

    public static string Name(DepthTier tier) => tier switch
    {
        DepthTier.Small => "Depth Anything V2 Small",
        DepthTier.Large => "Distill-Any-Depth Large",
        DepthTier.V2Base => "Depth Anything V2 Base",
        DepthTier.V2Large => "Depth Anything V2 Large",
        DepthTier.DA3Small => "Depth Anything 3 Small",
        DepthTier.DA3Base => "Depth Anything 3 Base",
        _ => "Distill-Any-Depth Base",
    };

    public override string ToString() => Name(Tier) + $" {Width}×{Height}";
}
