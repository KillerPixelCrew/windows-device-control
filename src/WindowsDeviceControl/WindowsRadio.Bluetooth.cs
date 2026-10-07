using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace WindowsDeviceControl;

public static partial class WindowsRadio
{
    private const string BluetoothAqs =
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\""
        + " OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

    private const string AepConnected = "System.Devices.Aep.IsConnected";
    private const string AepContainer = "System.Devices.Aep.ContainerId";
    private const string DeviceContainer = "System.Devices.ContainerId";

    /// <summary>The association endpoint properties every Bluetooth endpoint read asks for.</summary>
    private static readonly string[] EndpointProperties = [AepConnected, AepContainer];

    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(90);
    private static int _nextPairingToken;
    private static long _nextPairingAttempt;
    private static readonly ConcurrentDictionary<uint, PendingPairing> PendingPairings = new();
    private static readonly ConcurrentDictionary<long, byte> ActivePairingAttempts = new();
    private static readonly string[] ContainerProperties = [AepContainer, DeviceContainer];

    /// <summary>Lists Bluetooth devices, classic and Low Energy alike.</summary>
    /// <param name="pairedOnly">
    ///     True to list only already-paired devices; false to include every
    ///     device currently visible, which is what a "add a device" screen shows.
    /// </param>
    /// <returns>
    ///     The distinct devices found. Classic and Low Energy endpoints that share a device
    ///     container are combined. This is a point-in-time snapshot — use
    ///     <see cref="StartBluetoothWatch" /> to follow changes instead of polling this.
    /// </returns>
    public static IReadOnlyList<BluetoothDevice> ListBluetoothDevices(bool pairedOnly)
    {
        var filter = pairedOnly
            ? $"{BluetoothAqs} AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True"
            : BluetoothAqs;
        var found = DeviceInformation.FindAllAsync(
                filter,
                EndpointProperties,
                DeviceInformationKind.AssociationEndpoint)
            .WaitWinRt();
        return found.Select(ReadBluetoothDevice)
            .Where(device => device.Id.Length > 0)
            .GroupBy(
                device => device.Container.Length > 0
                    ? $"container:{device.Container}"
                    : $"endpoint:{device.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(MergeBluetoothEndpoints)
            .OrderBy(device => device.Name.Length == 0)
            .ThenBy(device => device.Name, StringComparer.Ordinal)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static BluetoothDevice MergeBluetoothEndpoints(
        IGrouping<string, BluetoothDevice> endpoints)
    {
        var preferred = endpoints
            .OrderByDescending(device => device.Paired)
            .ThenByDescending(device => device.Connected)
            .ThenByDescending(device => device.CanPair)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .First();
        var name = endpoints.Select(device => device.Name)
            .Where(value => value.Length > 0)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
        return preferred with
        {
            Name = name,
            Paired = endpoints.Any(device => device.Paired),
            CanPair = endpoints.Any(device => device.CanPair),
            Connected = endpoints.Any(device => device.Connected)
        };
    }

    /// <summary>Counts the currently connected Bluetooth devices.</summary>
    /// <returns>
    ///     How many distinct classic or Low Energy devices PnP reports as connected. A device
    ///     exposed through both transports is counted once by its device-container identity. Cheaper
    ///     than <see cref="ListBluetoothDevices" /> when all you need is whether anything is connected
    ///     — for a status icon, say.
    /// </returns>
    public static int ConnectedBluetoothCount()
    {
        // Keep fallible WinRT calls out of the type initializer.
        string[] selectors =
        [
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
        ];
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in selectors)
        {
            var devices = DeviceInformation.FindAllAsync(selector, ContainerProperties).WaitWinRt();
            foreach (var device in devices)
            {
                identities.Add(BluetoothIdentity(device.Id, device.Properties));
            }
        }

        return identities.Count;
    }

    /// <summary>Builds a cross-transport identity for connected-device counting.</summary>
    /// <param name="id">Association endpoint ID used when no container is usable.</param>
    /// <param name="properties">Requested AEP/container properties.</param>
    /// <returns>A container-prefixed normalized GUID or endpoint-prefixed ID; friendly names never determine identity.</returns>
    internal static string BluetoothIdentity(
        string id,
        IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(AepContainer, out var value)
            || properties.TryGetValue(DeviceContainer, out value))
        {
            var container = NormalizeContainer(value);
            if (container.Length > 0)
            {
                return $"container:{container}";
            }
        }

        return $"endpoint:{id}";
    }

    /// <summary>
    ///     The one container-identity form this library publishes: a GUID, or a string that parses as
    ///     one with or without braces, becomes its lower-case hyphenated form. The empty GUID and
    ///     anything else become empty, so endpoints without a real container are never merged.
    /// </summary>
    /// <param name="value">A GUID, GUID text or unreadable/missing property value.</param>
    /// <returns>A nonempty GUID in hyphenated form, or empty when no usable identity is available.</returns>
    internal static string NormalizeContainer(object? value)
    {
        var container = value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => Guid.Empty
        };
        return container == Guid.Empty ? string.Empty : container.ToString("D");
    }

    /// <summary>Starts a live feed of Bluetooth device changes.</summary>
    /// <param name="onChange">
    ///     Called for each change on a Windows device-watcher thread, never the caller's, and one call
    ///     at a time for this registration. Post to your own thread rather than waiting on it: the
    ///     registration's <see cref="IDisposable.Dispose" /> waits for a running callback, so a
    ///     synchronous wait on a thread that disposes it deadlocks. Exceptions it throws are
    ///     swallowed, because an exception escaping a WinRT watcher thread terminates the process.
    /// </param>
    /// <returns>
    ///     The registration. Disposing it is the stop: every handler is revoked and the watcher
    ///     stopped, and no callback runs once it has returned. Disposing it again does nothing.
    /// </returns>
    /// <remarks>
    ///     Every call creates an independent registration with a watcher of its own, so several can
    ///     run side by side and disposing one leaves the others reporting. The initial sweep reports
    ///     everything already present as <see cref="BluetoothChangeKind.Added" /> and then one
    ///     <see cref="BluetoothChangeKind.EnumerationCompleted" />; the feed stays live afterwards.
    ///     When Windows aborts the watcher the registration reports one
    ///     <see cref="BluetoothChangeKind.Stopped" /> and nothing after it. Dispose every registration
    ///     you start: the watcher holds the callback alive. Changes identify individual association
    ///     endpoints, unlike <see cref="ListBluetoothDevices" />, which merges shared containers.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onChange" /> is null.</exception>
    public static IDisposable StartBluetoothWatch(Action<BluetoothChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothAqs,
            EndpointProperties,
            DeviceInformationKind.AssociationEndpoint);
        var watch = new BluetoothWatch(watcher, onChange);
        try
        {
            watch.Start();
        }
        catch
        {
            watch.Dispose();
            throw;
        }

        return watch;
    }

    /// <summary>Pairs a Bluetooth device, running the ceremony through your own UI.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id" />.</param>
    /// <param name="onRequest">
    ///     Called when Windows asks something: show it, then answer with
    ///     <see cref="RespondToPairing" />. <b>You must answer</b>: the ceremony holds a deferral until
    ///     you do, and an unanswered question runs the attempt into its deadline. Raised on a Windows
    ///     thread, not the caller's. If it throws, the question is declined, the attempt ends and the
    ///     returned task faults with that exception.
    /// </param>
    /// <param name="cancellationToken">
    ///     Ends this attempt only. Its unanswered questions are declined first, so Windows is
    ///     never left waiting on an open deferral.
    /// </param>
    /// <returns>
    ///     The result Windows reported. The task faults with <see cref="TimeoutException" /> when the
    ///     attempt, including the display-PIN fallback ceremony, has not finished within 90 seconds,
    ///     and is cancelled when <paramref name="cancellationToken" /> ends it first.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId" /> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="onRequest" /> is null.</exception>
    /// <remarks>
    ///     Initially offers confirm-only, provide-PIN and PIN-match ceremonies. Only Windows'
    ///     RequiredHandlerNotRegistered result triggers one display-PIN fallback, within the same
    ///     90-second deadline. Each request token can be answered once. Native failures fault the task;
    ///     cleanup failures can be combined with the original failure in an <see cref="AggregateException" />.
    /// </remarks>
    public static Task<PairingResult> PairBluetoothAsync(
        string deviceId,
        Action<PairingRequest> onRequest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(onRequest);
        return PairBluetoothCoreAsync(deviceId, onRequest, PairingTimeout, cancellationToken);
    }

    private static async Task<PairingResult> PairBluetoothCoreAsync(
        string deviceId,
        Action<PairingRequest> onRequest,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _nextPairingAttempt);
        ActivePairingAttempts.TryAdd(attempt, 0);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var wait = new CancellationTokenSource();
        Exception? callbackFailure = null;
        Exception? endFailure = null;
        // Cancellation, from the caller or the deadline, first ends the attempt and completes its
        // pending deferrals and only then cancels the Windows wait, so the pairing operation is
        // never left waiting on a deferral of its own when the cancellation reaches it.
        var ending = bound.Token.Register(() =>
        {
            endFailure = EndPairingAttempt(attempt);
            try
            {
                wait.Cancel();
            }
            catch (Exception)
            {
                // A cancellation callback runs on a timer or worker thread, where an escaping
                // exception would end the process.
            }
        });
        bound.CancelAfter(deadline);
        DeviceInformationCustomPairing? custom = null;
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>? requested = null;
        var result = default(PairingResult);
        Exception? failure = null;
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(
                    deviceId,
                    EndpointProperties,
                    DeviceInformationKind.AssociationEndpoint)
                .AsTask(wait.Token)
                .ConfigureAwait(false);
            var deviceName = info.Name ?? string.Empty;
            custom = info.Pairing.Custom;
            requested = (sender, args) =>
            {
                if (OnPairingRequested(attempt, args, deviceName, onRequest) is not { } thrown)
                {
                    return;
                }

                callbackFailure ??= thrown;
                try
                {
                    _ = bound.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // The attempt already ended.
                }
            };
            custom.PairingRequested += requested;
            var status = (await custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly
                        | DevicePairingKinds.ProvidePin
                        | DevicePairingKinds.ConfirmPinMatch,
                        DevicePairingProtectionLevel.Default)
                    .AsTask(wait.Token)
                    .ConfigureAwait(false))
                .Status;
            if (status == DevicePairingResultStatus.RequiredHandlerNotRegistered)
            {
                status = (await custom.PairAsync(DevicePairingKinds.DisplayPin, DevicePairingProtectionLevel.Default)
                        .AsTask(wait.Token)
                        .ConfigureAwait(false))
                    .Status;
            }

            result = new PairingResult(MapPairingOutcome(status), (int)status);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            failure = cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new TimeoutException("Bluetooth pairing timed out.");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (custom is not null && requested is not null)
        {
            custom.PairingRequested -= requested;
        }

        // Waits for a cancellation callback that is already running, so its result is visible here.
        ending.Dispose();
        var cleanup = EndPairingAttempt(attempt) ?? endFailure;
        // A caller exception is the reason the attempt ended, ahead of the cancellation it caused.
        failure = callbackFailure ?? failure;
        if (cleanup is not null)
        {
            failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return result;
    }

    /// <summary>Registers one pairing question and hands it to the caller.</summary>
    /// <returns>The exception the caller threw, after declining the question; otherwise null.</returns>
    private static Exception? OnPairingRequested(
        long attempt,
        DevicePairingRequestedEventArgs args,
        string deviceName,
        Action<PairingRequest> onRequest)
    {
        PendingPairing? pending = null;
        uint token = 0;
        try
        {
            var deferral = args.GetDeferral();
            var kind = MapPairingKind(args.PairingKind);
            pending = new PendingPairing(attempt, kind, pin => Accept(args, pin), deferral.Complete);
            if (!ActivePairingAttempts.ContainsKey(attempt))
            {
                deferral.Complete();
                return null;
            }

            token = AddPendingPairing(pending);
            if (!ActivePairingAttempts.ContainsKey(attempt)
                && PendingPairings.TryRemove(new KeyValuePair<uint, PendingPairing>(token, pending)))
            {
                deferral.Complete();
                return null;
            }

            onRequest(new PairingRequest(token, kind, args.Pin ?? string.Empty, deviceName));
            return null;
        }
        catch (Exception ex)
        {
            // Declined without accepting: a caller that could not show the question must not leave
            // Windows waiting on it until the deadline.
            if (pending is not null
                && token != 0
                && PendingPairings.TryRemove(new KeyValuePair<uint, PendingPairing>(token, pending)))
            {
                try
                {
                    pending.Complete();
                }
                catch (Exception)
                {
                    // The attempt ends with the caller's exception either way.
                }
            }

            return ex;
        }
    }

    private static void Accept(DevicePairingRequestedEventArgs args, string? pin)
    {
        if (pin is null)
        {
            args.Accept();
        }
        else
        {
            args.Accept(pin);
        }
    }

    /// <summary>Answers a pairing question raised by <see cref="PairBluetoothAsync" />.</summary>
    /// <param name="token">
    ///     The <see cref="PairingRequest.Token" /> being answered. A token that is
    ///     unknown or already answered is ignored.
    /// </param>
    /// <param name="accept">True to proceed with pairing, false to reject it.</param>
    /// <param name="pin">
    ///     The PIN the user entered. Required to accept a question whose
    ///     <see cref="PairingRequest.Kind" /> is <see cref="PairingKind.ProvidePin" />, and ignored
    ///     for every other kind and for a rejection.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     An accepted <see cref="PairingKind.ProvidePin" /> question has no PIN. The question is
    ///     left unanswered, so the caller can still answer it.
    /// </exception>
    /// <remarks>
    ///     Safe to call from any thread, including the request callback. Native acceptance/deferral
    ///     failures propagate after the token is consumed; submitting it again does not retry the answer.
    /// </remarks>
    public static void RespondToPairing(uint token, bool accept, string? pin)
    {
        if (!PendingPairings.TryGetValue(token, out var pending))
        {
            return;
        }

        var providePin = accept && pending.Kind == PairingKind.ProvidePin;
        if (providePin)
        {
            ArgumentException.ThrowIfNullOrEmpty(pin);
        }

        if (!PendingPairings.TryRemove(new KeyValuePair<uint, PendingPairing>(token, pending)))
        {
            return;
        }

        try
        {
            if (accept)
            {
                pending.Accept(providePin ? pin : null);
            }
        }
        finally
        {
            pending.Complete();
        }
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id" />.</param>
    /// <returns>
    ///     Whether the device is no longer paired, including when it was not paired to begin
    ///     with, and the status Windows reported.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId" /> is null or empty.</exception>
    /// <remarks>
    ///     Blocks for the native unpairing result and propagates WinRT failures. This removes pairing
    ///     identity; use <see cref="CoreAudio.SetBluetoothAudioConnection" /> for a soft audio disconnect.
    /// </remarks>
    public static BluetoothUnpairResult UnpairBluetooth(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        var info = ReadEndpoint(deviceId);
        var result = info.Pairing.UnpairAsync().WaitWinRt();
        return MapUnpairing(result.Status);
    }

    internal static BluetoothUnpairResult MapUnpairing(DeviceUnpairingResultStatus status)
    {
        return new BluetoothUnpairResult(
            status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired,
            (int)status);
    }

    private static DeviceInformation ReadEndpoint(string id)
    {
        return DeviceInformation.CreateFromIdAsync(
                id,
                EndpointProperties,
                DeviceInformationKind.AssociationEndpoint)
            .WaitWinRt();
    }

    /// <summary>Marks an attempt finished and completes its unanswered deferrals.</summary>
    /// <returns>The failure completing them raised, or null.</returns>
    private static Exception? EndPairingAttempt(long attempt)
    {
        ActivePairingAttempts.TryRemove(attempt, out _);
        try
        {
            CompletePendingPairings(attempt);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static uint AddPendingPairing(PendingPairing pending)
    {
        while (true)
        {
            var token = unchecked((uint)Interlocked.Increment(ref _nextPairingToken));
            if (PendingPairings.TryAdd(token, pending))
            {
                return token;
            }
        }
    }

    private static void CompletePendingPairings(long attempt)
    {
        List<Exception>? failures = null;
        foreach (var entry in PendingPairings)
        {
            if (entry.Value.Attempt == attempt
                && PendingPairings.TryRemove(
                    new KeyValuePair<uint, PendingPairing>(entry.Key, entry.Value)))
            {
                try
                {
                    entry.Value.Complete();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Pending pairing deferrals could not be completed.", failures);
        }
    }

    private static BluetoothDevice ReadBluetoothDevice(DeviceInformation info)
    {
        var connected = info.Properties.TryGetValue(AepConnected, out var connectedValue)
                        && connectedValue is bool isConnected
                        && isConnected;
        var container = info.Properties.TryGetValue(AepContainer, out var containerValue)
            ? NormalizeContainer(containerValue)
            : string.Empty;
        return new BluetoothDevice(
            info.Id ?? string.Empty,
            info.Name ?? string.Empty,
            info.Pairing.IsPaired,
            info.Pairing.CanPair,
            connected,
            container);
    }

    private static PairingKind MapPairingKind(DevicePairingKinds kind)
    {
        return kind switch
        {
            DevicePairingKinds.ConfirmOnly => PairingKind.ConfirmOnly,
            DevicePairingKinds.DisplayPin => PairingKind.DisplayPin,
            DevicePairingKinds.ProvidePin => PairingKind.ProvidePin,
            DevicePairingKinds.ConfirmPinMatch => PairingKind.ConfirmPinMatch,
            _ => PairingKind.Unknown
        };
    }

    private static PairingOutcome MapPairingOutcome(DevicePairingResultStatus status)
    {
        return status switch
        {
            DevicePairingResultStatus.Paired => PairingOutcome.Paired,
            DevicePairingResultStatus.AlreadyPaired => PairingOutcome.AlreadyPaired,
            DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled =>
                PairingOutcome.Cancelled,
            DevicePairingResultStatus.AccessDenied => PairingOutcome.AccessDenied,
            DevicePairingResultStatus.OperationAlreadyInProgress => PairingOutcome.AlreadyInProgress,
            DevicePairingResultStatus.Failed
                or DevicePairingResultStatus.ConnectionRejected
                or DevicePairingResultStatus.TooManyConnections
                or DevicePairingResultStatus.HardwareFailure
                or DevicePairingResultStatus.AuthenticationTimeout
                or DevicePairingResultStatus.AuthenticationNotAllowed
                or DevicePairingResultStatus.AuthenticationFailure
                or DevicePairingResultStatus.NoSupportedProfiles => PairingOutcome.Failed,
            _ => PairingOutcome.Unknown
        };
    }

    /// <summary>One <see cref="StartBluetoothWatch" /> registration: its watcher, records and delivery gate.</summary>
    private sealed class BluetoothWatch : IDisposable
    {
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformation> _added;
        private readonly Action<BluetoothChange> _callback;
        private readonly TypedEventHandler<DeviceWatcher, object> _completed;
        private readonly object _gate = new();
        private readonly Dictionary<string, DeviceInformation> _records = new(StringComparer.Ordinal);
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> _removed;
        private readonly TypedEventHandler<DeviceWatcher, object> _stopped;
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> _updated;
        private readonly DeviceWatcher _watcher;
        private int _disposed;

        /// <summary>Set under the gate once nothing more may be delivered: disposed, or stopped by Windows.</summary>
        private bool _ended;

        internal BluetoothWatch(DeviceWatcher watcher, Action<BluetoothChange> callback)
        {
            _watcher = watcher;
            _callback = callback;
            _added = (_, info) => OnAdded(info);
            _updated = (_, update) => OnUpdated(update);
            _removed = (_, update) => OnRemoved(update);
            _completed = (_, _) => Publish(new BluetoothChange(BluetoothChangeKind.EnumerationCompleted, default));
            _stopped = (_, _) => OnStopped();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Taking the gate waits for a callback that is delivering now; every later one sees the
            // end and delivers nothing. The gate is reentrant, so a callback disposing its own
            // registration does not wait on itself.
            lock (_gate)
            {
                _ended = true;
            }

            _watcher.Added -= _added;
            _watcher.Updated -= _updated;
            _watcher.Removed -= _removed;
            _watcher.EnumerationCompleted -= _completed;
            _watcher.Stopped -= _stopped;
            try
            {
                _watcher.Stop();
            }
            catch (InvalidOperationException)
            {
                // A watcher that failed during Start, or that Windows aborted, is already stopped.
            }
        }

        internal void Start()
        {
            _watcher.Added += _added;
            _watcher.Updated += _updated;
            _watcher.Removed += _removed;
            _watcher.EnumerationCompleted += _completed;
            _watcher.Stopped += _stopped;
            _watcher.Start();
        }

        private void OnAdded(DeviceInformation info)
        {
            if (info.Id.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }

                _records[info.Id] = info;
                Raise(new BluetoothChange(BluetoothChangeKind.Added, ReadBluetoothDevice(info)));
            }
        }

        private void OnUpdated(DeviceInformationUpdate update)
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }

                if (_records.TryGetValue(update.Id, out var info))
                {
                    info.Update(update);
                    Raise(new BluetoothChange(BluetoothChangeKind.Updated, ReadBluetoothDevice(info)));
                    return;
                }
            }

            try
            {
                // The lookup can block in the device stack, so it runs outside the gate that
                // disposal and every other callback wait on. The result is still published under it.
                var resolved = ReadEndpoint(update.Id);
                lock (_gate)
                {
                    if (_ended)
                    {
                        return;
                    }

                    _records[resolved.Id] = resolved;
                    Raise(new BluetoothChange(BluetoothChangeKind.Updated, ReadBluetoothDevice(resolved)));
                }
            }
            catch
            {
                // A disappearing endpoint is followed by Removed; it has no update to publish.
            }
        }

        private void OnRemoved(DeviceInformationUpdate update)
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }

                _records.Remove(update.Id);
                Raise(new BluetoothChange(BluetoothChangeKind.Removed, new BluetoothDevice(
                    update.Id, string.Empty, false, false, false, string.Empty)));
            }
        }

        /// <summary>
        ///     The watcher stopped while this registration still owned it, which only Windows does:
        ///     disposal revokes this handler before it stops the watcher.
        /// </summary>
        private void OnStopped()
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }

                Raise(new BluetoothChange(BluetoothChangeKind.Stopped, default));
                _ended = true;
            }
        }

        private void Publish(BluetoothChange change)
        {
            lock (_gate)
            {
                if (!_ended)
                {
                    Raise(change);
                }
            }
        }

        // Callers hold the gate. An exception escaping a WinRT DeviceWatcher thread ends the
        // process, and a consumer that posts to a dispatcher that is shutting down throws exactly then.
        private void Raise(BluetoothChange change)
        {
            try
            {
                _callback(change);
            }
            catch
            {
                // Documented on StartBluetoothWatch: consumer exceptions are swallowed.
            }
        }
    }

    /// <summary>One unanswered pairing question and the two ways it can be answered.</summary>
    /// <param name="attempt">The attempt that raised it.</param>
    /// <param name="kind">The ceremony, which decides whether an accept carries the PIN.</param>
    /// <param name="accept">Accepts the question, with the PIN or, for null, without one.</param>
    /// <param name="complete">Completes the question's deferral.</param>
    private sealed class PendingPairing(
        long attempt,
        PairingKind kind,
        Action<string?> accept,
        Action complete)
    {
        internal long Attempt { get; } = attempt;
        internal PairingKind Kind { get; } = kind;
        internal Action<string?> Accept { get; } = accept;
        internal Action Complete { get; } = complete;
    }
}
