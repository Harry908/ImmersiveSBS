// Ported from DesktopSbS: DesktopSbS/Model/DisplayInfo.cs
using System.Drawing;
using System.Globalization;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Displays;

/// <summary>
/// How confidently a saved display identity was matched against a display that is currently
/// attached. Ordered from strongest to weakest.
/// </summary>
public enum DisplayMatchStrength
{
    None = 0,
    /// <summary>Resolution and position only. Last resort.</summary>
    Geometry = 1,
    /// <summary>Same GDI device name, e.g. \\.\DISPLAY2. Not stable across hot-plug.</summary>
    GdiDeviceName = 2,
    /// <summary>Same EDID vendor + product code. Stable per monitor model.</summary>
    Edid = 3,
    /// <summary>Same monitor device path. Stable per physical monitor.</summary>
    DevicePath = 4,
}

/// <summary>
/// The stable part of a display: what makes it *this* monitor rather than "whatever Windows
/// currently calls display 2".
/// </summary>
public sealed class DisplayIdentity
{
    /// <summary>
    /// Monitor device path, e.g. \\?\DISPLAY#TCL03D4#5&amp;1a2b3c4d&amp;0&amp;UID4353#{e6f07b5f-...}.
    /// The strongest key available, and the one settings are stored against.
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>Three-letter PnP vendor code decoded from EDID, e.g. "TCL".</summary>
    public string EdidManufacturer { get; set; } = string.Empty;

    public ushort EdidProductCode { get; set; }

    public bool EdidValid { get; set; }

    /// <summary>Monitor friendly name from EDID, e.g. "SmartGlasses".</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>GDI device name, e.g. \\.\DISPLAY2. Join key to DXGI outputs and Screen.</summary>
    public string GdiDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Where this display was the last time it was seen. Persisted purely as a last-resort
    /// matching hint; <see cref="MatchAgainst"/> only compares stable keys.
    /// </summary>
    public Rectangle LastKnownBounds { get; set; }

    /// <summary>
    /// "TCL-03D4": a readable secondary key. Empty when the EDID ids are not valid, so it never
    /// collides across monitors that report nothing.
    /// </summary>
    public string EdidKey =>
        !EdidValid || string.IsNullOrEmpty(EdidManufacturer)
            ? string.Empty
            : EdidManufacturer + "-" + EdidProductCode.ToString("X4");

    public override string ToString() =>
        string.IsNullOrEmpty(FriendlyName) ? (string.IsNullOrEmpty(GdiDeviceName) ? "(unknown display)" : GdiDeviceName) : FriendlyName;

    #region Persistence

    private const char FieldSeparator = '|';

    /// <summary>
    /// Serializes to a single settings string. Values are percent-escaped so a device path
    /// (which contains #, &amp;, backslashes and braces) round-trips intact.
    /// </summary>
    public string Serialize()
    {
        string[] fields =
        [
            "path=" + Escape(DevicePath),
            "vendor=" + Escape(EdidManufacturer),
            "product=" + EdidProductCode.ToString("X4"),
            "edidvalid=" + (EdidValid ? "1" : "0"),
            "gdi=" + Escape(GdiDeviceName),
            "name=" + Escape(FriendlyName),
            "bounds=" + LastKnownBounds.X + "," + LastKnownBounds.Y + "," +
                        LastKnownBounds.Width + "," + LastKnownBounds.Height,
        ];

        return string.Join(FieldSeparator, fields);
    }

    /// <summary>
    /// Parses what <see cref="Serialize"/> wrote. Returns null for empty or malformed input
    /// rather than throwing, so a corrupted setting degrades to "no saved display".
    /// </summary>
    public static DisplayIdentity? Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return null;

        var identity = new DisplayIdentity();
        bool anyField = false;

        foreach (string field in serialized.Split(FieldSeparator))
        {
            int eq = field.IndexOf('=');
            if (eq <= 0) continue;

            string key = field[..eq];
            string value = field[(eq + 1)..];
            anyField = true;

            switch (key)
            {
                case "path":
                    identity.DevicePath = Unescape(value);
                    break;
                case "vendor":
                    identity.EdidManufacturer = Unescape(value);
                    break;
                case "product":
                    if (ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort product))
                        identity.EdidProductCode = product;
                    break;
                case "edidvalid":
                    identity.EdidValid = value == "1";
                    break;
                case "gdi":
                    identity.GdiDeviceName = Unescape(value);
                    break;
                case "name":
                    identity.FriendlyName = Unescape(value);
                    break;
                case "bounds":
                    string[] parts = value.Split(',');
                    if (parts.Length == 4
                        && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
                        && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h))
                    {
                        identity.LastKnownBounds = new Rectangle(x, y, w, h);
                    }
                    break;
            }
        }

        return anyField ? identity : null;
    }

    private static string Escape(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("%", "%25").Replace("|", "%7C").Replace("=", "%3D");

    private static string Unescape(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("%3D", "=").Replace("%7C", "|").Replace("%25", "%");

    #endregion

    /// <summary>
    /// Scores this identity against a saved one. Callers pick the attached display with the
    /// highest strength, which survives a monitor coming back on another connector or index.
    /// </summary>
    public DisplayMatchStrength MatchAgainst(DisplayIdentity? saved)
    {
        if (saved == null) return DisplayMatchStrength.None;

        if (!string.IsNullOrEmpty(DevicePath)
            && string.Equals(DevicePath, saved.DevicePath, StringComparison.OrdinalIgnoreCase))
            return DisplayMatchStrength.DevicePath;

        string edidKey = EdidKey;
        if (!string.IsNullOrEmpty(edidKey)
            && string.Equals(edidKey, saved.EdidKey, StringComparison.OrdinalIgnoreCase))
            return DisplayMatchStrength.Edid;

        if (!string.IsNullOrEmpty(GdiDeviceName)
            && string.Equals(GdiDeviceName, saved.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
            return DisplayMatchStrength.GdiDeviceName;

        return DisplayMatchStrength.None;
    }
}

/// <summary>
/// The mode a display is currently running. Both the desktop size Windows lays windows out in
/// and the signal size sent down the cable are recorded, because they can differ.
/// </summary>
public sealed class DisplayMode
{
    /// <summary>Desktop width in pixels (the CCD source mode).</summary>
    public int Width { get; set; }

    /// <summary>Desktop height in pixels (the CCD source mode).</summary>
    public int Height { get; set; }

    /// <summary>Signal width from the CCD target mode active size, 0 if unknown.</summary>
    public int SignalWidth { get; set; }

    /// <summary>Signal height from the CCD target mode active size, 0 if unknown.</summary>
    public int SignalHeight { get; set; }

    public double RefreshRate { get; set; }

    public bool Interlaced { get; set; }

    public StereoFrameShape FrameShape => GlassesStateEvaluator.ClassifyFrame(Width, Height);

    public override string ToString()
    {
        string s = Width + "×" + Height;
        if (RefreshRate > 0) s += " @ " + RefreshRate.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
        if (Interlaced) s += " (interlaced)";
        return s;
    }
}

/// <summary>
/// One attached display: its stable identity, its current mode, and where it sits in the
/// virtual desktop.
/// </summary>
public sealed class DisplayInfo
{
    public DisplayIdentity Identity { get; set; } = new();

    public DisplayMode Mode { get; set; } = new();

    /// <summary>Monitor bounds in virtual-screen coordinates (physical pixels).</summary>
    public Rectangle Bounds { get; set; }

    /// <summary>Working area, i.e. bounds minus taskbar and appbars.</summary>
    public Rectangle WorkingArea { get; set; }

    public bool IsPrimary { get; set; }

    public uint DpiX { get; set; }

    /// <summary>
    /// True when this target is only visible to QueryDisplayConfig with QDC_INCLUDE_HMD, i.e.
    /// Windows treats it as a head-mounted display rather than as a desktop monitor.
    /// </summary>
    public bool IsHeadMounted { get; set; }

    /// <summary>
    /// The CCD targetAvailable flag. Windows can briefly report an active path whose target is
    /// not available yet during a hot-plug, so enumeration is worth retrying when false.
    /// </summary>
    public bool TargetAvailable { get; set; }

    public string OutputTechnology { get; set; } = string.Empty;

    public string AdapterId { get; set; } = string.Empty;

    public uint TargetId { get; set; }

    public uint SourceId { get; set; }

    /// <summary>False when built from the Screen fallback because the CCD query failed.</summary>
    public bool FromDisplayConfig { get; set; }

    /// <summary>True when the display is part of the desktop and can host windows.</summary>
    public bool HasDesktopBounds => Bounds.Width > 0 && Bounds.Height > 0;

    /// <summary>The virtual display driver's monitor (output technology "indirect virtual").</summary>
    public bool IsIndirectVirtual => OutputTechnology == nameof(DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.INDIRECT_VIRTUAL);

    public override string ToString() => Identity + " " + Mode;
}
