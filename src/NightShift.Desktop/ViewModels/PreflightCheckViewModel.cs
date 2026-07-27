using CommunityToolkit.Mvvm.Input;
using NightShift.Core.Preflight;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// One pill in the dashboard's preflight strip (plan.md §9.1: "click a red one to run its fix
/// action").
/// </summary>
/// <remarks>
/// Immutable apart from the command's own running state: <see cref="DashboardViewModel"/> rebuilds
/// the whole strip from each <see cref="PreflightResult"/>, which is simpler than diffing and makes
/// a stale pill impossible.
/// </remarks>
public sealed partial class PreflightCheckViewModel : ViewModelBase
{
    readonly Func<PreflightCheckViewModel, CancellationToken, Task> _applyFix;

    /// <param name="applyFix">
    /// Supplied by <see cref="DashboardViewModel"/>, which decides whether the fix is Core's to
    /// perform or the app's (a picker, a dialog, the shell).
    /// </param>
    public PreflightCheckViewModel(
        PreflightCheck check,
        Func<PreflightCheckViewModel, CancellationToken, Task> applyFix)
    {
        ArgumentNullException.ThrowIfNull(check);

        Check = check;
        _applyFix = applyFix ?? throw new ArgumentNullException(nameof(applyFix));
    }

    /// <summary>The underlying check, for anything the properties below do not surface.</summary>
    public PreflightCheck Check { get; }

    /// <summary>Stable identity; bind styling and tests to this, never to <see cref="Name"/>.</summary>
    public PreflightCheckId Id => Check.Id;

    /// <summary>Display name for the pill, e.g. <c>Folder trusted</c>.</summary>
    public string Name => Check.Name;

    /// <summary>Red / amber / green.</summary>
    public PreflightStatus Status => Check.Status;

    /// <summary>One sentence, always populated — green rows say what was verified.</summary>
    public string Message => Check.Message;

    /// <summary>Supporting lines for an expander: probed paths, ask rules, dirty files.</summary>
    public IReadOnlyList<string> Details => Check.Details;

    public bool HasDetails => Check.Details.Count > 0;

    public bool IsOk => Status == PreflightStatus.Ok;

    public bool IsWarning => Status == PreflightStatus.Warning;

    public bool IsError => Status == PreflightStatus.Error;

    /// <summary>True for a red row — one an unattended run cannot start through.</summary>
    public bool IsBlocking => Check.IsBlocking;

    /// <summary>What the fix would do, or null when nothing sensible can be offered.</summary>
    public PreflightFixAction? Fix => Check.Fix;

    public bool HasFix => Check.Fix is not null;

    /// <summary>Button wording, e.g. <c>Trust this folder</c>. Null when there is no fix.</summary>
    public string? FixLabel => Check.Fix?.Label;

    /// <summary>
    /// True when <see cref="NightShift.Core.Preflight.PreflightChecker"/> carries the fix out
    /// itself; false means the app owns it (a picker, a dialog, an editor).
    /// </summary>
    public bool IsFixAppliedByCore => Check.Fix?.IsAppliedByCore ?? false;

    [RelayCommand(CanExecute = nameof(HasFix), AllowConcurrentExecutions = false)]
    Task ApplyFixAsync(CancellationToken cancellationToken) => _applyFix(this, cancellationToken);
}
