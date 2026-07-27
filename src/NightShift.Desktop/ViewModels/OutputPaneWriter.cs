using NightShift.Core.Execution;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// Decides which stream events reach the dashboard's live output pane, and in what form
/// (plan.md §9.1, §5.4).
/// </summary>
/// <remarks>
/// <para>
/// The filter is the point of this class, and it is not cosmetic:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ClaudeInputJsonDeltaEvent"/> is the <em>most common</em> delta in a real
///     tool-using run, and its <c>partial_json</c> fragments are arbitrary slices of a JSON
///     document — one can end mid-escape-sequence. Appending them renders the pane as corruption,
///     so they never reach it.
///   </description></item>
///   <item><description>
///     <see cref="ClaudeToolResultEvent"/> is a <c>user</c>-typed line, but in an unattended run
///     <b>every</b> <c>user</c> line is one of these. Showing tool output as if the absent operator
///     had typed it is actively misleading, so it is excluded too.
///   </description></item>
///   <item><description>
///     With <c>--include-partial-messages</c> the same assistant text arrives twice: once as
///     <see cref="ClaudeTextDeltaEvent"/> fragments and again as the complete
///     <see cref="ClaudeMessageEvent"/>. The writer tracks whether anything streamed since the last
///     message boundary and drops the duplicate, so the pane never double-prints a turn.
///   </description></item>
/// </list>
/// <para>
/// Split out of <see cref="DashboardViewModel"/> so the rule can be tested against synthetic events
/// without a scheduler.
/// </para>
/// </remarks>
public sealed class OutputPaneWriter
{
    /// <summary>Prefix for the "running Write…" line a tool-use block start produces.</summary>
    public const string ToolUseMarker = "→";

    bool _streamedSinceLastMessage;

    public OutputPaneWriter(OutputRingBuffer? buffer = null)
    {
        Buffer = buffer ?? new OutputRingBuffer();
    }

    /// <summary>The bounded buffer this writer appends into.</summary>
    public OutputRingBuffer Buffer { get; }

    /// <summary>
    /// Appends <paramref name="streamEvent"/> if it belongs in the pane. Returns true when the
    /// buffer changed, so the caller knows whether to raise a change notification.
    /// </summary>
    public bool Write(ClaudeStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        switch (streamEvent)
        {
            case ClaudeTextDeltaEvent { Text.Length: > 0 } delta:
                Buffer.Append(delta.Text);
                _streamedSinceLastMessage = true;
                return true;

            case ClaudeMessageEvent message:
                return WriteMessage(message);

            case ClaudeContentBlockStartEvent { IsToolUse: true } block:
                // Without this the pane sits silent for the whole of a long tool call, because the
                // arguments stream as input_json_delta and those are excluded above.
                Buffer.EndLine();
                Buffer.AppendLine($"{ToolUseMarker} {block.ToolName ?? "tool"}");
                _streamedSinceLastMessage = false;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Forgets the streaming state; call between runs, alongside clearing the buffer.</summary>
    public void Reset() => _streamedSinceLastMessage = false;

    bool WriteMessage(ClaudeMessageEvent message)
    {
        // Text already streamed as deltas: close the line and swallow the duplicate.
        if (_streamedSinceLastMessage)
        {
            _streamedSinceLastMessage = false;
            Buffer.EndLine();
            return true;
        }

        if (message.Text.Length == 0)
        {
            return false;
        }

        Buffer.EndLine();
        Buffer.AppendLine(message.Text);
        return true;
    }
}
