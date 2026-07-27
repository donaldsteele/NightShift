using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.Platform;

/// <summary>
/// The real <see cref="IFolderPicker"/> and <see cref="IFilePicker"/>: Avalonia's
/// <see cref="IStorageProvider"/>, reached through the main window (plan.md §9.2 Project, §9.2
/// Claude, and the two preflight fixes that pick a path).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="TopLevel"/> is resolved <b>per call</b> rather than injected. A picker is
/// registered while the host is being built, which is before any window exists; capturing a window
/// at construction time would be impossible, and capturing one later would keep a closed window
/// alive.
/// </para>
/// <para>
/// Every path out of here is "no selection" rather than an exception: a cancelled dialog, a missing
/// window, a platform with no storage provider. The view models read null as "the user said no",
/// which is the only safe reading.
/// </para>
/// </remarks>
public sealed class StorageProviderPathPicker : IFolderPicker, IFilePicker
{
    public async Task<string?> PickFolderAsync(
        string title,
        string? startIn = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return await UiThread.RunAsync(async () =>
        {
            if (MainTopLevel?.StorageProvider is not { CanPickFolder: true } storage)
            {
                return null;
            }

            var options = new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await SuggestedFolderAsync(storage, startIn).ConfigureAwait(true),
            };

            var picked = await storage.OpenFolderPickerAsync(options).ConfigureAwait(true);
            return LocalPath(picked.Count > 0 ? picked[0] : null);
        }).ConfigureAwait(false);
    }

    public async Task<string?> PickFileAsync(
        string title,
        string? startIn = null,
        IReadOnlyList<string>? patterns = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return await UiThread.RunAsync(async () =>
        {
            if (MainTopLevel?.StorageProvider is not { CanOpen: true } storage)
            {
                return null;
            }

            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation =
                    await SuggestedFolderAsync(storage, StartFolderOf(startIn)).ConfigureAwait(true),
            };

            if (patterns is { Count: > 0 })
            {
                options.FileTypeFilter =
                [
                    new FilePickerFileType("Matching files") { Patterns = [.. patterns] },
                    FilePickerFileTypes.All,
                ];
            }

            var picked = await storage.OpenFilePickerAsync(options).ConfigureAwait(true);
            return LocalPath(picked.Count > 0 ? picked[0] : null);
        }).ConfigureAwait(false);
    }

    /// <summary>The main window, or null before one exists and after it has gone.</summary>
    internal static TopLevel? MainTopLevel =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>A file path's containing directory, so a file picker opens where the file lives.</summary>
    static string? StartFolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    static async Task<IStorageFolder?> SuggestedFolderAsync(IStorageProvider storage, string? startIn)
    {
        if (string.IsNullOrWhiteSpace(startIn) || !Directory.Exists(startIn))
        {
            return null;
        }

        try
        {
            return await storage.TryGetFolderFromPathAsync(startIn).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A start location is a nicety. Losing it must not lose the dialog.
            return null;
        }
    }

    /// <summary>
    /// The on-disk path of a picked item, or null when it only exists behind a non-file URI (a
    /// content provider on a sandboxed platform). Settings hold real paths; a URI would be saved and
    /// then fail every later existence check.
    /// </summary>
    static string? LocalPath(IStorageItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var path = item.TryGetLocalPath();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}

/// <summary>
/// Runs an asynchronous piece of work on the UI thread and awaits its <em>result</em>.
/// </summary>
/// <remarks>
/// Deliberately built on <see cref="Dispatcher.Post(Action)"/> and a
/// <see cref="TaskCompletionSource{TResult}"/> rather than <c>InvokeAsync</c>. The dispatcher's
/// overload set makes <c>InvokeAsync(Func&lt;Task&lt;T&gt;&gt;)</c> ambiguous to read — one overload
/// unwraps the inner task and one does not — and a picker that returned a task instead of a path
/// would fail silently rather than loudly.
/// </remarks>
internal static class UiThread
{
    public static Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Dispatcher.UIThread.CheckAccess())
        {
            return work();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }
}
