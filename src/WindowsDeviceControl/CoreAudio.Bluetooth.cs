using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

public static partial class CoreAudio
{
    private const uint DeviceStateAll = 0x0000000F;
    private const uint KsPropertyTypeGet = 1;

    private static readonly Guid DeviceTopologyId =
        new("2A07407E-6497-4A18-9787-32F79BD0D98F");

    private static readonly Guid KsControlId =
        new("28F54685-06FD-11D2-B27A-00A0C9223196");

    private static readonly Guid BluetoothAudioPropertySet =
        new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    private static readonly PropertyKey DeviceContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2);

    /// <summary>Both flows a device container can expose endpoints in.</summary>
    private static readonly DataFlow[] EndpointFlows = [DataFlow.Render, DataFlow.Capture];

    /// <summary>Lists every audio endpoint container, including disconnected Bluetooth devices.</summary>
    /// <returns>
    ///     One entry per readable nonempty container GUID from both flows and all endpoint states,
    ///     ordered by container. Includes non-Bluetooth endpoints; join with BluetoothDevice.Container
    ///     to identify Bluetooth audio. IDs use the same normalized GUID form as that property.
    /// </returns>
    /// <exception cref="COMException">Core Audio could not enumerate the endpoints.</exception>
    public static IReadOnlyList<BluetoothAudioContainer> ListBluetoothAudioContainers()
    {
        var groups = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        ForEachEndpoint(Enumerator(), endpoint =>
        {
            if (TryReadContainer(endpoint) is { } container)
            {
                var active = endpoint.GetState(out var state) >= 0
                             && state == DeviceStateActive;
                groups[container] = groups.GetValueOrDefault(container) || active;
            }

            return false;
        });
        return groups.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new BluetoothAudioContainer(entry.Key, entry.Value))
            .ToArray();
    }

    /// <summary>Requests a paired Bluetooth audio device's connection state without removing its pairing.</summary>
    /// <param name="containerId">
    ///     The container identifier from
    ///     <see cref="ListBluetoothAudioContainers" />.
    /// </param>
    /// <param name="connect">True to connect, false to disconnect.</param>
    /// <exception cref="ArgumentException"><paramref name="containerId" /> is null or empty.</exception>
    /// <exception cref="COMException">
    ///     Core Audio enumeration failed, or the last matching endpoint attempt failed with a COM error.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The container has no audio endpoint, or none of its endpoints accepted the request.
    /// </exception>
    /// <exception cref="InvalidCastException">The last matching endpoint lacked the required topology or KS interface.</exception>
    /// <remarks>
    ///     Synchronously tries matching endpoints until one accepts; otherwise propagates the last
    ///     attempt's failure. Temporary COM interfaces are released before return. Acceptance is not
    ///     observed connectivity; use a later snapshot. Run on a worker thread. A nonempty invalid GUID
    ///     matches no container and is reported as InvalidOperationException.
    /// </remarks>
    public static void SetBluetoothAudioConnection(string containerId, bool connect)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        var target = WindowsRadio.NormalizeContainer(containerId);
        var enumerator = Enumerator();
        Exception? last = null;
        var matched = false;
        var sent = ForEachEndpoint(enumerator, endpoint =>
        {
            if (!string.Equals(TryReadContainer(endpoint), target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matched = true;
            try
            {
                SendBluetoothAudioOneShot(enumerator, endpoint, connect);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                return false;
            }
        });
        if (sent)
        {
            return;
        }

        if (last is not null)
        {
            ExceptionDispatchInfo.Throw(last);
        }

        throw new InvalidOperationException(matched
            ? "No endpoint accepted the Bluetooth audio request."
            : "The Bluetooth device has no audio endpoint.");
    }

    /// <summary>Visits every endpoint of both flows in any state, releasing each after its visit.</summary>
    /// <returns>True when a visit stopped the walk by returning true.</returns>
    private static bool ForEachEndpoint(IMMDeviceEnumerator enumerator, Func<IMMDevice, bool> visit)
    {
        foreach (var flow in EndpointFlows)
        {
            IMMDeviceCollection? collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(ForgetEnumeratorIfGone(
                    enumerator,
                    enumerator.EnumAudioEndpoints(flow, DeviceStateAll, out collection)));
                if (collection is null)
                {
                    continue;
                }

                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                for (var index = 0u; index < count; index++)
                {
                    IMMDevice? endpoint = null;
                    try
                    {
                        if (collection.Item(index, out endpoint) >= 0 && endpoint is not null
                                                                      && visit(endpoint))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        Release(endpoint);
                    }
                }
            }
            finally
            {
                Release(collection);
            }
        }

        return false;
    }

    private static string? TryReadContainer(IMMDevice endpoint)
    {
        var container = ReadStringProperty(
            endpoint,
            DeviceContainerIdKey,
            static value => value.GuidValue is { } id ? WindowsRadio.NormalizeContainer(id) : null);
        return string.IsNullOrEmpty(container) ? null : container;
    }

    private static void SendBluetoothAudioOneShot(
        IMMDeviceEnumerator enumerator,
        IMMDevice endpoint,
        bool connect)
    {
        IDeviceTopology? topology = null;
        IConnector? connector = null;
        IMMDevice? adapter = null;
        IKsControl? control = null;
        try
        {
            Marshal.ThrowExceptionForHR(Activate(endpoint, DeviceTopologyId, out topology));
            if (topology is null)
            {
                throw new InvalidCastException("The endpoint does not expose IDeviceTopology.");
            }

            Marshal.ThrowExceptionForHR(topology.GetConnector(0, out connector));
            if (connector is null)
            {
                throw new InvalidOperationException("The endpoint has no topology connector.");
            }

            Marshal.ThrowExceptionForHR(connector.GetDeviceIdConnectedTo(out var adapterId));
            if (string.IsNullOrEmpty(adapterId))
            {
                throw new InvalidOperationException("The endpoint connector has no adapter device.");
            }

            Marshal.ThrowExceptionForHR(ForgetEnumeratorIfGone(enumerator, enumerator.GetDevice(adapterId, out adapter)));
            if (adapter is null)
            {
                throw new InvalidOperationException("The audio adapter could not be opened.");
            }

            Marshal.ThrowExceptionForHR(Activate(adapter, KsControlId, out control));
            if (control is null)
            {
                throw new InvalidCastException("The audio adapter does not expose IKsControl.");
            }

            var property = new KsProperty
            {
                Set = BluetoothAudioPropertySet,
                Id = connect ? 0u : 1u,
                Flags = KsPropertyTypeGet
            };
            Marshal.ThrowExceptionForHR(control.KsProperty(
                ref property,
                (uint)Marshal.SizeOf<KsProperty>(),
                0,
                0,
                out _));
        }
        finally
        {
            Release(control);
            Release(adapter);
            Release(connector);
            Release(topology);
        }
    }

    /// <summary>One device container that exposes Core Audio endpoints.</summary>
    /// <param name="Container">
    ///     The container identifier, which is what ties an audio endpoint back
    ///     to the Bluetooth device it belongs to.
    /// </param>
    /// <param name="Active">
    ///     Whether any endpoint returned DeviceStateActive. False also covers unreadable endpoint
    ///     state and does not independently prove a Bluetooth device is disconnected.
    /// </param>
    public readonly record struct BluetoothAudioContainer(string Container, bool Active);
}
