using System;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

// Change notifications: the default endpoint's volume and mute, and the endpoint set itself.
// A consumer that polled these once a second paid a COM round trip per second for the whole
// session; Core Audio tells us instead.
public static partial class CoreAudio
{
    /// <summary>What an endpoint watch observed.</summary>
    public enum AudioEndpointChange
    {
        /// <summary>An endpoint appeared.</summary>
        Added,

        /// <summary>An endpoint was removed.</summary>
        Removed,

        /// <summary>An endpoint changed state, for example unplugged or disabled.</summary>
        StateChanged,

        /// <summary>The console default endpoint of a direction changed.</summary>
        DefaultChanged
    }

    /// <summary>One endpoint watch event.</summary>
    /// <param name="Change">What changed.</param>
    /// <param name="EndpointId">
    ///     The endpoint's identifier, or null when a direction lost its default endpoint.
    /// </param>
    /// <param name="Direction">The direction whose default changed; null for the other changes.</param>
    public readonly record struct AudioEndpointWatchEvent(
        AudioEndpointChange Change,
        string? EndpointId,
        AudioDirection? Direction);

    /// <summary>
    ///     Starts reporting master volume and mute changes of the default endpoint in one direction.
    /// </summary>
    /// <param name="direction">Playback or recording endpoint.</param>
    /// <param name="onChanged">
    ///     Called on a Core Audio thread, not the caller's, whenever the endpoint's master volume,
    ///     mute or channel levels change, including changes this process makes. Marshal to your UI
    ///     thread before touching UI state and keep it short; exceptions it throws are swallowed
    ///     because a native callback cannot propagate them.
    /// </param>
    /// <param name="watch">
    ///     The registration. Dispose it to stop the callbacks; it is null when the return value is
    ///     an error.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     The watch is bound to the endpoint that is the default when it starts. When the default
    ///     changes, which <see cref="StartEndpointWatch" /> reports, dispose this watch and start a
    ///     new one; the old endpoint keeps reporting its own changes until then.
    /// </remarks>
    public static int StartVolumeWatch(AudioDirection direction, Action onChanged, out IDisposable? watch)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        watch = null;
        if (!IsDirection(direction))
        {
            return InvalidArgument;
        }

        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var result = OpenDefaultVolume(direction, out device, out volume);
            if (result < 0 || volume is null || device is null)
            {
                return result < 0 ? result : Failure;
            }

            var registration = new VolumeWatch(device, volume, onChanged);
            result = registration.Register();
            if (result < 0)
            {
                registration.Dispose();
                return result;
            }

            device = null;
            volume = null;
            watch = registration;
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(volume);
            Release(device);
        }
    }

    /// <summary>
    ///     Starts reporting endpoint arrivals, removals, state changes and console default changes.
    /// </summary>
    /// <param name="onEvent">
    ///     Called on a Core Audio thread, not the caller's. Marshal to your UI thread before touching
    ///     UI state and keep it short; exceptions it throws are swallowed because a native callback
    ///     cannot propagate them. Property changes on endpoints are not reported.
    /// </param>
    /// <param name="watch">
    ///     The registration. Dispose it to stop the callbacks; it is null when the return value is
    ///     an error.
    /// </param>
    /// <returns>Zero on success, otherwise the HRESULT Core Audio returned.</returns>
    /// <remarks>
    ///     Default changes are reported for the console role only, the role every other member of
    ///     this type reads and writes. Windows raises one notification per role, so a consumer that
    ///     needs the multimedia or communications default must enumerate on its own.
    /// </remarks>
    public static int StartEndpointWatch(Action<AudioEndpointWatchEvent> onEvent, out IDisposable? watch)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        watch = null;
        try
        {
            var registration = new EndpointWatch(Enumerator(), onEvent);
            var result = registration.Register();
            if (result < 0)
            {
                registration.Dispose();
                return result;
            }

            watch = registration;
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    private sealed class VolumeWatch : IDisposable
    {
        private readonly VolumeCallback _callback;
        private readonly object _gate = new();
        private nint _callbackPointer;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _volume;

        internal VolumeWatch(IMMDevice device, IAudioEndpointVolume volume, Action onChanged)
        {
            _device = device;
            _volume = volume;
            _callback = new VolumeCallback(onChanged);
        }

        internal int Register()
        {
            _callbackPointer = Marshal.GetComInterfaceForObject(_callback, typeof(IAudioEndpointVolumeCallback));
            return _volume!.RegisterControlChangeNotify(_callbackPointer);
        }

        public void Dispose()
        {
            IAudioEndpointVolume? volume;
            IMMDevice? device;
            nint pointer;
            lock (_gate)
            {
                volume = _volume;
                device = _device;
                pointer = _callbackPointer;
                _volume = null;
                _device = null;
                _callbackPointer = 0;
            }

            if (volume is null)
            {
                return;
            }

            // Unregistering waits for an in-flight callback to return, so nothing here holds a lock
            // the callback could want.
            _callback.Detach();
            try
            {
                if (pointer != 0)
                {
                    _ = volume.UnregisterControlChangeNotify(pointer);
                }
            }
            catch (COMException)
            {
                // The endpoint is gone; there is nothing left to unregister from.
            }
            finally
            {
                if (pointer != 0)
                {
                    Marshal.Release(pointer);
                }

                Release(volume);
                Release(device);
            }
        }
    }

    private sealed class EndpointWatch : IDisposable
    {
        private readonly EndpointCallback _callback;
        private readonly IMMDeviceEnumerator _enumerator;
        private nint _callbackPointer;
        private bool _disposed;

        internal EndpointWatch(IMMDeviceEnumerator enumerator, Action<AudioEndpointWatchEvent> onEvent)
        {
            _enumerator = enumerator;
            _callback = new EndpointCallback(onEvent);
        }

        internal int Register()
        {
            _callbackPointer = Marshal.GetComInterfaceForObject(_callback, typeof(IMMNotificationClient));
            return _enumerator.RegisterEndpointNotificationCallback(_callbackPointer);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callback.Detach();
            try
            {
                if (_callbackPointer != 0)
                {
                    _ = _enumerator.UnregisterEndpointNotificationCallback(_callbackPointer);
                }
            }
            catch (COMException)
            {
            }
            finally
            {
                if (_callbackPointer != 0)
                {
                    Marshal.Release(_callbackPointer);
                    _callbackPointer = 0;
                }
            }
        }
    }

    [ComVisible(true)]
    private sealed class VolumeCallback(Action onChanged) : IAudioEndpointVolumeCallback
    {
        private Action? _onChanged = onChanged;

        public int OnNotify(nint notificationData)
        {
            try
            {
                _onChanged?.Invoke();
            }
            catch
            {
                // A native callback cannot propagate an exception.
            }

            return 0;
        }

        internal void Detach()
        {
            _onChanged = null;
        }
    }

    [ComVisible(true)]
    private sealed class EndpointCallback(Action<AudioEndpointWatchEvent> onEvent) : IMMNotificationClient
    {
        private Action<AudioEndpointWatchEvent>? _onEvent = onEvent;

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            Raise(new AudioEndpointWatchEvent(AudioEndpointChange.StateChanged, deviceId, null));
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            Raise(new AudioEndpointWatchEvent(AudioEndpointChange.Added, deviceId, null));
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            Raise(new AudioEndpointWatchEvent(AudioEndpointChange.Removed, deviceId, null));
            return 0;
        }

        public int OnDefaultDeviceChanged(DataFlow flow, AudioRole role, string? defaultDeviceId)
        {
            if (role == AudioRole.Console && flow is DataFlow.Render or DataFlow.Capture)
            {
                Raise(new AudioEndpointWatchEvent(
                    AudioEndpointChange.DefaultChanged,
                    defaultDeviceId,
                    (AudioDirection)flow));
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            return 0;
        }

        internal void Detach()
        {
            _onEvent = null;
        }

        private void Raise(AudioEndpointWatchEvent change)
        {
            try
            {
                _onEvent?.Invoke(change);
            }
            catch
            {
                // A native callback cannot propagate an exception.
            }
        }
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(nint notificationData);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(
            DataFlow flow,
            AudioRole role,
            [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
