using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>The Windows idle transition held by a power request.</summary>
public enum WindowsPowerRequestKind
{
    /// <summary>Keeps the display on.</summary>
    Display = 0,
    /// <summary>Blocks automatic sleep while allowing the display to time out.</summary>
    System = 1,
}

/// <summary>Owns a Windows power-request handle and its diagnostic string. Dispose releases both.</summary>
/// <remarks>Windows may limit battery-powered requests; explicit user sleep still wins. Methods are thread-safe.</remarks>
public sealed class WindowsPowerRequest : IDisposable
{
    private readonly object _gate = new();
    private readonly string _reason;
    private readonly int _kind;
    private readonly IPowerRequestApi _api;
    private RequestHandle? _handle;
    private bool _held, _disposed;

    /// <summary>Creates an inert owner; no Windows request exists until Acquire.</summary>
    /// <param name="reason">Diagnostic text shown by Windows power-request tools.</param>
    /// <param name="kind">The idle transition to hold.</param>
    public WindowsPowerRequest(string reason, WindowsPowerRequestKind kind = WindowsPowerRequestKind.System)
        : this(reason, kind, new NativePowerRequestApi()) { }

    internal WindowsPowerRequest(string reason, WindowsPowerRequestKind kind, IPowerRequestApi api)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (kind is not WindowsPowerRequestKind.Display and not WindowsPowerRequestKind.System)
        { throw new ArgumentOutOfRangeException(nameof(kind)); }
        _reason = reason; _kind = (int)kind; _api = api;
    }

    /// <summary>Whether this owner has a confirmed outstanding request.</summary>
    public bool IsHeld { get { lock (_gate) { return _held; } } }

    /// <summary>Sets the request once. Repeated successful acquisition is inert; native failures throw Win32Exception.</summary>
    public void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held) { return; }
            _handle ??= new RequestHandle(_api, _reason);
            if (!_api.Set(_handle.DangerousGetHandle(), _kind)) { throw new Win32Exception(_api.LastError); }
            _held = true;
        }
    }

    /// <summary>Clears the request once. A native failure throws and keeps IsHeld true.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (!_held) { return; }
            if (!_api.Clear(_handle!.DangerousGetHandle(), _kind)) { throw new Win32Exception(_api.LastError); }
            _held = false;
        }
    }

    /// <summary>Closes the kernel request and releases the reason buffer. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _handle?.Dispose();
            _handle = null;
            _held = false;
        }
    }

    private sealed class RequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly IPowerRequestApi _api;
        private nint _reason;
        internal RequestHandle(IPowerRequestApi api, string reason) : base(true)
        {
            _api = api;
            _reason = Marshal.StringToHGlobalUni(reason);
            try
            {
                SetHandle(api.Create(_reason));
                if (IsInvalid) { throw new Win32Exception(api.LastError); }
            }
            catch { Marshal.FreeHGlobal(_reason); _reason = 0; throw; }
        }
        protected override bool ReleaseHandle()
        {
            _api.Close(handle);
            Marshal.FreeHGlobal(_reason);
            _reason = 0;
            return true;
        }
    }
}

internal interface IPowerRequestApi
{
    nint Create(nint reason);
    bool Set(nint request, int kind);
    bool Clear(nint request, int kind);
    void Close(nint request);
    int LastError { get; }
}

internal sealed partial class NativePowerRequestApi : IPowerRequestApi
{
    public nint Create(nint reason)
    {
        ReasonContext context = new() { Version = 0, Flags = 1, SimpleReasonString = reason };
        return PowerCreateRequest(in context);
    }
    public bool Set(nint request, int kind) => PowerSetRequest(request, kind);
    public bool Clear(nint request, int kind) => PowerClearRequest(request, kind);
    public void Close(nint request) => CloseHandle(request);
    public int LastError => Marshal.GetLastPInvokeError();

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        internal uint Version, Flags;
        internal nint SimpleReasonString;
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(in ReasonContext context);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint request, int kind);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint request, int kind);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
