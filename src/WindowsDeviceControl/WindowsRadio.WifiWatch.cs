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
    private const uint WlanNotificationSourceMsm = 0x00000010;
    private const uint AcmScanComplete = 7;
    private const uint AcmConnectionComplete = 10;
    private const uint AcmConnectionAttemptFail = 11;
    private const uint AcmDisconnected = 21;
    private const uint AcmScanListRefresh = 26;
    private static readonly object WifiWatchLock = new();
    private static WifiWatch? _wifiWatch;

    /// <summary>Starts reporting Wi-Fi scan and connection changes.</summary>
    /// <param name="onEvent">
    ///     Called on a Windows service thread, not the caller's. Marshal to your
    ///     UI thread before touching UI state, and keep the callback short — it runs inside the WLAN
    ///     notification path. Exceptions it throws are swallowed, because a native callback cannot
    ///     propagate them.
    /// </param>
    /// <exception cref="Win32Exception">
    ///     The WLAN handle could not be opened, or Windows refused to
    ///     register for notifications.
    /// </exception>
    /// <remarks>
    ///     There is one feed per process: calling this again replaces the previous callback
    ///     rather than adding a second one. Pair it with <see cref="StopWifiWatch" />.
    /// </remarks>
    public static void StartWifiWatch(Action<WifiWatchEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        // Disposing the previous registration can block: unregistering waits for an in-flight
        // callback to return, and that callback takes WifiWatchLock as its first act. Detach it
        // under the lock, then dispose it after the lock is released so a notification delivered
        // on the WLAN service thread at this exact moment cannot deadlock against this thread.
        DetachWatch()?.Dispose();
        var watch = new WifiWatch(onEvent);
        var registration = WlanNotificationRegistration.TryOpen(
                               (data, context) => OnWifiNotification(watch, data, context),
                               out var status)
                           ?? throw WlanFailure("WlanOpenHandle", status);
        watch.Registration = registration;
        status = registration.Register(WlanNotificationSourceAcm | WlanNotificationSourceMsm);
        if (status != ErrorSuccess)
        {
            status = registration.Register(WlanNotificationSourceAcm);
        }

        if (status != ErrorSuccess)
        {
            registration.Dispose();
            throw WlanFailure("WlanRegisterNotification", status);
        }

        // Published only once fully registered, so no callback can be in flight for it before
        // this and nothing can race to dispose it out from under a not-yet-successful setup.
        lock (WifiWatchLock)
        {
            _wifiWatch = watch;
        }
    }

    /// <summary>Stops reporting Wi-Fi changes and releases the notification handle.</summary>
    /// <remarks>
    ///     Safe to call when no watch is running. Call it before the process exits: the
    ///     callback is held by native code, and leaving it registered risks a call into an unloaded
    ///     delegate.
    /// </remarks>
    public static void StopWifiWatch()
    {
        DetachWatch()?.Dispose();
    }

    /// <summary>
    ///     Clears the active watch and returns its registration, still live, for the caller
    ///     to dispose outside <see cref="WifiWatchLock" />.
    /// </summary>
    private static WlanNotificationRegistration? DetachWatch()
    {
        lock (WifiWatchLock)
        {
            var watch = _wifiWatch;
            _wifiWatch = null;
            return watch?.Registration;
        }
    }

    private static void OnWifiNotification(WifiWatch watch, nint data, nint context)
    {
        try
        {
            lock (WifiWatchLock)
            {
                if (!ReferenceEquals(_wifiWatch, watch) || data == 0)
                {
                    return;
                }

                var notification = Marshal.PtrToStructure<WlanNotificationData>(data);
                if (notification.Source != WlanNotificationSourceAcm)
                {
                    return;
                }

                WifiWatchEvent? change = notification.Code switch
                {
                    AcmScanComplete or AcmScanListRefresh => WifiWatchEvent.ScanCompleted,
                    AcmConnectionComplete or AcmConnectionAttemptFail or AcmDisconnected =>
                        WifiWatchEvent.ConnectionChanged,
                    _ => null
                };
                if (change is { } raised)
                {
                    watch.Events(raised);
                }
            }
        }
        catch
        {
            // A native service callback cannot propagate managed failures.
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
                // OnWifiNotification guards the same way. Without it, a null notification pointer
                // (WLAN delivers one occasionally) throws ArgumentNullException, which the catch
                // below swallows silently instead of the deliberate early return every other
                // rejection here takes.
                if (data == 0)
                {
                    return;
                }

                var notification = Marshal.PtrToStructure<WlanNotificationData>(data);
                if (notification.Source != WlanNotificationSourceAcm
                    || notification.InterfaceId != _adapter
                    || notification.Code is not AcmConnectionComplete and not AcmConnectionAttemptFail)
                {
                    return;
                }

                var reason = 0u;
                var profile = string.Empty;
                if (notification.Data != 0
                    && notification.DataSize >= (uint)Marshal.SizeOf<WlanConnectionNotificationData>())
                {
                    var payload = Marshal.PtrToStructure<WlanConnectionNotificationData>(
                        notification.Data);
                    reason = payload.ReasonCode;
                    profile = NativeText.ReadFixed(payload.ProfileName, 256);
                }

                if (profile.Length > 0 && !string.Equals(profile, _profile, StringComparison.Ordinal))
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
    ///     the delegate alive until disposal has unregistered it and closed the handle.
    /// </remarks>
    private sealed class WlanNotificationRegistration : IDisposable
    {
        private readonly WlanNotificationCallback _callback;
        private readonly WlanClient _client;
        private readonly nint _pointer;
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

    private sealed class WifiWatch(Action<WifiWatchEvent> events)
    {
        internal Action<WifiWatchEvent> Events { get; } = events;
        internal WlanNotificationRegistration Registration { get; set; } = null!;
    }

    private readonly record struct ConnectionOutcome(bool Succeeded, uint Reason);
}
