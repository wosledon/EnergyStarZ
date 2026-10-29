# EnergyStarZ

[English Documentation](README.md) | [中文文档](README_zh.md)

EnergyStarZ 是一款为 Windows 设计的高性能能源效率管理器，通过调整进程优先级和限制后台应用程序来自动优化功耗。它有助于延长笔记本电脑的电池续航时间，同时为前台应用程序保持最佳性能。

## 功能特性

- **自动电源管理**：自动检测前台应用程序并对后台进程应用效率设置
- **三种操作模式**：
  - 自动模式：根据前台应用程序自动调整
  - 手动模式：停止响应前台应用切换，后台进程仍会按周期被节流
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
- `SwitchDebounceMs`：前台切换后节流上一个应用所需的最小间隔（默认：500）
- `EnableLogging`：启用/禁用日志（默认：true）
- `InitialMode`：启动模式（自动、手动或暂停）
- `BypassProcessList`：从电源管理中排除的进程列表
- `EnableAutoPowerMode`：使用电池时自动开启节能、接通交流电时暂停（默认：false）
- `LRUCacheSize`：保持不节流的最近使用应用数量（默认：5）
- `LRUDecayMinutes`：LRU 条目闲置多久后过期（默认：5）
- `LRUMinSize` / `LRUMaxSize`：LRU 大小自动调整的上下限（默认：3 / 15）
- `AudibleProcessList`：被视为"可能正在播音"而永不节流的进程名单（启发式）
- `EnableEfficiencyCoreBinding`：混合架构 CPU 上把被节流进程绑定到能效核（探测失败时自动禁用，默认：true）
- `EnableLowMemoryPriority`：对被节流进程设置低内存优先级（效果很强，但可能让内存密集型后台程序明显变慢，默认：true）
- `DeepModeIdleMinutes`：用户空闲多少分钟后进入深度节流模式（默认：5）
- `LowBatteryEscalatePercent`：电量低于该百分比时提升节流强度（默认：20）

## 工作原理

EnergyStarZ 监控前台应用程序切换，并对后台进程应用电源限制。当前台应用程序变为活动状态时，它会获得更高的优先级和性能。后台进程会被限制以节省能源。

应用程序使用 Windows 的 PROCESS_POWER_THROTTLING API 控制进程执行速度，并调整进程优先级以优化能源消耗。

### 分层节流策略

节流不再是单一的"开/关"，而是分层施加；每个后台进程被归入某一档，档位变化时只应用差异部分：

- **受保护（永不节流）**：硬编码系统进程、`BypassProcessList`、前台/待处理应用、LRU 最近使用应用、拥有可见顶层窗口的进程、`AudibleProcessList` 中的进程，以及处于全屏/演示模式时的相关进程。
- **Tier 1（默认后台）**：EcoQoS 执行速度节流 + `IDLE_PRIORITY_CLASS | PROCESS_MODE_BACKGROUND_BEGIN`（同时压低 IO 与内存调度）+ 低内存优先级。
- **Tier 2（升级档）**：在 Tier 1 基础上把进程绑定到能效核（仅混合架构 CPU）。触发条件：熄屏、用户长时间空闲、电量过低，或反馈测量（`QueryProcessCycleTime`）发现该进程仍持续占用 CPU。
- **还原**：清除全部控制（EcoQoS 关闭、恢复正常优先级、退出后台模式、释放 CPU set 绑定）。

在 16 核机器上、有 CPU 竞争的环境实测：Tier 1 可把繁忙后台进程的 CPU 周期降到约 28%，叠加低内存优先级后约 4%，退出时完全恢复。

## 系统要求

- Windows 11 24H2 或更高版本
- .NET 10.0 运行时

## 致谢

本项目基于 [imbushuo](https://github.com/imbushuo/EnergyStar) 的原始 EnergyStar 项目。特别感谢原作者在 Windows 电源管理方面的创新方法。

## 许可证

本项目采用 MIT 许可证 - 详情请见 LICENSE 文件。