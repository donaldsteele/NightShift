using NightShift.Core.Configuration;

namespace NightShift.Core.Execution;

/// <summary>Builds the single prompt string a run is launched with (plan.md §5.2).</summary>
public interface IPromptBuilder
{
    /// <summary>
    /// The exact text that goes into <c>claude -p "&lt;prompt&gt;"</c>. Pure and deterministic, so the
    /// Settings screen can call it on every keystroke to show a live preview (plan.md §9.2 "Prompt")
    /// and get precisely what a run would send.
    /// </summary>
    string Build(PilotSettings settings);
}

/// <summary>
/// Assembles the prompt from the two settings that own it: <see cref="PilotSettings.CavemanLevel"/>
/// and <see cref="PilotSettings.PromptTemplate"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>/caveman &lt;level&gt;</c> line is prepended here rather than stored in the template. If it
/// lived in the template, the level dropdown and the template editor would fight: editing the
/// template would silently pin an old level, and changing the level would have to rewrite text the
/// user had customised. Keeping it out means the template stays pure prose and the level stays a
/// single enum. (Slash commands are expanded from the prompt string before the run starts, which is
/// why this works at all in <c>-p</c> mode — see plan.md §5.2.)
/// </para>
/// <para>
/// Line endings are normalised to <c>\n</c>. The default template is a raw string literal, so it
/// carries whatever the source file was checked out with, and a template edited in the UI carries
/// whatever the text box produced; without normalising, the same settings would yield different
/// prompt bytes on different machines and every golden test would be checkout-dependent.
/// </para>
/// </remarks>
public sealed class PromptBuilder : IPromptBuilder
{
    /// <summary>The token in the template that is replaced with the plan file name.</summary>
    public const string PlanFileToken = "{planFile}";

    /// <summary>
    /// The token replaced with the rules for the plan's <see cref="PlanFormat"/>.
    /// </summary>
    public const string PlanConventionsToken = "{planConventions}";

    /// <summary>
    /// What to tell a run about a checkbox plan. This is the wording that shipped before the token
    /// existed, kept intact — it is the only instruction set with a night of evidence behind it.
    /// </summary>
    public const string CheckboxConventions = """
        Pick up from the first unchecked item in {planFile} and make concrete progress.
        - After finishing an item, tick its checkbox.
        - If an item is ambiguous or blocked, mark it `- [!]` with a one-line note
          explaining the blocker, then move to the next item.
        - Prefer finishing one item completely over starting several.
        """;

    /// <summary>
    /// What to tell a run about a milestone plan.
    /// </summary>
    /// <remarks>
    /// The marker wording is not cosmetic: it is the same one <c>PlanParser</c> reads back, so a run
    /// that follows these instructions is what makes the dashboard's tally move. Changing one without
    /// the other leaves a plan that is being worked on but never appears to progress.
    /// </remarks>
    public const string MilestoneConventions = """
        Work the lowest-numbered milestone in {planFile} that is not yet marked delivered,
        and complete the whole of it.
        - When it is done, append ` — **delivered YYYY-MM-DD**` to that milestone's heading
          and add a `**Status:**` line under it saying what shipped.
        - If the milestone is ambiguous or blocked, add a `**Blocked:**` line under its
          heading with a one-line reason, then move to the next milestone.
        - Prefer finishing one milestone completely over starting several.
        """;

    /// <summary>
    /// Slash command the caveman skill registers (plan.md §5.2).
    /// </summary>
    /// <remarks>
    /// <b>The plugin namespace is mandatory.</b> plan.md §5.2 specified <c>/caveman full</c>; that is
    /// wrong, and wrong in the worst possible way. Measured against Claude Code 2.1.220 on
    /// 2026-07-26:
    /// <code>
    /// claude -p "/caveman full\n\nReply with exactly: FORM-A-OK"
    ///   → result "Unknown command: /caveman", is_error FALSE, exit code 0
    /// claude -p "/caveman:caveman full\n\nReply with exactly: FORM-B-OK"
    ///   → result "FORM-B-OK", is_error false, exit code 0
    /// </code>
    /// The unnamespaced form does not just lose the caveman style — the entire prompt is swallowed,
    /// nothing runs, and Claude Code still reports success. An unattended pilot would burn every
    /// scheduled slot doing nothing while logging a clean run. Plugin commands are registered as
    /// <c>&lt;plugin&gt;:&lt;command&gt;</c>; the real inventory from a live session contains
    /// <c>caveman:caveman</c>, <c>caveman:caveman-review</c> and friends, with no bare alias.
    /// <see cref="HeadlessClaudeRunner"/> additionally treats an "Unknown command" result as a
    /// failed run, so a future rename cannot resurrect this silently.
    /// </remarks>
    public const string CavemanCommand = "/caveman:caveman";

    /// <summary>
    /// Mirrors <see cref="PilotSettings.PlanFileName"/>'s default without duplicating the literal, so
    /// a direct call with a blank plan file still produces the prompt a default install would send.
    /// </summary>
    static readonly string FallbackPlanFileName = new PilotSettings().PlanFileName;

    public string Build(PilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Build(settings.CavemanLevel, settings.PromptTemplate, settings.PlanFileName, settings.PlanFormat);
    }

    /// <summary>
    /// The whole prompt from its inputs: the caveman directive, a blank line, then the template body
    /// with every <see cref="PlanFileToken"/> and <see cref="PlanConventionsToken"/> substituted.
    /// </summary>
    /// <param name="level">Caveman intensity; emitted explicitly so a change to the skill's own
    /// default cannot change how runs behave (plan.md §5.2).</param>
    /// <param name="template">Prompt body. Blank falls back to
    /// <see cref="PilotSettings.DefaultPromptTemplate"/>.</param>
    /// <param name="planFileName">Substituted for the token. Blank falls back to <c>plan.md</c>.</param>
    /// <param name="planFormat">
    /// Which conventions to state. <see cref="PlanFormat.Auto"/> means the caller has not resolved it
    /// — the Settings preview, or a direct call — and yields the checkbox conventions, which is what
    /// this prompt said before the token existed.
    /// </param>
    public static string Build(
        CavemanLevel level,
        string? template,
        string? planFileName,
        PlanFormat planFormat = PlanFormat.Auto) =>
        $"{BuildCavemanDirective(level)}\n\n{ApplyPlanFile(template, planFileName, planFormat)}";

    /// <summary>The <c>/caveman &lt;level&gt;</c> line on its own.</summary>
    public static string BuildCavemanDirective(CavemanLevel level) =>
        $"{CavemanCommand} {level.ToCommandArgument()}";

    /// <summary>The conventions block for one plan format, with the plan file already substituted.</summary>
    public static string ConventionsFor(PlanFormat planFormat, string? planFileName) =>
        SubstitutePlanFile(
            planFormat == PlanFormat.Milestone ? MilestoneConventions : CheckboxConventions,
            planFileName);

    /// <summary>
    /// Substitutes the plan file and the conventions into the template. Every occurrence is replaced,
    /// and a template that never mentions a token is returned unchanged rather than treated as an
    /// error; a user is entitled to write a prompt that does not need them.
    /// </summary>
    public static string ApplyPlanFile(
        string? template,
        string? planFileName,
        PlanFormat planFormat = PlanFormat.Auto)
    {
        var body = string.IsNullOrWhiteSpace(template)
            ? PilotSettings.DefaultPromptTemplate
            : template;

        // Conventions first, because the block itself names the plan file.
        body = NormalizeNewLines(body)
            .Replace(PlanConventionsToken, ConventionsFor(planFormat, planFileName), StringComparison.Ordinal);

        return SubstitutePlanFile(body, planFileName).Trim();
    }

    static string SubstitutePlanFile(string text, string? planFileName)
    {
        var planFile = string.IsNullOrWhiteSpace(planFileName)
            ? FallbackPlanFileName
            : planFileName.Trim();

        return NormalizeNewLines(text).Replace(PlanFileToken, planFile, StringComparison.Ordinal);
    }

    /// <summary>CRLF and lone CR both become LF; see the class remarks for why this matters.</summary>
    static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
