using NightShift.Core.Configuration;

namespace NightShift.Core.Execution;

/// <summary>
/// Builds the <c>claude</c> argument list for a headless run (plan.md §5.4).
/// </summary>
/// <remarks>
/// Two flags are forbidden here and the tests assert it (plan.md §11.3, §5.3.3):
/// <list type="bullet">
/// <item><c>--bare</c> skips discovery of skills, plugins, hooks, MCP servers and <c>CLAUDE.md</c>,
/// and skips OAuth/keychain auth — it would break both <c>/caveman</c> and subscription
/// authentication. It is the single most likely mistake in this build.</item>
/// <item><c>--permission-mode plan</c> ends by waiting for a human to approve, which is exactly the
/// behaviour an unattended pilot exists to avoid.</item>
/// </list>
/// </remarks>
public static class ClaudeArgumentsBuilder
{
    /// <summary>Removed from Claude's context so it cannot pause to poll a developer who isn't there.</summary>
    public const string DisallowedTools = "AskUserQuestion";

    public static IReadOnlyList<string> Build(
        PilotSettings settings,
        string prompt,
        PermissionMode effectiveMode,
        string allowedTools,
        string? resumeSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        if (effectiveMode is not (PermissionMode.Auto or PermissionMode.AcceptEdits or PermissionMode.BypassPermissions))
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveMode),
                effectiveMode,
                "Only auto, acceptEdits and bypassPermissions may be launched unattended.");
        }

        var arguments = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-mode", effectiveMode.ToCliValue(),
        };

        if (!string.IsNullOrWhiteSpace(allowedTools))
        {
            arguments.Add("--allowedTools");
            arguments.Add(allowedTools.Trim());
        }

        arguments.Add("--disallowedTools");
        arguments.Add(DisallowedTools);

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            arguments.Add("--model");
            arguments.Add(settings.Model.Trim());
        }

        if (settings.ResumeStrategy == ResumeStrategy.Resume && !string.IsNullOrWhiteSpace(resumeSessionId))
        {
            arguments.Add("--resume");
            arguments.Add(resumeSessionId.Trim());
        }

        if (settings.SkipPermissionsFallback && effectiveMode == PermissionMode.BypassPermissions)
        {
            // Only ever reached when the user explicitly opted in; it implies bypassPermissions,
            // which is why it may not be combined with any other mode.
            arguments.Add("--dangerously-skip-permissions");
        }

        return arguments;
    }
}
