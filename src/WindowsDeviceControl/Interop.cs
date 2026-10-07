using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>Win32 error codes shared by the native callers, and the exceptions that preserve them.</summary>
internal static class Win32Error
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorFileNotFound = 2;
    internal const uint ErrorInvalidHandle = 6;
    internal const uint ErrorInvalidData = 13;
    internal const uint ErrorInsufficientBuffer = 122;
    internal const uint ErrorMoreData = 234;
    internal const uint ErrorNoMoreItems = 259;
    internal const uint ErrorNotFound = 1168;

    /// <summary>Throws the preserved native failure for a non-zero status.</summary>
    /// <param name="status">Native Win32 status; zero is success.</param>
    /// <param name="operation">Operation name included in the exception message.</param>
    /// <exception cref="Win32Exception"><paramref name="status" /> is nonzero.</exception>
    internal static void Check(uint status, string operation)
    {
        if (status != ErrorSuccess)
        {
            throw Failure(status, operation);
        }
    }

    /// <summary>The exception for one failed operation, carrying the native code unchanged.</summary>
    /// <param name="status">Native unsigned Win32 status.</param>
    /// <param name="operation">Operation name included in the message.</param>
    /// <returns>An exception preserving all status bits in NativeErrorCode.</returns>
    internal static Win32Exception Failure(uint status, string operation)
    {
        return new Win32Exception(unchecked((int)status), $"{operation} failed (status {status}).");
    }

    /// <summary>Throws the preserved WLAN failure for a non-zero status.</summary>
    /// <param name="operation">WLAN operation name included in the message.</param>
    /// <param name="status">Native WLAN status; zero is success.</param>
    /// <exception cref="Win32Exception"><paramref name="status" /> is nonzero.</exception>
    internal static void CheckWlan(string operation, uint status)
    {
        if (status != ErrorSuccess)
        {
            throw WlanFailure(operation, status);
        }
    }

    /// <summary>The WLAN form of <see cref="Failure" />, which keeps its own message wording.</summary>
    /// <param name="operation">WLAN operation name included in the message.</param>
    /// <param name="status">Native unsigned WLAN status.</param>
    /// <returns>An exception preserving all status bits in NativeErrorCode.</returns>
    internal static Win32Exception WlanFailure(string operation, uint status)
    {
        return new Win32Exception(unchecked((int)status), $"{operation} failed (Win32 {status}).");
    }
}

/// <summary>The kernel32 device calls shared by the backlight and storage readers.</summary>
internal static partial class Kernel32
{
    internal const uint OpenExisting = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}

/// <summary>Reads fixed-width native character buffers.</summary>
internal static unsafe class NativeText
{
    /// <summary>Reads a fixed-width native string, which is not always terminated.</summary>
    /// <param name="value">Pointer to the first character.</param>
    /// <param name="capacity">Maximum characters to read.</param>
    /// <returns>The string up to its terminator or capacity.</returns>
    internal static string ReadFixed(char* value, int capacity)
    {
        var length = 0;
        while (length < capacity && value[length] != '\0')
        {
            length++;
        }

        return new string(value, 0, length);
    }
}

/// <summary>Blocking completion for the WinRT operations behind this library's synchronous calls.</summary>
internal static class WinRt
{
    /// <summary>Waits for a WinRT operation on the calling thread.</summary>
    /// <param name="operation">WinRT operation to await synchronously.</param>
    /// <returns>The completed result; failures propagate without an AggregateException wrapper.</returns>
    /// <typeparam name="T">WinRT operation result type.</typeparam>
    internal static T WaitWinRt<T>(this IAsyncOperation<T> operation)
    {
        return operation.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Waits for a WinRT operation on the calling thread, cancelling it with the token.</summary>
    /// <param name="operation">WinRT operation to await synchronously.</param>
    /// <param name="cancellationToken">Cancellation forwarded to the WinRT operation.</param>
    /// <returns>The completed result; native failures and cancellation propagate to the caller.</returns>
    /// <typeparam name="T">WinRT operation result type.</typeparam>
    internal static T WaitWinRt<T>(this IAsyncOperation<T> operation, CancellationToken cancellationToken)
    {
        return operation.AsTask(cancellationToken).GetAwaiter().GetResult();
    }
}
