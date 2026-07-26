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

        return Build(settings.CavemanLevel, settings.PromptTemplate, settings.PlanFileName);
    }

    /// <summary>
    /// The whole prompt from its three inputs: the caveman directive, a blank line, then the template
    /// body with every <see cref="PlanFileToken"/> substituted.
    /// </summary>
    /// <param name="level">Caveman intensity; emitted explicitly so a change to the skill's own
    /// default cannot change how runs behave (plan.md §5.2).</param>
    /// <param name="template">Prompt body. Blank falls back to
    /// <see cref="PilotSettings.DefaultPromptTemplate"/>.</param>
    /// <param name="planFileName">Substituted for the token. Blank falls back to <c>plan.md</c>.</param>
    public static string Build(CavemanLevel level, string? template, string? planFileName) =>
        $"{BuildCavemanDirective(level)}\n\n{ApplyPlanFile(template, planFileName)}";

    /// <summary>The <c>/caveman &lt;level&gt;</c> line on its own.</summary>
    public static string BuildCavemanDirective(CavemanLevel level) =>
        $"{CavemanCommand} {level.ToCommandArgument()}";

    /// <summary>
    /// Substitutes the plan file into the template. Every occurrence is replaced — the default
    /// template names the file four times — and a template that never mentions it is returned
    /// unchanged rather than treated as an error; a user is entitled to write a prompt that does not
    /// need the token.
    /// </summary>
    public static string ApplyPlanFile(string? template, string? planFileName)
    {
        var body = string.IsNullOrWhiteSpace(template)
            ? PilotSettings.DefaultPromptTemplate
            : template;

        var planFile = string.IsNullOrWhiteSpace(planFileName)
            ? FallbackPlanFileName
            : planFileName.Trim();

        return NormalizeNewLines(body).Replace(PlanFileToken, planFile, StringComparison.Ordinal).Trim();
    }

    /// <summary>CRLF and lone CR both become LF; see the class remarks for why this matters.</summary>
    static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
