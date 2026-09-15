using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly object BluetoothWatchLock = new();
    private static BluetoothWatch? _bluetoothWatch;
    private static int _nextPairingToken;
    private static long _nextPairingAttempt;
    private static readonly ConcurrentDictionary<uint, PendingPairing> PendingPairings = new();
    private static readonly ConcurrentDictionary<long, byte> ActivePairingAttempts = new();
    private static readonly string[] ContainerProperties = [AepContainer, DeviceContainer];
    private static string[]? _connectedSelectors;

    /// <summary>Lists Bluetooth devices, classic and Low Energy alike.</summary>
    /// <param name="pairedOnly">True to list only already-paired devices; false to include every
    /// device currently visible, which is what a "add a device" screen shows.</param>
    /// <returns>The distinct devices found. Classic and Low Energy endpoints that share a device
    /// container are combined. This is a point-in-time snapshot — use
    /// <see cref="StartBluetoothWatch"/> to follow changes instead of polling this.</returns>
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
            Connected = endpoints.Any(device => device.Connected),
        };
    }

    /// <summary>Counts the currently connected Bluetooth devices.</summary>
    /// <returns>How many distinct classic or Low Energy devices PnP reports as connected. A device
    /// exposed through both transports is counted once by its device-container identity. Cheaper
    /// than <see cref="ListBluetoothDevices"/> when all you need is whether anything is connected
    /// — for a status icon, say.</returns>
    public static int ConnectedBluetoothCount()
    {
        // Built on first use rather than in the type initializer, so a failing WinRT call cannot
        // break every other member of this class.
        var selectors = _connectedSelectors ??=
        [
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
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

    internal static string BluetoothIdentity(
        string id,
        IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(AepContainer, out var value)
            || properties.TryGetValue(DeviceContainer, out value))
        {
            var container = value switch
            {
                Guid guid => guid.ToString("D"),
                string text when Guid.TryParse(text, out var guid) => guid.ToString("D"),
                _ => null,
            };
            if (container is not null)
            {
                return $"container:{container}";
            }
        }
        return $"endpoint:{id}";
    }

    /// <summary>Starts a live feed of Bluetooth device changes.</summary>
    /// <param name="onChange">Called for each change. Raised on a Windows device-watcher thread,
    /// not the caller's — marshal to your UI thread before touching UI state.</param>
    /// <remarks>
    /// Starting again replaces the previous feed rather than adding a second one, so this is safe
    /// to call on every screen entry. The initial sweep reports everything already present as
    /// <see cref="BluetoothChangeKind.Added"/> and then one
    /// <see cref="BluetoothChangeKind.EnumerationCompleted"/>; the feed stays live afterwards.
    /// Always pair with <see cref="StopBluetoothWatch"/> — the watcher holds callbacks alive.
    /// </remarks>
    public static void StartBluetoothWatch(Action<BluetoothChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        lock (BluetoothWatchLock)
        {
            StopBluetoothWatchCore();
            var watcher = DeviceInformation.CreateWatcher(
                BluetoothAqs,
                EndpointProperties,
                DeviceInformationKind.AssociationEndpoint);
            var watch = new BluetoothWatch(watcher, onChange);
            watch.Added = (_, info) => OnBluetoothAdded(watch, info);
            watch.Updated = (_, update) => OnBluetoothUpdated(watch, update);
            watch.Removed = (_, update) => OnBluetoothRemoved(watch, update);
            watch.Completed = (_, _) => PublishBluetoothChange(
                watch,
                new BluetoothChange(BluetoothChangeKind.EnumerationCompleted, default));
            _bluetoothWatch = watch;
            try
            {
                watcher.Added += watch.Added;
                watcher.Updated += watch.Updated;
                watcher.Removed += watch.Removed;
                watcher.EnumerationCompleted += watch.Completed;
                watcher.Start();
            }
            catch
            {
                StopBluetoothWatchCore();
                throw;
            }
        }
    }

    /// <summary>Stops reporting Bluetooth device changes.</summary>
    /// <remarks>Safe to call when no watch is running. Every WinRT event handler is revoked before
    /// this returns, so once it has, no further callback can arrive — which is what makes it safe
    /// to tear down whatever state the callback touched.</remarks>
    public static void StopBluetoothWatch()
    {
        lock (BluetoothWatchLock)
        {
            StopBluetoothWatchCore();
        }
    }

    private static void StopBluetoothWatchCore()
    {
        var watch = _bluetoothWatch;
        _bluetoothWatch = null;
        if (watch is null)
        {
            return;
        }
        if (watch.Added is not null)
        {
            watch.Watcher.Added -= watch.Added;
        }
        if (watch.Updated is not null)
        {
            watch.Watcher.Updated -= watch.Updated;
        }
        if (watch.Removed is not null)
        {
            watch.Watcher.Removed -= watch.Removed;
        }
        if (watch.Completed is not null)
        {
            watch.Watcher.EnumerationCompleted -= watch.Completed;
        }
        try
        {
            watch.Watcher.Stop();
        }
        catch (InvalidOperationException)
        {
            // A watcher that failed during Start can already be stopped or aborted.
        }
    }

    private static void OnBluetoothAdded(BluetoothWatch watch, DeviceInformation info)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch) || info.Id.Length == 0)
            {
                return;
            }
            watch.Records[info.Id] = info;
            watch.Callback(new BluetoothChange(
                BluetoothChangeKind.Added,
                ReadBluetoothDevice(info)));
        }
    }

    private static void OnBluetoothUpdated(
        BluetoothWatch watch,
        DeviceInformationUpdate update)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch))
            {
                return;
            }
            if (watch.Records.TryGetValue(update.Id, out var info))
            {
                info.Update(update);
                watch.Callback(new BluetoothChange(
                    BluetoothChangeKind.Updated,
                    ReadBluetoothDevice(info)));
                return;
            }
        }
        try
        {
            // The lookup can block in the device stack, so it runs without the lock that stopping
            // the watch and every other callback wait on. The result is still published under it.
            var resolved = ReadEndpoint(update.Id);
            lock (BluetoothWatchLock)
            {
                if (!ReferenceEquals(_bluetoothWatch, watch))
                {
                    return;
                }
                watch.Records[resolved.Id] = resolved;
                watch.Callback(new BluetoothChange(
                    BluetoothChangeKind.Updated,
                    ReadBluetoothDevice(resolved)));
            }
        }
        catch
        {
            // A disappearing endpoint is followed by Removed; it has no update to publish.
        }
    }

    private static void OnBluetoothRemoved(
        BluetoothWatch watch,
        DeviceInformationUpdate update)
    {
        lock (BluetoothWatchLock)
        {
            if (!ReferenceEquals(_bluetoothWatch, watch))
            {
                return;
            }
            watch.Records.Remove(update.Id);
            watch.Callback(new BluetoothChange(BluetoothChangeKind.Removed, new BluetoothDevice(
                update.Id, string.Empty, false, false, false, string.Empty)));
        }
    }

    private static void PublishBluetoothChange(BluetoothWatch watch, BluetoothChange change)
    {
        lock (BluetoothWatchLock)
        {
            if (ReferenceEquals(_bluetoothWatch, watch))
            {
                watch.Callback(change);
            }
        }
    }

    /// <summary>Pairs a Bluetooth device, running the ceremony through your own UI.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id"/>.</param>
    /// <param name="onRequest">Called when Windows asks something — show it, then answer with
    /// <see cref="RespondToPairing"/>. <b>You must answer</b>: the ceremony holds a deferral that
    /// expires, and an unanswered request fails the pairing.</param>
    /// <param name="onFinished">Called once when the attempt ends, with the result, or with an
    /// exception if one escaped. Both arguments are null only if the attempt was abandoned.</param>
    /// <remarks>
    /// Returns immediately; the ceremony runs on a worker thread and both callbacks are raised
    /// there. This is the piece that is hard to find elsewhere — Windows supports several pairing
    /// ceremonies, and the right one depends on the device, so
    /// <see cref="PairingRequest.Kind"/> tells you which prompt to show.
    /// </remarks>
    public static void PairBluetooth(
        string deviceId,
        Action<PairingRequest> onRequest,
        Action<PairingResult?, Exception?> onFinished)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(onRequest);
        ArgumentNullException.ThrowIfNull(onFinished);
        var attempt = Interlocked.Increment(ref _nextPairingAttempt);
        _ = Task.Run(() =>
        {
            ActivePairingAttempts.TryAdd(attempt, 0);
            PairingResult? completed = null;
            Exception? failure = null;
            DeviceInformationCustomPairing? custom = null;
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>?
                requested = null;
            try
            {
                var info = ReadEndpoint(deviceId);
                custom = info.Pairing.Custom;
                requested = (_, args) =>
                    {
                        var deferral = args.GetDeferral();
                        var pending = new PendingPairing(attempt, args, deferral);
                        if (!ActivePairingAttempts.ContainsKey(attempt))
                        {
                            deferral.Complete();
                            return;
                        }
                        var token = AddPendingPairing(pending);
                        if (!ActivePairingAttempts.ContainsKey(attempt)
                            && PendingPairings.TryRemove(
                                new KeyValuePair<uint, PendingPairing>(token, pending)))
                        {
                            deferral.Complete();
                            return;
                        }
                        onRequest(new PairingRequest(
                            token,
                            MapPairingKind(args.PairingKind),
                            args.Pin ?? string.Empty,
                            info.Name ?? string.Empty));
                    };
                custom.PairingRequested += requested;
                var result = Pair(
                    custom,
                    DevicePairingKinds.ConfirmOnly
                        | DevicePairingKinds.ProvidePin
                        | DevicePairingKinds.ConfirmPinMatch,
                    attempt);
                if (result.Status == DevicePairingResultStatus.RequiredHandlerNotRegistered)
                {
                    result = Pair(custom, DevicePairingKinds.DisplayPin, attempt);
                }
                completed = new PairingResult(
                    MapPairingOutcome(result.Status),
                    (int)result.Status);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ActivePairingAttempts.TryRemove(attempt, out _);
                if (custom is not null && requested is not null)
                {
                    custom.PairingRequested -= requested;
                }
                try
                {
                    CompletePendingPairings(attempt);
                }
                catch (Exception cleanupFailure)
                {
                    failure = failure is null
                        ? cleanupFailure
                        : new AggregateException(failure, cleanupFailure);
                }
            }
            onFinished(failure is null ? completed : null, failure);
        });
    }

    /// <summary>Answers a pairing question raised by <see cref="PairBluetooth"/>.</summary>
    /// <param name="token">The <see cref="PairingRequest.Token"/> being answered. A token that is
    /// unknown or already answered is ignored.</param>
    /// <param name="accept">True to proceed with pairing, false to reject it.</param>
    /// <param name="pin">The PIN the user entered. Required when
    /// <see cref="PairingRequest.Kind"/> is <see cref="PairingKind.ProvidePin"/>, and ignored
    /// otherwise.</param>
    /// <remarks>Safe to call from any thread, including directly from the request callback.</remarks>
    public static void RespondToPairing(uint token, bool accept, string? pin)
    {
        if (!PendingPairings.TryRemove(token, out var pending))
        {
            return;
        }
        try
        {
            if (accept)
            {
                if (string.IsNullOrEmpty(pin))
                {
                    pending.Args.Accept();
                }
                else
                {
                    pending.Args.Accept(pin);
                }
            }
        }
        finally
        {
            pending.Deferral.Complete();
        }
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="deviceId">The device's <see cref="BluetoothDevice.Id"/>.</param>
    /// <returns><see langword="true"/> when the device is no longer paired, including when it was
    /// not paired to begin with.</returns>
    public static bool UnpairBluetooth(string deviceId)
    {
        var info = ReadEndpoint(deviceId);
        var result = info.Pairing.UnpairAsync().WaitWinRt();
        return result.Status is DeviceUnpairingResultStatus.Unpaired
            or DeviceUnpairingResultStatus.AlreadyUnpaired;
    }

    private static DeviceInformation ReadEndpoint(string id) => DeviceInformation.CreateFromIdAsync(
            id,
            EndpointProperties,
            DeviceInformationKind.AssociationEndpoint)
        .WaitWinRt();

    private static DevicePairingResult Pair(
        DeviceInformationCustomPairing pairing,
        DevicePairingKinds kinds,
        long attempt)
    {
        using var timeout = new CancellationTokenSource(PairingTimeout);
        try
        {
            return pairing.PairAsync(kinds, DevicePairingProtectionLevel.Default)
                .WaitWinRt(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            CompletePendingPairings(attempt);
            throw new TimeoutException("Bluetooth pairing timed out.");
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
                    entry.Value.Deferral.Complete();
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
            ? containerValue switch
            {
                Guid id => id.ToString("D"),
                string text => text.Trim('{', '}').ToLowerInvariant(),
                _ => string.Empty,
            }
            : string.Empty;
        return new BluetoothDevice(
            info.Id ?? string.Empty,
            info.Name ?? string.Empty,
            info.Pairing.IsPaired,
            info.Pairing.CanPair,
            connected,
            container);
    }

    private static PairingKind MapPairingKind(DevicePairingKinds kind) => kind switch
    {
        DevicePairingKinds.ConfirmOnly => PairingKind.ConfirmOnly,
        DevicePairingKinds.DisplayPin => PairingKind.DisplayPin,
        DevicePairingKinds.ProvidePin => PairingKind.ProvidePin,
        DevicePairingKinds.ConfirmPinMatch => PairingKind.ConfirmPinMatch,
        _ => PairingKind.Unknown,
    };

    private static PairingOutcome MapPairingOutcome(DevicePairingResultStatus status) => status switch
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
        _ => PairingOutcome.Unknown,
    };

    private sealed class BluetoothWatch(
        DeviceWatcher watcher,
        Action<BluetoothChange> callback)
    {
        internal DeviceWatcher Watcher { get; } = watcher;
        internal Action<BluetoothChange> Callback { get; } = callback;
        internal Dictionary<string, DeviceInformation> Records { get; } =
            new(StringComparer.Ordinal);
        internal TypedEventHandler<DeviceWatcher, DeviceInformation>? Added { get; set; }
        internal TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Updated { get; set; }
        internal TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Removed { get; set; }
        internal TypedEventHandler<DeviceWatcher, object>? Completed { get; set; }
    }

    private sealed record PendingPairing(
        long Attempt,
        DevicePairingRequestedEventArgs Args,
        Deferral Deferral);
}
