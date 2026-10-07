using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace WindowsDeviceControl;

/// <summary>Owns one local audio preview through Windows Media Foundation and the default audio route.</summary>
/// <remarks>
///     Single owner: <see cref="Play" />, <see cref="Stop" /> and <see cref="Dispose" /> are called from
///     one thread at a time. Only <see cref="Failed" /> arrives on another thread.
/// </remarks>
public sealed class AudioFilePreview : IDisposable
{
    private MediaPlayer? _player;
    private bool _disposed;

    /// <summary>Raised on the media callback thread when the file or codec cannot be played.</summary>
    /// <remarks>
    ///     Carries Windows' error class, the extended HRESULT and its message. Exceptions the
    ///     handler throws are swallowed, because they cannot cross the native media callback.
    /// </remarks>
    public event Action<AudioPreviewFailure>? Failed;

    /// <summary>Stops the preceding preview and starts a local file without altering system volume.</summary>
    /// <param name="path">An absolute path to an existing audio file.</param>
    /// <exception cref="ObjectDisposedException">This owner has already been disposed.</exception>
    /// <exception cref="ArgumentException">The path is not absolute or does not exist.</exception>
    /// <exception cref="COMException">
    ///     Windows media playback is unavailable, as on N editions without the Media Feature Pack,
    ///     or the file could not be opened as a media source.
    /// </exception>
    /// <remarks>
    ///     Return means playback was requested, not that decoding completed or sound was audible.
    ///     Later media failures arrive through <see cref="Failed" />. Invalid paths leave the preceding preview alone;
    ///     after validation the preceding preview is stopped before the new player is created.
    /// </remarks>
    public void Play(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new ArgumentException("An existing absolute audio path is required.", nameof(path));
        }

        Stop();
        var player = new MediaPlayer { AutoPlay = false };
        Volatile.Write(ref _player, player);
        player.MediaFailed += (_, args) =>
        {
            // A queued failure from a retired preview cannot replace the next preview's status.
            if (!ReferenceEquals(Volatile.Read(ref _player), player))
            {
                return;
            }

            try
            {
                Failed?.Invoke(new AudioPreviewFailure(
                    args.Error,
                    args.ExtendedErrorCode?.HResult ?? 0,
                    args.ErrorMessage ?? string.Empty));
            }
            catch
            {
                // A consumer's exception must not cross a native media callback boundary.
            }
        };
        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            player.Play();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Stops playback and releases its source.</summary>
    /// <remarks>
    ///     Does nothing when no player exists, including after disposal. The player is released even
    ///     when pausing fails; any pause or disposal exception propagates. Serialize with other owner calls.
    /// </remarks>
    public void Stop()
    {
        var player = Interlocked.Exchange(ref _player, null);
        if (player is null)
        {
            return;
        }

        try
        {
            player.Pause();
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>Releases the player and stops playback.</summary>
    /// <remarks>Idempotent. Future Play calls throw ObjectDisposedException; Stop remains harmless.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

/// <summary>Why an <see cref="AudioFilePreview" /> could not play.</summary>
/// <param name="Error">Windows' class of media failure.</param>
/// <param name="HResult">The extended error's HRESULT; zero when Windows supplied none.</param>
/// <param name="Message">Windows' description of the failure; empty when it supplied none.</param>
public readonly record struct AudioPreviewFailure(MediaPlayerError Error, int HResult, string Message);
