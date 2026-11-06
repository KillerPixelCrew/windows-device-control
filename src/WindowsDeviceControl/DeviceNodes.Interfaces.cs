using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static unsafe partial class DeviceNodes
{
    // CM_DRP_* registry properties (SPDRP value + 1).
    private const uint DrpDeviceDesc = 0x01;
    private const uint DrpFriendlyName = 0x0D;
    private const uint DrpCapabilities = 0x10;

    /// <summary><c>DEVPKEY_Device_InstanceId</c>.</summary>
    private static readonly DevPropKey DevicePropertyInstanceId = new()
    {
        Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        Pid = 256
    };

    /// <summary>Resolves a device-interface path to its present devnode.</summary>
    /// <param name="interfacePath">
    ///     A device-interface path, for example from <see cref="WindowsStorage.ListDiskInterfaces" />.
    /// </param>
    /// <param name="devInst">The devnode handle, or zero when it was not resolved.</param>
    /// <returns>True when a nonempty interface instance ID resolves to a current devnode; otherwise false.</returns>
    public static bool TryGetDevNode(string interfacePath, out uint devInst)
    {
        devInst = 0;
        var size = 1024u;
        var buffer = stackalloc char[512];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in DevicePropertyInstanceId, out _, buffer, ref size,
                0) != 0)
        {
            return false;
        }

        // size is the byte count cfgmgr32 wrote back (buffer holds 512 chars).
        var instanceId = NativeText.ReadFixed(buffer, (int)(Math.Min(size, 1024u) / 2));
        return instanceId.Length > 0 && Locate(out devInst, instanceId, 0) == 0;
    }

    /// <summary>Reads a devnode's device instance identifier, the stable identity a list row keys on.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <returns>Device instance ID bounded to the native buffer, or an empty string on query failure.</returns>
    public static string GetDeviceInstanceId(uint devInst)
    {
        // CM_Get_Device_IDW is capped at MAX_DEVICE_ID_LEN (200); an id that exactly fills the buffer
        // carries no terminator, so it is decoded bounded.
        var buffer = stackalloc char[200];
        return CM_Get_Device_IDW(devInst, buffer, 200, 0) == 0 ? NativeText.ReadFixed(buffer, 200) : "";
    }

    /// <summary>Reads a devnode's display name: the friendly name when set, else the device description.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <returns>Friendly name, device description, or an empty string when neither is available.</returns>
    public static string GetDeviceDisplayName(uint devInst)
    {
        var name = ReadRegistryString(devInst, DrpFriendlyName);
        return name.Length > 0 ? name : ReadRegistryString(devInst, DrpDeviceDesc);
    }

    /// <summary>Reads a devnode's <c>CM_DEVCAP_*</c> capability bits.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <returns>The bits, or zero when they could not be read.</returns>
    internal static uint ReadCapabilities(uint devInst)
    {
        var length = 4u;
        uint capabilities = 0;
        return CM_Get_DevNode_Registry_PropertyW(devInst, DrpCapabilities, out _, (byte*)&capabilities, ref length,
            0) == 0
            ? capabilities
            : 0;
    }

    /// <summary>Reads a devnode's parent.</summary>
    /// <param name="devInst">The devnode.</param>
    /// <param name="parent">The parent devnode.</param>
    /// <returns>True when Windows named a parent.</returns>
    internal static bool TryGetParent(uint devInst, out uint parent)
    {
        return CM_Get_Parent(out parent, devInst, 0) == 0;
    }

    private static string ReadRegistryString(uint devInst, uint property)
    {
        var length = 1024u;
        var buffer = stackalloc byte[1024];
        // REG_SZ data is not guaranteed NUL-terminated; decode at most the returned byte count.
        return CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0) != 0
            ? ""
            : NativeText.ReadFixed((char*)buffer, (int)(Math.Min(length, 1024u) / 2));
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_PropertyW(
        string deviceInterface, in DevPropKey propertyKey, out uint propertyType, char* propertyBuffer,
        ref uint propertyBufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regDataType, byte* buffer, ref uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial uint CM_Get_Device_IDW(uint devInst, char* buffer, uint bufferLength, uint flags);

    /// <summary>DEVPROPKEY, blittable: a property category GUID plus an id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }
}
