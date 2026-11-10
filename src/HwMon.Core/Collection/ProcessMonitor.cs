using System.Diagnostics;
using System.Runtime.InteropServices;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection;

/// <summary>
/// Processos que mais usam CPU, RAM e disco, agrupados pelo nome do programa (como o Gerenciador de Tarefas junta
/// as abas do Chrome). Só roda quando alguém pede (/v1/processes): a primeira chamada só guarda a base, e cada
/// chamada seguinte mede a diferença desde a anterior. Acesso limitado (PROCESS_QUERY_LIMITED_INFORMATION), então
/// funciona até para processos protegidos; os que recusarem ficam de fora.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly object _lock = new();
    private Dictionary<int, (string Name, long CpuTicks, ulong IoBytes)> _previous = new();
    private long _previousStamp;
    private ProcessesResponse? _last;

    public ProcessesResponse Sample(int top = 8)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_last is not null && Stopwatch.GetElapsedTime(_previousStamp, now) < TimeSpan.FromSeconds(1.5)) return _last;

            var current = new Dictionary<int, (string Name, long CpuTicks, ulong IoBytes)>();
            var ram = new Dictionary<int, ulong>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id is 0 or 4) continue; // Ocioso e System
                    var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        long cpu = 0;
                        if (GetProcessTimes(handle, out _, out _, out var kernel, out var user)) cpu = kernel + user;
                        ulong io = 0;
                        if (GetProcessIoCounters(handle, out var counters)) io = counters.ReadTransferCount + counters.WriteTransferCount;
                        var mem = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                        if (K32GetProcessMemoryInfo(handle, ref mem, mem.Size)) ram[p.Id] = (ulong)mem.WorkingSetSize;
                        current[p.Id] = (p.ProcessName, cpu, io);
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }

            var seconds = _previousStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousStamp, now).TotalSeconds;
            var cores = Environment.ProcessorCount;
            var byName = new Dictionary<string, ProcessUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, now_) in current)
            {
                if (!byName.TryGetValue(now_.Name, out var u)) byName[now_.Name] = u = new ProcessUsage { Name = now_.Name };
                u.Count++;
                u.RamGb += ram.GetValueOrDefault(pid) / 1_073_741_824.0;
                if (seconds > 0 && _previous.TryGetValue(pid, out var before) && before.Name == now_.Name)
                {
                    u.CpuPercent += Math.Max(0, now_.CpuTicks - before.CpuTicks) / 1e7 / seconds / cores * 100;
                    u.DiskMBs += (now_.IoBytes >= before.IoBytes ? now_.IoBytes - before.IoBytes : 0) / 1e6 / seconds;
                }
            }
            _previous = current;
            _previousStamp = now;

            var all = byName.Values.ToList();
            var picked = all.OrderByDescending(u => u.CpuPercent).Take(top)
                .Concat(all.OrderByDescending(u => u.RamGb).Take(top))
                .Concat(all.OrderByDescending(u => u.DiskMBs).Take(top))
                .Distinct()
                .Select(u => new ProcessUsage
                {
                    Name = u.Name, Count = u.Count,
                    CpuPercent = Math.Round(u.CpuPercent, 1), RamGb = Math.Round(u.RamGb, 3), DiskMBs = Math.Round(u.DiskMBs, 2),
                })
                .ToList();
            _last = new ProcessesResponse
            {
                Time = DateTimeOffset.Now,
                LogicalCores = cores,
                IntervalSeconds = Math.Round(seconds, 1),
                ProcessCount = current.Count,
                Processes = picked,
            };
            return _last;
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
