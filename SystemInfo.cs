using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace PCMonitorConsoleApp;

public record MemoryInfo(double TotalMB, double UsedMB, float PercentUsed);

public record DiskInfo(
    string Name,
    double TotalSizeGB,
    double FreeSpaceGB,
    float PercentUsed,
    float ReadSpeedMBps,
    float WriteSpeedMBps);

public record NetworkInfo(
    string Name,
    float DownloadSpeedMbps,
    float UploadSpeedMbps,
    double TotalDownloadedGB,
    double TotalUploadedGB);

public record CpuHwInfo(float Temperature);

public record GpuInfo(
    string Name,
    float CoreLoad,
    float CoreTemp,
    float CoreClock,
    float MemoryClock,
    double VramTotalMB,
    double VramUsedMB,
    float VramPercentUsed);

public static class HardwareMonitor
{
    private static Computer _computer;

    private static IHardware? _cpu;
    private static IHardware? _gpu;
    private static IHardware? _memory;
    private static IHardware? _motherboard;

    public static void Initialize()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };
        _computer.Open();

        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _motherboard = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        _memory = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        _gpu = _computer.Hardware.FirstOrDefault(h =>
            h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia ||
            h.HardwareType == HardwareType.GpuIntel);
    }

    public static void Shutdown()
    {
        _computer?.Close();
    }

    public static string GetCpuName()
    {
        return _cpu?.Name ?? "N/A";
    }

    public static string GetGpuName()
    {
        return _gpu?.Name ?? "N/A";
    }

    public static string GetMotherboardName()
    {
        _motherboard?.Update();
        return _motherboard?.Name ?? "N/A";
    }

    public static CpuHwInfo GetCpuInfo()
    {
        if (_cpu == null) return new CpuHwInfo(0);
        _cpu.Update();
        var tempSensor =
            _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "CPU Package");
        return new CpuHwInfo(tempSensor?.Value ?? 0);
    }

    public static MemoryInfo GetMemoryInfo()
    {
        if (_memory == null) return new MemoryInfo(0, 0, 0);

        _memory.Update();

        var usedMemSensor =
            _memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used");
        var availableMemSensor =
            _memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Available");

        if (usedMemSensor?.Value == null || availableMemSensor?.Value == null) return new MemoryInfo(0, 0, 0);

        var usedGB = usedMemSensor.Value.Value;
        var availableGB = availableMemSensor.Value.Value;
        var totalGB = usedGB + availableGB;

        var percent = totalGB > 0 ? (float)(usedGB / totalGB * 100.0) : 0;

        return new MemoryInfo(totalGB * 1024, usedGB * 1024, percent);
    }

    public static GpuInfo? GetGpuInfo()
    {
        if (_gpu == null) return null;

        _gpu.Update();
        var coreLoad = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core"))
            ?.Value ?? 0;
        var coreTemp =
            _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core"))
                ?.Value ??
            0;
        var coreClock = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core"))
            ?.Value ?? 0;
        var memClock = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Memory"))
            ?.Value ?? 0;
        var vramUsedSensor =
            _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Used"));
        var vramTotalSensor =
            _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Total"));

        double vramUsedMB = vramUsedSensor?.Value ?? 0;
        double vramTotalMB = vramTotalSensor?.Value ?? 0;

        var vramPercent = vramTotalMB > 0 ? (float)(vramUsedMB * 100 / vramTotalMB) : 0;
        return new GpuInfo(_gpu.Name, coreLoad, coreTemp, coreClock, memClock, vramTotalMB, vramUsedMB, vramPercent);
    }
}

public record CpuInfo(float TotalUsage, int ProcessCount, int ThreadCount);

public static partial class SystemInfo
{
    private static PerformanceCounter? _cpuTotalCounter;
    private static PerformanceCounter? _processCountCounter;
    private static PerformanceCounter? _threadCountCounter;
    private static List<PerformanceCounter>? _diskReadCounters;
    private static List<PerformanceCounter>? _diskWriteCounters;
    private static List<PerformanceCounter>? _netSentCounters;
    private static List<PerformanceCounter>? _netReceivedCounters;

    private static List<string>? _diskInstances;
    private static List<string>? _netInstances;
    private static DriveInfo[]? _allDrives;
    private static Dictionary<string, string>? _sanitizedCounterMap;
    private static NetworkInterface[]? _networkInterfaces;

    private static readonly HttpClient SHttpClient = new();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex SanitizeRegex();

    public static void Initialize()
    {
        _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _processCountCounter = new PerformanceCounter("System", "Processes");
        _threadCountCounter = new PerformanceCounter("System", "Threads");

        var diskCategory = new PerformanceCounterCategory("LogicalDisk");
        _diskInstances =
            new List<string>(diskCategory.GetInstanceNames().Where(i => !i.Equals("_Total") && i.EndsWith(":")));
        _diskReadCounters = _diskInstances.Select(i => new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", i))
            .ToList();
        _diskWriteCounters = _diskInstances
            .Select(i => new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", i)).ToList();

        var netCategory = new PerformanceCounterCategory("Network Interface");
        _netInstances = new List<string>(netCategory.GetInstanceNames());
        _netSentCounters = _netInstances.Select(i => new PerformanceCounter("Network Interface", "Bytes Sent/sec", i))
            .ToList();
        _netReceivedCounters = _netInstances
            .Select(i => new PerformanceCounter("Network Interface", "Bytes Received/sec", i)).ToList();

        _cpuTotalCounter.NextValue();
        _diskReadCounters.ForEach(c => c.NextValue());
        _diskWriteCounters.ForEach(c => c.NextValue());
        _netSentCounters.ForEach(c => c.NextValue());
        _netReceivedCounters.ForEach(c => c.NextValue());

        _allDrives = DriveInfo.GetDrives();
        _networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        _sanitizedCounterMap = _netInstances.ToDictionary(SanitizeName);
    }

    public static CpuInfo GetCpuInfo()
    {
        return new CpuInfo(_cpuTotalCounter.NextValue(), (int)_processCountCounter.NextValue(),
            (int)_threadCountCounter.NextValue());
    }

    public static List<DiskInfo> GetDiskInfo()
    {
        var diskInfos = new List<DiskInfo>();
        if (_diskInstances == null) return diskInfos;
        for (var i = 0; i < _diskInstances.Count; i++)
        {
            var drive = _allDrives?.FirstOrDefault(d => d.Name.StartsWith(_diskInstances[i]));
            if (drive is not { IsReady: true }) continue;
            if (_diskReadCounters == null) continue;
            if (_diskWriteCounters != null)
                diskInfos.Add(new DiskInfo(drive.Name, drive.TotalSize / (1024.0 * 1024.0 * 1024.0),
                    drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0),
                    (float)((drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize),
                    _diskReadCounters[i].NextValue() / (1024.0f * 1024.0f),
                    _diskWriteCounters[i].NextValue() / (1024.0f * 1024.0f)));
        }

        return diskInfos;
    }

    private static string SanitizeName(string name)
    {
        return SanitizeRegex().Replace(name, "");
    }

    public static List<NetworkInfo> GetNetworkInfo()
    {
        var networkInfos = new List<NetworkInfo>();

        if (_networkInterfaces == null) return networkInfos;
        foreach (var ni in _networkInterfaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType ==
                NetworkInterfaceType.Loopback) continue;

            var sanitizedDescription = SanitizeName(ni.Description);

            if (!_sanitizedCounterMap.TryGetValue(sanitizedDescription, out var originalInstanceName)) continue;
            var instanceIndex = _netInstances.IndexOf(originalInstanceName);
            if (instanceIndex == -1) continue;

            var downloadSpeed = _netReceivedCounters[instanceIndex].NextValue() * 8 / (1000.0f * 1000.0f);
            var uploadSpeed = _netSentCounters[instanceIndex].NextValue() * 8 / (1000.0f * 1000.0f);

            var stats = ni.GetIPv4Statistics();

            networkInfos.Add(new NetworkInfo(
                ni.Name,
                downloadSpeed,
                uploadSpeed,
                stats.BytesReceived / (1024.0 * 1024.0 * 1024.0),
                stats.BytesSent / (1024.0 * 1024.0 * 1024.0)
            ));
        }

        return networkInfos;
    }

    public static async Task<string> GetExternalIpAddressAsync()
    {
        try
        {
            return (await SHttpClient.GetStringAsync("https://api.ipify.org")).Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting external IP: {ex.Message}");
            return "N/A";
        }
    }

    public static string GetLocalIpAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return ip.ToString();

        return "N/A";
    }
}