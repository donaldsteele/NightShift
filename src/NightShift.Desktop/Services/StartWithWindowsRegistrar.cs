using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace NightShift.Desktop.Services;

/// <summary>
/// The "Start with Windows" setting (plan.md §9.2 Schedule, §9.4), backed by
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
/// <remarks>
/// <para>
/// HKCU rather than HKLM deliberately: the per-user key needs no elevation, and a pilot that
/// babysits one developer's project directory has no business starting for every account on the
/// machine.
/// </para>
/// <para>
/// Every method is a no-op that reports failure on a non-Windows OS rather than throwing. The rest
/// of NightShift builds and runs cross-platform, and a settings screen that crashes on Linux
/// because a toggle exists would be a worse bug than a toggle that does nothing there.
/// </para>
/// </remarks>
public sealed class StartWithWindowsRegistrar
{
    /// <summary>The classic per-user autorun key, relative to <c>HKEY_CURRENT_USER</c>.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name written under <see cref="RunKeyPath"/>.</summary>
    public const string DefaultValueName = "NightShift";

    readonly ILogger<StartWithWindowsRegistrar> _logger;
    readonly Func<string?> _resolveCommandLine;

    /// <param name="valueName">Overridable so a test never touches the real "NightShift" value.</param>
    /// <param name="resolveCommandLine">
    /// Overrides what gets written. Defaults to the quoted path of the running executable.
    /// </param>
    public StartWithWindowsRegistrar(
        ILogger<StartWithWindowsRegistrar> logger,
        string? valueName = null,
        Func<string?>? resolveCommandLine = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ValueName = string.IsNullOrWhiteSpace(valueName) ? DefaultValueName : valueName;
        _resolveCommandLine = resolveCommandLine ?? DefaultCommandLine;
    }

    /// <summary>The value name this instance reads and writes.</summary>
    public string ValueName { get; }

    /// <summary>False everywhere except Windows; the UI should disable the toggle when false.</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// The command line a fresh registration would write: the running executable's path, quoted.
    /// Null when the host cannot report its own path (a single-file publish edge case).
    /// </summary>
    public static string? DefaultCommandLine()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : $"\"{path}\"";
    }

    /// <summary>
    /// The registered command line, or null when NightShift is not registered (or this is not
    /// Windows). Reading never throws.
    /// </summary>
    public string? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadCore();
    }

    /// <summary>True when a value for <see cref="ValueName"/> exists under the Run key.</summary>
    public bool IsRegistered => Read() is { Length: > 0 };

    /// <summary>
    /// Writes the autorun value. Returns false — and logs — when it could not be written, so the
    /// caller can put the toggle back rather than lie about the state of the machine.
    /// </summary>
    public bool Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var commandLine = _resolveCommandLine();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            _logger.LogWarning("Could not determine this executable's path; not registering autorun.");
            return false;
        }

        return RegisterCore(commandLine);
    }

    /// <summary>Deletes the autorun value. Absent already counts as success.</summary>
    public bool Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return UnregisterCore();
    }

    /// <summary>Registers or unregisters to match <paramref name="enabled"/>.</summary>
    public bool Apply(bool enabled) => enabled ? Register() : Unregister();

    [SupportedOSPlatform("windows")]
    string? ReadCore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not read the autorun key.");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    bool RegisterCore(string commandLine)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _logger.LogWarning("The autorun key could not be opened for writing.");
                return false;
            }

            key.SetValue(ValueName, commandLine, RegistryValueKind.String);
            _logger.LogInformation("Registered autorun: {ValueName} = {CommandLine}", ValueName, commandLine);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not write the autorun key.");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    bool UnregisterCore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _logger.LogInformation("Removed the autorun registration {ValueName}.", ValueName);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not remove the autorun key.");
            return false;
        }
    }
}
