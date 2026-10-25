using ImmersiveSBS.Depth;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Frames of a real video for tests that need real pictures: Big Buck Bunny (© Blender
/// Foundation, CC-BY 3.0), 10 s at 720p, in %LOCALAPPDATA%\ImmersiveSBS\samples (not in the
/// repo), decoded by Windows' own media stack. Without it, a drawn scene.
/// </summary>
public static class SampleClip
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "samples", "big-buck-bunny-720p-10s.mp4");

    public static bool Exists => System.IO.File.Exists(Path);

    /// <summary><paramref name="count"/> frames from 1 s on, every <paramref name="step"/>-th at 30 fps, scaled to w×h, as BGRA.</summary>
    public static async Task<List<byte[]>> Frames(int count, int w, int h, int step = 1)
    {
        if (!Exists) return Drawn(count, w, h);
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path);
        MediaClip clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var frames = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            // One at a time: asked for a list 1/30 s apart, GetThumbnailsAsync gave one picture for all.
            using ImageStream stream = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(1 + i * step / 30.0), w, h,
                VideoFramePrecision.NearestFrame);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = (uint)w, ScaledHeight = (uint)h }, ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            frames.Add(pixels.DetachPixelData());
        }
        return frames;
    }

    /// <summary>A model's input: planar RGB with ImageNet's mean and deviation, as the app's input shader makes it.</summary>
    public static float[] ModelInput(byte[] bgra, int plane)
    {
        var input = new float[3 * plane];
        for (int i = 0; i < plane; i++)
        {
            input[i] = (bgra[4 * i + 2] / 255f - ImageNet.MeanR) / ImageNet.StdR;
            input[plane + i] = (bgra[4 * i + 1] / 255f - ImageNet.MeanG) / ImageNet.StdG;
            input[2 * plane + i] = (bgra[4 * i] / 255f - ImageNet.MeanB) / ImageNet.StdB;
        }
        return input;
    }

    /// <summary>The model's runner (as the depth service picks it), or null (with the reason) when it cannot run here.</summary>
    public static IDepthRunner? TryLoad(DepthModel model, out string why)
    {
        why = "";
        if (!model.Exists)
        {
            why = $"{model.FilePath} is missing";
            return null;
        }
        try
        {
            return model.Temporal ? VideoDepthEstimator.Load(model, _ => { }) : DepthEstimator.Load(model, _ => { });
        }
        catch (InvalidOperationException e)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>A drawn scene: a textured floor, a bright figure moving across it, and a line of outlined "letters".</summary>
    private static List<byte[]> Drawn(int count, int w, int h)
    {
        var frames = new List<byte[]>();
        for (int f = 0; f < count; f++)
        {
            var bgra = new byte[4 * w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = 0.4 + 0.2 * Math.Sin(x * 0.3 * (1 + y / (double)h)) * Math.Sin(y * 0.2);
                    double dx = x - (w * 0.2 + f * 4), dy = y - h * 0.5;
                    if (dx * dx / 900 + dy * dy / 3600 < 1) v = 0.85;
                    if (y > h * 0.85 && y < h * 0.9 && x > w / 3 && x < 2 * w / 3) v = x % 6 < 2 ? 0.95 : x % 6 == 2 ? 0.05 : v;
                    byte b = (byte)(255 * Math.Clamp(v, 0, 1));
                    int i = 4 * (y * w + x);
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = b;
                    bgra[i + 3] = 255;
                }
            frames.Add(bgra);
        }
        return frames;
    }
}
