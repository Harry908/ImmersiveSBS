// Ported from DesktopSbS: DesktopSbS/Model/KnownGlasses.cs
using System.Globalization;

namespace ImmersiveSBS.Displays;

/// <summary>A display-glasses model with its published optics.</summary>
public sealed record GlassesModel(string Id, string Brand, string Name, double FieldOfView,
    int EyeWidth, int EyeHeight, params string[] EdidKeys)
{
    public string Label =>
        Brand + " " + Name + " (" + FieldOfView.ToString("0.#", CultureInfo.InvariantCulture) + "°)";

    public override string ToString() => Label;
}

/// <summary>
/// Display glasses on the market, researched 2026-09-29 (DesktopSbS math.md, 5.8). Pure data,
/// used here to recognise the glasses among the attached displays. Most makers do not publish
/// their EDID keys, so only some entries have one.
/// </summary>
public static class KnownGlasses
{
    public static readonly IReadOnlyList<GlassesModel> All =
    [
        new("lenovo-legion-1", "Lenovo", "Legion Glasses (Gen 1)", 38, 1920, 1080),
        new("viture-one", "VITURE", "One", 43, 1920, 1080),
        new("lenovo-legion-2", "Lenovo", "Legion Glasses Gen 2", 43.5, 1920, 1080),
        new("viture-pro", "VITURE", "Pro", 46, 1920, 1080),
        new("xreal-air", "XREAL", "Air / Air 2 / Air 2 Pro", 46, 1920, 1080),
        new("rayneo-air-2s", "RayNeo", "Air 2s", 46, 1920, 1080),
        new("rayneo-air-3s", "RayNeo", "Air 3s / Air 3s Pro", 47, 1920, 1080),
        new("rayneo-air-4-pro", "RayNeo", "Air 4 Pro", 47, 1920, 1080),
        new("tcl-nxtwear-s", "TCL", "NxtWear S", 47, 1920, 1080),
        new("tcl-nxtwear-s-plus", "TCL", "NxtWear S+", 49, 1920, 1080),
        new("xreal-one", "XREAL", "One", 50, 1920, 1080),
        new("rokid-max", "Rokid", "Max / Max 2 / AR Lite", 50, 1920, 1080),
        new("viture-luma", "VITURE", "Luma", 50, 1920, 1080),
        new("xreal-air-2-ultra", "XREAL", "Air 2 Ultra", 52, 1920, 1080),
        new("xreal-1s", "XREAL", "1S", 52, 1920, 1200),
        new("viture-luma-pro", "VITURE", "Luma Pro", 52, 1920, 1080),
        new("viture-luma-ultra", "VITURE", "Luma Ultra", 52, 1920, 1200),
        // MRG-4100: linux-hardware.org EDID entry named "XREAL One Pro".
        new("xreal-one-pro", "XREAL", "One Pro", 57, 1920, 1080, "MRG-4100"),
        new("asus-rog-xreal-r1", "ASUS", "ROG XREAL R1", 57, 1920, 1080),
        new("viture-beast", "VITURE", "The Beast", 58, 1920, 1080),
        // TCL-03D4: captured from a GT Max on 2026-09-29, identical in 2D and 3D.
        new("rayneo-gt-max", "RayNeo", "GT Max", 59, 1920, 1080, "TCL-03D4"),
    ];

    /// <summary>EDID key of the VirtualDrivers Virtual Display Driver monitor ("VDD by MTT").</summary>
    public const string VirtualMonitorEdidKey = "MTT-1337";

    public static GlassesModel? FindByEdidKey(string? edidKey) =>
        string.IsNullOrEmpty(edidKey)
            ? null
            : All.FirstOrDefault(m => m.EdidKeys.Any(k => string.Equals(k, edidKey, StringComparison.OrdinalIgnoreCase)));
}
