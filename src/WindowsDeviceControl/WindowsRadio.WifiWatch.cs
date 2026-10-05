using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using static WindowsDeviceControl.Win32Error;

namespace WindowsDeviceControl;

public static unsafe partial class WindowsRadio
{
    private const uint WlanNotificationSourceNone = 0;
    private const uint WlanNotificationSourceAcm = 0x00000008;
    private const uint AcmScanComplete = 7;
    private const uint AcmConnectionComplete = 10;
    private const uint AcmConnectionAttemptFail = 11;
    private const uint AcmDisconnected = 21;
    private const uint AcmScanListRefresh = 26;

    /// <summary>Starts reporting Wi-Fi scan and connection changes.</summary>
    /// <param name="onEvent">
    ///     Called on a WLAN service thread, never the caller's, and one call at a time for this
    ///     registration. Post to your own thread before touching UI state, and keep the callback
    ///     short: it runs inside the WLAN notification path. Exceptions it throws are swallowed,
    ///     because a native callback cannot propagate them.
    /// </param>
    /// <returns>
    ///     The registration. Disposing it is the stop: it unregisters, waits for a callback that is
    ///     already running to return, then closes the WLAN handle. Disposing it again does nothing.
    /// </returns>
    /// <exception cref="Win32Exception">
    ///     The WLAN handle could not be opened, or Windows refused to
    ///     register for notifications.
    /// </exception>
    /// <remarks>
    ///     Every call creates an independent registration on a WLAN handle of its own, so several
    ///     can run side by side and disposing one leaves the others reporting. Never dispose a
    ///     registration from inside its own callback: Windows waits for the running callback before
    ///     the unregister returns, so that call never completes. Post the disposal to another thread
    ///     instead. Dispose every registration before the process exits, because native code holds
    ///     the callback until then. A registration ends silently when the WLAN service restarts;
    ///     start a new one when you want the feed back.
    /// </remarks>
    public static IDisposable StartWifiWatch(Action<WifiWatchEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var watch = new WifiWatch(onEvent);
        var registration = WlanNotificationRegistration.TryOpen(watch.OnNotification, out var status)
                           ?? throw WlanFailure("WlanOpenHandle", status);
        status = registration.Register(WlanNotificationSourceAcm);
        if (status != ErrorSuccess)
        {
            registration.Dispose();
            throw WlanFailure("WlanRegisterNotification", status);
        }

        watch.Registration = registration;
        return watch;
    }

    /// <summary>
    ///     Whether an ACM completion names the profile being joined. A completion without a profile
    ///     name could be any connection on the adapter, an unrelated auto-connect included, so it
    ///     decides nothing; the wait then ends at its timeout and the connected network is checked.
    /// </summary>
    internal static bool IsCompletionForProfile(string profile, string expected)
    {
        return profile.Length > 0 && string.Equals(profile, expected, StringComparison.Ordinal);
    }

    /// <summary>The change an ACM notification code reports, or null for one that changes nothing.</summary>
    private static WifiWatchEvent? MapWatchEvent(uint code)
    {
        return code switch
        {
            AcmScanComplete or AcmScanListRefresh => WifiWatchEvent.ScanCompleted,
            AcmConnectionComplete or AcmConnectionAttemptFail or AcmDisconnected =>
                WifiWatchEvent.ConnectionChanged,
            _ => null
        };
    }

    /// <summary>One <see cref="StartWifiWatch" /> registration and its delivery gate.</summary>
    private sealed class WifiWatch(Action<WifiWatchEvent> events) : IDisposable
    {
        private readonly object _gate = new();
        private int _disposed;

        internal WlanNotificationRegistration? Registration { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Unregistering waits for a callback that is already running, and that callback holds
            // _gate, so the registration is released outside it.
            Registration?.Dispose();
        }

        internal void OnNotification(nint data, nint context)
        {
            try
            {
                // WLAN occasionally delivers a null notification pointer.
                if (data == 0 || Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                var notification = *(WlanNotificationData*)data;
                if (notification.Source != WlanNotificationSourceAcm
                    || MapWatchEvent(notification.Code) is not { } change)
                {
                    return;
                }

                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        events(change);
                    }
                }
            }
            catch
            {
                // A native service callback cannot propagate managed failures.
            }
        }
    }

    private sealed class ConnectionVerdict : IDisposable
    {
        private readonly Guid _adapter;
        private readonly string _profile;
        private readonly ManualResetEventSlim _ready = new(false);
        private ConnectionOutcome? _outcome;
        private WlanNotificationRegistration? _registration;

        private ConnectionVerdict(Guid adapter, string profile)
        {
            _adapter = adapter;
            _profile = profile;
        }

        public void Dispose()
        {
            _registration?.Dispose();
            _ready.Dispose();
        }

        public static ConnectionVerdict? TryStart(
            Guid adapter,
            string profile,
            out uint status)
        {
            var verdict = new ConnectionVerdict(adapter, profile);
            verdict._registration = WlanNotificationRegistration.TryOpen(verdict.OnNotification, out status);
            if (verdict._registration is not null)
            {
                status = verdict._registration.Register(WlanNotificationSourceAcm);
                if (status == ErrorSuccess)
                {
                    return verdict;
                }
            }

            verdict.Dispose();
            return null;
        }

        internal ConnectionOutcome? Wait(TimeSpan timeout)
        {
            return _ready.Wait(timeout) ? _outcome : null;
        }

        private void OnNotification(nint data, nint context)
        {
            try
            {
                // WLAN occasionally delivers a null notification pointer.
                if (data == 0)
                {
                    return;
                }

                var notification = *(WlanNotificationData*)data;
                if (notification.Source != WlanNotificationSourceAcm
                    || notification.InterfaceId != _adapter
                    || notification.Code is not AcmConnectionComplete and not AcmConnectionAttemptFail)
                {
                    return;
                }

                var reason = 0u;
                var profile = string.Empty;
                if (notification.Data != 0
                    && notification.DataSize >= (uint)sizeof(WlanConnectionNotificationData))
                {
                    var payload = (WlanConnectionNotificationData*)notification.Data;
                    reason = payload->ReasonCode;
                    profile = NativeText.ReadFixed(payload->ProfileName, 256);
                }

                if (!IsCompletionForProfile(profile, _profile))
                {
                    return;
                }

                _outcome = new ConnectionOutcome(
                    notification.Code == AcmConnectionComplete && reason == 0,
                    reason);
                _ready.Set();
            }
            catch
            {
                // Native callback failures cannot cross WLANAPI.
            }
        }
    }

    /// <summary>A WLAN notification callback registered on a client handle of its own.</summary>
    /// <remarks>
    ///     Native code holds only the delegate's function pointer, so the registration keeps
    ///     the delegate alive until disposal has unregistered it and closed the handle. Disposal
    ///     happens once, however often it is called.
    /// </remarks>
    private sealed class WlanNotificationRegistration : IDisposable
    {
        private readonly WlanNotificationCallback _callback;
        private readonly WlanClient _client;
        private readonly nint _pointer;
        private int _disposed;
        private bool _registered;

        private WlanNotificationRegistration(
            WlanClient client,
            WlanNotificationCallback callback,
            nint pointer)
        {
            _client = client;
            _callback = callback;
            _pointer = pointer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Unregister before closing: the unregister waits for a running callback, so the
            // delegate stays rooted and the handle open until no native call can reach either.
            if (_registered)
            {
                WlanRegisterNotification(_client.Handle, WlanNotificationSourceNone, 0, 0, 0, 0, 0);
            }

            _client.Dispose();
            GC.KeepAlive(_callback);
        }

        /// <summary>Opens a client handle for the callback, or returns null with the open status.</summary>
        internal static WlanNotificationRegistration? TryOpen(
            WlanNotificationCallback callback,
            out uint status)
        {
            var client = WlanClient.TryOpen(out status);
            if (client is null)
            {
                return null;
            }

            try
            {
                return new WlanNotificationRegistration(
                    client,
                    callback,
                    Marshal.GetFunctionPointerForDelegate(callback));
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>Registers the callback for the given notification sources.</summary>
        internal uint Register(uint sources)
        {
            var status = WlanRegisterNotification(_client.Handle, sources, 1, _pointer, 0, 0, 0);
            _registered |= status == ErrorSuccess;
            return status;
        }
    }

    private readonly record struct ConnectionOutcome(bool Succeeded, uint Reason);
}
