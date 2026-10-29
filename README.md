# EnergyStarZ

**[中文文档](README_zh.md)** | English Documentation | **[项目主页](https://wosledon.github.io/EnergyStarZ/)**

![Version](https://img.shields.io/badge/version-1.1.0-brightgreen)
![Platform](https://img.shields.io/badge/platform-Windows%2011%2024H2+-0078D6)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)
![Build](https://github.com/wosledon/EnergyStarZ/actions/workflows/build-release.yml/badge.svg)

**Windows 能效管理器：自动节流后台进程 —— 笔记本更省电，前台更流畅。**

EnergyStarZ watches which app you are actually using and makes everything else get out of the way — CPU, disk and memory. On a laptop that means longer battery life; on a desktop or on AC power it means a smoother, more responsive foreground app.

---

## ✨ Features

| | |
|---|---|
| **🎯 前台感知** | Listens for foreground window switches. The app you just switched to is boosted instantly; the one you left is throttled. |
| **▤ 分层节流** | Not a single on/off switch — EcoQoS + idle priority + background I/O + low memory priority, escalating to efficiency cores when needed. |
| **⛨ 安全优先** | System processes, your whitelist, apps with visible windows, media playback, fullscreen video and recent apps are never throttled. |
| **▣ 托盘与热键** | Lives in the system tray. Switch modes with `Ctrl+Alt+A / P / R`, or use the menu. English and Chinese included. |
| **↺ 即时还原** | Switch to an app and every restriction is removed immediately. Pause or fully disable at any time — everything is restored. |
| **◔ 极小占用** | A single Toolhelp32 snapshot for process enumeration, batched log writes, and a dedicated writer thread. A few MB of RAM. |

---

## 📥 Installation

### Download a release (recommended)

Grab the latest build from the [Releases](https://github.com/wosledon/EnergyStarZ/releases/latest) page:

| File | When to use |
|---|---|
| `EnergyStarZ-x64-SelfContained-1.1.0.zip` | **Recommended** — bundles the .NET runtime, no prerequisites |
| `EnergyStarZ-x86-SelfContained-1.1.0.zip` | 32-bit systems |
| `EnergyStarZ-x64-FrameworkDependent-1.1.0.zip` | You already have the .NET 10 desktop runtime — much smaller |
| `EnergyStarZ-x86-FrameworkDependent-1.1.0.zip` | 32-bit + runtime already installed |

Unzip anywhere and run `EnergyStarZ.exe` — it appears in the system tray.

### Build from source

```bash
git clone https://github.com/wosledon/EnergyStarZ.git
cd EnergyStarZ/src/EnergyStarZ
dotnet run
```

---

## 🎮 Usage

### System tray menu

Right-click the tray icon:

| Menu item | What it does |
|---|---|
| **Auto / Manual / Paused** | Switch operation mode (see below) |
| **Auto Power Mode** | Use the aggressive profile automatically on battery |
| **Energy Management** | Completely disable or re-enable energy management |
| **Edit Configuration** | Open `appsettings.json` |
| **Reload Configuration** | Apply config changes without restarting |
| **Exit** | Close the application |

### Operation modes

| Mode | Behaviour |
|---|---|
| **Auto** | Reacts to foreground switches and throttles background processes continuously |
| **Manual** | Stops reacting to foreground switches; background processes are still throttled periodically |
| **Paused** | No throttling at all — the app stays warm so resuming is instant |

### Hotkeys

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+A` | Cycle Auto → Manual → Paused |
| `Ctrl+Alt+P` | Switch to Paused |
| `Ctrl+Alt+R` | Switch to Auto |

---

## ⚙️ Configuration

Edit `appsettings.json` next to the executable, then use **Reload Configuration** in the tray menu.

### Basics

| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch. `false` makes the app fully dormant: no hooks, no background work, no throttling — and any throttling left over from a previous run is restored on startup |
| `InitialMode` | `"Auto"` | Startup mode: `Auto`, `Manual` or `Paused` |
| `ScanIntervalMinutes` | `10` | Interval for the background process sweep |
| `SwitchDebounceMs` | `500` | Minimum gap between foreground switches before the previous app is throttled |
| `EnableLogging` | `true` | Write `energy.log` next to the executable (rotates at 1 MB) |

### What is protected

| Key | Default | Description |
|---|---|---|
| `BypassProcessList` | see file | Processes excluded from power management, matched case-insensitively |
| `AudibleProcessList` | see file | Processes treated as "possibly playing audio" and never throttled (heuristic) |

### Recently used apps (LRU)

Apps you switch between often are kept unthrottled so switching back is instant. The cache size adapts to how often you switch.

| Key | Default | Description |
|---|---|---|
| `LRUCacheSize` | `5` | Number of recently used apps kept unthrottled |
| `LRUDecayMinutes` | `5` | Idle time before an LRU entry expires |
| `LRUMinSize` / `LRUMaxSize` | `3` / `15` | Bounds for automatic LRU size adjustment |

### Throttling strength

| Key | Default | Description |
|---|---|---|
| `EnableLowMemoryPriority` | `true` | Apply low memory priority to throttled processes. Very effective, but can noticeably slow memory-heavy background apps |
| `LowMemoryPriorityOnAcPower` | `false` | Also apply it on AC power — off by default so background tasks keep their speed |
| `EnableEfficiencyCoreBinding` | `true` | Pin throttled processes to efficiency cores on hybrid CPUs. Auto-disabled (with a log entry) when the CPU cannot be enumerated or is not hybrid |

### Deep mode

Deep mode escalates throttling when you are clearly not using the machine.

| Key | Default | Description |
|---|---|---|
| `DeepModeIdleMinutes` | `5` | Idle time (no keyboard/mouse) before deep mode kicks in |
| `LowBatteryEscalatePercent` | `20` | Battery level below which throttling is escalated (battery only) |

### Power source

| Key | Default | Description |
|---|---|---|
| `EnableAutoPowerMode` | `false` | Automatically use the aggressive profile on battery |
| `PauseOnAcPower` | `false` | When auto power mode is on, fully pause management on AC power. `false` keeps throttling on AC for responsiveness |

---

## 🔧 How it works

EnergyStarZ monitors foreground application switches and applies power throttling to background processes. When a process becomes the foreground application it receives higher priority and performance; background processes are throttled to conserve energy. It uses Windows' `PROCESS_POWER_THROTTLING` API to control execution speed and adjusts process priorities to optimize energy consumption.

### Tiered throttling policy

Throttling is layered instead of a single on/off switch. Each background process is placed in a tier, and only the difference is applied when the tier changes:

| Tier | What is applied |
|---|---|
| **Protected** — never throttled | Hardcoded system processes · `BypassProcessList` · the foreground/pending app · LRU recently-used apps · processes with a visible top-level window · `AudibleProcessList` · fullscreen/presentation mode |
| **Tier 1** — default background | EcoQoS execution-speed throttle + `IDLE_PRIORITY_CLASS` \| `PROCESS_MODE_BACKGROUND_BEGIN` (which also lowers I/O and memory scheduling) + low memory priority |
| **Tier 2** — escalated | Tier 1 plus binding to efficiency cores on hybrid CPUs. Entered when the screen is off, the user has been idle, battery is low, or the process still burns CPU after feedback measurement (`QueryProcessCycleTime`) |
| **Restore** | All controls are cleared: EcoQoS off, normal priority, background mode end, CPU sets released |

### Measured, not promised

Under CPU contention on a 16-core machine (24 saturated background processes, measured with `QueryProcessCycleTime`):

| Configuration | Background CPU |
|---|---|
| Baseline | 100% |
| EcoQoS only | ~92% |
| + idle priority & background mode | **28%** |
| + low memory priority | **3.7%** |
| Restored | 100% |

Foreground responsiveness (16 saturated background processes, 60fps foreground loop):

| Wake-up latency | Before | After |
|---|---|---|
| p50 | 16.7 ms | 15.1 ms |
| p99 | 29.2 ms | **16.2 ms** |
| max | 31.3 ms | **16.3 ms** |

A 16 ms p99 means a steady 60fps; 29 ms means roughly one dropped frame in eight. Meanwhile the background processes still consumed 95.6% of their previous CPU — they finish their work, they just get out of the way.

---

## ❓ FAQ

**Does it help on a desktop, or a laptop plugged into power?**
Yes — the benefit shifts from battery life to responsiveness. Background processes yield CPU, disk and memory the moment your foreground app needs them, so frame pacing and app switching stay smooth. On hybrid Intel desktops, efficiency-core binding also keeps the P-cores free for your game.

**Will it slow down my downloads, builds or antivirus?**
They run in background mode, so they finish slower while the system is busy — that is the trade-off that keeps your foreground app smooth. Anything with a visible window, anything you used recently, and anything in your whitelist is exempt. On AC power the most aggressive lever (low memory priority) is off by default.

**How do I turn it off completely?**
Untick **Energy Management** in the tray menu (instant, restores everything), or set `"Enabled": false` in `appsettings.json` (the app starts fully dormant and also cleans up anything left over from a previous run). **Paused** mode is the lighter option — no throttling, but the app stays warm for an instant resume.

**Is it safe? What about music or a video call?**
Critical system processes are hardcoded as protected. Processes with a visible window are never throttled, and common media/communication apps are exempt by default (configurable via `AudibleProcessList`). Fullscreen video and presentation mode are detected and respected.

**Why does it require Windows 11 24H2?**
The efficiency-mode and power-throttling APIs it relies on only behave correctly from build 26100 onwards. On older builds the app shows a message and exits instead of misbehaving.

---

## Requirements

- Windows 11 24H2 (build 26100) or later
- .NET 10.0 runtime (only for the framework-dependent builds)

## Acknowledgments

This project is based on the original EnergyStar project by [imbushuo](https://github.com/imbushuo/EnergyStar). Special thanks to the original author for the innovative approach to Windows power management.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
