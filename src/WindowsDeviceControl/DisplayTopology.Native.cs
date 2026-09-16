using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static partial class DisplayTopology
{
    internal const uint OnlyActivePaths = 0x2;
    internal const uint AllPaths = 0x1;
    internal const uint SdcValidate = 0x40;
    internal const uint SdcApply = 0x80;
    internal const uint SaveToDatabase = 0x200;
    private const uint UseSupplied = 0x20;
    private const uint AllowChanges = 0x400;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount,
        [In] [Out] PathInfo[] paths, ref uint modeCount, [In] [Out] ModeInfo[] modes, nint topologyId);

    [LibraryImport("user32.dll")]
    private static partial int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
        uint modeCount, [In] ModeInfo[] modes, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref TargetDeviceName packet);

    // The native CCD shapes are internal rather than private so the layout editor beside this class
    // can build a supplied configuration from the same declarations. One decoded layout, one set of
    // offsets: a second copy would be a second thing to get wrong.
    [StructLayout(LayoutKind.Sequential)]
    internal record struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    internal readonly record struct RouteKey(Luid Adapter, uint Id);

    internal sealed record NativeSnapshot(PathInfo[] Paths, ModeInfo[] Modes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>Source half of a mode record: the desktop rectangle this display shows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Region2D
    {
        public uint Cx;
        public uint Cy;
    }

    /// <summary>Target half of a mode record: the signal the adapter drives.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq;
        public Rational VSyncFreq;
        public Region2D ActiveSize;
        public Region2D TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct ModeUnion
    {
        [FieldOffset(0)] public VideoSignalInfo Target;
        [FieldOffset(0)] public SourceMode Source;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ModeUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }
}
