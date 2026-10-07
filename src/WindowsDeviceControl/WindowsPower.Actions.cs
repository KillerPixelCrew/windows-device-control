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
    SignOut
}

public static partial class WindowsPower
{
    private static readonly IPowerActionApi PowerActionApi = new NativePowerActionApi();

    /// <summary>Requests standby or hibernation off-thread; completes when Windows returns, normally after resume.</summary>
    /// <param name="hibernate">True requests hibernation; false requests standby.</param>
    /// <param name="cancellationToken">Cancels admission only. A dispatched suspend cannot be cancelled.</param>
    /// <returns>A task completing after the native call returns, normally after resume.</returns>
    /// <exception cref="Win32Exception">Windows refused the suspend request.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurred before dispatch.</exception>
    public static Task SuspendAsync(bool hibernate, CancellationToken cancellationToken = default)
    {
        return SuspendAsync(hibernate, PowerActionApi, cancellationToken);
    }

    internal static Task SuspendAsync(bool hibernate, IPowerActionApi api, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            api.Suspend(hibernate);
        }, cancellationToken);
    }

    /// <summary>Requests shutdown, restart or sign-out through the system tool, without an interactive window.</summary>
    /// <param name="action">The explicitly requested operation.</param>
    /// <param name="cancellationToken">Cancels before dispatch or waiting; it does not undo a dispatched operation.</param>
    /// <returns>Completes when the tool accepts the request, not when Windows finishes the transition.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action" /> is undefined.</exception>
    /// <exception cref="Win32Exception">The system tool could not start or returned a nonzero exit code.</exception>
    /// <exception cref="InvalidOperationException">The process could not be created.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurred before dispatch or while waiting.</exception>
    /// <remarks>Uses the absolute system-directory shutdown.exe path; applications are not forcibly closed.</remarks>
    public static Task RequestActionAsync(WindowsPowerAction action,
        CancellationToken cancellationToken = default)
    {
        return RequestActionAsync(action, PowerActionApi, cancellationToken);
    }

    internal static async Task RequestActionAsync(WindowsPowerAction action, IPowerActionApi api,
        CancellationToken cancellationToken)
    {
        var start = ActionStartInfo(action);
        cancellationToken.ThrowIfCancellationRequested();
        var exitCode = await api.RunToolAsync(start, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new Win32Exception(exitCode, "Windows refused the power request.");
        }
    }

    internal static ProcessStartInfo ActionStartInfo(WindowsPowerAction action)
    {
        return new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "shutdown.exe"), action switch
                {
                    WindowsPowerAction.Shutdown => "/s /t 0",
                    WindowsPowerAction.Restart => "/r /t 0",
                    WindowsPowerAction.SignOut => "/l",
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                })
            { UseShellExecute = false, CreateNoWindow = true };
    }

    private sealed class NativePowerActionApi : IPowerActionApi
    {
        /// <inheritdoc />
        public bool Suspend(bool hibernate)
        {
            if (!SetSuspendState(hibernate, false, false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return true;
        }

        /// <inheritdoc />
        public async Task<int> RunToolAsync(ProcessStartInfo start, CancellationToken cancellationToken)
        {
            using var process = Process.Start(start) ??
                                throw new InvalidOperationException("Windows power tool did not start.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}

/// <summary>Boundary for native suspend and system-tool dispatch; neither operation reverses an accepted transition.</summary>
internal interface IPowerActionApi
{
    /// <summary>Requests standby or hibernation synchronously.</summary>
    /// <param name="hibernate">True selects hibernation; false selects standby.</param>
    /// <returns>True when the native call returns; refusals throw Win32Exception, normally before any transition.</returns>
    bool Suspend(bool hibernate);
    /// <summary>Starts a system tool and waits for its exit.</summary>
    /// <param name="start">Absolute executable and arguments prepared by the caller.</param>
    /// <param name="cancellationToken">Cancels waiting without terminating a dispatched process.</param>
    /// <returns>The process exit code, with start failures or cancellation propagated.</returns>
    Task<int> RunToolAsync(ProcessStartInfo start, CancellationToken cancellationToken);
}
