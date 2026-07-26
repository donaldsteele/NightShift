namespace NightShift.Core.Configuration;

/// <summary>
/// Resolves every file and directory NightShift owns, rooted at a single app-data directory.
/// Layout is fixed by the implementation plan §8:
/// <code>
/// NightShift/
/// ├─ settings.json
/// ├─ state.json
/// ├─ logs/nightshift-.log
/// └─ runs/
///    ├─ index.jsonl
///    └─ &lt;runId&gt;.log
/// </code>
/// The root is injectable so tests can point at a temp directory instead of the real profile.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Directory name created under <c>%APPDATA%</c>.</summary>
    public const string AppFolderName = "NightShift";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// The default location: <c>%APPDATA%\NightShift</c> on Windows, the XDG/`~/.config`
    /// equivalent elsewhere.
    /// </summary>
    public static AppPaths CreateDefault() => new(
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            AppFolderName));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string StateFile => Path.Combine(Root, "state.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Serilog rolling-file template. Serilog splices the date in before the extension, so this
    /// produces <c>nightshift-20260726.log</c>.
    /// </summary>
    public string LogFileTemplate => Path.Combine(LogsDirectory, "nightshift-.log");

    public string RunsDirectory => Path.Combine(Root, "runs");

    public string RunIndexFile => Path.Combine(RunsDirectory, "index.jsonl");

    public string RunTranscriptFile(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Path.Combine(RunsDirectory, runId + ".log");
    }

    /// <summary>Creates every directory in the layout. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RunsDirectory);
    }
}
