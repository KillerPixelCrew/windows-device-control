using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace WindowsDeviceControl;

/// <summary>A kernel-observed process start, including creation sequence identities immune to PID reuse.</summary>
/// <param name="Identity">The exact image and creation time supplied by Windows.</param>
/// <param name="Sequence">The kernel's process creation sequence.</param>
/// <param name="ParentSequence">The creating process's creation sequence.</param>
public sealed record ProcessStartIdentity(ProcessImageIdentity Identity, ulong Sequence, ulong ParentSequence);

/// <summary>Owns a private, read-only ETW process-start watch. Requires Windows tracing permission.</summary>
/// <remarks>No command lines are delivered. Dispose stops only this watch's uniquely named trace session.</remarks>
public sealed class ProcessStartWatch : IAsyncDisposable
{
    private static readonly Guid Provider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private readonly TraceEventSession _session;
    private readonly Task _reader;
    private readonly Dictionary<uint, Start> _starts = [];
    private readonly List<(string Device, string Drive)> _drives = [];
    private readonly Action<ProcessStartIdentity> _started;
    private readonly int _sessionId;
    private bool _lost;

    /// <summary>Starts capturing before returning. Notifications run on a dedicated reader task.</summary>
    /// <param name="sessionId">The interactive session whose process starts are delivered.</param>
    /// <param name="started">Receives copied creation identities. The callback must not block or throw.</param>
    /// <exception cref="UnauthorizedAccessException">Windows refused tracing permission.</exception>
    public ProcessStartWatch(int sessionId, Action<ProcessStartIdentity> started)
    {
        ArgumentNullException.ThrowIfNull(started);
        _sessionId = sessionId;
        _started = started;
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var device = new StringBuilder(512);
            if (QueryDosDevice(drive[..2], device, device.Capacity) > 0)
            {
                _drives.Add((device.ToString(), drive[..2]));
            }
        }

        _session = new TraceEventSession("WindowsDeviceControl.ProcessStart." + Guid.NewGuid().ToString("N"));
        try
        {
            _session.Source.Dynamic.All += Read;
            // Process starts carry creation sequences; the first executable image-load event supplies
            // the exact image even when the process exited before a user-mode snapshot could open it.
            _session.EnableProvider(Provider, TraceEventLevel.Informational, 0x50);
            _reader = Task.Run(() => _session.Source.Process());
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <summary>Stops this private trace and joins its callback reader before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _session.Stop(noThrow: true);
            await _reader.ConfigureAwait(false);
        }
        finally
        {
            _session.Dispose();
        }
    }

    /// <summary>Requests delivery of this watch's buffered events without waiting for a buffer to fill.</summary>
    public void Flush()
    {
        _session.Flush();
    }

    private void Read(TraceEvent data)
    {
        if (data.ProviderGuid != Provider)
        {
            return;
        }

        // Missing start events could conceal a reused id or missing ancestor. Never infer through loss.
        if (_lost || _session.Source.EventsLost > 0)
        {
            _lost = true;
            return;
        }

        if ((int)data.ID == 1)
        {
            var id = Convert.ToUInt32(data.PayloadByName("ProcessID"));
            _starts.Remove(id);
            if (Convert.ToInt32(data.PayloadByName("SessionID")) != _sessionId
                || data.PayloadByName("ProcessSequenceNumber") is not { } sequence
                || data.PayloadByName("ParentProcessSequenceNumber") is not { } parent)
            {
                return;
            }

            if (Convert.ToUInt64(sequence) == 0)
            {
                return;
            }

            _starts[id] = new Start(Convert.ToUInt64(sequence), Convert.ToUInt64(parent),
                ((DateTime)data.PayloadByName("CreateTime")).ToUniversalTime());
        }
        else if ((int)data.ID == 5)
        {
            var id = Convert.ToUInt32(data.PayloadByName("ProcessID"));
            if (!_starts.TryGetValue(id, out var start) || start.Delivered
                || data.TimeStamp.ToUniversalTime() < start.Created
                || data.PayloadByName("ImageName") is not string image
                || !image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            image = image.Replace(@"\\", @"\", StringComparison.Ordinal);
            foreach (var drive in _drives)
            {
                if (image.StartsWith(drive.Device + @"\", StringComparison.OrdinalIgnoreCase))
                {
                    image = drive.Drive + image[drive.Device.Length..];
                    break;
                }
            }

            if (!Path.IsPathFullyQualified(image) || image.StartsWith(@"\Device\", StringComparison.Ordinal))
            {
                return;
            }

            _starts[id] = start with { Delivered = true };
            _started(new ProcessStartIdentity(new ProcessImageIdentity(id, start.Created, image),
                start.Sequence, start.ParentSequence));
        }
    }

    private sealed record Start(ulong Sequence, ulong ParentSequence, DateTime Created, bool Delivered = false);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int length);
}
