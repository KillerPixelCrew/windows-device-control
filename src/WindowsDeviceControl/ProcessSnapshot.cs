using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>A process creation identity and its parent at the time of a process snapshot.</summary>
/// <param name="Id">The process id.</param>
/// <param name="ParentId">The creating process id, which may already have exited or been reused.</param>
/// <param name="Name">The image filename, useful only for narrowing inspection.</param>
public sealed record ProcessSnapshotEntry(uint Id, uint ParentId, string Name);

/// <summary>A verified live process image in one interactive session.</summary>
/// <param name="Id">The process id.</param>
/// <param name="Started">The UTC creation time, distinguishing reused process ids.</param>
/// <param name="Image">The full image path.</param>
public sealed record ProcessImageIdentity(uint Id, DateTime Started, string Image);

/// <summary>An owned process handle retaining a creation identity even after exit, preventing id reuse.</summary>
public sealed class ProcessImageLease : IDisposable
{
    private readonly SafeProcessHandle _handle;

    internal ProcessImageLease(nint handle, ProcessImageIdentity identity)
    {
        _handle = new SafeProcessHandle(handle, ownsHandle: true);
        Identity = identity;
    }

    /// <summary>The image and creation time read through the retained handle.</summary>
    public ProcessImageIdentity Identity { get; }

    /// <summary>Releases the retained process handle without affecting the process.</summary>
    public void Dispose()
    {
        _handle.Dispose();
    }
}

/// <summary>Read-only process ancestry and exact image observations. No processes are started or retained.</summary>
public static partial class ProcessSnapshot
{
    /// <summary>Resolves an executable's physical path through an opened file, including parent junctions.</summary>
    /// <param name="image">The configured executable path.</param>
    /// <returns>The physical DOS path, or null when it cannot be opened or resolved.</returns>
    public static unsafe string? ResolveImagePath(string image)
    {
        try
        {
            using var file = File.OpenHandle(image, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new char[32768];
            fixed (char* pointer = buffer)
            {
                var length = GetFinalPathNameByHandleW(file, pointer, (uint)buffer.Length, 0);
                if (length == 0 || length >= buffer.Length)
                {
                    return null;
                }

                var path = new string(pointer, 0, (int)length);
                return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + path[8..]
                    : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads process ids and parent ids without opening every process.</summary>
    /// <returns>The snapshot, or an empty list if Windows refuses enumeration.</returns>
    public static IReadOnlyList<ProcessSnapshotEntry> Read()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new nint(-1))
        {
            return [];
        }

        try
        {
            List<ProcessSnapshotEntry> entries = [];
            NativeEntry entry = new() { Size = (uint)Marshal.SizeOf<NativeEntry>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    entries.Add(new ProcessSnapshotEntry(entry.Id, entry.ParentId, entry.Name));
                } while (Process32NextW(snapshot, ref entry));
            }

            return entries;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>Reads the exact image and creation time through one process handle.</summary>
    /// <param name="id">The process to inspect.</param>
    /// <param name="sessionId">The required interactive session.</param>
    /// <returns>Its identity, or null if it exited, belongs to another session or cannot be inspected.</returns>
    public static ProcessImageIdentity? Inspect(uint id, int sessionId)
    {
        using var lease = Retain(id, sessionId);
        return lease?.Identity;
    }

    /// <summary>Retains the exact process object so its id cannot be reused while observing descendants.</summary>
    /// <param name="id">The process to inspect.</param>
    /// <param name="sessionId">The required interactive session.</param>
    /// <returns>An owned lease, or null when the process cannot be verified.</returns>
    public static unsafe ProcessImageLease? Retain(uint id, int sessionId)
    {
        var process = OpenProcess(0x1000, false, id);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (!ProcessIdToSessionId(id, out var actualSession) || actualSession != sessionId)
            {
                return null;
            }

            var buffer = new char[32768];
            var length = (uint)buffer.Length;
            fixed (char* pointer = buffer)
            {
                if (!QueryFullProcessImageNameW(process, 0, pointer, ref length)
                    || !GetProcessTimes(process, out var created, out _, out _, out _))
                {
                    return null;
                }

                var lease = new ProcessImageLease(process, new ProcessImageIdentity(id,
                    DateTime.FromFileTimeUtc(created), new string(pointer, 0, (int)length)));
                process = 0;
                return lease;
            }
        }
        finally
        {
            if (process != 0)
            {
                CloseHandle(process);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeEntry
    {
        public uint Size;
        public uint Usage;
        public uint Id;
        public nuint Heap;
        public uint Module;
        public uint Threads;
        public uint ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref NativeEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref NativeEntry entry);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out int sessionId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* image, ref uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long created, out long exited, out long kernel,
        out long user);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);
}
