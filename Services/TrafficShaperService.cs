using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NetPrio.Models;

namespace NetPrio.Services;

public record TrafficStats(
    double HighMbps,
    double MedMbps,
    double LowMbps,
    double LowLimitMbps,
    double MedLimitMbps,
    bool IsLowCapped,
    bool IsMedCapped);

public class TrafficShaperService
{
    private const ulong WINDIVERT_FLAG_SNIFF = 1;

    private readonly PriorityConfig _config;
    private CancellationTokenSource? _cts;
    private IntPtr _divertHandle = WinDivertNative.INVALID_HANDLE_VALUE;
    private IntPtr _loopbackSniffHandle = WinDivertNative.INVALID_HANDLE_VALUE;

    private ConcurrentDictionary<int, string> _tcpPortMap = new();
    private ConcurrentDictionary<int, string> _udpPortMap = new();

    private volatile bool _medProcessRunning;
    private volatile bool _highProcessRunning;

    private long _highBytesWindow;
    private long _medBytesWindow;
    private long _lowBytesWindow;

    private double _currentLowBytesPerSec = double.MaxValue;
    private double _currentMedBytesPerSec = double.MaxValue;

    public event Action<string>? StatusChanged;
    public event Action<TrafficStats>? StatsUpdated;

    public TrafficShaperService(PriorityConfig config)
    {
        _config = config;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;

        _divertHandle = WinDivertNative.WinDivertOpen(
            "!loopback and ip and (tcp or udp)",
            WinDivertNative.WINDIVERT_LAYER_NETWORK,
            0,
            0);

        if (_divertHandle == WinDivertNative.INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            StatusChanged?.Invoke($"Ошибка запуска WinDivert (код {err}). Проверьте WinDivert.dll и WinDivert64.sys.");
            return;
        }

        // Отдельный SNIFF-хэндл для отслеживания приложений, работающих через локальный прокси (127.0.0.1)
        _loopbackSniffHandle = WinDivertNative.WinDivertOpen(
            "loopback and ip and (tcp or udp)",
            WinDivertNative.WINDIVERT_LAYER_NETWORK,
            1,
            WINDIVERT_FLAG_SNIFF);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorProcessesLoopAsync(_cts.Token));
        _ = Task.Run(() => AdaptiveControlLoopAsync(_cts.Token));
        _ = Task.Run(() => PacketLoop(_cts.Token));

        if (_loopbackSniffHandle != WinDivertNative.INVALID_HANDLE_VALUE)
            _ = Task.Run(() => LoopbackSniffLoop(_cts.Token));

        StatusChanged?.Invoke("Адаптивный шейпер активен");
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_divertHandle != WinDivertNative.INVALID_HANDLE_VALUE)
        {
            WinDivertNative.WinDivertClose(_divertHandle);
            _divertHandle = WinDivertNative.INVALID_HANDLE_VALUE;
        }
        if (_loopbackSniffHandle != WinDivertNative.INVALID_HANDLE_VALUE)
        {
            WinDivertNative.WinDivertClose(_loopbackSniffHandle);
            _loopbackSniffHandle = WinDivertNative.INVALID_HANDLE_VALUE;
        }
        _cts = null;
        _currentLowBytesPerSec = double.MaxValue;
        _currentMedBytesPerSec = double.MaxValue;
        StatusChanged?.Invoke("Шейпер остановлен");
    }

    private async Task MonitorProcessesLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var runningNames = Process.GetProcesses()
                    .Select(p => { try { return p.ProcessName + ".exe"; } catch { return ""; } })
                    .Where(n => n.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _highProcessRunning = _config.HighestApps.Any(a => runningNames.Contains(a));
                _medProcessRunning = _config.MediumApps.Any(a => runningNames.Contains(a));

                RefreshPortMaps();
            }
            catch { /* ignore transient errors */ }

            await Task.Delay(1500, ct);
        }
    }

    private async Task AdaptiveControlLoopAsync(CancellationToken ct)
    {
        double emaHigh = 0;
        double emaMed = 0;
        double currentLowMbps = _config.ChannelDownloadMbps;
        double currentMedMbps = _config.ChannelDownloadMbps;
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct);
            tick++;

            long hb = Interlocked.Exchange(ref _highBytesWindow, 0);
            long mb = Interlocked.Exchange(ref _medBytesWindow, 0);
            long lb = Interlocked.Exchange(ref _lowBytesWindow, 0);

            double instHigh = (hb * 8.0) / (0.25 * 1_000_000.0);
            double instMed = (mb * 8.0) / (0.25 * 1_000_000.0);
            double instLow = (lb * 8.0) / (0.25 * 1_000_000.0);

            emaHigh = 0.55 * instHigh + 0.45 * emaHigh;
            emaMed = 0.55 * instMed + 0.45 * emaMed;

            double total = Math.Max(1.0, _config.ChannelDownloadMbps);
            double floor = Math.Clamp(_config.MinimumFloorMbps, 0.5, total);

            bool highSpike = instHigh > 1.2;
            bool highBusy = emaHigh > 0.35;

            double medTarget = total;
            if (highSpike)
                medTarget = Math.Max(floor, total * (_config.MediumCapPercent / 100.0));
            else if (highBusy)
                medTarget = Math.Clamp(total - emaHigh * 1.30, floor, total);

            double maxLowCeiling = _medProcessRunning ? total * 0.85 : total;
            double lowTarget = maxLowCeiling;

            if (highSpike)
            {
                lowTarget = Math.Max(floor, total * (_config.LowCapPercent / 100.0));
            }
            else if (highBusy || emaMed > 0.35)
            {
                double headroomUsed = (emaHigh * 1.30) + (emaMed * 1.15);
                lowTarget = Math.Clamp(maxLowCeiling - headroomUsed, floor, maxLowCeiling);
            }

            double stepUp = total * 0.04;

            currentMedMbps = medTarget < currentMedMbps
                ? medTarget
                : Math.Min(medTarget, currentMedMbps + stepUp);

            currentLowMbps = lowTarget < currentLowMbps
                ? lowTarget
                : Math.Min(lowTarget, currentLowMbps + stepUp);

            bool capMed = currentMedMbps < total * 0.98;
            bool capLow = currentLowMbps < total * 0.98;

            _currentMedBytesPerSec = capMed ? (currentMedMbps * 1_000_000.0) / 8.0 : double.MaxValue;
            _currentLowBytesPerSec = capLow ? (currentLowMbps * 1_000_000.0) / 8.0 : double.MaxValue;

            if (tick % 2 == 0)
            {
                StatsUpdated?.Invoke(new TrafficStats(
                    Math.Round(emaHigh, 1),
                    Math.Round(emaMed, 1),
                    Math.Round(instLow, 1),
                    Math.Round(currentLowMbps, 1),
                    Math.Round(currentMedMbps, 1),
                    capLow,
                    capMed));
            }
        }
    }

    private void LoopbackSniffLoop(CancellationToken ct)
    {
        var packet = new byte[65535];
        while (!ct.IsCancellationRequested && _loopbackSniffHandle != WinDivertNative.INVALID_HANDLE_VALUE)
        {
            if (!WinDivertNative.WinDivertRecv(_loopbackSniffHandle, packet, (uint)packet.Length, out uint recvLen, out _))
                break;

            if (recvLen < 24) continue;
            int ihl = (packet[0] & 0x0F) * 4;
            byte proto = packet[9];
            if (recvLen < ihl + 4) continue;

            int srcPort = (packet[ihl] << 8) | packet[ihl + 1];
            int dstPort = (packet[ihl + 2] << 8) | packet[ihl + 3];

            var map = proto == 6 ? _tcpPortMap : (proto == 17 ? _udpPortMap : null);
            if (map == null) continue;

            map.TryGetValue(srcPort, out var srcExe);
            map.TryGetValue(dstPort, out var dstExe);

            if ((srcExe != null && _config.HighestApps.Contains(srcExe, StringComparer.OrdinalIgnoreCase)) ||
                (dstExe != null && _config.HighestApps.Contains(dstExe, StringComparer.OrdinalIgnoreCase)))
            {
                Interlocked.Add(ref _highBytesWindow, recvLen);
            }
            else if ((srcExe != null && _config.MediumApps.Contains(srcExe, StringComparer.OrdinalIgnoreCase)) ||
                     (dstExe != null && _config.MediumApps.Contains(dstExe, StringComparer.OrdinalIgnoreCase)))
            {
                Interlocked.Add(ref _medBytesWindow, recvLen);
            }
        }
    }

    private void PacketLoop(CancellationToken ct)
    {
        var packet = new byte[65535];
        double lowTokens = 1_000_000;
        double medTokens = 1_000_000;
        var sw = Stopwatch.StartNew();
        double lastSec = sw.Elapsed.TotalSeconds;

        while (!ct.IsCancellationRequested && _divertHandle != WinDivertNative.INVALID_HANDLE_VALUE)
        {
            if (!WinDivertNative.WinDivertRecv(_divertHandle, packet, (uint)packet.Length, out uint recvLen, out var addr))
                break;

            double now = sw.Elapsed.TotalSeconds;
            double dt = now - lastSec;
            lastSec = now;

            double lowLimit = _currentLowBytesPerSec;
            double medLimit = _currentMedBytesPerSec;

            if (lowLimit < double.MaxValue)
                lowTokens = Math.Min(lowLimit, lowTokens + dt * lowLimit);
            if (medLimit < double.MaxValue)
                medTokens = Math.Min(medLimit, medTokens + dt * medLimit);

            bool shouldDrop = false;
            var ownerExe = IdentifyProcessFromPacket(packet, (int)recvLen, addr.IsOutbound);

            if (ownerExe != null)
            {
                if (_config.HighestApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                {
                    Interlocked.Add(ref _highBytesWindow, recvLen);
                }
                else if (_config.MediumApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                {
                    if (medLimit < double.MaxValue)
                    {
                        if (medTokens >= recvLen) medTokens -= recvLen;
                        else shouldDrop = true;
                    }
                    if (!shouldDrop) Interlocked.Add(ref _medBytesWindow, recvLen);
                }
                else if (_config.LowApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                {
                    if (lowLimit < double.MaxValue)
                    {
                        if (lowTokens >= recvLen) lowTokens -= recvLen;
                        else shouldDrop = true;
                    }
                    if (!shouldDrop) Interlocked.Add(ref _lowBytesWindow, recvLen);
                }
            }

            if (!shouldDrop)
            {
                WinDivertNative.WinDivertHelperCalcChecksums(packet, recvLen, ref addr, 0);
                WinDivertNative.WinDivertSend(_divertHandle, packet, recvLen, out _, ref addr);
            }
        }
    }

    private string? IdentifyProcessFromPacket(byte[] packet, int len, bool isOutbound)
    {
        if (len < 24) return null;
        int ihl = (packet[0] & 0x0F) * 4;
        byte proto = packet[9];
        if (len < ihl + 4) return null;

        int srcPort = (packet[ihl] << 8) | packet[ihl + 1];
        int dstPort = (packet[ihl + 2] << 8) | packet[ihl + 3];
        int localPort = isOutbound ? srcPort : dstPort;

        if (proto == 6 && _tcpPortMap.TryGetValue(localPort, out var tcpExe)) return tcpExe;
        if (proto == 17 && _udpPortMap.TryGetValue(localPort, out var udpExe)) return udpExe;
        return null;
    }

    private void RefreshPortMaps()
    {
        var pidToName = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { pidToName[p.Id] = p.ProcessName + ".exe"; } catch { }
        }

        _tcpPortMap = ReadTcpTable(pidToName);
        _udpPortMap = ReadUdpTable(pidToName);
    }

    private static ConcurrentDictionary<int, string> ReadTcpTable(Dictionary<int, string> pidMap)
    {
        var result = new ConcurrentDictionary<int, string>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) == 0)
            {
                int count = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = buffer + 4;
                for (int i = 0; i < count; i++)
                {
                    int localPortNet = Marshal.ReadInt32(rowPtr + 8);
                    int port = ((localPortNet & 0xFF) << 8) | ((localPortNet >> 8) & 0xFF);
                    int pid = Marshal.ReadInt32(rowPtr + 20);
                    if (pidMap.TryGetValue(pid, out var name)) result[port] = name;
                    rowPtr += 24;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private static ConcurrentDictionary<int, string> ReadUdpTable(Dictionary<int, string> pidMap)
    {
        var result = new ConcurrentDictionary<int, string>();
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, 1, 0);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, 2, 1, 0) == 0)
            {
                int count = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = buffer + 4;
                for (int i = 0; i < count; i++)
                {
                    int localPortNet = Marshal.ReadInt32(rowPtr + 4);
                    int port = ((localPortNet & 0xFF) << 8) | ((localPortNet >> 8) & 0xFF);
                    int pid = Marshal.ReadInt32(rowPtr + 8);
                    if (pidMap.TryGetValue(pid, out var name)) result[port] = name;
                    rowPtr += 12;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);
}
