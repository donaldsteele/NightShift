namespace NightShift.Desktop.Services;

/// <summary>
/// Picking a directory (plan.md §9.2 Project, and the
/// <see cref="NightShift.Core.Preflight.PreflightFixCommand.SetProjectDirectory"/> fix).
/// </summary>
/// <remarks>
/// The real implementation is <c>IStorageProvider.OpenFolderPickerAsync</c>, which needs a
/// <see cref="Avalonia.Controls.TopLevel"/> and therefore cannot exist until a window does. Keeping
/// it behind an interface is what lets <c>SettingsViewModel</c> and <c>DashboardViewModel</c> be
/// tested without a UI, and lets the view supply the real thing at composition time.
/// </remarks>
public interface IFolderPicker
{
    /// <summary>
    /// Shows the picker and returns the chosen absolute path, or null when the user cancelled or no
    /// picker is available. Implementations must not throw for a cancelled dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="startIn">Directory to open at, when it still exists.</param>
    Task<string?> PickFolderAsync(
        string title,
        string? startIn = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Picking a file — the Claude Code executable (plan.md §9.2 Claude, and the
/// <see cref="NightShift.Core.Preflight.PreflightFixCommand.SetClaudeExecutablePath"/> fix).
/// </summary>
public interface IFilePicker
{
    /// <summary>
    /// Shows the picker and returns the chosen absolute path, or null when the user cancelled or no
    /// picker is available.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="startIn">Directory to open at, when it still exists.</param>
    /// <param name="patterns">Glob patterns to filter on, e.g. <c>claude.cmd</c>. Null means any file.</param>
    Task<string?> PickFileAsync(
        string title,
        string? startIn = null,
        IReadOnlyList<string>? patterns = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The placeholder registered before a window exists: every pick is answered "cancelled".
/// </summary>
/// <remarks>
/// Registered by <see cref="DesktopServiceCollectionExtensions.AddNightShiftDesktop"/> so the view
/// models can be resolved during startup and in the XAML designer. The view layer replaces these
/// registrations with <c>IStorageProvider</c>-backed implementations once a
/// <see cref="Avalonia.Controls.TopLevel"/> is available.
/// </remarks>
public sealed class UnavailablePathPicker : IFolderPicker, IFilePicker
{
    public Task<string?> PickFolderAsync(
        string title,
        string? startIn = null,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> PickFileAsync(
        string title,
        string? startIn = null,
        IReadOnlyList<string>? patterns = null,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
