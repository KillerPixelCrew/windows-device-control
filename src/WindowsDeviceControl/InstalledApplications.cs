using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

/// <summary>One entry of the set Windows shows in Start.</summary>
/// <param name="DisplayName">The name Windows shows.</param>
/// <param name="ParsingName">
///     The entry's identity inside the Applications folder: an application user model id for a packaged app, a
///     known-folder relative path or an application-chosen id for a Start Menu shortcut. It names the entry for
///     <see cref="InstalledApplications.ReadIconsAsync" />.
/// </param>
/// <param name="AppUserModelId">The packaged application's user model id, or null for a desktop application.</param>
/// <param name="TargetPath">The file a desktop entry starts, or null when Windows does not expose one.</param>
/// <param name="Arguments">The arguments of a desktop entry's shortcut, or an empty string.</param>
public sealed record InstalledApplication(
    string DisplayName,
    string ParsingName,
    string? AppUserModelId,
    string? TargetPath,
    string Arguments);

/// <summary>An application's icon as raw pixels.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="PremultipliedBgra">Top-down rows of premultiplied BGRA, four bytes per pixel.</param>
public sealed record InstalledApplicationIcon(int Width, int Height, byte[] PremultipliedBgra);

/// <summary>
///     Reads the Applications folder (<c>shell:AppsFolder</c>), the one list Windows merges from the all-users and
///     per-user Start Menus and the installed packages.
/// </summary>
/// <remarks>
///     Shell namespace objects are apartment-bound, so every read runs on a short-lived STA thread of its own and
///     never on the caller's. An entry that cannot be read is skipped, not fatal to the listing.
/// </remarks>
public static partial class InstalledApplications
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint SigdnNormalDisplay = 0;
    private const uint SigdnParentRelativeParsing = 0x80018001;
    private const int SiigbfIconOnly = 0x4;
    private const int DibRgbColors = 0;

    private static readonly Guid AppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
    private static readonly Guid EnumItemsHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid ShellItemId = typeof(IShellItem).GUID;
    private static readonly Guid EnumShellItemsId = typeof(IEnumShellItems).GUID;
    private static readonly Guid ImageFactoryId = typeof(IShellItemImageFactory).GUID;

    private static readonly PropertyKey LinkTargetParsingPath =
        new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    private static readonly PropertyKey LinkArguments = new(new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), 100);

    /// <summary>Lists the Applications folder.</summary>
    /// <param name="cancellationToken">Cancels the listing between entries.</param>
    /// <returns>Every readable entry, in the folder's order.</returns>
    /// <exception cref="OperationCanceledException">The listing was cancelled.</exception>
    /// <exception cref="COMException">The folder could not be opened or enumerated at all.</exception>
    public static Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken cancellationToken = default)
    {
        return RunOnStaAsync<IReadOnlyList<InstalledApplication>>(() => List(cancellationToken));
    }

    /// <summary>Reads icons of Applications folder entries.</summary>
    /// <param name="parsingNames">The entries' <see cref="InstalledApplication.ParsingName" /> values.</param>
    /// <param name="size">The square icon size in pixels.</param>
    /// <param name="cancellationToken">Cancels the read between entries.</param>
    /// <returns>Each name's icon, or null where Windows has none. Every requested name is present.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<IReadOnlyDictionary<string, InstalledApplicationIcon?>> ReadIconsAsync(
        IReadOnlyList<string> parsingNames, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsingNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return RunOnStaAsync<IReadOnlyDictionary<string, InstalledApplicationIcon?>>(() =>
        {
            Dictionary<string, InstalledApplicationIcon?> icons = new(StringComparer.Ordinal);
            foreach (var name in parsingNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                icons[name] = ReadIcon(name, size);
            }

            return icons;
        });
    }

    /// <summary>Whether a package family is installed for the current user.</summary>
    /// <param name="familyName">The package family name.</param>
    /// <returns>True when at least one package of the family is registered.</returns>
    public static bool IsPackageFamilyInstalled(string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        uint count = 0;
        uint length = 0;
        return GetPackagesByPackageFamily(familyName, ref count, 0, ref length, 0) == ErrorInsufficientBuffer
               && count > 0;
    }

    private static List<InstalledApplication> List(CancellationToken cancellationToken)
    {
        var folderId = AppsFolder;
        var itemId = ShellItemId;
        Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(ref folderId, 0, 0, ref itemId, out var folderPointer));
        var folder = TakeObject<IShellItem>(folderPointer);
        try
        {
            var handler = EnumItemsHandler;
            var enumId = EnumShellItemsId;
            Marshal.ThrowExceptionForHR(folder.BindToHandler(0, ref handler, ref enumId, out var enumPointer));
            var items = TakeObject<IEnumShellItems>(enumPointer);
            try
            {
                List<InstalledApplication> found = [];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (items.Next(1, out var item, out var fetched) != 0 || fetched == 0 || item is null)
                    {
                        return found;
                    }

                    try
                    {
                        if (Describe(item) is { } application)
                        {
                            found.Add(application);
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(folder);
        }
    }

    private static InstalledApplication? Describe(IShellItem item)
    {
        if (item.GetDisplayName(SigdnNormalDisplay, out var name) != 0
            || item.GetDisplayName(SigdnParentRelativeParsing, out var parsing) != 0
            || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing))
        {
            return null;
        }

        if (IsPackagedId(parsing))
        {
            return new InstalledApplication(name, parsing, parsing, null, "");
        }

        var target = ReadString(item, LinkTargetParsingPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            target = ExpandKnownFolder(parsing);
        }

        return new InstalledApplication(name, parsing, null, target, ReadString(item, LinkArguments) ?? "");
    }

    /// <summary>Whether an Applications folder id has the packaged form, <c>family_publisher!application</c>.</summary>
    /// <param name="id">The entry's id.</param>
    /// <returns>True for a packaged application's user model id.</returns>
    internal static bool IsPackagedId(string id)
    {
        var separator = id.IndexOf('!');
        if (separator <= 0 || separator == id.Length - 1 || id.IndexOf('!', separator + 1) >= 0)
        {
            return false;
        }

        // A package family name ends in an underscore and the 13-character publisher hash.
        var underscore = id.LastIndexOf('_', separator);
        return underscore > 0 && separator - underscore - 1 == 13;
    }

    private static string? ReadString(IShellItem item, PropertyKey key)
    {
        return item is IShellItem2 detailed && detailed.GetString(ref key, out var value) == 0 ? value : null;
    }

    /// <summary>Expands a parsing name such as <c>{known-folder-guid}\Tool\tool.exe</c> to its file.</summary>
    private static string? ExpandKnownFolder(string parsing)
    {
        var end = parsing.IndexOf('}');
        if (!parsing.StartsWith('{') || end < 0 || end + 2 >= parsing.Length
            || !Guid.TryParse(parsing.AsSpan(0, end + 1), out var folder)
            || SHGetKnownFolderPath(ref folder, 0, 0, out var pointer) != 0)
        {
            return null;
        }

        try
        {
            return Path.Combine(Marshal.PtrToStringUni(pointer) ?? "", parsing[(end + 2)..]);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static InstalledApplicationIcon? ReadIcon(string parsingName, int size)
    {
        var folderId = AppsFolder;
        var factoryId = ImageFactoryId;
        if (SHCreateItemInKnownFolder(ref folderId, 0, parsingName, ref factoryId, out var pointer) != 0)
        {
            return null;
        }

        var factory = TakeObject<IShellItemImageFactory>(pointer);
        try
        {
            if (factory.GetImage(new NativeSize(size, size), SiigbfIconOnly, out var bitmap) != 0 || bitmap == 0)
            {
                return null;
            }

            try
            {
                return ReadPixels(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }
    }

    private static unsafe InstalledApplicationIcon? ReadPixels(nint bitmap)
    {
        NativeBitmap description = default;
        if (GetObjectW(bitmap, sizeof(NativeBitmap), &description) == 0 || description.Width <= 0
            || description.Height == 0)
        {
            return null;
        }

        var width = description.Width;
        var height = Math.Abs(description.Height);
        var pixels = new byte[width * height * 4];
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // Top-down, so rows read in display order.
            Planes = 1,
            BitCount = 32
        };
        var context = CreateCompatibleDC(0);
        if (context == 0)
        {
            return null;
        }

        try
        {
            fixed (byte* destination = pixels)
            {
                return GetDIBits(context, bitmap, 0, (uint)height, destination, &header, DibRgbColors) == height
                    ? new InstalledApplicationIcon(width, height, pixels)
                    : null;
            }
        }
        finally
        {
            DeleteDC(context);
        }
    }

    private static T TakeObject<T>(nint pointer) where T : class
    {
        try
        {
            return (T)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> work)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Applications folder reader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderItem(ref Guid folderId, uint flags, nint token, ref Guid interfaceId,
        out nint item);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemInKnownFolder(ref Guid folderId, uint flags, string item,
        ref Guid interfaceId, out nint result);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetPackagesByPackageFamily(string familyName, ref uint count, nint names,
        ref uint bufferLength, nint buffer);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint context);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint context);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint context, nint bitmap, uint start, uint lines, void* bits,
        BitmapInfoHeader* info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);

        [PreserveSig]
        int GetParent(out nint parent);

        [PreserveSig]
        int GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(nint other, uint hint, out int order);
    }

    [ComImport]
    [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig]
        int BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);

        [PreserveSig]
        int GetParent(out nint parent);

        [PreserveSig]
        int GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(nint other, uint hint, out int order);

        [PreserveSig]
        int GetPropertyStore(int flags, ref Guid interfaceId, out nint store);

        [PreserveSig]
        int GetPropertyStoreWithCreateObject(int flags, nint createObject, ref Guid interfaceId, out nint store);

        [PreserveSig]
        int GetPropertyStoreForKeys(nint keys, uint count, int flags, ref Guid interfaceId, out nint store);

        [PreserveSig]
        int GetPropertyDescriptionList(ref PropertyKey key, ref Guid interfaceId, out nint list);

        [PreserveSig]
        int Update(nint context);

        [PreserveSig]
        int GetProperty(ref PropertyKey key, nint value);

        [PreserveSig]
        int GetCLSID(ref PropertyKey key, out Guid value);

        [PreserveSig]
        int GetFileTime(ref PropertyKey key, out long value);

        [PreserveSig]
        int GetInt32(ref PropertyKey key, out int value);

        [PreserveSig]
        int GetString(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    }

    [ComImport]
    [Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint bitmap);
    }
}
