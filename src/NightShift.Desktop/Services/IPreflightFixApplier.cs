using NightShift.Core.Configuration;
using NightShift.Core.Preflight;

namespace NightShift.Desktop.Services;

/// <summary>
/// The half of a preflight fix that <see cref="PreflightChecker"/> performs itself (plan.md §7).
/// </summary>
/// <remarks>
/// <see cref="IPreflightChecker"/> exposes only <c>CheckAsync</c>; <c>ApplyFixAsync</c> lives on the
/// concrete <see cref="PreflightChecker"/>, which takes seven collaborators. Narrowing it to this
/// one method is what lets a test prove the dashboard routes
/// <see cref="PreflightFixAction.IsAppliedByCore"/> fixes to Core and everything else to a picker,
/// a dialog or the shell, without standing up a process runner and a credential reader.
/// </remarks>
public interface IPreflightFixApplier
{
    Task<PreflightFixResult> ApplyAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>Forwards to <see cref="PreflightChecker.ApplyFixAsync"/>.</summary>
public sealed class PreflightFixApplier : IPreflightFixApplier
{
    readonly PreflightChecker _checker;

    public PreflightFixApplier(PreflightChecker checker)
    {
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
    }

    public Task<PreflightFixResult> ApplyAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken = default) =>
        _checker.ApplyFixAsync(fix, settings, cancellationToken);
}
