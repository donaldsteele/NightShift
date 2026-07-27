using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// plan.md §9.1: "cap the in-memory buffer at ~5k lines (ring buffer) and keep the full text on
/// disk". These pin the cap and the fragment handling that makes streaming look right.
/// </summary>
public class OutputRingBufferTests
{
    [Fact]
    public void DefaultCapacityIsThePlansFiveThousandLines()
    {
        Assert.Equal(5000, OutputRingBuffer.DefaultCapacity);
        Assert.Equal(5000, new OutputRingBuffer().Capacity);
    }

    [Fact]
    public void KeepsOnlyTheMostRecentLinesOnceTheCapIsReached()
    {
        var buffer = new OutputRingBuffer(capacity: 10);

        for (var i = 0; i < 100; i++)
        {
            buffer.AppendLine($"line {i}");
        }

        Assert.Equal(10, buffer.LineCount);
        Assert.Equal(90, buffer.DroppedLineCount);

        var lines = buffer.GetLines();
        Assert.Equal("line 90", lines[0]);
        Assert.Equal("line 99", lines[^1]);
        Assert.DoesNotContain("line 89", buffer.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentsAreJoinedUntilTheirNewlineArrives()
    {
        var buffer = new OutputRingBuffer();

        buffer.Append("Read");
        buffer.Append("ing pl");
        buffer.Append("an.md");

        // Still one in-progress line, and already visible so the pane streams rather than jumps.
        Assert.Equal(1, buffer.LineCount);
        Assert.Equal("Reading plan.md", buffer.GetText());

        buffer.Append("\nnext");
        Assert.Equal(2, buffer.LineCount);
        Assert.Equal("Reading plan.md\nnext", buffer.GetText());
    }

    [Fact]
    public void OneFragmentCarryingSeveralNewlinesBecomesSeveralLines()
    {
        var buffer = new OutputRingBuffer();

        buffer.Append("a\nb\nc");

        Assert.Equal(3, buffer.LineCount);
        Assert.Equal(["a", "b", "c"], buffer.GetLines());
    }

    [Fact]
    public void CarriageReturnsAreStrippedSoTheMonospacePaneHasNoBoxes()
    {
        var buffer = new OutputRingBuffer();

        buffer.Append("first\r\nsecond\r\n");

        Assert.Equal(["first", "second"], buffer.GetLines());
    }

    [Fact]
    public void AnUnterminatedLineCannotGrowWithoutBound()
    {
        var buffer = new OutputRingBuffer(capacity: 4);

        // A wedged process spewing with no newline would otherwise defeat the line cap entirely.
        buffer.Append(new string('x', OutputRingBuffer.MaxPendingLineLength * 3));

        Assert.True(buffer.LineCount <= 4);
        Assert.True(buffer.GetText().Length <= OutputRingBuffer.MaxPendingLineLength * 4 + 4);
    }

    [Fact]
    public void EndLineNeverAddsABlankLine()
    {
        var buffer = new OutputRingBuffer();

        buffer.EndLine();
        buffer.EndLine();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(string.Empty, buffer.GetText());
    }

    [Fact]
    public void ClearResetsTheDroppedCounter()
    {
        var buffer = new OutputRingBuffer(capacity: 2);
        buffer.AppendLine("a");
        buffer.AppendLine("b");
        buffer.AppendLine("c");
        Assert.Equal(1, buffer.DroppedLineCount);

        buffer.Clear();

        Assert.Equal(0, buffer.DroppedLineCount);
        Assert.True(buffer.IsEmpty);
    }
}
