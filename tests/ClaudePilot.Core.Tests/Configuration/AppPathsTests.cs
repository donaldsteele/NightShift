using ClaudePilot.Core.Configuration;

namespace ClaudePilot.Core.Tests.Configuration;

public sealed class AppPathsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "claudepilot-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void Layout_matches_the_documented_shape()
    {
        var paths = new AppPaths(_root);

        Assert.Equal(Path.Combine(_root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "state.json"), paths.StateFile);
        Assert.Equal(Path.Combine(_root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(_root, "logs", "claudepilot-.log"), paths.LogFileTemplate);
        Assert.Equal(Path.Combine(_root, "runs"), paths.RunsDirectory);
        Assert.Equal(Path.Combine(_root, "runs", "index.jsonl"), paths.RunIndexFile);
        Assert.Equal(Path.Combine(_root, "runs", "abc123.log"), paths.RunTranscriptFile("abc123"));
    }

    [Fact]
    public void EnsureCreated_is_idempotent()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.RunsDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_blank_root(string root) =>
        Assert.Throws<ArgumentException>(() => new AppPaths(root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
