using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>One Windows device node and its Configuration Manager problem code.</summary>
/// <param name="InstanceId">Exact device instance identifier, suitable for a targeted PnP operation.</param>
/// <param name="ProblemCode">Configuration Manager problem code; 22 means disabled.</param>
public sealed record DeviceNode(string InstanceId, uint ProblemCode);

/// <summary>Read-only device-node enumeration and problem status through Configuration Manager.</summary>
/// <remarks>Calls are synchronous. Callers own device selection, writes, permission and recovery policy.</remarks>
public static partial class DeviceNodes
{
    /// <summary>Lists device nodes in one exact Windows setup class, including disabled devices.</summary>
    /// <param name="setupClass">The device setup class GUID, not an interface class.</param>
    /// <param name="presentOnly">True for connected nodes; false also includes registered disconnected nodes.</param>
    /// <returns>Device identifiers and current problem codes; nodes removed during enumeration are omitted.</returns>
    /// <exception cref="Win32Exception">Configuration Manager refused enumeration or status retrieval.</exception>
    /// <exception cref="InvalidOperationException">The list exceeded the bound or repeatedly changed size.</exception>
    public static IReadOnlyList<DeviceNode> Enumerate(Guid setupClass, bool presentOnly = true)
    {
        var filter = setupClass.ToString("B");
        // CM_GETIDLIST_FILTER_CLASS, optionally combined with CM_GETIDLIST_FILTER_PRESENT.
        var flags = presentOnly ? 0x300u : 0x200u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Check(GetListSize(out var length, filter, flags));
            if (length > 1048576)
            {
                throw new InvalidOperationException("Device identifier list exceeds the supported size.");
            }
            var buffer = new char[Math.Max(length, 1)];
            uint result;
            unsafe
            {
                fixed (char* pointer = buffer)
                {
                    result = GetList(filter, pointer, (uint)buffer.Length, flags);
                }
            }
            if (result == 0x1A) // CR_BUFFER_SMALL: a read-only enumeration race.
            {
                continue;
            }
            Check(result);
            var nodes = new List<DeviceNode>();
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ReadProblem(id) is { } problem)
                {
                    nodes.Add(new DeviceNode(id, problem));
                }
            }
            return nodes;
        }
        throw new InvalidOperationException("Device identifier list kept changing during enumeration.");
    }

    /// <summary>Reads the problem code of one exact registered device, including a disconnected node.</summary>
    /// <param name="instanceId">The complete Windows device instance identifier.</param>
    /// <returns>The problem code, or null when Windows no longer exposes the node's status.</returns>
    /// <exception cref="Win32Exception">Configuration Manager refused the status query.</exception>
    public static uint? ReadProblem(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var result = Locate(out var node, instanceId, flags: 1); // CM_LOCATE_DEVNODE_PHANTOM.
        if (result == 0x0D)
        {
            return null;
        }
        Check(result);
        result = GetStatus(out _, out var problem, node, flags: 0);
        if (result == 0x0D)
        {
            return null;
        }
        Check(result);
        return problem;
    }

    /// <summary>Tests whether Windows holds a configuration change for one exact node until restart.</summary>
    /// <param name="instanceId">The complete Windows device instance identifier.</param>
    /// <returns>True when the node reports DN_NEED_RESTART, for example after a disable of an in-use device.</returns>
    /// <exception cref="Win32Exception">Configuration Manager refused the status query.</exception>
    public static bool IsRestartPending(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var result = Locate(out var node, instanceId, flags: 1); // CM_LOCATE_DEVNODE_PHANTOM.
        if (result == 0x0D)
        {
            return false;
        }
        Check(result);
        result = GetStatus(out var status, out _, node, flags: 0);
        if (result == 0x0D)
        {
            return false;
        }
        Check(result);
        return (status & 0x01000000) != 0; // DN_NEED_RESTART.
    }

    /// <summary>Tests whether one exact device node remains registered, including disconnected nodes.</summary>
    /// <param name="instanceId">The complete Windows device instance identifier.</param>
    /// <returns>True for a present or phantom node; false when it is no longer registered.</returns>
    /// <exception cref="Win32Exception">Configuration Manager refused the lookup.</exception>
    public static bool IsRegistered(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var result = Locate(out _, instanceId, flags: 1);
        if (result == 0x0D)
        {
            return false;
        }
        Check(result);
        return true;
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)MapError(result, defaultError: 31));
        }
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetListSize(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetList(string filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint Locate(out uint node, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static partial uint GetStatus(out uint status, out uint problem, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_MapCrToWin32Err")]
    private static partial uint MapError(uint result, uint defaultError);
}
