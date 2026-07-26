using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// A stand-in for a running `claude`. Tests push stdout text at it and decide when it exits, so the
/// runner's streaming, stall and timeout logic can be exercised without spawning anything.
/// </summary>
public sealed class FakeClaudeProcess : IClaudeProcess
{
    readonly PipeReader _stdout = new();
    readonly PipeReader _stderr = new();
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool WasKilled { get; private set; }

    public TextReader StandardOutput => _stdout;

    public TextReader StandardError => _stderr;

    /// <summary>Makes the next read return <paramref name="text"/>.</summary>
    public void EmitStdout(string text) => _stdout.Write(text);

    public void EmitStderr(string text) => _stderr.Write(text);

    /// <summary>Ends stdout and completes the process with <paramref name="exitCode"/>.</summary>
    public void Exit(int exitCode)
    {
        _stdout.Complete();
        _stderr.Complete();
        _exit.TrySetResult(exitCode);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void KillTree()
    {
        WasKilled = true;
        _stdout.Complete();
        _stderr.Complete();
        _exit.TrySetResult(1);
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    /// <summary>A TextReader fed asynchronously, so a read blocks until the test writes or completes.</summary>
    sealed class PipeReader : TextReader
    {
        readonly Lock _gate = new();
        readonly Queue<string> _chunks = new();
        TaskCompletionSource _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool _completed;

        public void Write(string text)
        {
            lock (_gate)
            {
                _chunks.Enqueue(text);
                _available.TrySetResult();
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                _available.TrySetResult();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_chunks.Count > 0)
                    {
                        var chunk = _chunks.Peek();
                        var length = Math.Min(chunk.Length, buffer.Length);
                        chunk.AsSpan(0, length).CopyTo(buffer.Span);

                        if (length == chunk.Length)
                        {
                            _chunks.Dequeue();
                        }
                        else
                        {
                            _chunks.Dequeue();
                            _chunks.Enqueue(chunk[length..]);
                        }

                        return length;
                    }

                    if (_completed)
                    {
                        return 0;
                    }

                    _available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _available.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var buffer = new char[1024];
            var read = await ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return read == 0 ? null : new string(buffer, 0, read).TrimEnd('\r', '\n');
        }
    }
}

/// <summary>Hands out a prepared <see cref="FakeClaudeProcess"/> and records what it was asked to start.</summary>
public sealed class FakeClaudeProcessLauncher(FakeClaudeProcess process) : IClaudeProcessLauncher
{
    public ClaudeProcessStart? LastStart { get; private set; }

    public int StartCount { get; private set; }

    public IClaudeProcess Start(ClaudeProcessStart start)
    {
        LastStart = start;
        StartCount++;
        return process;
    }
}
