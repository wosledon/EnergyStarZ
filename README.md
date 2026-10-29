# EnergyStarZ

[中文文档](README_zh.md) | [English Documentation](README.md)

EnergyStarZ is a high-performance energy efficiency manager for Windows that automatically optimizes power consumption by adjusting process priorities and throttling background applications. It helps extend battery life on laptops while maintaining optimal performance for foreground applications.

## Features

- **Automatic Power Management**: Automatically detects foreground applications and applies efficiency settings to background processes
- **Three Operation Modes**:
  - Auto Mode: Automatically adjusts based on foreground applications
  - Manual Mode: Stops reacting to foreground switches; background processes are still throttled periodically
  - Paused Mode: Disables all power management
- **System Tray Interface**: Convenient access through system tray icon
- **Hotkey Support**: Quick mode switching with keyboard shortcuts
- **Multilingual Support**: Available in English and Chinese
- **Configurable Settings**: Customizable via appsettings.json
- **Low Resource Usage**: Minimal CPU and memory footprint

## Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/wosledon/EnergyStarZ.git
   ```

2. Navigate to the source directory:
   ```bash
   cd EnergyStarZ/src/EnergyStarZ
   ```

3. Build the project:
   ```bash
   dotnet build
   ```

4. Run the application:
   ```bash
   dotnet run
   ```

## Usage

### System Tray Menu
Right-click the system tray icon to access the menu:
- **Auto Mode/Manual Mode/Paused Mode**: Switch between operation modes
- **Edit Configuration**: Open appsettings.json for editing
- **Reload Configuration**: Reload settings without restarting
- **Exit**: Close the application

### Hotkeys
- `Ctrl+Alt+A`: Toggle between operation modes
- `Ctrl+Alt+P`: Switch to Paused mode
- `Ctrl+Alt+R`: Switch to Auto mode

### Configuration
Edit `appsettings.json` to customize:
- `ScanIntervalMinutes`: Interval for background process scanning (default: 10)
- `SwitchDebounceMs`: Minimum interval between foreground switches before throttling the previous app (default: 500)
- `EnableLogging`: Enable/disable logging (default: true)
- `InitialMode`: Startup mode (Auto, Manual, or Paused)
- `BypassProcessList`: List of processes to exclude from power management
- `EnableAutoPowerMode`: Auto-enable energy saving on battery and pause on AC power (default: false)
- `LRUCacheSize`: Number of recently used apps kept unthrottled (default: 5)
- `LRUDecayMinutes`: Idle time before an LRU entry expires (default: 5)
- `LRUMinSize` / `LRUMaxSize`: Bounds for automatic LRU size adjustment (default: 3 / 15)
- `AudibleProcessList`: Processes treated as "possibly playing audio" and never throttled (heuristic)
- `EnableEfficiencyCoreBinding`: Pin throttled processes to efficiency cores on hybrid CPUs (auto-disabled when undetectable, default: true)
- `EnableLowMemoryPriority`: Apply low memory priority to throttled processes (very effective, may slow memory-heavy background apps, default: true)
- `DeepModeIdleMinutes`: Idle time before entering deep throttling mode (default: 5)
- `LowBatteryEscalatePercent`: Battery level below which throttling is escalated (default: 20)

## How It Works

EnergyStarZ monitors foreground application switches and applies power throttling to background processes. When a process becomes the foreground application, it receives higher priority and performance. Background processes are throttled to conserve energy.

The application uses Windows' PROCESS_POWER_THROTTLING API to control process execution speed and adjusts process priorities to optimize energy consumption.

### Tiered throttling policy

Throttling is layered instead of a single on/off switch. Each background process is placed in a tier, and only the difference is applied when the tier changes:

- **Protected (never throttled)**: hardcoded system processes, `BypassProcessList`, the foreground/pending app, LRU recently-used apps, processes with a visible top-level window, processes in `AudibleProcessList`, and anything in fullscreen/presentation mode.
- **Tier 1** (default background): EcoQoS execution-speed throttle + `IDLE_PRIORITY_CLASS | PROCESS_MODE_BACKGROUND_BEGIN` (which also lowers I/O and memory scheduling) + low memory priority.
- **Tier 2** (escalated): Tier 1 plus binding to efficiency cores on hybrid CPUs. Entered when the screen is off, the user has been idle, battery is low, or the process still burns CPU after feedback measurement (`QueryProcessCycleTime`).
- **Restore**: all controls are cleared (EcoQoS off, normal priority, background mode end, CPU sets released).

Measured under CPU contention on a 16-core machine, Tier 1 reduced a busy background process to ~28% of its cycles, and adding low memory priority brought it to ~4%, with full restoration afterwards.

## Requirements

- Windows 11 24H2 or later
- .NET 10.0 Runtime

## Acknowledgments

This project is based on the original EnergyStar project by [imbushuo](https://github.com/imbushuo/EnergyStar). Special thanks to the original author for the innovative approach to Windows power management.

## License

This project is licensed under the MIT License - see the LICENSE file for details.