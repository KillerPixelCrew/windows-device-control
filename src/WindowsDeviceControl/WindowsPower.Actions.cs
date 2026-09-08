using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDeviceControl;

/// <summary>Explicit Windows session termination requests.</summary>
public enum WindowsPowerAction
{
    /// <summary>Shut down Windows without forcing applications to close.</summary>
    Shutdown,
    /// <summary>Restart Windows without forcing applications to close.</summary>
    Restart,
    /// <summary>Sign out the calling user's session.</summary>
    SignOut,
}

public static partial class WindowsPower
{
    /// <summary>Requests standby or hibernation off-thread; completes when Windows returns, normally after resume.</summary>
    /// <param name="hibernate">True requests hibernation; false requests standby.</param>
    /// <param name="cancellationToken">Cancels admission only. A dispatched suspend cannot be cancelled.</param>
    /// <returns>A task that faults with Win32Exception on a native refusal.</returns>
    public static Task SuspendAsync(bool hibernate, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!SetSuspendState(hibernate, false, false))
            { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        }, cancellationToken);

    /// <summary>Requests shutdown, restart or sign-out through the system tool, without an interactive window.</summary>
    /// <param name="action">The explicitly requested operation.</param>
    /// <param name="cancellationToken">Cancels before dispatch or waiting; it does not undo a dispatched operation.</param>
    /// <returns>Completes when the tool accepts the request, not when Windows finishes the transition.</returns>
    public static async Task RequestActionAsync(WindowsPowerAction action, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo start = ActionStartInfo(action);
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows power tool did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) { throw new Win32Exception(process.ExitCode, "Windows refused the power request."); }
    }

    internal static ProcessStartInfo ActionStartInfo(WindowsPowerAction action) => new(
        Path.Combine(Environment.SystemDirectory, "shutdown.exe"), action switch
        {
            WindowsPowerAction.Shutdown => "/s /t 0",
            WindowsPowerAction.Restart => "/r /t 0",
            WindowsPowerAction.SignOut => "/l",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        })
    { UseShellExecute = false, CreateNoWindow = true };

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
