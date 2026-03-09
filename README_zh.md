# EnergyStarZ

[English Documentation](README.md) | [中文文档](README_zh.md)

EnergyStarZ 是一款为 Windows 设计的高性能能源效率管理器，通过调整进程优先级和限制后台应用程序来自动优化功耗。它有助于延长笔记本电脑的电池续航时间，同时为前台应用程序保持最佳性能。

## 功能特性

- **自动电源管理**：自动检测前台应用程序并对后台进程应用效率设置
- **三种操作模式**：
  - 自动模式：根据前台应用程序自动调整
  - 手动模式：允许手动控制电源设置
  - 暂停模式：禁用所有电源管理
- **系统托盘界面**：通过系统托盘图标便捷访问
- **热键支持**：通过键盘快捷键快速切换模式
- **多语言支持**：支持英文和中文
- **可配置设置**：可通过 appsettings.json 自定义
- **低资源占用**：最小的 CPU 和内存占用

## 安装

1. 克隆仓库：
   ```bash
   git clone https://github.com/wosledon/EnergyStarZ.git
   ```

2. 导航到源码目录：
   ```bash
   cd EnergyStarZ/src/EnergyStarZ
   ```

3. 构建项目：
   ```bash
   dotnet build
   ```

4. 运行应用程序：
   ```bash
   dotnet run
   ```

## 使用方法

### 系统托盘菜单
右键点击系统托盘图标访问菜单：
- **自动模式/手动模式/暂停模式**：在操作模式间切换
- **编辑配置**：打开 appsettings.json 进行编辑
- **重载配置**：无需重启即可重载设置
- **退出**：关闭应用程序

### 热键
- `Ctrl+Alt+A`：在操作模式间切换
- `Ctrl+Alt+P`：切换到暂停模式
- `Ctrl+Alt+R`：切换到自动模式

### 配置
编辑 `appsettings.json` 自定义：
- `ScanIntervalMinutes`：后台进程扫描间隔（默认：10）
- `ThrottleDelaySeconds`：应用效率设置前的延迟（默认：30）
- `EnableLogging`：启用/禁用日志（默认：true）
- `InitialMode`：启动模式（自动、手动或暂停）
- `BypassProcessList`：从电源管理中排除的进程列表

## 工作原理

EnergyStarZ 监控前台应用程序切换，并对后台进程应用电源限制。当前台应用程序变为活动状态时，它会获得更高的优先级和性能。后台进程会被限制以节省能源。

应用程序使用 Windows 的 PROCESS_POWER_THROTTLING API 控制进程执行速度，并调整进程优先级以优化能源消耗。

## 系统要求

- Windows 11 24H2 或更高版本
- .NET 10.0 运行时

## 致谢

本项目基于 [imbushuo](https://github.com/imbushuo/EnergyStar) 的原始 EnergyStar 项目。特别感谢原作者在 Windows 电源管理方面的创新方法。

## 许可证

本项目采用 MIT 许可证 - 详情请见 LICENSE 文件。