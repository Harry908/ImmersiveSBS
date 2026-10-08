// Ported from DesktopSbS: DesktopSbS/Interop/DisplayConfig.cs
using System;
using System.Runtime.InteropServices;

namespace ImmersiveSBS.Interop
{
    /// <summary>
    /// Interop for the Windows Connecting and Configuring Displays (CCD) API.
    ///
    /// This is the only API that exposes a display identity surviving a mode
    /// change: the monitor device path and the EDID manufacturer/product ids.
    /// System.Windows.Forms.Screen exposes neither, and its ordering
    /// is not stable across hot-plug, which is why the app cannot key its
    /// settings on a screen index.
    ///
    /// Nothing here changes the display configuration. SetDisplayConfig is
    /// deliberately not imported.
    /// </summary>
    public static class DisplayConfig
    {
        private const string dll = "user32.dll";

        #region Query flags

        public const uint QDC_ALL_PATHS = 0x00000001;
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        public const uint QDC_DATABASE_CURRENT = 0x00000004;

        /// <summary>
        /// Head-mounted displays are hidden from a normal query. Targets that show
        /// up only when this flag is set are HMDs held by a VR runtime rather than
        /// ordinary desktop monitors.
        /// </summary>
        public const uint QDC_INCLUDE_HMD = 0x00000020;

        public const int ERROR_SUCCESS = 0;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        #endregion

        #region Enums

        public enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
        {
            GET_SOURCE_NAME = 1,
            GET_TARGET_NAME = 2,
            GET_TARGET_PREFERRED_MODE = 3,
            GET_ADAPTER_NAME = 4
        }

        public enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
        {
            SOURCE = 1,
            TARGET = 2,
            DESKTOP_IMAGE = 3
        }

        public enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
        {
            OTHER = 0xFFFFFFFF,
            HD15 = 0,
            SVIDEO = 1,
            COMPOSITE_VIDEO = 2,
            COMPONENT_VIDEO = 3,
            DVI = 4,
            HDMI = 5,
            LVDS = 6,
            D_JPN = 8,
            SDI = 9,
            DISPLAYPORT_EXTERNAL = 10,
            DISPLAYPORT_EMBEDDED = 11,
            UDI_EXTERNAL = 12,
            UDI_EMBEDDED = 13,
            SDTVDONGLE = 14,
            MIRACAST = 15,
            INDIRECT_WIRED = 16,
            INDIRECT_VIRTUAL = 17,
            DISPLAYPORT_USB_TUNNEL = 18,
            INTERNAL = 0x80000000
        }

        public enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
        {
            UNSPECIFIED = 0,
            PROGRESSIVE = 1,
            INTERLACED_UPPERFIELDFIRST = 2,
            INTERLACED_LOWERFIELDFIRST = 3
        }

        public enum DISPLAYCONFIG_ROTATION : uint
        {
            IDENTITY = 1,
            ROTATE90 = 2,
            ROTATE180 = 3,
            ROTATE270 = 4
        }

        public enum DISPLAYCONFIG_SCALING : uint
        {
            IDENTITY = 1,
            CENTERED = 2,
            STRETCHED = 3,
            ASPECTRATIOCENTEREDMAX = 4,
            CUSTOM = 5,
            PREFERRED = 128
        }

        public enum DISPLAYCONFIG_PIXELFORMAT : uint
        {
            PIXELFORMAT_8BPP = 1,
            PIXELFORMAT_16BPP = 2,
            PIXELFORMAT_24BPP = 3,
            PIXELFORMAT_32BPP = 4,
            PIXELFORMAT_NONGDI = 5
        }

        #endregion

        #region Structs

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;

            public override string ToString()
            {
                return string.Format("{0:X8}-{1:X8}", HighPart, LowPart);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;

            public double ToHz()
            {
                return Denominator == 0 ? 0 : (double)Numerator / Denominator;
            }

            public override string ToString()
            {
                return Denominator == 0 ? "n/a" : ToHz().ToString("0.###") + " Hz";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECTL
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
            public DISPLAYCONFIG_ROTATION rotation;
            public DISPLAYCONFIG_SCALING scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public DISPLAYCONFIG_PIXELFORMAT pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECTL DesktopImageRegion;
            public RECTL DesktopImageClip;
        }

        /// <summary>
        /// The three mode variants overlay each other in the native union, at
        /// offset 16 (after infoType, id and adapterId).
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)]
            public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
            [FieldOffset(4)]
            public uint id;
            [FieldOffset(8)]
            public LUID adapterId;
            [FieldOffset(16)]
            public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(16)]
            public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(16)]
            public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;

            /// <summary>Bit 2 of flags: the EDID ids above are meaningful.</summary>
            public bool EdidIdsValid
            {
                get { return (flags & 0x4) != 0; }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            /// <summary>
            /// The GDI device name, e.g. \\.\DISPLAY2. This is the join key between
            /// the CCD world and System.Windows.Forms.Screen.DeviceName.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        #endregion

        #region Functions

        [DllImport(dll)]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport(dll)]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport(dll)]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport(dll)]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        #endregion

        #region Helpers

        /// <summary>
        /// Decodes the packed 3-letter PnP vendor code from EDID bytes 8-9.
        /// The field is stored big-endian, so it is byte-swapped first.
        /// </summary>
        public static string DecodeEdidManufacturer(ushort edidManufactureId)
        {
            if (edidManufactureId == 0) return string.Empty;

            ushort id = (ushort)((edidManufactureId >> 8) | (edidManufactureId << 8));

            char c1 = (char)('A' + ((id >> 10) & 0x1F) - 1);
            char c2 = (char)('A' + ((id >> 5) & 0x1F) - 1);
            char c3 = (char)('A' + (id & 0x1F) - 1);

            if (!char.IsLetter(c1) || !char.IsLetter(c2) || !char.IsLetter(c3))
            {
                return string.Empty;
            }

            return new string(new[] { c1, c2, c3 });
        }

        #endregion
    }
}
