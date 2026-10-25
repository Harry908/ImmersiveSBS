namespace ImmersiveSBS.Depth;

/// <summary>
/// The ImageNet normalisation every model here was trained with: x = (rgb − mean) / std, rgb in
/// [0, 1]. The GPU preprocess (StereoSynth's Preprocess shader) writes the same numbers.
/// </summary>
public static class ImageNet
{
    public const float MeanR = 0.485f, MeanG = 0.456f, MeanB = 0.406f;
    public const float StdR = 0.229f, StdG = 0.224f, StdB = 0.225f;
}
