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
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

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

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop WinDivert",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(2000);
        }
        catch { /* ignore if another app holds WinDivert */ }

        StatusChanged?.Invoke("Шейпер остановлен");
    }

    private async Task MonitorProcessesLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (pidMap, runningNames) = GetProcessSnapshot();

                _highProcessRunning = _config.HighestApps.Any(a => runningNames.Contains(a));
                _medProcessRunning = _config.MediumApps.Any(a => runningNames.Contains(a));

                _tcpPortMap = ReadTcpTable(pidMap);
                _udpPortMap = ReadUdpTable(pidMap);
            }
            catch { /* ignore transient errors */ }

            await Task.Delay(2000, ct);
        }
    }

    private static (Dictionary<int, string> PidMap, HashSet<string> Names) GetProcessSnapshot()
    {
        var pidMap = new Dictionary<int, string>(128);
        var names = new HashSet<string>(64, StringComparer.OrdinalIgnoreCase);

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return (pidMap, names);

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    if (!string.IsNullOrEmpty(entry.szExeFile))
                    {
                        pidMap[(int)entry.th32ProcessID] = entry.szExeFile;
                        names.Add(entry.szExeFile);
                    }
                }
                while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return (pidMap, names);
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
            try
            {

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
                lowTarget = Math.Clamp(maxLowCeiling - headroomUsed, Math.Min(floor, maxLowCeiling), maxLowCeiling);
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
            catch (OperationCanceledException) { break; }
            catch { /* keep telemetry loop alive */ }
        }
    }

    private void LoopbackSniffLoop(CancellationToken ct)
    {
        const int maxBatch = 64;
        var packetBuf = new byte[maxBatch * 2048];
        var addrs = new WinDivertNative.WINDIVERT_ADDRESS[maxBatch];

        while (!ct.IsCancellationRequested && _loopbackSniffHandle != WinDivertNative.INVALID_HANDLE_VALUE)
        {
            uint addrLen = (uint)(maxBatch * WinDivertNative.ADDR_SIZE);
            if (!WinDivertNative.WinDivertRecvEx(_loopbackSniffHandle, packetBuf, (uint)packetBuf.Length, out uint recvLen, 0, addrs, ref addrLen, IntPtr.Zero))
                break;

            int count = (int)(addrLen / WinDivertNative.ADDR_SIZE);
            int offset = 0;

            for (int i = 0; i < count && offset + 24 <= recvLen; i++)
            {
                int pktLen = (packetBuf[offset + 2] << 8) | packetBuf[offset + 3];
                if (pktLen < 24 || offset + pktLen > recvLen) break;

                int ihl = (packetBuf[offset] & 0x0F) * 4;
                byte proto = packetBuf[offset + 9];
                if (pktLen >= ihl + 4)
                {
                    int srcPort = (packetBuf[offset + ihl] << 8) | packetBuf[offset + ihl + 1];
                    int dstPort = (packetBuf[offset + ihl + 2] << 8) | packetBuf[offset + ihl + 3];

                    var map = proto == 6 ? _tcpPortMap : (proto == 17 ? _udpPortMap : null);
                    if (map != null)
                    {
                        map.TryGetValue(srcPort, out var srcExe);
                        map.TryGetValue(dstPort, out var dstExe);

                        if ((srcExe != null && _config.HighestApps.Contains(srcExe, StringComparer.OrdinalIgnoreCase)) ||
                            (dstExe != null && _config.HighestApps.Contains(dstExe, StringComparer.OrdinalIgnoreCase)))
                        {
                            Interlocked.Add(ref _highBytesWindow, pktLen);
                        }
                        else if ((srcExe != null && _config.MediumApps.Contains(srcExe, StringComparer.OrdinalIgnoreCase)) ||
                                 (dstExe != null && _config.MediumApps.Contains(dstExe, StringComparer.OrdinalIgnoreCase)))
                        {
                            Interlocked.Add(ref _medBytesWindow, pktLen);
                        }
                    }
                }
                offset += pktLen;
            }
        }
    }

    private void PacketLoop(CancellationToken ct)
    {
        const int maxBatch = 64;
        var recvBuf = new byte[maxBatch * 2048];
        var sendBuf = new byte[maxBatch * 2048];
        var recvAddrs = new WinDivertNative.WINDIVERT_ADDRESS[maxBatch];
        var sendAddrs = new WinDivertNative.WINDIVERT_ADDRESS[maxBatch];

        var sendPin = GCHandle.Alloc(sendBuf, GCHandleType.Pinned);
        IntPtr sendBasePtr = sendPin.AddrOfPinnedObject();

        double lowTokens = 1_000_000;
        double medTokens = 1_000_000;
        var sw = Stopwatch.StartNew();
        double lastSec = sw.Elapsed.TotalSeconds;

        try
        {
            while (!ct.IsCancellationRequested && _divertHandle != WinDivertNative.INVALID_HANDLE_VALUE)
            {
                uint addrLen = (uint)(maxBatch * WinDivertNative.ADDR_SIZE);
                if (!WinDivertNative.WinDivertRecvEx(_divertHandle, recvBuf, (uint)recvBuf.Length, out uint recvLen, 0, recvAddrs, ref addrLen, IntPtr.Zero))
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

                bool skipChecksum = _config.SkipChecksumRecalc;
                int count = (int)(addrLen / WinDivertNative.ADDR_SIZE);
                int recvOffset = 0;
                int sendOffset = 0;
                int sendCount = 0;

                for (int i = 0; i < count && recvOffset + 24 <= recvLen; i++)
                {
                    int pktLen = (recvBuf[recvOffset + 2] << 8) | recvBuf[recvOffset + 3];
                    if (pktLen < 24 || recvOffset + pktLen > recvLen) break;

                    bool shouldDrop = false;
                    var ownerExe = IdentifyProcessFromPacket(recvBuf, recvOffset, pktLen, recvAddrs[i].IsOutbound);

                    if (ownerExe != null)
                    {
                        if (_config.HighestApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                        {
                            Interlocked.Add(ref _highBytesWindow, pktLen);
                        }
                        else if (_config.MediumApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                        {
                            if (medLimit < double.MaxValue)
                            {
                                if (medTokens >= pktLen) medTokens -= pktLen;
                                else shouldDrop = true;
                            }
                            if (!shouldDrop) Interlocked.Add(ref _medBytesWindow, pktLen);
                        }
                        else if (_config.LowApps.Contains(ownerExe, StringComparer.OrdinalIgnoreCase))
                        {
                            if (lowLimit < double.MaxValue)
                            {
                                if (lowTokens >= pktLen) lowTokens -= pktLen;
                                else shouldDrop = true;
                            }
                            if (!shouldDrop) Interlocked.Add(ref _lowBytesWindow, pktLen);
                        }
                    }

                    if (!shouldDrop)
                    {
                        Buffer.BlockCopy(recvBuf, recvOffset, sendBuf, sendOffset, pktLen);
                        sendAddrs[sendCount] = recvAddrs[i];
                        if (!skipChecksum)
                        {
                            WinDivertNative.WinDivertHelperCalcChecksums(sendBasePtr + sendOffset, (uint)pktLen, ref sendAddrs[sendCount], 0);
                        }
                        sendOffset += pktLen;
                        sendCount++;
                    }

                    recvOffset += pktLen;
                }

                if (sendCount > 0)
                {
                    WinDivertNative.WinDivertSendEx(_divertHandle, sendBuf, (uint)sendOffset, out _, 0, sendAddrs, (uint)(sendCount * WinDivertNative.ADDR_SIZE), IntPtr.Zero);
                }
            }
        }
        finally
        {
            sendPin.Free();
        }
    }

    private string? IdentifyProcessFromPacket(byte[] packet, int offset, int len, bool isOutbound)
    {
        if (len < 24) return null;
        int ihl = (packet[offset] & 0x0F) * 4;
        byte proto = packet[offset + 9];
        if (len < ihl + 4) return null;

        int srcPort = (packet[offset + ihl] << 8) | packet[offset + ihl + 1];
        int dstPort = (packet[offset + ihl + 2] << 8) | packet[offset + ihl + 3];
        int localPort = isOutbound ? srcPort : dstPort;

        if (proto == 6 && _tcpPortMap.TryGetValue(localPort, out var tcpExe)) return tcpExe;
        if (proto == 17 && _udpPortMap.TryGetValue(localPort, out var udpExe)) return udpExe;
        return null;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);
}
