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
    System = 1
}

/// <summary>Owns a Windows power-request handle and its diagnostic string. Dispose releases both.</summary>
/// <remarks>Windows may limit battery-powered requests; explicit user sleep still wins. Methods are thread-safe.</remarks>
public sealed class WindowsPowerRequest : IDisposable
{
    private readonly IPowerRequestApi _api;
    private readonly object _gate = new();
    private readonly int _kind;
    private readonly string _reason;
    private RequestHandle? _handle;
    private bool _held, _disposed;

    /// <summary>Creates an inert owner; no Windows request exists until Acquire.</summary>
    /// <param name="reason">Diagnostic text shown by Windows power-request tools.</param>
    /// <param name="kind">The idle transition to hold.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind" /> is not Display or System.</exception>
    public WindowsPowerRequest(string reason, WindowsPowerRequestKind kind = WindowsPowerRequestKind.System)
        : this(reason, kind, new NativePowerRequestApi())
    {
    }

    internal WindowsPowerRequest(string reason, WindowsPowerRequestKind kind, IPowerRequestApi api)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (kind is not WindowsPowerRequestKind.Display and not WindowsPowerRequestKind.System)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _reason = reason;
        _kind = (int)kind;
        _api = api;
    }

    /// <summary>Whether Windows accepted an acquisition that this owner has not released or disposed.</summary>
    /// <remarks>This tracks the handle's request, not proof that Windows will prevent the transition.</remarks>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>Closes the kernel request and releases the reason buffer. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle?.Dispose();
            _handle = null;
            _held = false;
        }
    }

    /// <summary>Sets the request once. Repeated successful acquisition is inert; native failures throw Win32Exception.</summary>
    /// <exception cref="ObjectDisposedException">This owner has already been disposed.</exception>
    /// <exception cref="Win32Exception">Creating or setting the request failed; IsHeld remains false.</exception>
    public void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held)
            {
                return;
            }

            _handle ??= new RequestHandle(_api, _reason);
            if (!_api.Set(_handle.DangerousGetHandle(), _kind))
            {
                throw new Win32Exception(_api.LastError);
            }

            _held = true;
        }
    }

    /// <summary>Clears the request once. A native failure throws and keeps IsHeld true.</summary>
    /// <remarks>Does nothing when not held, including after disposal. A failure is not retried automatically.</remarks>
    /// <exception cref="Win32Exception">Windows refused to clear the outstanding request.</exception>
    public void Release()
    {
        lock (_gate)
        {
            if (!_held)
            {
                return;
            }

            if (!_api.Clear(_handle!.DangerousGetHandle(), _kind))
            {
                throw new Win32Exception(_api.LastError);
            }

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
                if (IsInvalid)
                {
                    throw new Win32Exception(api.LastError);
                }
            }
            catch
            {
                Marshal.FreeHGlobal(_reason);
                _reason = 0;
                throw;
            }
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

/// <summary>Native power-request boundary; the owner retains the handle and reason buffer until Close.</summary>
internal interface IPowerRequestApi
{
    /// <summary>Win32 error from the immediately preceding failed native operation.</summary>
    int LastError { get; }
    /// <summary>Creates a kernel power-request handle borrowing the reason string.</summary>
    /// <param name="reason">NUL-terminated UTF-16 diagnostic string kept alive until the handle is closed.</param>
    /// <returns>An owned request handle, or a zero/invalid handle with LastError set.</returns>
    nint Create(nint reason);
    /// <summary>Adds one native request count of the selected kind.</summary>
    /// <param name="request">Live request handle owned by the caller.</param>
    /// <param name="kind">Native POWER_REQUEST_TYPE value.</param>
    /// <returns>True when accepted; false with LastError on refusal.</returns>
    bool Set(nint request, int kind);
    /// <summary>Clears one matching request count.</summary>
    /// <param name="request">Live request handle with a previously accepted request.</param>
    /// <param name="kind">Same native request type used for Set.</param>
    /// <returns>True when accepted; false with LastError while ownership remains unchanged.</returns>
    bool Clear(nint request, int kind);
    /// <summary>Closes the handle, releasing all outstanding request counts.</summary>
    /// <param name="request">Handle to release once; its reason buffer can be freed after this call.</param>
    void Close(nint request);
}

internal sealed partial class NativePowerRequestApi : IPowerRequestApi
{
    /// <inheritdoc />
    public nint Create(nint reason)
    {
        ReasonContext context = new() { Version = 0, Flags = 1, LocalizedReasonModuleOrSimpleString = reason };
        return PowerCreateRequest(in context);
    }

    /// <inheritdoc />
    public bool Set(nint request, int kind)
    {
        return PowerSetRequest(request, kind);
    }

    /// <inheritdoc />
    public bool Clear(nint request, int kind)
    {
        return PowerClearRequest(request, kind);
    }

    /// <inheritdoc />
    public void Close(nint request)
    {
        CloseHandle(request);
    }

    /// <inheritdoc />
    public int LastError => Marshal.GetLastPInvokeError();

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

    /// <summary>
    ///     REASON_CONTEXT declared through its detailed arm, the larger union member, so the managed size
    ///     matches the native one on both bitnesses. The simple string shares the first field's offset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReasonContext
    {
        internal uint Version, Flags;
        internal nint LocalizedReasonModuleOrSimpleString;
        internal uint LocalizedReasonId, ReasonStringCount;
        internal nint ReasonStrings;
    }
}
