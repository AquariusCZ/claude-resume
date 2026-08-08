using System.ComponentModel;
using System.Runtime.InteropServices;
using AiResume.Core.Contracts;

namespace AiResume.Worker.Supervision;

/// <summary>进程快照条目(Toolhelp32 视角:exe 文件名,无参数)。</summary>
public sealed record ProcessSnapshotEntry(int Pid, int ParentPid, string ExePath);

/// <summary>进程探测结果。Liveness=Unknown 表示查询本身失败(快照/句柄错误),非进程状态。</summary>
public sealed record ProcessProbeResult(ProcessLiveness Liveness, DateTimeOffset? StartedAt, string? ExePath);

/// <summary>
/// 进程探测接口:存在性 + 创建时间 + exe 文件名。注入点:测试用 FakeProbe 模拟 Unknown,
/// 生产用 NativeProcessProbe(Toolhelp32 + GetProcessTimes,零第三方依赖)。
/// </summary>
public interface IProcessProbe
{
    ProcessProbeResult Probe(int pid);

    IReadOnlyList<ProcessSnapshotEntry> EnumerateAll();
}

/// <summary>
/// 原生实现:
/// - CreateToolhelp32Snapshot 判定存在性并取 exe 文件名;快照未命中 = Gone(明确不存在)。
/// - OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + GetProcessTimes 取真实创建时间;
///   句柄打开失败(权限等)时启动时间不可得 → 由核验方归入 Unverifiable(fail-closed)。
/// - 快照创建失败 → Unknown(查询失败,不是进程消失,禁止据此清理登记)。
/// </summary>
public sealed class NativeProcessProbe : IProcessProbe
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public ProcessProbeResult Probe(int pid)
    {
        try
        {
            foreach (var entry in EnumerateAll())
            {
                if (entry.Pid != pid)
                {
                    continue;
                }

                DateTimeOffset? startedAt = null;
                IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        if (GetProcessTimes(handle, out long creation, out _, out _, out _))
                        {
                            startedAt = DateTimeOffset.FromFileTime(creation);
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }

                return new ProcessProbeResult(ProcessLiveness.Alive, startedAt, entry.ExePath);
            }

            return new ProcessProbeResult(ProcessLiveness.Gone, null, null);
        }
        catch
        {
            // 查询本身失败:未知,不当作 gone(fail-closed)。
            return new ProcessProbeResult(ProcessLiveness.Unknown, null, null);
        }
    }

    public IReadOnlyList<ProcessSnapshotEntry> EnumerateAll()
    {
        var result = new List<ProcessSnapshotEntry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot 失败。");
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process32FirstW 失败。");
            }

            do
            {
                result.Add(new ProcessSnapshotEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
