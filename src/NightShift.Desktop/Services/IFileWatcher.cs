using Microsoft.Extensions.Logging;

namespace NightShift.Desktop.Services;

/// <summary>
/// Tells a view model that one file changed on disk.
/// </summary>
/// <remarks>
/// Behind an interface for the same reason <see cref="IShellLauncher"/> is: so a test can raise
/// "the file changed" without a disk, an OS watcher, or a timing race on a build agent.
/// </remarks>
public interface IFileWatcher : IDisposable
{
    /// <summary>Starts watching <paramref name="path"/>, replacing any previous target.</summary>
    void Watch(string path);

    /// <summary>Stops watching. Safe to call when nothing is being watched.</summary>
    void Stop();

    /// <summary>Raised on an arbitrary thread when the watched file may have changed.</summary>
    event EventHandler? Changed;
}

/// <summary>The real watcher, over <see cref="FileSystemWatcher"/>.</summary>
/// <remarks>
/// <para>
/// <b>It watches the directory, filtered to the file name, not the file.</b> A watcher bound to a
/// file stops reporting the moment that file is replaced by a rename — which is exactly how
/// <c>AtomicFile</c> writes (temp file, then <c>File.Move</c>) and exactly what <c>git checkout</c>
/// does. <c>NotifyFilters.FileName</c> is what catches the rename landing.
/// </para>
/// <para>
/// It reports "something happened" and nothing more. Deciding whether the contents actually differ
/// is the view model's job, because only the view model knows what it last wrote.
/// </para>
/// </remarks>
public sealed class FileSystemFileWatcher : IFileWatcher
{
    readonly ILogger<FileSystemFileWatcher> _logger;

    FileSystemWatcher? _watcher;

    public FileSystemFileWatcher(ILogger<FileSystemFileWatcher> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public event EventHandler? Changed;

    public void Watch(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Stop();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var name = Path.GetFileName(path);

        if (directory is null || name.Length == 0 || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            watcher.Changed += Raise;
            watcher.Created += Raise;
            watcher.Deleted += Raise;
            watcher.Renamed += Raise;

            // A watcher that dies takes the conflict warning with it, so say so rather than
            // leaving the window silently unprotected.
            watcher.Error += (_, e) =>
                _logger.LogWarning(e.GetException(), "The plan file watcher stopped.");

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Watching is a convenience. Failing to watch must never stop the window opening.
            _logger.LogWarning(ex, "Could not watch {Path} for changes.", path);
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;

        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void Dispose() => Stop();

    void Raise(object? sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
