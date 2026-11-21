# NetPrio

A lightweight Windows 11 **WinUI 3** utility for dynamic per-application network prioritization powered by **WinDivert** — works even without a QoS-capable router and alongside TUN-based VPNs.

## Features
- **3 Priority Tiers (`Highest` / `Medium` / `Low`)**: Move `.exe` processes between tiers or pick from running processes.
- **Adaptive Shaping (Fast Attack / Slow Release)**: Automatically throttles background downloads (e.g., Steam, torrents) only when high-priority apps are actively using the network, then smoothly restores bandwidth.
- **Local Proxy & VPN Friendly**: Includes a non-blocking `WINDIVERT_FLAG_SNIFF` loopback monitor for apps routed through local SOCKS5/HTTP proxies (`127.0.0.1`).
- **System Tray & Bilingual UI**: Minimizes to the system tray on minimize or close (`✕`), with automatic/manual **English** and **Russian** UI switching.

## Requirements
1. **Windows 11 (x64)** with Administrator privileges (required by WinDivert).
2. Download **WinDivert 2.2.2** (`WinDivert-2.2.2-A.zip`) from the [official WinDivert repository](https://github.com/basil00/WinDivert/releases) and place `WinDivert.dll` and `WinDivert64.sys` (from `x64/`) next to `NetPrio.exe` (or in the project root before building).

## Build from CLI
```powershell
dotnet build -c Release -p:Platform=x64
dotnet run -p:Platform=x64
```

Configuration is stored in `%APPDATA%\NetPrio\config.json`.
