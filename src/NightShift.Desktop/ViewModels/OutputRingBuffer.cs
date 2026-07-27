using System.Text;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// The dashboard's live output pane, capped at <see cref="DefaultCapacity"/> lines (plan.md §9.1:
/// "cap the in-memory buffer at ~5k lines (ring buffer) and keep the full text on disk").
/// </summary>
/// <remarks>
/// <para>
/// A run streams for up to <see cref="NightShift.Core.Configuration.PilotSettings.MaxRunDurationMinutes"/>
/// minutes and can emit tens of thousands of fragments. Without a cap the pane is an unbounded
/// <see cref="StringBuilder"/> in a process that is meant to sit in the tray overnight, so the
/// oldest line is dropped once the cap is reached. Nothing is lost: the full transcript is written
/// to <c>runs/&lt;runId&gt;.log</c> by <see cref="NightShift.Core.History.RunHistoryStore"/>, which
/// is what the <c>Open log</c> button opens.
/// </para>
/// <para>
/// Text arrives as <em>fragments</em>, not lines — <see cref="NightShift.Core.Execution.ClaudeTextDeltaEvent"/>
/// carries arbitrary slices that may split a line anywhere — so an incomplete trailing line is held
/// separately and only becomes a buffered line when its newline arrives. It still shows up in
/// <see cref="GetText"/>, so the pane streams smoothly instead of appearing a line at a time.
/// </para>
/// </remarks>
public sealed class OutputRingBuffer
{
    /// <summary>plan.md §9.1's "~5k lines".</summary>
    public const int DefaultCapacity = 5000;

    /// <summary>
    /// Hard cap on an unterminated line. A wedged child process that emits megabytes with no newline
    /// would otherwise defeat the line cap entirely by growing one "line" forever.
    /// </summary>
    public const int MaxPendingLineLength = 32 * 1024;

    readonly Queue<string> _lines;
    readonly StringBuilder _pending = new();
    readonly Lock _sync = new();

    public OutputRingBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _lines = new Queue<string>(Math.Min(capacity, 1024));
    }

    /// <summary>Maximum number of completed lines retained.</summary>
    public int Capacity { get; }

    /// <summary>Completed lines held, plus one for an unterminated trailing line.</summary>
    public int LineCount
    {
        get
        {
            lock (_sync)
            {
                return _lines.Count + (_pending.Length > 0 ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// How many lines have been dropped off the front since the last <see cref="Clear"/>. Non-zero
    /// means the pane is a window onto the run, not the whole of it — worth saying so in the UI.
    /// </summary>
    public int DroppedLineCount { get; private set; }

    public bool IsEmpty => LineCount == 0;

    /// <summary>
    /// Appends a fragment. Embedded newlines split it into lines; anything after the last newline is
    /// held as the in-progress line.
    /// </summary>
    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_sync)
        {
            var start = 0;
            while (true)
            {
                var index = text.IndexOf('\n', start);
                if (index < 0)
                {
                    _pending.Append(text, start, text.Length - start);
                    break;
                }

                _pending.Append(text, start, index - start);
                PushPending();
                start = index + 1;
            }

            if (_pending.Length > MaxPendingLineLength)
            {
                PushPending();
            }
        }
    }

    /// <summary>Appends a fragment and terminates the line it lands on.</summary>
    public void AppendLine(string? text)
    {
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _pending.Append(text);
            }

            PushPending();
        }
    }

    /// <summary>Ends the in-progress line if there is one. A no-op otherwise, so it never adds blanks.</summary>
    public void EndLine()
    {
        lock (_sync)
        {
            if (_pending.Length > 0)
            {
                PushPending();
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            _pending.Clear();
            DroppedLineCount = 0;
        }
    }

    /// <summary>Everything currently buffered, newline-joined, including the in-progress line.</summary>
    public string GetText()
    {
        lock (_sync)
        {
            if (_lines.Count == 0)
            {
                return _pending.ToString();
            }

            var builder = new StringBuilder();
            foreach (var line in _lines)
            {
                builder.Append(line).Append('\n');
            }

            builder.Append(_pending);
            return builder.ToString();
        }
    }

    /// <summary>A snapshot of the buffered lines, oldest first, including the in-progress line.</summary>
    public IReadOnlyList<string> GetLines()
    {
        lock (_sync)
        {
            var lines = new List<string>(_lines.Count + 1);
            lines.AddRange(_lines);
            if (_pending.Length > 0)
            {
                lines.Add(_pending.ToString());
            }

            return lines;
        }
    }

    void PushPending()
    {
        // CRLF input would otherwise leave a stray CR at the end of every line, which renders as a
        // box in a monospace pane.
        if (_pending.Length > 0 && _pending[^1] == '\r')
        {
            _pending.Length--;
        }

        _lines.Enqueue(_pending.ToString());
        _pending.Clear();

        while (_lines.Count > Capacity)
        {
            _lines.Dequeue();
            DroppedLineCount++;
        }
    }
}
