using NightShift.Core.Execution;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// What may and may not reach the live output pane. The exclusions are the interesting half: raw
/// JSON shards and tool results in the pane are the two ways this feature reads as broken.
/// </summary>
public class OutputPaneWriterTests
{
    [Fact]
    public void TextDeltasStream()
    {
        var writer = new OutputPaneWriter();

        Assert.True(writer.Write(new ClaudeTextDeltaEvent("{}", "Reading ")));
        Assert.True(writer.Write(new ClaudeTextDeltaEvent("{}", "plan.md")));

        Assert.Equal("Reading plan.md", writer.Buffer.GetText());
    }

    [Fact]
    public void InputJsonDeltaFragmentsNeverReachThePane()
    {
        var writer = new OutputPaneWriter();

        // The most common delta in a real tool-using run, and arbitrary slices of a JSON document —
        // one can end mid-escape-sequence. In the pane they read as corruption.
        Assert.False(writer.Write(new ClaudeInputJsonDeltaEvent("{}", "")));
        Assert.False(writer.Write(new ClaudeInputJsonDeltaEvent("{}", "{\"file_pa")));
        Assert.False(writer.Write(new ClaudeInputJsonDeltaEvent("{}", "th\":\"C:\\\\co")));

        Assert.True(writer.Buffer.IsEmpty);
    }

    [Fact]
    public void ToolResultsNeverReachThePane()
    {
        var writer = new OutputPaneWriter();

        // In an unattended run *every* `user` line is one of these. Rendering it as a human turn
        // would claim an operator said something when there is no operator.
        Assert.False(writer.Write(new ClaudeToolResultEvent("{}", "toolu_1", "42 files changed")));

        Assert.True(writer.Buffer.IsEmpty);
    }

    [Fact]
    public void SystemNoiseNeverReachesThePane()
    {
        var writer = new OutputPaneWriter();

        Assert.False(writer.Write(new ClaudeInitEvent("{}")));
        Assert.False(writer.Write(new ClaudeStatusEvent("{}", "requesting")));
        Assert.False(writer.Write(new ClaudeRateLimitEvent("{}")));
        Assert.False(writer.Write(new ClaudeUnknownEvent("{}", "stream_event", "message_start")));
        Assert.False(writer.Write(new ClaudeNonJsonLineEvent("npm notice")));
        Assert.False(writer.Write(new ClaudeMalformedLineEvent("{oops", "bad json")));

        Assert.True(writer.Buffer.IsEmpty);
    }

    [Fact]
    public void AMessageWithNoPrecedingDeltasIsWritten()
    {
        var writer = new OutputPaneWriter();

        Assert.True(writer.Write(
            new ClaudeMessageEvent("{}", ClaudeMessageRole.Assistant, "Ticked item 12.")));

        Assert.Equal("Ticked item 12.\n", writer.Buffer.GetText());
    }

    [Fact]
    public void TextAlreadyStreamedAsDeltasIsNotPrintedTwiceByItsMessage()
    {
        var writer = new OutputPaneWriter();

        // With --include-partial-messages the same text arrives as deltas and then again whole.
        writer.Write(new ClaudeTextDeltaEvent("{}", "Ticked "));
        writer.Write(new ClaudeTextDeltaEvent("{}", "item 12."));
        writer.Write(new ClaudeMessageEvent("{}", ClaudeMessageRole.Assistant, "Ticked item 12."));

        Assert.Equal("Ticked item 12.\n", writer.Buffer.GetText());
    }

    [Fact]
    public void EachMessageBoundaryRearmsTheDuplicateGuard()
    {
        var writer = new OutputPaneWriter();

        writer.Write(new ClaudeTextDeltaEvent("{}", "first"));
        writer.Write(new ClaudeMessageEvent("{}", ClaudeMessageRole.Assistant, "first"));
        writer.Write(new ClaudeMessageEvent("{}", ClaudeMessageRole.Assistant, "second"));

        Assert.Equal("first\nsecond\n", writer.Buffer.GetText());
    }

    [Fact]
    public void AToolUseBlockStartAnnouncesItselfSoThePaneIsNotSilent()
    {
        var writer = new OutputPaneWriter();

        Assert.True(writer.Write(new ClaudeContentBlockStartEvent("{}", "tool_use")
        {
            ToolName = "Write",
            ToolUseId = "toolu_1",
        }));

        Assert.Equal($"{OutputPaneWriter.ToolUseMarker} Write\n", writer.Buffer.GetText());
    }

    [Fact]
    public void ATextBlockStartIsNotAnnounced()
    {
        var writer = new OutputPaneWriter();

        Assert.False(writer.Write(new ClaudeContentBlockStartEvent("{}", "text") { Index = 0 }));
        Assert.True(writer.Buffer.IsEmpty);
    }

    [Fact]
    public void ResetForgetsTheStreamingStateBetweenRuns()
    {
        var writer = new OutputPaneWriter();
        writer.Write(new ClaudeTextDeltaEvent("{}", "half a sentence"));

        writer.Buffer.Clear();
        writer.Reset();

        writer.Write(new ClaudeMessageEvent("{}", ClaudeMessageRole.Assistant, "a whole one"));

        Assert.Equal("a whole one\n", writer.Buffer.GetText());
    }

    [Fact]
    public void TheCapStillAppliesToStreamedText()
    {
        var writer = new OutputPaneWriter(new OutputRingBuffer(capacity: 5));

        for (var i = 0; i < 50; i++)
        {
            writer.Write(new ClaudeTextDeltaEvent("{}", $"line {i}\n"));
        }

        Assert.Equal(5, writer.Buffer.LineCount);
        Assert.Equal(45, writer.Buffer.DroppedLineCount);
    }
}
