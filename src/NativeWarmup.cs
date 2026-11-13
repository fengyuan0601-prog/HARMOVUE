// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalMedia;

// One app-owned completion gate; no externally supplied process identity.
// The UI starts work but never waits; only CreateEngine's existing worker uses the gate.
internal sealed class NativeWarmup : IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly CancellationToken token;
    readonly object stopLock = new();
    bool stopped;
    internal Task Completion { get; }

    NativeWarmup()
    {
        token = lifetime.Token;
        if (Environment.GetEnvironmentVariable("LOCALMEDIA_DISABLE_WARMUP") == "1")
        {
            Diagnostics.TracePlayback("managed warmup disabled");
            Completion = Task.CompletedTask;
        }
        else
            // Do not pass token to Task.Run: even cancellation before scheduling must
            // execute the worker's finally and publish real cleanup completion.
            Completion = Task.Run(Run);
    }

    internal static NativeWarmup Begin() => new();

    internal void WaitBeforeNativeLoad()
    {
        token.ThrowIfCancellationRequested();
        Diagnostics.TracePlayback("managed warmup wait begin");
        Completion.GetAwaiter().GetResult(); // Existing engine worker only; no UI await/wait.
        token.ThrowIfCancellationRequested();
        Diagnostics.TracePlayback("managed warmup wait end");
    }

    void Run()
    {
        Diagnostics.TracePlayback("managed warmup begin");
        try { RunContained(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Diagnostics.TracePlayback("managed warmup cancelled"); }
        catch (IOException error) { Unavailable("I/O", error.HResult); }
        catch (UnauthorizedAccessException error) { Unavailable("permission", error.HResult); }
        catch (ArgumentException error) { Unavailable("argument", error.HResult); }
        // A handle-wait failure means quiescence was not demonstrated. Keep this
        // task faulted rather than starting a competing native load in that case.
        finally { Diagnostics.TracePlayback("managed warmup completion"); }
    }

    void RunContained()
    {
        token.ThrowIfCancellationRequested();
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Harmovue.NativePrewarm.exe"));
        using var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { Unavailable("job"); return; }
        // Only the known helper may run in this job; it has no subprocess feature.
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2008, ActiveProcesses = 1 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { Unavailable("job limits"); return; }

        // Cancel closes only this app's private job. A DangerousAddRef below keeps
        // the kernel handle alive across CreateProcess, including a concurrent
        // cancel; atomic job assignment still precedes the child's first instruction.
        using var cancelJob = token.UnsafeRegister(static state => ((SafeFileHandle)state!).Dispose(), job);
        SafeProcessHandle? process = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            if (!TryStart(helper, job, out process, out uint pid)) return;
            if (!ReadIdentity(process!, helper, pid, out long created))
            { Unavailable("identity"); return; }
            Diagnostics.TracePlayback("managed warmup started", detail: "pid=" + pid + ";created=" + created.ToString("X16"));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(15))
                { Diagnostics.TracePlayback("managed warmup timeout"); return; }
                uint state = WaitForSingleObject(process!, 50);
                if (state == 258) continue;
                if (state != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Warmup process wait failed.");
                if (!GetProcessTimes(process!, out long finalCreated, out _, out _, out _) || finalCreated != created)
                { Unavailable("creation identity"); return; }
                if (!GetExitCodeProcess(process!, out uint code)) { Unavailable("exit code"); return; }
                token.ThrowIfCancellationRequested();
                Diagnostics.TracePlayback(code == 0 ? "managed warmup end" : "managed warmup unavailable: exit " + code);
                return;
            }
        }
        finally
        {
            // KILL_ON_JOB_CLOSE is also the crash guarantee. Keep the ORIGINAL
            // process handle, and do not complete/fall back until it really signals.
            // This wait is worker-only; cancellation and UI shutdown never wait here.
            job.Dispose();
            if (process is not null)
            {
                try
                {
                    if (WaitForSingleObject(process, uint.MaxValue) != 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Warmup cleanup wait failed.");
                    Diagnostics.TracePlayback("managed warmup process exited");
                }
                finally { process.Dispose(); }
            }
        }
    }

    bool TryStart(string helper, SafeFileHandle job, out SafeProcessHandle? process, out uint pid)
    {
        process = null; pid = 0;
        token.ThrowIfCancellationRequested();
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0 || size > 65536) { Unavailable("attribute size"); return false; }
        nint attributes = Marshal.AllocHGlobal((nint)size), slot = 0;
        bool initialized = false, jobReference = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            { Unavailable("attributes"); return false; }
            initialized = true;
            // Serialize acquisition against cancellation's SafeHandle.Dispose.
            token.ThrowIfCancellationRequested();
            try { job.DangerousAddRef(ref jobReference); }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
            slot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(slot, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x2000D, slot, (nuint)IntPtr.Size, 0, 0))
            { Unavailable("atomic job attribute"); return false; }
            token.ThrowIfCancellationRequested();
            var startup = new StartupEx { Info = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupEx>() }, Attributes = attributes };
            // No inherited handles, terminal, persistent environment, or cwd change.
            // Never retry as an uncontained Process.Start / suspended-then-assign.
            if (!CreateProcessW(helper, new StringBuilder("\"" + helper + "\""), 0, 0, false,
                0x08080000, 0, null, ref startup, out var created))
            { Unavailable("create process"); return false; }
            process = new SafeProcessHandle(created.Process, true);
            using (var thread = new SafeFileHandle(created.Thread, true)) { }
            pid = created.ProcessId;
            return true;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (slot != 0) Marshal.FreeHGlobal(slot);
            Marshal.FreeHGlobal(attributes);
            if (jobReference) job.DangerousRelease();
        }
    }

    static bool ReadIdentity(SafeProcessHandle process, string expectedPath, uint pid, out long created)
    {
        created = 0;
        if (GetProcessId(process) != pid || !GetProcessTimes(process, out created, out _, out _, out _) || created <= 0) return false;
        var actual = new StringBuilder(32768); uint length = (uint)actual.Capacity;
        return QueryFullProcessImageNameW(process, 0, actual, ref length)
            && string.Equals(Path.GetFullPath(actual.ToString()), expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    static void Unavailable(string stage) => Unavailable(stage, Marshal.GetLastWin32Error());
    static void Unavailable(string stage, int error) => Diagnostics.TracePlayback("managed warmup unavailable: " + stage, detail: error.ToString());

    public void Dispose()
    {
        lock (stopLock)
        {
            if (stopped) return;
            stopped = true;
            lifetime.Cancel(); // SafeHandle close is immediate, never waits for worker.
        }
        _ = Completion.ContinueWith(completed =>
        {
            _ = completed.Exception; // Observe a rare API/cleanup failure on early exit.
            lock (stopLock) lifetime.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes; public nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
