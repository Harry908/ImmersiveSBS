using System.IO;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>Where model files are found: shipped next to the app first, then the user's folder.</summary>
public class DepthModelTests
{
    [Fact]
    public void AModelShippedNextToTheAppIsUsedBeforeTheUsersCopy()
    {
        // The shipped copy goes in the test's own output folder, never among the user's models.
        var model = new DepthModel(DepthTier.VdaSmall, DepthPreset.Balanced);
        string shipped = Path.Combine(DepthModel.AppModelsDirectory, model.Stem + ".onnx");
        Assert.Equal(Path.Combine(DepthModel.UserModelsDirectory, model.Stem + ".onnx"), model.FilePath);

        Directory.CreateDirectory(DepthModel.AppModelsDirectory);
        try
        {
            File.WriteAllBytes(shipped, [0]);
            Assert.Equal(shipped, model.FilePath);
            // A temporal model needs its first-frame graph too.
            Assert.Equal(File.Exists(model.FirstFilePath), model.Exists);
        }
        finally
        {
            File.Delete(shipped);
        }
        Assert.Equal(Path.Combine(DepthModel.UserModelsDirectory, model.Stem + ".onnx"), model.FilePath);
    }
}
