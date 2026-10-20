using System.IO;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>Where model files are found: shipped next to the app first, then the user's folder.</summary>
public class DepthModelTests
{
    [Fact]
    public void AModelShippedNextToTheAppIsUsedBeforeTheUsersCopy()
    {
        // A size no test loads for real (Video Depth Anything has no Fast export).
        var model = new DepthModel(DepthTier.VdaSmall, DepthPreset.Fast);
        string shipped = Path.Combine(DepthModel.AppModelsDirectory, model.Stem + ".onnx");
        Assert.Equal(Path.Combine(DepthModel.UserModelsDirectory, model.Stem + ".onnx"), model.FilePath);

        Directory.CreateDirectory(DepthModel.AppModelsDirectory);
        try
        {
            File.WriteAllBytes(shipped, [0]);
            Assert.Equal(shipped, model.FilePath);
            // A temporal model needs its first-frame graph too.
            Assert.False(model.Exists);
        }
        finally
        {
            File.Delete(shipped);
        }
        Assert.Equal(Path.Combine(DepthModel.UserModelsDirectory, model.Stem + ".onnx"), model.FilePath);
    }
}
