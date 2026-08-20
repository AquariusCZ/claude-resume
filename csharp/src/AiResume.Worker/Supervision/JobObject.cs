using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiResume.Worker.Supervision;

/// <summary>
/// Windows Job Object 封装(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)。
///
/// 决策说明(安全关键,全项目进程边界基础):
/// 1. 句柄在宿主存活期间保持打开;宿主退出/Dispose 时句柄关闭,内核自动终止整个进程树。
///    这是崩溃场景的最终兜底:即使 registry 补全失败或进程逃逸登记,孤儿进程树也会随宿主死亡。
/// 2. 全部 P/Invoke 手写,不引入第三方包(红线)。
/// 3. Assign 之后子进程及其全部后代自动继承 Job(Windows 8+ 默认继承),无需逐代分配。
/// </summary>
public sealed class JobObject : IDisposable
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;

    private IntPtr _handle;

    public string JobId { get; }

    public JobObject(string? jobId = null)
    {
        JobId = jobId ?? Guid.NewGuid().ToString("D");
        _handle = CreateJobObjectW(IntPtr.Zero, $"Local\\airesume-{JobId}");
        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW 失败。");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
                throw new Win32Exception(error, "SetInformationJobObject 失败。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>把子进程放入 Job;失败时由调用方负责终止进程并清理登记。</summary>
    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(JobObject));
        }

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 失败。");
        }
    }

    /// <summary>返回 Job 内仍存活的全部进程数，包含已脱离外层 cmd 的后代。</summary>
    public int GetActiveProcessCount()
    {
        IntPtr handle = Volatile.Read(ref _handle);
        if (handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(JobObject));
        }

        int size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    handle,
                    JobObjectBasicAccountingInformation,
                    ptr,
                    (uint)size,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject 失败。");
            }

            var info = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(ptr);
            return checked((int)info.ActiveProcesses);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>请求内核终止 Job 内整棵进程树；句柄继续保留，供调用方确认 ActiveProcesses 归零。</summary>
    public void TerminateAll(uint exitCode = 1)
    {
        IntPtr handle = Volatile.Read(ref _handle);
        if (handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(JobObject));
        }

        if (!TerminateJobObject(handle, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject 失败。");
        }
    }

    /// <summary>
    /// 关闭 Job 句柄 → kill-on-close 终止整棵进程树(终止的优先手段)。
    /// 幂等:重复调用无副作用;关闭后 Dispose 不再重复关闭。
    /// </summary>
    public void CloseAndKill()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        CloseAndKill();
        GC.SuppressFinalize(this);
    }

    ~JobObject()
    {
        CloseAndKill();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
