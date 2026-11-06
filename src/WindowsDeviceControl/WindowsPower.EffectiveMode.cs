using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsDeviceControl;

/// <summary>
///     One registration for Windows' effective power mode notifications
///     (<c>PowerRegisterForEffectivePowerModeNotifications</c>). Disposing it stops the notifications.
/// </summary>
/// <remarks>
///     Created only by <see cref="WindowsPower.RegisterEffectivePowerModeNotification" />. The native
///     callback receives a registration id rather than a pointer to this object, so a notification that
///     races disposal finds nothing to call instead of a freed handle. A callback already running may
///     still complete; none starts after the registration is removed. Disposal is idempotent.
/// </remarks>
public sealed class EffectivePowerModeRegistration : IDisposable
{
    private readonly WindowsPower.EffectiveModeCallback _callback;
    private readonly Func<nint, int> _unregister;
    private nint _registration;

    internal EffectivePowerModeRegistration(nint id, nint registration, WindowsPower.EffectiveModeCallback callback,
        Func<nint, int> unregister)
    {
        Id = id;
        _registration = registration;
        _callback = callback;
        _unregister = unregister;
    }

    internal nint Id { get; }

    /// <summary>
    ///     Stops the notifications through <see cref="Unregister" />. A refusal is passed, as the exception
    ///     for its HRESULT, to the <c>failed</c> handler given at registration; it is never thrown.
    /// </summary>
    public void Dispose()
    {
        var result = Unregister();
        if (result < 0)
        {
            _callback.Fail(Marshal.GetExceptionForHR(result)!);
        }
    }

    /// <summary>Stops the notifications and reports what Windows answered.</summary>
    /// <returns>
    ///     The HRESULT of <c>PowerUnregisterFromEffectivePowerModeNotifications</c>, or zero when this
    ///     registration was already removed. A failure leaves nothing to call: the callback is removed from
    ///     the dispatch table first, so a registration Windows keeps reaches no caller code.
    /// </returns>
    public int Unregister()
    {
        var registration = Interlocked.Exchange(ref _registration, 0);
        if (registration == 0)
        {
            return 0;
        }

        WindowsPower.RemoveEffectiveModeCallback(Id);
        return _unregister(registration);
    }
}

public static unsafe partial class WindowsPower
{
    /// <summary><c>EFFECTIVE_POWER_MODE_V2</c>, the version that reports the Windows 11 modes.</summary>
    private const uint EffectivePowerModeV2 = 2;

    private static readonly Dictionary<nint, EffectiveModeCallback> EffectiveModeCallbacks = [];
    private static readonly object EffectiveModeGate = new();
    private static nint _lastEffectiveModeId;

    /// <summary>Registers for effective power mode changes, whoever moves the Windows power mode slider.</summary>
    /// <param name="changed">
    ///     Called on a Windows thread-pool thread: once right after registration with the current mode, and
    ///     then on every change. The effective mode is what <see cref="GetEffectiveMode" /> reads.
    /// </param>
    /// <param name="failed">
    ///     Receives an exception <paramref name="changed" /> threw, on the same thread; when null the
    ///     exception is discarded. An exception never crosses the native boundary, which would terminate
    ///     the process, and an exception <paramref name="failed" /> throws there is discarded too. It also
    ///     receives an unregistration Windows refused when the registration is disposed.
    /// </param>
    /// <returns>The registration; dispose it to stop the notifications.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changed" /> is null.</exception>
    /// <exception cref="Exception">The exception for the HRESULT Windows returned when it refused the registration.</exception>
    public static EffectivePowerModeRegistration RegisterEffectivePowerModeNotification(Action changed,
        Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(changed);
        EffectiveModeCallback callback = new(changed, failed);
        var id = AddEffectiveModeCallback(callback);
        var result = PowerRegisterForEffectivePowerModeNotifications(
            EffectivePowerModeV2, &OnEffectiveModeChanged, id, out var registration);
        if (result >= 0)
        {
            return new EffectivePowerModeRegistration(id, registration, callback,
                PowerUnregisterFromEffectivePowerModeNotifications);
        }

        RemoveEffectiveModeCallback(id);
        throw Marshal.GetExceptionForHR(result)!;
    }

    /// <summary>Adds a callback to the dispatch table under a new id, the context Windows calls back with.</summary>
    internal static nint AddEffectiveModeCallback(EffectiveModeCallback callback)
    {
        lock (EffectiveModeGate)
        {
            var id = ++_lastEffectiveModeId;
            EffectiveModeCallbacks.Add(id, callback);
            return id;
        }
    }

    internal static void RemoveEffectiveModeCallback(nint id)
    {
        lock (EffectiveModeGate)
        {
            EffectiveModeCallbacks.Remove(id);
        }
    }

    /// <summary>Calls the callback registered under an id, if one still is; never throws.</summary>
    /// <param name="id">The context Windows passed back.</param>
    internal static void DispatchEffectiveModeChange(nint id)
    {
        EffectiveModeCallback? callback;
        lock (EffectiveModeGate)
        {
            EffectiveModeCallbacks.TryGetValue(id, out callback);
        }

        callback?.Invoke();
    }

    [UnmanagedCallersOnly]
    private static void OnEffectiveModeChanged(int mode, nint context)
    {
        DispatchEffectiveModeChange(context);
    }

    [LibraryImport("powrprof.dll")]
    private static partial int PowerRegisterForEffectivePowerModeNotifications(
        uint version, delegate* unmanaged<int, nint, void> callback, nint context, out nint registration);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerUnregisterFromEffectivePowerModeNotifications(nint registration);

    /// <summary>One registration's caller code: the change callback and the optional failure handler.</summary>
    /// <param name="Changed">Called for every notification.</param>
    /// <param name="Failed">Receives what <paramref name="Changed" /> threw and a refused unregistration.</param>
    internal sealed record EffectiveModeCallback(Action Changed, Action<Exception>? Failed)
    {
        /// <summary>Runs <see cref="Changed" />; an exception goes to <see cref="Fail" />.</summary>
        internal void Invoke()
        {
            try
            {
                Changed();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Hands an exception to <see cref="Failed" />; one it throws is discarded.</summary>
        /// <param name="error">The failure.</param>
        internal void Fail(Exception error)
        {
            try
            {
                Failed?.Invoke(error);
            }
            catch (Exception)
            {
                // Reached from an UnmanagedCallersOnly method, where an escaping exception ends the process.
            }
        }
    }
}
