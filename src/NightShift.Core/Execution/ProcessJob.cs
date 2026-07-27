using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>
/// A Windows job object that kills every process assigned to it when NightShift dies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every other safeguard in this app — the stall detector,
/// <c>MaxRunDurationMinutes</c>, <c>Process.Kill(entireProcessTree: true)</c> — only works if our
/// code is alive to run it. Kill NightShift itself and none of them fire.
/// </para>
/// <para>
/// Measured on 2026-07-27 before this class existed: killing only the NightShift process left the
/// child <c>claude.exe</c> running, and it went on to commit to the repository <b>7 and 19 seconds
/// after its supervisor was gone</b>, with no <see cref="History.RunRecord"/> ever written. It died
/// only incidentally, when a write to the now-dead parent's stdout pipe failed. Second-generation
/// grandchildren (a <c>sleep.exe</c> spawned by a tool call) survived for minutes.
/// </para>
/// <para>
/// A job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> moves that guarantee into the OS: when
/// the last handle to the job closes — including because the process holding it was terminated
/// without warning — Windows terminates every process in the job, grandchildren included.
/// </para>
/// <para>
/// Best effort by design. If the job cannot be created or a process cannot be assigned, the run still
/// proceeds: an unattended pilot that refuses to work because of a missing OS nicety is worse than
/// one whose cleanup is imperfect. Every failure is logged.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ProcessJob : IDisposable
{
    const int ExtendedLimitInformationClass = 9;
    const uint KillOnJobClose = 0x2000;

    readonly SafeJobHandle _handle;
    bool _disposed;

    ProcessJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// Creates the job, or returns null when the platform or the OS will not provide one. Callers
    /// treat null as "no OS-level cleanup available" and carry on.
    /// </summary>
    public static ProcessJob? TryCreate(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            logger.LogWarning(
                "Could not create a job object (error {Error}); a killed NightShift may leave claude running.",
                Marshal.GetLastWin32Error());
            handle.Dispose();
            return null;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                logger.LogWarning(
                    "Could not set kill-on-close on the job object (error {Error}).",
                    Marshal.GetLastWin32Error());
                handle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        logger.LogDebug("Job object created; child processes will be terminated if NightShift dies.");
        return new ProcessJob(handle);
    }

    /// <summary>
    /// Puts <paramref name="process"/> — and everything it goes on to spawn — under the job.
    /// </summary>
    public bool TryAssign(Process process, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logger);

        if (_disposed)
        {
            return false;
        }

        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
            {
                return true;
            }

            logger.LogWarning(
                "Could not assign claude (pid {Pid}) to the job object (error {Error}); it may outlive NightShift.",
                process.Id,
                Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The process exited between Start() and here.
            logger.LogDebug(ex, "Could not assign an already-exited process to the job object.");
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the last handle is what triggers the kill. Ordinary shutdown has already reaped
        // the children, so this is normally a no-op.
        _handle.Dispose();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    sealed partial class SafeJobHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
