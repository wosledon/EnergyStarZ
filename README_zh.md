# EnergyStarZ

[English Documentation](README.md) | **中文文档** | **[项目主页](https://wosledon.github.io/EnergyStarZ/)**

![Version](https://img.shields.io/badge/version-1.1.0-brightgreen)
![Platform](https://img.shields.io/badge/platform-Windows%2011%2024H2+-0078D6)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)
![Build](https://github.com/wosledon/EnergyStarZ/actions/workflows/build-release.yml/badge.svg)

**Windows 能效管理器：自动节流后台进程 —— 笔记本更省电，前台更流畅。**

EnergyStarZ 监视你真正在用的应用，让其他所有程序在 CPU、磁盘和内存上立刻让路。笔记本上这意味着更长的续航；台式机或接电源时，意味着前台应用更流畅、更跟手。

---

## ✨ 功能特性

| | |
|---|---|
| **🎯 前台感知** | 监听前台窗口切换。刚切过去的应用立即提速，离开的应用随即被节流。 |
| **▤ 分层节流** | 不是简单的开/关——EcoQoS + 空闲优先级 + 后台 IO + 低内存优先级，必要时升级到能效核。 |
| **⛨ 安全优先** | 系统进程、白名单、有可见窗口的应用、正在播音的程序、全屏视频和最近使用过的应用永不节流。 |
| **▣ 托盘与热键** | 常驻系统托盘。用 `Ctrl+Alt+A / P / R` 切换模式，或使用菜单。内置中英文。 |
| **↺ 即时还原** | 切回某个应用时所有限制立即解除。随时暂停或完全关闭——所有状态都会被还原。 |
| **◔ 极小占用** | 单次 Toolhelp32 快照枚举进程、日志批量落盘、独立写线程。仅占用数 MB 内存。 |

---

## 📥 安装

### 下载发行版（推荐）

从 [Releases](https://github.com/wosledon/EnergyStarZ/releases/latest) 页面获取最新构建：

| 文件 | 适用场景 |
|---|---|
| `EnergyStarZ-x64-SelfContained-1.1.0.zip` | **推荐**——内置 .NET 运行时，无需任何前置条件 |
| `EnergyStarZ-x86-SelfContained-1.1.0.zip` | 32 位系统 |
| `EnergyStarZ-x64-FrameworkDependent-1.1.0.zip` | 已装 .NET 10 桌面运行时——体积小得多 |
| `EnergyStarZ-x86-FrameworkDependent-1.1.0.zip` | 32 位 + 已装运行时 |

解压到任意位置运行 `EnergyStarZ.exe`，图标会出现在系统托盘。

### 从源码构建

```bash
git clone https://github.com/wosledon/EnergyStarZ.git
cd EnergyStarZ/src/EnergyStarZ
dotnet run
```

---

## 🎮 使用

### 系统托盘菜单

右键点击托盘图标：

| 菜单项 | 作用 |
|---|---|
| **自动 / 手动 / 暂停** | 切换操作模式（见下表） |
| **自动电源模式** | 使用电池时自动采用激进档 |
| **节能管理** | 完全关闭或重新启用节能管理 |
| **编辑配置** | 打开 `appsettings.json` |
| **重载配置** | 无需重启即可应用配置更改 |
| **退出** | 关闭应用程序 |

### 操作模式

| 模式 | 行为 |
|---|---|
| **自动** | 响应前台切换，并持续节流后台进程 |
| **手动** | 停止响应前台切换；后台进程仍会按周期被节流 |
| **暂停** | 完全不节流——应用保持热状态，恢复是瞬间的 |

### 热键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+Alt+A` | 循环 自动 → 手动 → 暂停 |
| `Ctrl+Alt+P` | 切换到暂停模式 |
| `Ctrl+Alt+R` | 切换到自动模式 |

---

## ⚙️ 配置

编辑 exe 旁边的 `appsettings.json`，然后在托盘菜单中选择**重载配置**。

### 基础

| 键 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关。`false` 时应用完全休眠：不安装钩子、无后台工作、不施加任何节流，并在启动时还原上次运行可能残留的节流状态 |
| `InitialMode` | `"Auto"` | 启动模式：`Auto`、`Manual` 或 `Paused` |
| `ScanIntervalMinutes` | `10` | 后台进程扫描间隔 |
| `SwitchDebounceMs` | `500` | 前台切换后节流上一个应用所需的最小间隔 |
| `EnableLogging` | `true` | 在 exe 旁写入 `energy.log`（1MB 后轮转） |

### 保护范围

| 键 | 默认值 | 说明 |
|---|---|---|
| `BypassProcessList` | 见文件 | 从电源管理中排除的进程列表，忽略大小写 |
| `AudibleProcessList` | 见文件 | 被视为"可能正在播音"而永不节流的进程名单（启发式） |

### 最近使用应用（LRU）

你频繁切换的应用会保持不节流，切回去时无需等待。缓存大小会随切换频率自动调整。

| 键 | 默认值 | 说明 |
|---|---|---|
| `LRUCacheSize` | `5` | 保持不节流的最近使用应用数量 |
| `LRUDecayMinutes` | `5` | LRU 条目闲置多久后过期 |
| `LRUMinSize` / `LRUMaxSize` | `3` / `15` | LRU 大小自动调整的上下限 |

### 节流强度

| 键 | 默认值 | 说明 |
|---|---|---|
| `EnableLowMemoryPriority` | `true` | 对被节流进程设置低内存优先级。效果很强，但可能让内存密集型后台程序明显变慢 |
| `LowMemoryPriorityOnAcPower` | `false` | 交流电供电时也施加低内存优先级——默认关闭，以免拖慢交流电下的后台任务 |
| `EnableWorkingSetTrim` | `true` | 释放实测空闲的被节流进程的常驻内存（见下文） |
| `WorkingSetTrimMinMB` | `64` | 只裁剪工作集超过该值的进程，避免为省少量内存打扰小程序 |
| `EnableEfficiencyCoreBinding` | `true` | 混合架构 CPU 上把被节流进程绑定到能效核。无法枚举或非混合架构时自动禁用（并记入日志） |

### 深度模式

当你明显不在用电脑时，深度模式会提升节流强度。

| 键 | 默认值 | 说明 |
|---|---|---|
| `DeepModeIdleMinutes` | `5` | 无键鼠输入多久后进入深度模式 |
| `LowBatteryEscalatePercent` | `20` | 电量低于该百分比时提升节流强度（仅电池供电） |

### 电源来源

| 键 | 默认值 | 说明 |
|---|---|---|
| `EnableAutoPowerMode` | `false` | 使用电池时自动采用激进档 |
| `PauseOnAcPower` | `false` | 启用自动电源模式后，接通交流电时是否完全暂停管理。`false` 表示交流电上继续节流以提升流畅度 |

---

## 🔧 工作原理

EnergyStarZ 监控前台应用程序切换，并对后台进程应用电源限制。当前台应用程序变为活动状态时，它会获得更高的优先级和性能；后台进程会被限制以节省能源。程序使用 Windows 的 `PROCESS_POWER_THROTTLING` API 控制进程执行速度，并调整进程优先级以优化能源消耗。

### 分层节流策略

节流不再是单一的"开/关"，而是分层施加；每个后台进程被归入某一档，档位变化时只应用差异部分：

| 档位 | 施加的控制 |
|---|---|
| **受保护**——永不节流 | 硬编码系统进程 · `BypassProcessList` · 前台/待处理应用 · LRU 最近使用应用 · 拥有可见顶层窗口的进程 · `AudibleProcessList` · 全屏/演示模式 |
| **Tier 1**——默认后台 | EcoQoS 执行速度节流 + `IDLE_PRIORITY_CLASS` \| `PROCESS_MODE_BACKGROUND_BEGIN`（同时压低 IO 与内存调度）+ 低内存优先级 |
| **Tier 2**——升级档 | 在 Tier 1 基础上绑定到混合架构 CPU 的能效核。触发条件：熄屏、用户长时间空闲、电量过低，或反馈测量（`QueryProcessCycleTime`）发现该进程仍持续占用 CPU |
| **还原** | 清除全部控制：EcoQoS 关闭、恢复正常优先级、退出后台模式、释放 CPU set 绑定 |

### 用数据说话

16 核机器、CPU 竞争环境实测（24 个满载后台进程，使用 `QueryProcessCycleTime` 测量）：

| 配置 | 后台进程 CPU |
|---|---|
| 基线 | 100% |
| 仅 EcoQoS | ~92% |
| + 空闲优先级与后台模式 | **28%** |
| + 低内存优先级 | **3.7%** |
| 还原后 | 100% |

前台响应速度（16 个满载后台进程 + 60fps 前台循环）：

| 唤醒延迟 | 节流前 | 节流后 |
|---|---|---|
| p50 | 16.7 ms | 15.1 ms |
| p99 | 29.2 ms | **16.2 ms** |
| 最大值 | 31.3 ms | **16.3 ms** |

16ms 的 p99 意味着稳定 60fps；29ms 则大约每 8 帧丢 1 帧。而后台进程仍消耗原有 95.6% 的 CPU——活照样干完，只是不再挡路。

### 内存

低内存优先级会让内存管理器更早回收被节流进程的页面。在此之上，**实测空闲**的被节流进程（连续两次测得低于 2% 单核占用，约 1 分钟）会用 `EmptyWorkingSet` 立即释放常驻内存。

实测一个空闲但常驻 514MB 的进程：工作集降到 **0 MB** 且不再回流——只有进程真正再次访问时页面才会换入。这为前台应用腾出内存，同时减少内存压缩与缺页开销。它只作用于已被节流的进程，你正在用的程序完全不受影响。

---

## ❓ 常见问题

**台式机或笔记本接电源时也有用吗？**
有——收益从省电转向流畅度。后台进程在前台应用需要资源的瞬间立刻让位，帧节奏与切换应用的卡顿随之改善。在混合架构的 Intel 台式机上，能效核绑定还能把 P 核留给游戏。

**会拖慢我的下载、编译或杀毒软件吗？**
它们会以后台模式运行，系统繁忙时完成得更慢——这是保持前台流畅的代价。有可见窗口的程序、你最近用过的程序以及白名单中的程序都不受影响。交流电下默认还会关闭最激进的低内存优先级。

**如何完全关闭？**
在托盘菜单中取消勾选**节能管理**（立即生效并还原所有状态），或在 `appsettings.json` 中设置 `"Enabled": false`（应用完全休眠启动，并清理上次运行可能残留的状态）。**暂停模式**是更轻的选择——不节流，但保持热启动以便瞬间恢复。

**安全吗？听音乐或视频通话会受影响吗？**
关键系统进程已硬编码保护。有可见窗口的进程永不节流，常见媒体/通讯应用默认豁免（可通过 `AudibleProcessList` 配置）。全屏视频与演示模式会被检测并尊重。

**为什么要求 Windows 11 24H2？**
它所依赖的效率模式与电源节流 API 从 build 26100 起才能正确工作。在更旧的版本上，应用会提示并退出，而不是错误运行。

---

## 系统要求

- Windows 11 24H2（build 26100）或更高版本
- .NET 10.0 运行时（仅框架依赖构建需要）

## 致谢

本项目基于 [imbushuo](https://github.com/imbushuo/EnergyStar) 的原始 EnergyStar 项目。特别感谢原作者在 Windows 电源管理方面的创新方法。

## 许可证

本项目采用 MIT 许可证——详见 [LICENSE](LICENSE) 文件。
