using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

public static unsafe partial class WindowsRadio
{
    private const int WlanConnectionModeProfile = 0;
    private const int Dot11BssTypeInfrastructure = 1;
    private const int WlanIntfOpcodeCurrentConnection = 7;

    /// <summary>Reads the records of a WLAN list: a count, an index, then fixed-size records.</summary>
    /// <param name="list">The list WLANAPI returned. The caller still frees it.</param>
    /// <param name="read">Decodes one record while the list is still allocated.</param>
    /// <remarks>Every record Windows counted is read; the list carries its own count.</remarks>
    /// <typeparam name="TRecord">Native fixed-layout record following the eight-byte list header.</typeparam>
    /// <typeparam name="TResult">Managed value produced for each record.</typeparam>
    /// <returns>Decoded records in native order; native memory remains owned by the caller.</returns>
    internal static TResult[] ReadWlanList<TRecord, TResult>(
        nint list,
        Func<TRecord, TResult> read)
        where TRecord : struct
    {
        var count = checked((int)(uint)Marshal.ReadInt32(list));
        var start = list + 8;
        var stride = Marshal.SizeOf<TRecord>();
        var results = new TResult[count];
        for (var index = 0; index < results.Length; index++)
        {
            results[index] = read(Marshal.PtrToStructure<TRecord>(start + checked(index * stride)));
        }

        return results;
    }

    internal static byte[] ReadSsidBytes(Dot11Ssid ssid)
    {
        var length = Math.Min((int)ssid.Length, 32);
        var bytes = new byte[length];
        var source = ssid.Value;
        Marshal.Copy((nint)source, bytes, 0, length);
        return bytes;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(
        uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanScan(
        nint clientHandle, in Guid interfaceId, nint ssid, nint ies, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetAvailableNetworkList(
        nint clientHandle, in Guid interfaceId, uint flags, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetProfileList(
        nint clientHandle, in Guid interfaceId, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanGetProfile(
        nint clientHandle,
        in Guid interfaceId,
        string profileName,
        nint reserved,
        out nint profileXml,
        ref uint flags,
        out uint access);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanSetProfile(
        nint clientHandle,
        in Guid interfaceId,
        uint flags,
        string profileXml,
        string? securityDescriptor,
        int overwrite,
        nint reserved,
        out uint reasonCode);

    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanDeleteProfile(
        nint clientHandle, in Guid interfaceId, string profileName, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanConnect(
        nint clientHandle,
        in Guid interfaceId,
        in WlanConnectionParameters parameters,
        nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanDisconnect(nint clientHandle, in Guid interfaceId, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(
        nint clientHandle,
        in Guid interfaceId,
        int opcode,
        nint reserved,
        out uint dataSize,
        out nint data,
        out int opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanRegisterNotification(
        nint clientHandle,
        uint notificationSource,
        int ignoreDuplicate,
        nint callback,
        nint callbackContext,
        nint reserved,
        nint previousSource);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanReasonCodeToString(
        uint reasonCode, uint bufferSize, char* buffer, nint reserved);

    /// <summary>One WLAN client handle, closed once however often it is disposed.</summary>
    private sealed class WlanClient : IDisposable
    {
        private int _closed;

        private WlanClient(nint handle)
        {
            Handle = handle;
        }

        internal nint Handle { get; }

        public void Dispose()
        {
            // A second close of the same raw value could close a handle another client now owns.
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                WlanCloseHandle(Handle, 0);
            }
        }

        public static WlanClient Open()
        {
            return TryOpen(out var status) ?? throw WlanFailure("WlanOpenHandle", status);
        }

        public static WlanClient? TryOpen(out uint status)
        {
            status = WlanOpenHandle(2, 0, out _, out var handle);
            return status == ErrorSuccess ? new WlanClient(handle) : null;
        }

        /// <summary>Every WLAN interface; empty on a machine without one.</summary>
        internal IReadOnlyList<WlanInterfaceInfo> Interfaces()
        {
            var status = WlanEnumInterfaces(Handle, 0, out var list);
            CheckWlan("WlanEnumInterfaces", status);
            if (list == 0)
            {
                return [];
            }

            try
            {
                return ReadWlanList(list, static (WlanInterfaceInfo adapter) => adapter);
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }

        /// <summary>Every WLAN interface, for an operation that needs at least one.</summary>
        /// <exception cref="InvalidOperationException">Windows reported no WLAN interface.</exception>
        internal IReadOnlyList<WlanInterfaceInfo> RequireInterfaces()
        {
            var adapters = Interfaces();
            return adapters.Count == 0
                ? throw new InvalidOperationException("Windows reported no WLAN interface.")
                : adapters;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WlanNotificationCallback(nint data, nint context);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        internal uint Length;
        internal fixed byte Value[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanInterfaceInfo
    {
        internal Guid Id;
        internal fixed char Description[256];
        internal int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanProfileInfo
    {
        internal fixed char Name[256];
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanAvailableNetwork
    {
        internal fixed char ProfileName[256];
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal uint BssidCount;
        internal int Connectable;
        internal uint NotConnectableReason;
        internal uint PhyTypeCount;
        internal fixed int PhyTypes[8];
        internal int MorePhyTypes;
        internal uint SignalQuality;
        internal int SecurityEnabled;
        internal int DefaultAuthAlgorithm;
        internal int DefaultCipherAlgorithm;
        internal uint Flags;

        // WLAN_AVAILABLE_NETWORK ends dwFlags, dwReserved. Leaving the reserved word out makes the
        // struct 624 bytes against the native 628, and ReadWlanList strides by Marshal.SizeOf, so
        // every record after the first would be decoded four bytes per index too early.
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal fixed byte Bssid[6];
        internal int PhyType;
        internal uint PhyIndex;
        internal uint SignalQuality;
        internal uint RxRate;
        internal uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanSecurityAttributes
    {
        internal int SecurityEnabled;
        internal int OneXEnabled;
        internal int AuthAlgorithm;
        internal int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        internal int State;
        internal int Mode;
        internal fixed char ProfileName[256];
        internal WlanAssociationAttributes Association;
        internal WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanConnectionParameters
    {
        internal int Mode;
        internal nint Profile;
        internal nint Ssid;
        internal nint DesiredBssidList;
        internal int BssType;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanNotificationData
    {
        internal uint Source;
        internal uint Code;
        internal Guid InterfaceId;
        internal uint DataSize;
        internal nint Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionNotificationData
    {
        internal int Mode;
        internal fixed char ProfileName[256];
        internal Dot11Ssid Ssid;
        internal int BssType;
        internal int SecurityEnabled;
        internal uint ReasonCode;
        internal uint Flags;
        internal char ProfileXml;
    }
}
