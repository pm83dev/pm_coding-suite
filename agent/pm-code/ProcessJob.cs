using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalCodeAgent.Core;

/// <summary>
/// Job Object di Windows: raggruppa un processo e TUTTI i suoi discendenti, anche quelli
/// staccati con Start-Process, che Process.Kill(entireProcessTree) non vede più perché il
/// padre (pwsh) è già uscito. Osservato: 5 "Start-Process dotnet run" di un'app console
/// in loop infinito rimasti vivi per ore, a riempire file di output da 20 MB e bloccare i
/// file di input. Su Linux/macOS è un no-op.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private IntPtr _handle;

    private ProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Mette il processo corrente in un job che uccide tutto alla sua chiusura: quando
    /// l'estensione termina pm-code (Stop in chat, chiusura di VS Code) muoiono anche tutti
    /// i processi avviati dall'agente. L'handle resta aperto per tutta la vita del processo.
    /// </summary>
    public static void KillChildrenOnExit()
    {
        var job = Create();
        if (job == null) return;
        if (!AssignProcessToJobObject(job._handle, Process.GetCurrentProcess().Handle))
            job.Dispose();
        else
            _selfJob = job;
    }
    private static ProcessJob? _selfJob;

    /// <summary>Job per un singolo comando: alla chiusura (Dispose) termina ciò che resta.</summary>
    public static ProcessJob? ForProcess(Process proc)
    {
        var job = Create();
        if (job == null) return null;
        try
        {
            if (AssignProcessToJobObject(job._handle, proc.Handle)) return job;
        }
        catch { /* processo già uscito */ }
        job.Dispose();
        return null;
    }

    /// <summary>PID dei processi ancora vivi nel job.</summary>
    public List<int> ActivePids()
    {
        var pids = new List<int>();
        if (_handle == IntPtr.Zero) return pids;
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: 2 uint (assegnati, in lista) + array di ULONG_PTR
        const int maxPids = 256;
        var size = 8 + IntPtr.Size * maxPids;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buf, (uint)size, IntPtr.Zero))
                return pids;
            var count = Marshal.ReadInt32(buf, 4);
            for (int i = 0; i < count; i++)
                pids.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return pids;
    }

    public void Terminate()
    {
        if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
    }

    /// <summary>
    /// Discendenti di un processo (anche già uscito) ricostruiti dal PID del padre, avviati
    /// dopo <paramref name="since"/> per non confondere un PID riutilizzato. Serve perché il
    /// pwsh installato dallo Store (pacchetto MSIX) crea i figli FUORI dal job: verificato con
    /// "Start-Process ping", che restava vivo dopo TerminateJobObject.
    /// </summary>
    public static List<int> Descendants(int rootPid, DateTime since)
    {
        var result = new List<int>();
        if (!IsWindows) return result;
        var parentOf = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref entry); ok; ok = Process32Next(snap, ref entry))
                parentOf[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }

        var frontier = new HashSet<int> { rootPid };
        while (frontier.Count > 0)
        {
            var next = new HashSet<int>();
            foreach (var (pid, parent) in parentOf)
            {
                if (!frontier.Contains(parent) || pid == rootPid || result.Contains(pid)) continue;
                try { if (Process.GetProcessById(pid).StartTime < since) continue; }
                catch { continue; } // uscito nel frattempo o non accessibile
                result.Add(pid);
                next.Add(pid);
            }
            frontier = next;
        }
        return result;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle); // KILL_ON_JOB_CLOSE: chiude anche i processi rimasti
        _handle = IntPtr.Zero;
    }

    private static ProcessJob? Create()
    {
        if (!IsWindows) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)len))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return new ProcessJob(handle);
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo, IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
