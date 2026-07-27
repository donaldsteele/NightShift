using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NightShift.Desktop.Services;

/// <summary>
/// Handing a path or URL to the OS shell: "click the directory to open it in Explorer" (plan.md
/// §9.1), <c>Open log</c>, <c>Open transcript file</c> (§9.3), <c>Open app data folder</c> (§9.2
/// Advanced), and the preflight fixes that open the plan or settings file (§7).
/// </summary>
/// <remarks>
/// Behind an interface purely so tests can assert "the view model asked to open <em>this</em> path"
/// without a shell actually launching Explorer, a text editor or a browser on the build agent.
/// </remarks>
public interface IShellLauncher
{
    /// <summary>Opens a file or directory with its default handler. False when it could not be opened.</summary>
    bool OpenPath(string path);

    /// <summary>
    /// Opens the containing folder with <paramref name="path"/> selected, falling back to opening
    /// the folder itself where the platform cannot select.
    /// </summary>
    bool RevealInFileManager(string path);

    /// <summary>Opens an <c>http</c>/<c>https</c> URL in the default browser.</summary>
    bool OpenUrl(string url);
}

/// <summary>The real launcher: <see cref="Process"/> with <c>UseShellExecute</c>.</summary>
public sealed class ShellLauncher : IShellLauncher
{
    readonly ILogger<ShellLauncher> _logger;

    public ShellLauncher(ILogger<ShellLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _logger.LogWarning("Cannot open {Path}: it does not exist.", path);
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && (File.Exists(path) || Directory.Exists(path)))
        {
            // /select, needs the path as a single argument and Explorer is famously literal about it.
            return Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { "/select,", Path.GetFullPath(path) },
            });
        }

        var container = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        return container is not null && OpenPath(container);
    }

    public bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Refusing to open {Url}: only http and https are allowed.", url);
            return false;
        }

        return Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Could not launch {Target} through the shell.", startInfo.FileName);
            return false;
        }
    }
}
