# EnergyStarZ

EnergyStarZ is a high-performance energy efficiency manager for Windows that automatically optimizes power consumption by adjusting process priorities and throttling background applications. It helps extend battery life on laptops while maintaining optimal performance for foreground applications.

## Features

- **Automatic Power Management**: Automatically detects foreground applications and applies efficiency settings to background processes
- **Three Operation Modes**:
  - Auto Mode: Automatically adjusts based on foreground applications
  - Manual Mode: Allows manual control over power settings
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
- `ThrottleDelaySeconds`: Delay before applying efficiency settings (default: 30)
- `EnableLogging`: Enable/disable logging (default: true)
- `InitialMode`: Startup mode (Auto, Manual, or Paused)
- `BypassProcessList`: List of processes to exclude from power management

## How It Works

EnergyStarZ monitors foreground application switches and applies power throttling to background processes. When a process becomes the foreground application, it receives higher priority and performance. Background processes are throttled to conserve energy.

The application uses Windows' PROCESS_POWER_THROTTLING API to control process execution speed and adjusts process priorities to optimize energy consumption.

## Requirements

- Windows 11 24H2 or later
- .NET 10.0 Runtime

## Acknowledgments

This project is based on the original EnergyStar project by [imbushuo](https://github.com/imbushuo/EnergyStar). Special thanks to the original author for the innovative approach to Windows power management.

## License

This project is licensed under the MIT License - see the LICENSE file for details.