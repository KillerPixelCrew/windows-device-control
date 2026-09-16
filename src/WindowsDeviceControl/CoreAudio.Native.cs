using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

// The COM declarations, PROPVARIANT cleanup and the shared device enumerator live here and stay
// private to CoreAudio.
public static partial class CoreAudio
{
    private const int ClsctxAll = 23;
    private const uint StorageModeRead = 0;

    private static readonly object EnumeratorGate = new();
    private static IMMDeviceEnumerator? _enumerator;

    /// <summary>The process-wide device enumerator, created on first use.</summary>
    /// <remarks>
    ///     MMDeviceEnumerator is free-threaded. It is created on a multithreaded-apartment
    ///     thread, so a first call from a UI thread cannot tie it to that thread's message loop. A
    ///     failed creation is not remembered: the next call tries again, as a fresh enumerator per
    ///     call did.
    /// </remarks>
    private static IMMDeviceEnumerator Enumerator()
    {
        if (Volatile.Read(ref _enumerator) is { } existing)
        {
            return existing;
        }

        lock (EnumeratorGate)
        {
            return _enumerator ??= Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA
                ? CreateEnumerator()
                : Task.Run(CreateEnumerator).GetAwaiter().GetResult();
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        return (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
    }

    /// <summary>
    ///     Activates one interface on a device. An activation that succeeds without exposing
    ///     <typeparamref name="T" /> is released and leaves the instance null.
    /// </summary>
    private static int Activate<T>(IMMDevice device, Guid interfaceId, out T? instance)
        where T : class
    {
        var result = device.Activate(ref interfaceId, ClsctxAll, 0, out var activated);
        instance = result >= 0 ? activated as T : null;
        if (instance is null)
        {
            Release(activated);
        }

        return result;
    }

    /// <summary>
    ///     Reads one endpoint property, clearing the variant and releasing the store before
    ///     returning.
    /// </summary>
    private static string? ReadStringProperty(
        IMMDevice endpoint,
        PropertyKey key,
        Func<PropVariant, string?> read)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            return endpoint.OpenPropertyStore(StorageModeRead, out store) >= 0 && store is not null
                                                                               && store.GetValue(ref key, out value) >=
                                                                               0
                ? read(value)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            // RCWs are keyed by native IUnknown identity, and the process-wide cached enumerator
            // can hand the same identity to more than one caller (a volume key handler racing a
            // QAM poll, for instance). FinalReleaseComObject forces that RCW's reference count to
            // zero regardless of how many holders remain, severing it out from under whichever
            // caller runs second; ReleaseComObject only drops this caller's own claim.
            Marshal.ReleaseComObject(value);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    private enum DataFlow
    {
        Render,
        Capture
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    // PROPVARIANT is 24 bytes on x64: an 8-byte vt and reserved header plus a 16-byte union, whose
    // widest arms are DECIMAL and the counted arrays. The two declared fields alone make the managed
    // struct 16 bytes, and it is blittable, so GetValue writes 24 bytes into whatever 16-byte local
    // the caller allocated and overwrites the rest of that stack frame. The explicit size is the
    // whole fix; the trailing bytes are never read here.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private readonly struct PropVariant
    {
        [FieldOffset(0)] private readonly ushort _variantType;

        [FieldOffset(8)] private readonly nint _pointerValue;

        internal string? StringValue
            => _variantType == 31 && _pointerValue != 0
                ? Marshal.PtrToStringUni(_pointerValue)
                : null;

        internal Guid? GuidValue
            => _variantType == 72 && _pointerValue != 0
                ? Marshal.PtrToStructure<Guid>(_pointerValue)
                : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        internal Guid Set;
        internal uint Id;
        internal uint Flags;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClient;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            DataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            DataFlow dataFlow,
            AudioRole role,
            out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    // IID_IMMDeviceCollection from mmdeviceapi.h. A wrong IID here is invisible until runtime:
    // EnumAudioEndpoints succeeds natively, then the interop QI for the declared IID answers
    // E_NOINTERFACE and every endpoint enumeration throws InvalidCastException.
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IPropertyStore? properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        [PreserveSig]
        int GetDataFlow(out DataFlow dataFlow);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, nint eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, nint eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, nint eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(nint eventContext);

        [PreserveSig]
        int VolumeStepDown(nint eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }

    [ComImport]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);

        [PreserveSig]
        int GetConnector(uint index, out IConnector? connector);

        [PreserveSig]
        int GetSubunitCount(out uint count);

        [PreserveSig]
        int GetSubunit(uint index, out nint subunit);

        [PreserveSig]
        int GetPartById(uint id, out nint part);

        [PreserveSig]
        int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);

        [PreserveSig]
        int GetSignalPath(nint from, nint to, int rejectMixedPaths, out nint parts);
    }

    [ComImport]
    [Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig]
        int GetType(out int connectorType);

        [PreserveSig]
        int GetDataFlow(out int flow);

        [PreserveSig]
        int ConnectTo(IConnector other);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);

        [PreserveSig]
        int GetConnectedTo(out IConnector? connector);

        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? connectorId);

        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(
            ref KsProperty property,
            uint propertyLength,
            nint propertyData,
            uint dataLength,
            out uint bytesReturned);

        [PreserveSig]
        int KsMethod(nint method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned);

        [PreserveSig]
        int KsEvent(nint eventData, uint eventLength, nint eventDataOut, uint dataLength, out uint bytesReturned);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(nint deviceId, out nint format);

        [PreserveSig]
        int GetDeviceFormat(nint deviceId, int isDefault, out nint format);

        [PreserveSig]
        int ResetDeviceFormat(nint deviceId);

        [PreserveSig]
        int SetDeviceFormat(nint deviceId, nint endpointFormat, nint mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(nint deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(nint deviceId, nint period);

        [PreserveSig]
        int GetShareMode(nint deviceId, nint mode);

        [PreserveSig]
        int SetShareMode(nint deviceId, nint mode);

        [PreserveSig]
        int GetPropertyValue(nint deviceId, ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetPropertyValue(nint deviceId, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string endpointId,
            AudioRole role);

        [PreserveSig]
        int SetEndpointVisibility(nint deviceId, int visible);
    }
}
