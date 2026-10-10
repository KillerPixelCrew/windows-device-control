using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Display;
using Windows.Devices.Enumeration;

namespace WindowsDeviceControl;

public static partial class DisplayLayouts
{
    /// <summary>Reads active CCD placement and connected monitor interfaces, including Windows-disabled displays.</summary>
    /// <returns>
    ///     One detached observation per exact monitor interface. Connected interfaces omitted by a CCD identity
    ///     query are included as available and inactive; only CCD supplies an active mode or desktop position.
    /// </returns>
    /// <remarks>
    ///     Synchronous and read-only; run on a worker. Monitor interface enumeration and lookup share a five-second
    ///     budget. If that budget expires, completed monitor reads and the CCD observation are retained. This method
    ///     is intended for inventory and explicit editor refresh, not high-rate topology polling. Windows still
    ///     validates every layout apply against the current native paths.
    /// </remarks>
    /// <exception cref="System.ComponentModel.Win32Exception">The initial CCD query failed.</exception>
    /// <exception cref="COMException">Windows refused monitor-interface discovery.</exception>
    public static DisplayArrangement ObserveConnected()
    {
        var observed = Observe();
        var targets = new List<DisplayTargetObservation>(observed.Targets);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        try
        {
            var interfaces = DeviceInformation.FindAllAsync(DisplayMonitor.GetDeviceSelector())
                .WaitWinRt(deadline.Token);
            foreach (var device in interfaces)
            {
                if (!device.IsEnabled)
                {
                    continue;
                }

                try
                {
                    var monitor = DisplayMonitor.FromInterfaceIdAsync(device.Id).WaitWinRt(deadline.Token);
                    if (monitor is null)
                    {
                        continue;
                    }

                    var adapter = monitor.DisplayAdapterId;
                    var target = new DisplayTargetIdentity(device.Id, null, null, monitor.DisplayName,
                        adapter.LowPart, adapter.HighPart, monitor.DisplayAdapterTargetId)
                    {
                        EdidIdentity = DisplayEdid.ReadIdentity(device.Id)
                    };
                    AddObservation(targets, new DisplayTargetObservation(target, true, false, null));
                }
                catch (COMException)
                {
                    // A monitor can unplug between interface enumeration and its property read.
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }

        return new DisplayArrangement(targets, Fingerprint(targets), DateTimeOffset.UtcNow);
    }
}
