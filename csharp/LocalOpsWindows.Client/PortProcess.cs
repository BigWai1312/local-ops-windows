using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalOpsWindows.Client;

public sealed record PortProcess(int Pid, int Port, long Created, string ImagePath)
{
    public static PortProcess Inspect(int port, int? expectedPid = null, long? expectedCreated = null)
    {
        if (port is < 1 or > 65535 || IsManagementPort(port))
            throw new InvalidOperationException("此端口不支持强制停止（包含运维台自己的管理端口）。");
        var owners = ListenerPids(port);
        if (owners.Count != 1)
            throw new InvalidOperationException(owners.Count == 0
                ? "端口已经释放，请刷新后启动项目。"
                : "同一端口有多个监听进程，无法确定唯一目标，请在服务监控中核对。");
        var pid = owners.Single();
        if (expectedPid is not null && pid != expectedPid)
            throw new InvalidOperationException("端口占用者已经变化，请重新查看并确认。");
        using var handle = Open(pid, terminate: false);
        var target = ReadIdentity(handle, pid, port);
        if (expectedCreated is not null && target.Created != expectedCreated)
            throw new InvalidOperationException("原进程已退出或 PID 已复用，已取消操作。");
        return target;
    }

    public void Stop()
    {
        if (Port is < 1 or > 65535 || IsManagementPort(Port))
            throw new InvalidOperationException("此端口不支持强制停止。");
        // Validate and terminate through the same handle so PID reuse cannot redirect the operation.
        using var handle = Open(Pid, terminate: true);
        if (ReadIdentity(handle, Pid, Port) != this)
            throw new InvalidOperationException("进程身份已变化，请重新查看并确认。");
        if (!ListenerPids(Port).SetEquals(new[] { Pid }))
            throw new InvalidOperationException("端口占用者已经变化，已取消操作。");
        if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (WaitForSingleObject(handle, 5000) != 0)
            throw new InvalidOperationException("已发送结束请求，但进程尚未退出，请刷新核实；不会自动重复强停。");
    }

    private static SafeProcessHandle Open(int pid, bool terminate)
    {
        if (pid <= 4 || pid == Environment.ProcessId)
            throw new InvalidOperationException("不能结束系统进程或客户端自身。");
        var handle = OpenProcess(0x1000 | 0x100000 | (terminate ? 1u : 0u), false, pid);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static bool IsManagementPort(int port)
        => port is >= 9600 and <= 9609 or >= 11000 and <= 11009;

    private static PortProcess ReadIdentity(SafeProcessHandle handle, int pid, int port)
    {
        if (WaitForSingleObject(handle, 0) != 0x102)
            throw new InvalidOperationException("进程已退出，请刷新后重试。");
        if (!IsProcessCritical(handle, out var critical)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical) throw new InvalidOperationException("不能结束 Windows 关键进程。");
        if (!ProcessIdToSessionId(pid, out var session)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var current = Process.GetCurrentProcess();
        if (session == 0 || session != current.SessionId)
            throw new InvalidOperationException("不能结束系统服务或其他登录会话的进程。");
        if (!OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (identity.User is null || identity.User != self.User)
                throw new InvalidOperationException("只能手动结束当前 Windows 用户的进程。");
        }
        var size = 32768;
        var image = new StringBuilder(size);
        if (!QueryFullProcessImageName(handle, 0, image, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = image.ToString();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            || new[] { "LocalOpsWindows.Client.exe", "LocalOpsWindowsBackend.exe", "LocalOpsWindows.exe" }
                .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能通过端口强停 Windows 组件或运维台自身。");
        if (!GetProcessTimes(handle, out var createdFileTime, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var created = ((long)createdFileTime.High << 32) | createdFileTime.Low;
        return new PortProcess(pid, port, created, path);
    }

    internal static HashSet<int> ListenerPids(int port)
    {
        var result = new HashSet<int>();
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (status != 0 && status != 122) throw new Win32Exception((int)status);
            if (size < 4) continue;
            for (var attempt = 0; ; attempt++)
            {
                var allocated = size;
                var buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (status == 122 && attempt < 3) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    var stride = family == 2 ? 24 : 56;
                    var count = Marshal.ReadInt32(buffer);
                    if (count < 0 || count > (allocated - 4) / stride)
                        throw new InvalidOperationException("监听端口快照无效，请刷新重试。");
                    for (var index = 0; index < count; index++)
                    {
                        var row = IntPtr.Add(buffer, 4 + index * stride);
                        var portOffset = family == 2 ? 8 : 20;
                        var pidOffset = family == 2 ? 20 : 52;
                        var encoded = Marshal.ReadInt32(row, portOffset);
                        var localPort = ((encoded & 255) << 8) | ((encoded >> 8) & 255);
                        if (localPort == port) result.Add(Marshal.ReadInt32(row, pidOffset));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public int High;
    }
}
