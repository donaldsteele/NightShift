using NightShift.Core.Configuration;
using NightShift.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace NightShift.Core.Tests.Usage;

public sealed class CompositeUsageProviderTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    sealed class StubProvider(UsageSource source, Func<UsageSnapshot> factory) : IUsageProvider
    {
        public int Calls { get; private set; }

        /// <summary>Whether the composite forwarded a forced refresh, checked by its own test.</summary>
        public bool? LastForceRefresh { get; private set; }

        public UsageSource Source => source;

        public Task<UsageSnapshot> GetUsageAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastForceRefresh = forceRefresh;
            return Task.FromResult(factory());
        }
    }

    static UsageSnapshot Available(UsageSource source, double fiveHour, bool approximate = false) =>
        new(new UsageWindow(fiveHour, null), null, null, null, source, approximate, Now);

    static ISettingsStore Settings(UsageProviderPreference preference)
    {
        var store = Substitute.For<ISettingsStore>();
        store.Current.Returns(new PilotSettings { UsageProviderOrder = preference });
        return store;
    }

    static CompositeUsageProvider Create(ISettingsStore settings, params IUsageProvider[] providers) =>
        new(providers, settings, NullLogger<CompositeUsageProvider>.Instance);

    [Fact]
    public async Task A_forced_refresh_is_forwarded_to_the_provider()
    {
        // The composite owns no cache of its own, so "force" is only meaningful if it reaches the
        // provider that does.
        var oauth = new StubProvider(UsageSource.OAuth, () => Available(UsageSource.OAuth, 20));
        var composite = Create(Settings(UsageProviderPreference.OAuthThenCcusage), oauth);

        await composite.GetUsageAsync();
        Assert.False(oauth.LastForceRefresh);

        await composite.GetUsageAsync(forceRefresh: true);
        Assert.True(oauth.LastForceRefresh);
    }

    [Fact]
    public async Task Preferred_provider_wins_and_the_fallback_is_never_asked()
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => Available(UsageSource.OAuth, 33));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => Available(UsageSource.Ccusage, 99, true));

        var snapshot = await Create(Settings(UsageProviderPreference.OAuthThenCcusage), oauth, ccusage)
            .GetUsageAsync();

        Assert.Equal(UsageSource.OAuth, snapshot.Source);
        Assert.Equal(33, snapshot.FiveHour!.UtilizationPercent);
        Assert.Equal(0, ccusage.Calls);
    }

    [Fact]
    public async Task Falls_back_when_the_preferred_provider_is_unavailable()
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => UsageSnapshot.Unavailable("401", Now));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => Available(UsageSource.Ccusage, 42, true));

        var composite = Create(Settings(UsageProviderPreference.OAuthThenCcusage), oauth, ccusage);
        var snapshot = await composite.GetUsageAsync();

        Assert.Equal(UsageSource.Ccusage, snapshot.Source);
        Assert.True(snapshot.IsApproximate);
        Assert.Equal(1, ccusage.Calls);

        var health = composite.Health;
        Assert.Contains(health, h => h.Source == UsageSource.OAuth && !h.IsHealthy && h.LastReason == "401");
        Assert.Contains(health, h => h.Source == UsageSource.Ccusage && h.IsHealthy);
    }

    [Fact]
    public async Task Preference_order_is_honoured()
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => Available(UsageSource.OAuth, 10));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => Available(UsageSource.Ccusage, 20, true));

        var snapshot = await Create(Settings(UsageProviderPreference.CcusageThenOAuth), oauth, ccusage)
            .GetUsageAsync();

        Assert.Equal(UsageSource.Ccusage, snapshot.Source);
        Assert.Equal(0, oauth.Calls);
    }

    [Theory]
    [InlineData(UsageProviderPreference.OAuthOnly, UsageSource.Ccusage)]
    [InlineData(UsageProviderPreference.CcusageOnly, UsageSource.OAuth)]
    public async Task Single_provider_preferences_never_consult_the_other(
        UsageProviderPreference preference,
        UsageSource excluded)
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => UsageSnapshot.Unavailable("down", Now));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => UsageSnapshot.Unavailable("down", Now));

        var composite = Create(Settings(preference), oauth, ccusage);
        await composite.GetUsageAsync();

        var excludedStub = excluded == UsageSource.OAuth ? oauth : ccusage;
        Assert.Equal(0, excludedStub.Calls);
    }

    [Fact]
    public async Task All_providers_failing_yields_unavailable_with_every_reason()
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => UsageSnapshot.Unavailable("login expired", Now));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => UsageSnapshot.Unavailable("npx missing", Now));

        var snapshot = await Create(Settings(UsageProviderPreference.OAuthThenCcusage), oauth, ccusage)
            .GetUsageAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Equal(UsageSource.None, snapshot.Source);
        Assert.Contains("login expired", snapshot.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("npx missing", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_provider_is_contained_and_the_next_one_still_runs()
    {
        var oauth = new StubProvider(UsageSource.OAuth, () => throw new InvalidOperationException("bug"));
        var ccusage = new StubProvider(UsageSource.Ccusage, () => Available(UsageSource.Ccusage, 5, true));

        var composite = Create(Settings(UsageProviderPreference.OAuthThenCcusage), oauth, ccusage);
        var snapshot = await composite.GetUsageAsync();

        Assert.Equal(UsageSource.Ccusage, snapshot.Source);
        Assert.Contains(composite.Health, h => h.Source == UsageSource.OAuth && !h.IsHealthy);
    }

    [Fact]
    public async Task No_providers_at_all_is_reported_not_thrown()
    {
        var snapshot = await Create(Settings(UsageProviderPreference.OAuthThenCcusage)).GetUsageAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("no usage providers", snapshot.UnavailableReason, StringComparison.Ordinal);
    }
}
