namespace NightShift.Core.Tests;

/// <summary>Throwaway directory for a single test, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nightshift-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }
}
