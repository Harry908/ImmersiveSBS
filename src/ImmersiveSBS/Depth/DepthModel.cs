using System.IO;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The model ladder (video-3d-plan.md D3/D4), fastest first within each family; all shippable
/// (Apache-2.0 or MIT). Removed 2026-10-11 after the glasses runs: Distill-Any-Depth Large (one
/// 518×518 size that squeezes 16:9, 41 ms a frame), Depth Anything V2 Base (non-commercial, no
/// better than Distill-Any-Depth Base at the same cost) and V2 Large (non-commercial; at
/// 756×420 it kept the GPU 96–99 % busy and capture fell to 8–12 fps). Settings naming them
/// load as <see cref="Base"/>.
/// </summary>
public enum DepthTier
{
    /// <summary>Depth Anything V2 Small (Apache-2.0): fastest, smoother, loses thin structure.</summary>
    Small,

    /// <summary>Distill-Any-Depth Base (MIT): the default; smooth edges at little cost.</summary>
    Base,

    /// <summary>
    /// Depth Anything 3 Small (Apache-2.0), the 2025 generation. Exported any-view; used here with
    /// one image and its depth output only (the camera head is cut off), and its output is depth,
    /// not inverse depth (<see cref="DepthModel.OutputIsDepth"/>).
    /// </summary>
    DA3Small,

    /// <summary>Depth Anything 3 Base (Apache-2.0); as <see cref="DA3Small"/>.</summary>
    DA3Base,

    /// <summary>
    /// Video Depth Anything Small (Apache-2.0), streaming: a Depth Anything V2 Small encoder whose
    /// head attends over the hidden states of the frames before (<see cref="VideoDepthEstimator"/>).
    /// Two files: the step and the first frame (<see cref="DepthModel.FirstFilePath"/>).
    /// </summary>
    VdaSmall,
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
/// exports, so every tier and size is its own file, {stem}_{H}x{W}.onnx, looked for in the
/// app's own models folder (where a package ships them), then in the user's.
/// </summary>
public sealed record DepthModel(DepthTier Tier, DepthPreset Preset)
{
    /// <summary>Models shipped with the app: the models folder next to ImmersiveSBS.exe.</summary>
    public static string AppModelsDirectory => Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>Models the user adds: %LOCALAPPDATA%\ImmersiveSBS\models.</summary>
    public static string UserModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "models");

    /// <summary>Where model files are looked for, for messages about a missing one.</summary>
    public static string ModelsDirectory => $"{AppModelsDirectory} or {UserModelsDirectory}";

    /// <summary>A model file: the app's copy if it ships one, else the user's (which may not exist).</summary>
    private static string Find(string fileName)
    {
        string shipped = Path.Combine(AppModelsDirectory, fileName);
        return File.Exists(shipped) ? shipped : Path.Combine(UserModelsDirectory, fileName);
    }

    /// <summary>The model outputs depth (larger = farther), not relative inverse depth: the estimator inverts it.</summary>
    public bool OutputIsDepth => Tier is DepthTier.DA3Small or DepthTier.DA3Base;

    /// <summary>The model carries state from frame to frame (<see cref="VideoDepthEstimator"/>).</summary>
    public bool Temporal => Tier == DepthTier.VdaSmall;

    /// <summary>
    /// The model's depth edges step in whole model pixels (Depth Anything V2 and 3; iw3 issue
    /// #406): depth anti-aliasing is on for it by default. Distill-Any-Depth's are already smooth.
    /// </summary>
    public bool SteppedEdges => Tier is not DepthTier.Base;

    public int Width => Preset switch { DepthPreset.Fast => 392, DepthPreset.Quality => 756, _ => 518 };

    public int Height => Preset switch { DepthPreset.Fast => 224, DepthPreset.Quality => 420, _ => 294 };

    /// <summary>File name without extension, also the engine cache key.</summary>
    public string Stem => Family + $"_{Height}x{Width}";

    private string Family => Tier switch
    {
        DepthTier.Small => "da2-small",
        DepthTier.DA3Small => "da3-small",
        DepthTier.DA3Base => "da3-base",
        DepthTier.VdaSmall => "vda-small",
        _ => "dad-base",
    };

    public string FilePath => Find(Stem + ".onnx");

    /// <summary>A temporal model's graph for the first frame (no state before it): {family}-first_{H}x{W}.onnx.</summary>
    public string FirstStem => Family + $"-first_{Height}x{Width}";

    public string FirstFilePath => Find(FirstStem + ".onnx");

    public bool Exists => File.Exists(FilePath) && (!Temporal || File.Exists(FirstFilePath));

    public static string Name(DepthTier tier) => tier switch
    {
        DepthTier.Small => "Depth Anything V2 Small",
        DepthTier.DA3Small => "Depth Anything 3 Small",
        DepthTier.DA3Base => "Depth Anything 3 Base",
        DepthTier.VdaSmall => "Video Depth Anything Small",
        _ => "Distill-Any-Depth Base",
    };

    public override string ToString() => Name(Tier) + $" {Width}×{Height}";
}
