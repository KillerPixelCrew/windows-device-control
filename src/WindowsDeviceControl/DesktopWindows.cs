namespace WindowsDeviceControl;

/// <summary>Requests addressed to another process's top-level window.</summary>
/// <remarks>Nothing here terminates a process; the window's owner decides what a request does.</remarks>
public static class DesktopWindows
{
    /// <summary>
    ///     Posts <c>WM_CLOSE</c> to a window, the same request as its close button, when it still belongs
    ///     to the process it was listed for.
    /// </summary>
    /// <param name="window">The window that was listed.</param>
    /// <param name="processId">The process that owned it when it was listed; a reused handle is left alone.</param>
    /// <returns>Whether the request was posted to that window.</returns>
    /// <remarks>
    ///     Posted, not sent, so a hung window cannot stall the caller. The application may ask the user to
    ///     confirm first or ignore the request.
    /// </remarks>
    public static bool RequestClose(nint window, uint processId)
    {
        if (window == 0 || User32.GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId)
        {
            return false;
        }

        return User32.PostMessageW(window, User32.WmClose, 0, 0);
    }
}
