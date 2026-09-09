using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tedd.Defrag.Core;
using Windows.Win32;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.System.RemoteDesktop;

namespace Tedd.Defrag.Windows;

/// <summary>Only attach in an isolated execution process. The broker and interfaces must remain outside this job.</summary>
public sealed unsafe class WorkerLimits : IDisposable
{
    private readonly SafeFileHandle _job;
    public WorkerLimits(ResourcePolicy policy)
    {
        policy.Validate();
        _job = PInvoke.CreateJobObject(null, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION memory = default;
            memory.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
            memory.ProcessMemoryLimit = (nuint)(policy.MemoryMiB * 1024L * 1024);
            memory.JobMemoryLimit = memory.ProcessMemoryLimit;
            Check(PInvoke.SetInformationJobObject(_job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, new ReadOnlySpan<byte>(&memory, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))));
            JOBOBJECT_CPU_RATE_CONTROL_INFORMATION cpu = new() { ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP };
            cpu.CpuRate = (uint)policy.CpuPercent * 100;
            Check(PInvoke.SetInformationJobObject(_job, JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation, new ReadOnlySpan<byte>(&cpu, sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION))));
            using var process = Process.GetCurrentProcess();
            Check(PInvoke.AssignProcessToJobObject(_job, process.SafeHandle));
            // The CLR started before this process joined the capped job. Refresh its heap budget
            // now, leaving committed memory for native I/O, JIT code, stacks and serialization.
            ulong heapLimit = (ulong)Math.Min(policy.MemoryMiB * 1024L * 1024 * 3 / 5, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            AppContext.SetData("GCHeapHardLimit", heapLimit);
            GC.RefreshMemoryLimit();
            if (policy.AffinityMask != 0)
            {
                if (PInvoke.GetActiveProcessorGroupCount() != 1) throw new NotSupportedException("Affinity masks are limited to single processor-group systems. Leave affinity automatic on systems with more than 64 logical processors.");
                nuint processMask = 0, systemMask = 0;
                Check(PInvoke.GetProcessAffinityMask(PInvoke.GetCurrentProcess(), &processMask, &systemMask));
                if (((nuint)policy.AffinityMask & systemMask) != (nuint)policy.AffinityMask) throw new ArgumentException("Affinity selects unavailable logical processors.");
                Check(PInvoke.SetProcessAffinityMask(PInvoke.GetCurrentProcess(), (nuint)policy.AffinityMask));
            }
            if (policy.Background) Check(PInvoke.SetPriorityClass(PInvoke.GetCurrentProcess(), PROCESS_CREATION_FLAGS.PROCESS_MODE_BACKGROUND_BEGIN));
        }
        catch { _job.Dispose(); throw; }
    }
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() => _job.Dispose();
}

public static unsafe class ActivityGate
{
    public static bool MayRun(ResourcePolicy policy, out string reason)
    {
        reason = "";
        if (policy.AcOnly)
        {
            if (!PInvoke.GetSystemPowerStatus(out var power) || power.ACLineStatus != 1) { reason = "Waiting for known AC power"; return false; }
        }
        if (!policy.IdleOnly) return true;
        int session = Process.GetCurrentProcess().SessionId;
        if (!PInvoke.WTSEnumerateSessions(default, 0, 1, out WTS_SESSION_INFOW* sessions, out uint count)) { reason = "Session activity unavailable"; return false; }
        try
        {
            for (int i = 0; i < count; i++)
                if (sessions[i].State == WTS_CONNECTSTATE_CLASS.WTSActive && sessions[i].SessionId != session)
                { reason = "Another interactive session is active; idle state is unknown"; return false; }
        }
        finally { PInvoke.WTSFreeMemory(sessions); }
        LASTINPUTINFO input = new() { cbSize = (uint)sizeof(LASTINPUTINFO) };
        if (!PInvoke.GetLastInputInfo(ref input)) { reason = "Session idle state unavailable"; return false; }
        uint idleMs = unchecked((uint)Environment.TickCount - input.dwTime);
        if (idleMs < policy.IdleSeconds * 1000L) { reason = "Waiting for sustained user inactivity"; return false; }
        return true;
    }
}
