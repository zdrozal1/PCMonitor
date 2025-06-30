using System.Text;
using static PCMonitorConsoleApp.SystemInfo;

namespace PCMonitorConsoleApp;

internal static class Program
{
    private static volatile bool s_keepRunning = true;

    private static string s_internalIP = string.Empty;
    private static string s_externalIP = string.Empty;
    private static string s_cpuName = string.Empty;
    private static string s_motherboardName = string.Empty;
    private static string s_machineInfo = string.Empty;
    private static string s_gpuName = string.Empty;

    private static readonly string s_header = "--- PC LIVE SYSTEM MONITOR ---";
    private static readonly string s_sysInfoLabel = "[ SYSTEM INFORMATION ]";
    private static readonly string s_sysUsageLabel = "[ SYSTEM USAGE ]";
    private static readonly string s_gpuInfoLabel = "[ GPU INFORMATION ]";
    private static readonly string s_gpuNotDetected = "  GPU not detected or supported by LibreHardwareMonitor.";
    private static readonly string s_diskLabel = "[ DISK DRIVES & I/O ]";
    private static readonly string s_networkLabel = "[ NETWORK ACTIVITY ]";
    private static readonly string s_exitMessage = "Press Ctrl+C to exit.";

    private static void Main(string[] args)
    {
        var refreshDelayMs = 1000;
        if (args.Length > 0 && int.TryParse(args[0], out var delaySec) && delaySec >= 1)
        {
            refreshDelayMs = delaySec * 1000;
            Console.WriteLine($"Custom refresh rate set to {delaySec} seconds.");
        }
        else
        {
            Console.WriteLine(
                "Using default refresh rate of 1 second. You can set a custom rate (in seconds) by running: .\\PCMonitorConsoleApp.exe 5");
        }

        Console.Title = "System Monitor";
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            s_keepRunning = false;
        };

        Console.WriteLine("SystemInfo Initializing..");
        Initialize();
        Console.WriteLine("SystemInfo Initialized");

        Console.WriteLine("HardwareMonitor Initializing..");
        HardwareMonitor.Initialize();
        Console.WriteLine("HardwareMonitor Initialized");

        Console.WriteLine("All Monitoring initialized. Attempting to detect CPU, GPU, and Network interfaces.");

        s_internalIP = GetLocalIpAddress();
        s_externalIP = GetExternalIpAddressAsync().GetAwaiter().GetResult();
        s_cpuName = HardwareMonitor.GetCpuName();
        s_motherboardName = HardwareMonitor.GetMotherboardName();
        s_gpuName = HardwareMonitor.GetGpuName();
        s_machineInfo = $"  Machine    : {Environment.MachineName} | User: {Environment.UserName}";
        var cpuCoreCount = $"({Environment.ProcessorCount} Cores)";
        var refreshRateSec = refreshDelayMs / 1000;

        while (s_keepRunning)
        {
            var cpuInfo = GetCpuInfo();
            var cpuHwInfo = HardwareMonitor.GetCpuInfo();
            var memInfo = HardwareMonitor.GetMemoryInfo();
            var diskInfos = GetDiskInfo();
            var networkInfos = GetNetworkInfo();
            var gpuInfo = HardwareMonitor.GetGpuInfo();

            Console.Clear();

            Console.WriteLine(s_header);
            Console.WriteLine(
                $"--- Last Updated: {DateTime.Now:HH:mm:ss} | Refresh Delay: {refreshRateSec}s ---");
            Console.WriteLine();

            Console.WriteLine(s_sysInfoLabel);
            Console.WriteLine($"  CPU Model  : {s_cpuName} {cpuCoreCount}");
            if (s_motherboardName != "N/A") Console.WriteLine($"  Motherboard: {s_motherboardName}");
            Console.WriteLine(s_machineInfo);
            Console.WriteLine();

            Console.WriteLine(s_sysUsageLabel);
            WriteProgressBar("CPU Usage", cpuInfo.TotalUsage, $"{cpuHwInfo.Temperature:F1}°C",
                GetColorForPercentage(cpuInfo.TotalUsage), GetColorForTemperature(cpuHwInfo.Temperature));

            WriteProgressBar("RAM Usage", memInfo.PercentUsed, $"{memInfo.UsedMB:F0} / {memInfo.TotalMB:F0} MB",
                GetColorForPercentage(memInfo.PercentUsed), GetColorForPercentage(memInfo.PercentUsed));
            Console.WriteLine($"  Processes : {cpuInfo.ProcessCount} | Threads: {cpuInfo.ThreadCount}");
            Console.WriteLine();

            if (gpuInfo != null)
            {
                Console.WriteLine($"[ GPU: {s_gpuName} ]");

                WriteProgressBar("GPU Core", gpuInfo.CoreLoad, $"{gpuInfo.CoreTemp:F1}°C",
                    GetColorForPercentage(gpuInfo.CoreLoad), GetColorForTemperature(gpuInfo.CoreTemp));

                WriteProgressBar("GPU VRAM", gpuInfo.VramPercentUsed,
                    $"{gpuInfo.VramUsedMB / 1024.0:F1} / {gpuInfo.VramTotalMB / 1024.0:F1} GB",
                    GetColorForPercentage(gpuInfo.VramPercentUsed), GetColorForPercentage(gpuInfo.VramPercentUsed)
                );

                Console.WriteLine(
                    $"  Clock    : Core: {gpuInfo.CoreClock:F0} MHz | Memory: {gpuInfo.MemoryClock:F0} MHz");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(s_gpuInfoLabel);
                Console.WriteLine(s_gpuNotDetected);
                Console.WriteLine();
            }

            Console.WriteLine(s_diskLabel);
            foreach (var disk in diskInfos)
            {
                var io = $"R: {disk.ReadSpeedMBps:F1} MB/s | W: {disk.WriteSpeedMBps:F1} MB/s";
                WriteProgressBar(disk.Name.Replace("\\", ""), disk.PercentUsed,
                    $"{disk.FreeSpaceGB:F1} GB Free of {disk.TotalSizeGB:F1} GB | {io}",
                    GetColorForPercentage(disk.PercentUsed));
            }

            Console.WriteLine();

            Console.WriteLine(s_networkLabel);

            foreach (var net in networkInfos.Where(net =>
                         net.DownloadSpeedMbps > 0.01 || net.UploadSpeedMbps > 0.01 || net.TotalDownloadedGB > 0.01))
            {
                Console.WriteLine(
                    $"  {net.Name,-25} | ↓ {net.DownloadSpeedMbps,6:F2} Mbps | ↑ {net.UploadSpeedMbps,6:F2} Mbps");
                Console.WriteLine($"  {' ',-25} | IPV4: " + s_internalIP);
                Console.WriteLine($"  {' ',-25} | External: " + s_externalIP);
            }

            Console.WriteLine();

            Console.WriteLine(s_exitMessage);
            Thread.Sleep(refreshDelayMs);
        }

        Console.WriteLine("\n\nShutting down...");
        HardwareMonitor.Shutdown();
        Thread.Sleep(200);
    }

    private static void WriteProgressBar(string title, float percentage, string detailText = "",
        ConsoleColor? detailColor = null, ConsoleColor? percentageColor = null)
    {
        const int barLength = 25;
        var clampedPercentage = Math.Max(0, Math.Min(100, percentage));
        var filledLength = (int)Math.Round(barLength * clampedPercentage / 100.0);

        var bar = new StringBuilder(barLength);
        bar.Append('█', filledLength);
        bar.Append('░', barLength - filledLength);

        Console.Write($"  {title,-10}: [");

        Console.ForegroundColor = GetColorForPercentage(percentage);
        Console.Write(bar.ToString());
        Console.ResetColor();
        Console.Write("] ");

        if (detailColor.HasValue) Console.ForegroundColor = detailColor.Value;
        Console.Write($"{percentage,5:F1}%");
        if (detailColor.HasValue) Console.ResetColor();

        if (!string.IsNullOrEmpty(detailText))
        {
            Console.Write(" | ");
            if (percentageColor.HasValue) Console.ForegroundColor = percentageColor.Value;
            Console.Write(detailText);
            if (percentageColor.HasValue) Console.ResetColor();
        }

        Console.WriteLine();
    }

    private static ConsoleColor GetColorForPercentage(float percentage)
    {
        return percentage switch
        {
            > 75.0f => ConsoleColor.Red,
            > 50.0f => ConsoleColor.Yellow,
            > 25.0f => ConsoleColor.DarkCyan,
            _ => ConsoleColor.White
        };
    }

    private static ConsoleColor GetColorForTemperature(float temperature)
    {
        return temperature switch
        {
            > 85.0f => ConsoleColor.Red,
            > 75.0f => ConsoleColor.Yellow,
            > 50.0f => ConsoleColor.DarkCyan,
            _ => ConsoleColor.White
        };
    }
}