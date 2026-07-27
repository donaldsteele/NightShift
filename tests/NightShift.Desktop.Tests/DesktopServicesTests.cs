using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void TheFirstInstanceOwnsTheMutexAndTheSecondDoesNot()
    {
        var name = "NightShift.Tests." + Guid.NewGuid().ToString("n");

        using var first = new SingleInstanceGuard(name);
        Assert.True(first.IsFirstInstance);

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void ReleasingLetsTheNextLaunchThrough()
    {
        var name = "NightShift.Tests." + Guid.NewGuid().ToString("n");

        using (var first = new SingleInstanceGuard(name))
        {
            Assert.True(first.IsFirstInstance);
        }

        using var next = new SingleInstanceGuard(name);
        Assert.True(next.IsFirstInstance);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        var guard = new SingleInstanceGuard("NightShift.Tests." + Guid.NewGuid().ToString("n"));
        guard.Dispose();
        guard.Dispose();
    }
}

public class StartWithWindowsRegistrarTests
{
    static StartWithWindowsRegistrar Create(Func<string?>? commandLine = null) =>
        new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StartWithWindowsRegistrar>.Instance,
            "NightShift.Tests." + Guid.NewGuid().ToString("n")[..8],
            commandLine ?? (() => "\"C:\\fake\\NightShift.exe\""));

    [Fact]
    public void ItReportsWhetherThisPlatformSupportsIt()
    {
        Assert.Equal(OperatingSystem.IsWindows(), Create().IsSupported);
    }

    [Fact]
    public void OffWindowsEveryOperationIsANoOpRatherThanAThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var registrar = Create();

        Assert.Null(registrar.Read());
        Assert.False(registrar.IsRegistered);
        Assert.False(registrar.Register());
        Assert.False(registrar.Unregister());
    }

    [Fact]
    public void RegisterThenUnregisterRoundTripsThroughTheRunKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var registrar = Create();
        try
        {
            Assert.False(registrar.IsRegistered);

            Assert.True(registrar.Register());
            Assert.True(registrar.IsRegistered);
            Assert.Equal("\"C:\\fake\\NightShift.exe\"", registrar.Read());

            Assert.True(registrar.Unregister());
            Assert.False(registrar.IsRegistered);
        }
        finally
        {
            registrar.Unregister();
        }
    }

    [Fact]
    public void RegisteringWithNoResolvableExecutablePathFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var registrar = Create(() => null);

        Assert.False(registrar.Register());
        Assert.False(registrar.IsRegistered);
    }

    [Fact]
    public void ApplyIsRegisterOrUnregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var registrar = Create();
        try
        {
            Assert.True(registrar.Apply(true));
            Assert.True(registrar.IsRegistered);

            Assert.True(registrar.Apply(false));
            Assert.False(registrar.IsRegistered);
        }
        finally
        {
            registrar.Unregister();
        }
    }
}

public class ImmediateUiDispatcherTests
{
    [Fact]
    public async Task EverythingRunsInline()
    {
        var dispatcher = ImmediateUiDispatcher.Instance;
        var ran = 0;

        Assert.True(dispatcher.IsOnUiThread);

        dispatcher.Post(() => ran++);
        Assert.Equal(1, ran);

        await dispatcher.InvokeAsync(() => ran++);
        Assert.Equal(2, ran);

        Assert.Equal(7, await dispatcher.InvokeAsync(() => Task.FromResult(7)));
    }
}

public class ClaudeUserSettingsAutoModeEnvironmentStoreTests
{
    [Fact]
    public async Task WritingAlwaysSplicesInDefaultsAndKeepsUnrelatedKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nightshift-automode", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(path, """{ "model": "opus", "autoMode": { "other": 1 } }""");

            var store = new ClaudeUserSettingsAutoModeEnvironmentStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeUserSettingsAutoModeEnvironmentStore>.Instance,
                path);

            Assert.Null(await store.WriteAsync(["registry.internal"]));

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"$defaults\"", json, StringComparison.Ordinal);
            Assert.Contains("registry.internal", json, StringComparison.Ordinal);
            // Writing autoMode.environment must not discard the user's other settings.
            Assert.Contains("\"model\"", json, StringComparison.Ordinal);
            Assert.Contains("\"other\"", json, StringComparison.Ordinal);

            var read = await store.ReadAsync();
            // The sentinel is never surfaced to the editor — it cannot be deleted from the UI.
            Assert.Equal(["registry.internal"], read.Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ADuplicateDefaultsEntryIsNotWrittenTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nightshift-automode", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new ClaudeUserSettingsAutoModeEnvironmentStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeUserSettingsAutoModeEnvironmentStore>.Instance,
                path);

            await store.WriteAsync(["$defaults", "registry.internal", "$defaults"]);

            var json = await File.ReadAllTextAsync(path);
            var occurrences = json.Split("$defaults").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class TimeSpanTextTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(42 * 60, "42m")]
    [InlineData((2 * 60 + 14) * 60, "2h 14m")]
    [InlineData(3 * 24 * 3600 + 4 * 3600, "3d 04h")]
    public void DescribesDurationsThePlansWay(int seconds, string expected) =>
        Assert.Equal(expected, TimeSpanText.Describe(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ATimeAlreadyPassedReadsAsNowRatherThanANegativeCountdown()
    {
        var now = new DateTimeOffset(2026, 7, 26, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal("now", TimeSpanText.DescribeUntil(now.AddMinutes(-5), now));
        Assert.Equal("in 42m", TimeSpanText.DescribeUntil(now.AddMinutes(42), now));
    }
}
