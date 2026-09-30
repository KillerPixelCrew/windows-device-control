using System;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace WindowsDeviceControl;

/// <summary>Owns one local audio preview through Windows Media Foundation and the default audio route.</summary>
public sealed class AudioFilePreview : IDisposable
{
    private MediaPlayer? _player;
    private bool _disposed;

    /// <summary>Raised on the media callback thread when the file or codec cannot be played.</summary>
    public event Action<string>? Failed;

    /// <summary>Stops the preceding preview and starts a local file without altering system volume.</summary>
    /// <param name="path">An absolute path to an existing audio file.</param>
    /// <exception cref="ArgumentException">The path is not absolute or does not exist.</exception>
    public void Play(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new ArgumentException("An existing absolute audio path is required.", nameof(path));
        }

        Stop();
        var player = new MediaPlayer { AutoPlay = false };
        _player = player;
        player.MediaFailed += (_, args) =>
        {
            // A queued failure from a retired preview cannot replace the next preview's status.
            if (!ReferenceEquals(_player, player))
            {
                return;
            }

            try
            {
                Failed?.Invoke(args.ErrorMessage);
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
    public void Stop()
    {
        var player = _player;
        _player = null;
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
