using EnergyStarZ.Interop;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;
using EnergyStarZ.Config;
using EnergyStarZ.Utilities;
using Microsoft.Extensions.Configuration;

namespace EnergyStarZ
{
    public class EnergyManager : IDisposable
    {
        // 配置常量
        private const int DefaultSwitchDebounceMs = 500;
        private const int MinWindowsBuild = 26100;
        private const int ExitErrorCode = 120;

        public static int RequiredWindowsBuild => MinWindowsBuild;
        public static int ExitErrorCodeValue => ExitErrorCode;

        // 硬编码最低保护列表 - 无论配置如何，这些进程永远不被节流
        // 包含 P0 核心系统进程和部分 P1 用户体验关键进程
        private static readonly HashSet<string> _hardcodedBypassList = new(StringComparer.OrdinalIgnoreCase)
        {
            // === P0 核心系统进程 ===
            "csrss.exe",              // Client/Server Runtime Subsystem
            "smss.exe",               // Session Manager Subsystem
            "wininit.exe",            // Windows 启动进程
            "winlogon.exe",           // Windows 登录管理器
            "lsass.exe",              // Local Security Authority Subsystem Service
            "services.exe",           // Service Control Manager
            "svchost.exe",            // Service Host（承载多个 Windows 服务）
            "Registry",               // Registry hive 加载进程（无 .exe 扩展）

            // === P1 显示与 Shell ===
            "dwm.exe",                // Desktop Window Manager
            "explorer.exe",           // Windows Shell（任务栏、文件管理器）
            "fontdrvhost.exe",        // 字体驱动宿主进程
            "sihost.exe",             // Shell Infrastructure Host（通知、操作中心）
            "ShellExperienceHost.exe",// Shell 体验宿主
            "StartMenuExperienceHost.exe", // 开始菜单
            "SearchHost.exe",         // Windows 搜索 UI 宿主

            // === P1 输入与语言 ===
            "ctfmon.exe",             // Text Services Framework（输入法支持）
            "ChsIME.exe",             // 中文输入法（简体）
        };

        public static IReadOnlyCollection<string> BypassProcessList
        {
            get
            {
                lock (_bypassListLock)
                {
                    // 合并配置列表和硬编码保护列表
                    var mergedList = new HashSet<string>(_bypassProcessList, StringComparer.OrdinalIgnoreCase);
                    mergedList.UnionWith(_hardcodedBypassList);
                    return mergedList.ToList().AsReadOnly();
                }
            }
        }

        private static readonly object _bypassListLock = new();
        private static HashSet<string> _bypassProcessList = new(StringComparer.OrdinalIgnoreCase);

        // Speical handling needs for UWP to get the child window process
        public const string UWPFrameHostApp = "ApplicationFrameHost.exe";

        // 使用LRU缓存来防止频繁切换，通过 _lruLock 保证线程安全
        private static readonly object _lruLock = new();
        private static readonly LinkedList<(uint pid, string name, DateTime lastAccess)> _recentlyUsedApps = new();
        private static readonly Dictionary<uint, LinkedListNode<(uint pid, string name, DateTime lastAccess)>> _appLookup = new();
        private static int _lruCacheSize = 5;
        private static int _minLRUSize = 3;
        private static int _maxLRUSize = 15;
        private static TimeSpan _lruDecayPeriod = TimeSpan.FromMinutes(5); // LRU 时间衰减周期
        private static DateTime _lastLRUAdjustment = DateTime.UtcNow;
        private static int _foregroundSwitchCount = 0; // 前台切换计数器（用于自动调整）

        private static readonly object _pendingProcLock = new();
        private static uint _pendingProcPid = 0;

        private static IntPtr pThrottleOn = IntPtr.Zero;
        private static IntPtr pThrottleOff = IntPtr.Zero;
        private static int szControlBlock = 0;
        private static bool _unmanagedResourcesInitialized = false;

        private static readonly ThreadLocal<StringBuilder> _threadLocalStringBuilder =
            new(() => new StringBuilder(1024));

        private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;

        private static readonly ConcurrentDictionary<int, ProcessState> ProcessCache = new();

        // 后台任务
        private static CancellationTokenSource? _backgroundTasksCts;
        private static Task? _processCacheRefreshTask;
        private static Task? _powerStatusCheckTask;
        
        private static volatile bool _isBatteryPowered = false;
        private static volatile bool _autoPowerModeEnabled = false;
        private static volatile int _batteryLevelPercent = -1; // -1 = 未知

        // 反馈升级参数
        private const double EscalationCpuThreshold = 0.10;   // 节流后仍超过 10% 单核占用
        private const double DeescalationCpuThreshold = 0.02; // 低于 2% 视为已收敛
        private const int EscalationConsecutiveSamples = 3;
        private const int DeescalationConsecutiveSamples = 5;

        // 深度模式参数
        private static int _deepModeIdleMinutes = 5;
        private static int _lowBatteryEscalatePercent = 20;

        // 工作集裁剪参数
        private static volatile bool _workingSetTrimEnabled = true;
        private static int _workingSetTrimMinMB = 64;
        private const double WorkingSetTrimIdleThreshold = 0.02; // 低于 2% 单核占用视为空闲
        private const int WorkingSetTrimIdleSamples = 2;         // 连续 2 次（约 1 分钟）才裁剪

        public static bool WorkingSetTrimEnabled
        {
            get => _workingSetTrimEnabled;
            set => _workingSetTrimEnabled = value;
        }

        public static int WorkingSetTrimMinMB
        {
            get => _workingSetTrimMinMB;
            set => _workingSetTrimMinMB = Math.Clamp(value, 8, 4096);
        }

        public static int DeepModeIdleMinutes
        {
            get => _deepModeIdleMinutes;
            set => _deepModeIdleMinutes = Math.Max(1, value);
        }

        public static int LowBatteryEscalatePercent
        {
            get => _lowBatteryEscalatePercent;
            set => _lowBatteryEscalatePercent = Math.Clamp(value, 5, 95);
        }

        private static volatile PowerMode _currentMode = PowerMode.Auto;

        /// <summary>
        /// 节能管理总开关。关闭后：不安装钩子、后台循环空转、
        /// 不施加任何节流，并还原所有已节流的进程
        /// </summary>
        private static volatile bool _enabled = true;

        public static bool IsEnabled => _enabled;

        /// <summary>
        /// 运行时切换节能管理总开关
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
                return;

            _enabled = enabled;

            if (!enabled)
            {
                // 立即还原所有被节流的进程，并停止响应前台切换
                _ = RestoreAllProcessesToNormalAsync();
                HookManager.UnsubscribeWindowEvents();
                AppLogger.Info("Energy management completely disabled.");
            }
            else
            {
                // 重新启用：先刷新进程缓存，再按当前模式恢复工作
                RefreshProcessCache();
                if (CurrentMode != PowerMode.Paused)
                {
                    HookManager.SubscribeToWindowEvents();
                }

                AppLogger.Info("Energy management enabled.");
            }
        }

        /// <summary>
        /// 启动时的安全清理：刷新一次进程缓存并还原所有节流状态，
        /// 用于清理上次异常退出可能残留的节流（完全关闭模式下启动时调用）
        /// </summary>
        public static async Task CleanupThrottledProcessesAsync()
        {
            RefreshProcessCache();
            await RestoreAllProcessesToNormalAsync();
        }

        public static PowerMode CurrentMode
        {
            get => _currentMode;
            set => _currentMode = value;
        }

        private static DateTime _lastSwitchTime = DateTime.MinValue;
        // 电源状态切换的独立防抖时间戳：_lastSwitchTime 会被每次前台窗口
        // 切换更新，复用它会导致用户活跃操作时漏掉 AC/电池状态变化
        private static DateTime _lastPowerModeSwitchTime = DateTime.MinValue;
        private static int _switchDebounceMs = DefaultSwitchDebounceMs;

        // 单例实例用于生命周期管理
        private static EnergyManager? _instance;
        private static readonly object _instanceLock = new();

        public static EnergyManager Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    _instance ??= new EnergyManager();
                    return _instance;
                }
            }
        }

        private EnergyManager()
        {
            InitializeUnmanagedResources();
            StartTimers();
            LoadConfiguration();
        }

        public static void LoadConfiguration()
        {
            LoadConfiguration(new AppSettings());
        }

        /// <summary>
        /// 重新加载配置并原地应用到指定实例。
        /// 重载配置时传入调用方持有的同一个实例，使其能看到最新值
        /// （例如 HouseKeeping 定时器的扫描间隔）
        /// </summary>
        public static void LoadConfiguration(AppSettings settings)
        {
            try
            {
                var config = ConfigurationHelper.LoadConfiguration();
                config.GetSection("AppSettings").Bind(settings);

                lock (_bypassListLock)
                {
                    _bypassProcessList = new HashSet<string>(settings.BypassProcessList ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                }

                lock (_lruLock)
                {
                    _lruCacheSize = Math.Max(1, settings.LRUCacheSize);

                    // 加载 LRU 时间衰减配置（默认 5 分钟）
                    if (settings.LRUDecayMinutes > 0)
                    {
                        _lruDecayPeriod = TimeSpan.FromMinutes(settings.LRUDecayMinutes);
                    }

                    // 加载 LRU 自动调整配置
                    if (settings.LRUMinSize > 0 && settings.LRUMaxSize >= settings.LRUMinSize)
                    {
                        _minLRUSize = settings.LRUMinSize;
                        _maxLRUSize = settings.LRUMaxSize;
                        _lruCacheSize = Math.Clamp(_lruCacheSize, _minLRUSize, _maxLRUSize);
                    }
                }

                _switchDebounceMs = settings.SwitchDebounceMs > 0 ? settings.SwitchDebounceMs : DefaultSwitchDebounceMs;

                // 使 EnableLogging 配置真正生效
                AppLogger.Enabled = settings.EnableLogging;

                ProcessContext.SetAudibleProcessList(settings.AudibleProcessList);
                ProcessContext.EfficiencyCoreBindingEnabled = settings.EnableEfficiencyCoreBinding;
                LowMemoryPriorityEnabled = settings.EnableLowMemoryPriority;
                LowMemoryPriorityOnAcPower = settings.LowMemoryPriorityOnAcPower;
                PauseOnAcPower = settings.PauseOnAcPower;
                WorkingSetTrimEnabled = settings.EnableWorkingSetTrim;
                WorkingSetTrimMinMB = settings.WorkingSetTrimMinMB;
                _enabled = settings.Enabled;
                DeepModeIdleMinutes = settings.DeepModeIdleMinutes;
                LowBatteryEscalatePercent = settings.LowBatteryEscalatePercent;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to load configuration: {ex.Message}");
            }
        }

        private void InitializeUnmanagedResources()
        {
            if (_unmanagedResourcesInitialized)
                return;

            try
            {
                szControlBlock = Marshal.SizeOf<Interop.Win32Api.PROCESS_POWER_THROTTLING_STATE>();
                pThrottleOn = Marshal.AllocHGlobal(szControlBlock);
                pThrottleOff = Marshal.AllocHGlobal(szControlBlock);

                var throttleState = new Interop.Win32Api.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = Interop.Win32Api.PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = Interop.Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = Interop.Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                };

                var unthrottleState = new Interop.Win32Api.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = Interop.Win32Api.PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = Interop.Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = Interop.Win32Api.ProcessorPowerThrottlingFlags.None,
                };

                Marshal.StructureToPtr(throttleState, pThrottleOn, false);
                Marshal.StructureToPtr(unthrottleState, pThrottleOff, false);

                _unmanagedResourcesInitialized = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to initialize unmanaged resources: {ex.Message}");
                ReleaseUnmanagedResources();
                throw;
            }
        }

        private void StartTimers()
        {
            _backgroundTasksCts = new CancellationTokenSource();

            _processCacheRefreshTask = Task.Run(() => RefreshProcessCacheLoop(_backgroundTasksCts.Token), _backgroundTasksCts.Token);
            _powerStatusCheckTask = Task.Run(() => PowerStatusCheckLoop(_backgroundTasksCts.Token), _backgroundTasksCts.Token);
        }

        private static async Task RefreshProcessCacheLoop(CancellationToken cancellationToken)
        {
            AppLogger.Info("Process cache refresh loop started.");

            int consecutiveFailures = 0;
            const int maxFailuresBeforeBackoff = 3;
            const int backoffSeconds = 60;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_enabled && CurrentMode != PowerMode.Paused)
                    {
                        RefreshProcessCache();
                        consecutiveFailures = 0; // 重置失败计数

                        // 定期清理过期的 LRU 条目（每分钟一次）
                        CleanupExpiredApps();

                        // 反馈升级/回退：根据实测 CPU 占用调整档位
                        EvaluateEscalation();
                    }
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    AppLogger.Error($"RefreshProcessCache (failure {consecutiveFailures}): {ex.Message}");

                    // 连续失败后退避
                    if (consecutiveFailures >= maxFailuresBeforeBackoff)
                    {
                        AppLogger.Warn($"Too many failures, backing off for {backoffSeconds}s");
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
                        consecutiveFailures = 0;
                        continue;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            AppLogger.Info("Process cache refresh loop stopped.");
        }

        private static async Task PowerStatusCheckLoop(CancellationToken cancellationToken)
        {
            AppLogger.Info("Power status check loop started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    CheckPowerStatus();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"CheckPowerStatus: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            AppLogger.Info("Power status check loop stopped.");
        }

        private static void CheckPowerStatus()
        {
            if (!_enabled)
                return;

            // 电源状态始终更新：它决定节流策略的激进程度（电池=激进，交流电=偏流畅度）
            var powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            bool isOnBattery = powerLineStatus == PowerLineStatus.Offline;

            // 记录电量百分比（-1 表示未知，避免误判为低电量）
            try
            {
                float life = SystemInformation.PowerStatus.BatteryLifePercent;
                _batteryLevelPercent = life is > 0f and <= 1f ? (int)Math.Round(life * 100f) : -1;
            }
            catch
            {
                _batteryLevelPercent = -1;
            }

            if (isOnBattery != _isBatteryPowered)
            {
                _isBatteryPowered = isOnBattery;
                AppLogger.Info(isOnBattery
                    ? "Power source changed to battery (aggressive throttling profile)."
                    : "Power source changed to AC (balanced throttling profile).");

                // 档位变化会改变内存优先级等策略，立即按新档位重算
                ThrottleAllUserBackgroundProcesses();
            }

            if (!_autoPowerModeEnabled)
                return;

            // 防抖：避免电池/AC 快速切换时频繁触发
            var timeSinceLastPowerSwitch = DateTime.UtcNow - _lastPowerModeSwitchTime;
            if (timeSinceLastPowerSwitch < TimeSpan.FromSeconds(10))
            {
                return;
            }

            if (isOnBattery != _wasOnBatteryAtLastSwitch)
            {
                if (isOnBattery)
                {
                    CurrentMode = PowerMode.Auto;
                    HookManager.SubscribeToWindowEvents();
                    AppLogger.Info("Switched to Battery Mode - Automatic energy saving enabled");
                }
                else if (_pauseOnAcPower)
                {
                    // 仅在用户明确要求时，接交流电才暂停管理
                    CurrentMode = PowerMode.Paused;
                    HookManager.UnsubscribeWindowEvents();
                    AppLogger.Info("Switched to AC Mode - Energy saving paused (PauseOnAcPower)");
                }
                else
                {
                    // 默认：交流电上继续节流，收益是前台流畅度而非省电
                    CurrentMode = PowerMode.Auto;
                    HookManager.SubscribeToWindowEvents();
                    AppLogger.Info("Switched to AC Mode - Background throttling kept for responsiveness");
                }

                _wasOnBatteryAtLastSwitch = isOnBattery;
                _lastPowerModeSwitchTime = DateTime.UtcNow;
            }
        }

        public static void SetAutoPowerModeEnabled(bool enabled)
        {
            _autoPowerModeEnabled = enabled;
        }

        ~EnergyManager()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 取消后台任务
                _backgroundTasksCts?.Cancel();
                
                try
                {
                    // 等待任务完成（最多等待 5 秒）
                    var tasks = new List<Task>();
                    if (_processCacheRefreshTask != null) tasks.Add(_processCacheRefreshTask);
                    if (_powerStatusCheckTask != null) tasks.Add(_powerStatusCheckTask);
                    
                    if (tasks.Count > 0)
                    {
                        Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Error waiting for background tasks: {ex.Message}");
                }
                finally
                {
                    _backgroundTasksCts?.Dispose();
                    _backgroundTasksCts = null;
                }

                ProcessCache.Clear();
                lock (_lruLock)
                {
                    _recentlyUsedApps.Clear();
                    _appLookup.Clear();
                }
            }

            ReleaseUnmanagedResources();
        }
        private static void AddOrUpdateApp(uint pid, string name)
        {
            lock (_lruLock)
            {
                var now = DateTime.UtcNow;

                if (_appLookup.TryGetValue(pid, out var node))
                {
                    _recentlyUsedApps.Remove(node);
                }
                else if (_recentlyUsedApps.Count >= _lruCacheSize)
                {
                    var oldestNode = _recentlyUsedApps.Last;
                    if (oldestNode != null)
                    {
                        _appLookup.Remove(oldestNode.Value.pid);
                        _recentlyUsedApps.RemoveLast();
                    }
                }

                var newNode = new LinkedListNode<(uint pid, string name, DateTime lastAccess)>((pid, name, now));
                _recentlyUsedApps.AddFirst(newNode);
                _appLookup[pid] = newNode;

                // 增加前台切换计数
                _foregroundSwitchCount++;

                // 检查是否需要自动调整 LRU 大小
                AutoAdjustLRUSize(now);
            }
        }

        private static bool IsAppInRecentList(uint pid)
        {
            lock (_lruLock)
            {
                return _appLookup.ContainsKey(pid);
            }
        }

        private static void CleanupExpiredApps()
        {
            lock (_lruLock)
            {
                var now = DateTime.UtcNow;
                var node = _recentlyUsedApps.Last;

                while (node != null)
                {
                    var prevNode = node.Previous;

                    // 时间衰减：超过 5 分钟未访问则移除
                    if ((now - node.Value.lastAccess) > _lruDecayPeriod)
                    {
                        _appLookup.Remove(node.Value.pid);
                        _recentlyUsedApps.Remove(node);
                    }

                    node = prevNode;
                }
            }
        }

        /// <summary>
        /// 自动调整 LRU 缓存大小
        /// 根据前台切换频率动态调整：切换频繁则增大，否则缩小
        /// </summary>
        private static void AutoAdjustLRUSize(DateTime now)
        {
            // 每 2 分钟检查一次
            if ((now - _lastLRUAdjustment).TotalMinutes < 2)
                return;

            lock (_lruLock)
            {
                var elapsedMinutes = (now - _lastLRUAdjustment).TotalMinutes;
                var switchesPerMinute = _foregroundSwitchCount / Math.Max(1, elapsedMinutes);

                int targetSize;

                if (switchesPerMinute > 10)
                {
                    // 高频切换用户（>10 次/分钟）：增大 LRU
                    targetSize = Math.Min(_maxLRUSize, _lruCacheSize + 2);
                }
                else if (switchesPerMinute > 5)
                {
                    // 中频切换用户（5-10 次/分钟）：保持当前大小
                    targetSize = _lruCacheSize;
                }
                else
                {
                    // 低频切换用户（<5 次/分钟）：缩小 LRU
                    targetSize = Math.Max(_minLRUSize, _lruCacheSize - 1);
                }

                // 如果 LRU 中实际应用数量很少，也缩小
                var activeAppCount = _recentlyUsedApps.Count;
                if (activeAppCount < _lruCacheSize / 2 && _lruCacheSize > _minLRUSize)
                {
                    targetSize = Math.Max(_minLRUSize, activeAppCount + 1);
                }

                if (targetSize != _lruCacheSize)
                {
                    AppLogger.Info($"Auto-adjusting LRU size from {_lruCacheSize} to {targetSize} (switches/min: {switchesPerMinute:F1}, active apps: {activeAppCount})");
                    _lruCacheSize = targetSize;

                    // 如果缩小，淘汰最旧的条目
                    while (_recentlyUsedApps.Count > _lruCacheSize)
                    {
                        var oldestNode = _recentlyUsedApps.Last;
                        if (oldestNode != null)
                        {
                            _appLookup.Remove(oldestNode.Value.pid);
                            _recentlyUsedApps.RemoveLast();
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // 重置计数器
                _foregroundSwitchCount = 0;
                _lastLRUAdjustment = now;
            }
        }

        private static void ReleaseUnmanagedResources()
        {
            if (!_unmanagedResourcesInitialized)
                return;

            if (pThrottleOn != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pThrottleOn);
                pThrottleOn = IntPtr.Zero;
            }
            if (pThrottleOff != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pThrottleOff);
                pThrottleOff = IntPtr.Zero;
            }

            _unmanagedResourcesInitialized = false;
        }

        private static void ToggleEfficiencyMode(IntPtr hProcess, bool enable)
        {
            // 暂停模式下不施加节流，但必须允许恢复（enable=false），
            // 否则切换到暂停模式时无法还原已被节流的进程
            if (enable && CurrentMode == PowerMode.Paused)
                return;

            try
            {
                Interop.Win32Api.SetProcessInformation(hProcess, Interop.Win32Api.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    enable ? pThrottleOn : pThrottleOff, (uint)szControlBlock);

                // 无论 SetProcessInformation 成功与否，都尝试设置优先级作为补充
                // PROCESS_MODE_BACKGROUND_BEGIN 会同时压低 IO 与内存调度优先级，
                // 比单纯 IDLE_PRIORITY_CLASS 更省电（减少磁盘唤醒与工作集争抢）
                Interop.Win32Api.SetPriorityClass(hProcess, enable
                    ? Interop.Win32Api.PriorityClass.IDLE_PRIORITY_CLASS | Interop.Win32Api.PriorityClass.PROCESS_MODE_BACKGROUND_BEGIN
                    : Interop.Win32Api.PriorityClass.NORMAL_PRIORITY_CLASS | Interop.Win32Api.PriorityClass.PROCESS_MODE_BACKGROUND_END);

                // 低内存优先级：让内存管理器更早回收该进程工作集，降低内存功耗
                // （效果很强，但可能让内存密集型后台程序明显变慢，可在配置中关闭；
                //   默认只在电池供电下启用，交流电上以避免影响后台任务速度为主）
                if (ShouldApplyLowMemoryPriority)
                {
                    int memoryPriority = enable ? MemoryPriorityLow : MemoryPriorityNormal;
                    IntPtr pMemoryPriority = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        Marshal.WriteInt32(pMemoryPriority, memoryPriority);
                        Interop.Win32Api.SetProcessInformation(hProcess, Interop.Win32Api.PROCESS_INFORMATION_CLASS.ProcessMemoryPriority,
                            pMemoryPriority, (uint)sizeof(int));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pMemoryPriority);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error applying efficiency mode to process: {ex.Message}");
            }
        }

        // MEMORY_PRIORITY：1=VeryLow, 2=Low, 5=Normal
        private const int MemoryPriorityLow = 2;
        private const int MemoryPriorityNormal = 5;

        private static volatile bool _lowMemoryPriorityEnabled = true;
        private static volatile bool _lowMemoryPriorityOnAcPower = false;
        private static volatile bool _pauseOnAcPower = false;
        private static bool _wasOnBatteryAtLastSwitch = true;

        public static bool LowMemoryPriorityEnabled
        {
            get => _lowMemoryPriorityEnabled;
            set => _lowMemoryPriorityEnabled = value;
        }

        public static bool LowMemoryPriorityOnAcPower
        {
            get => _lowMemoryPriorityOnAcPower;
            set => _lowMemoryPriorityOnAcPower = value;
        }

        public static bool PauseOnAcPower
        {
            get => _pauseOnAcPower;
            set => _pauseOnAcPower = value;
        }

        /// <summary>
        /// 是否使用激进档位（电池供电）。交流电/台式机使用偏流畅度的平衡档
        /// </summary>
        public static bool IsAggressiveProfile => _isBatteryPowered;

        /// <summary>
        /// 当前是否应该施加低内存优先级
        /// </summary>
        private static bool ShouldApplyLowMemoryPriority =>
            _lowMemoryPriorityEnabled && (_isBatteryPowered || _lowMemoryPriorityOnAcPower);

        /// <summary>
        /// 把进程绑定到能效核（或解除绑定）。仅在探测到混合架构 CPU 时生效
        /// </summary>
        private static void ToggleEfficiencyCoreBinding(IntPtr hProcess, bool enable)
        {
            if (!ProcessContext.IsHybridCpu)
                return;

            try
            {
                var cpuSets = ProcessContext.GetEfficiencyCoreCpuSets();
                if (cpuSets == null || cpuSets.Length == 0)
                    return;

                bool ok = enable
                    ? Interop.Win32Api.SetProcessDefaultCpuSets(hProcess, cpuSets, (uint)cpuSets.Length, IntPtr.Zero)
                    : Interop.Win32Api.SetProcessDefaultCpuSets(hProcess, null, 0, IntPtr.Zero);

                if (!ok)
                {
                    AppLogger.Warn($"SetProcessDefaultCpuSets({(enable ? "bind" : "clear")}) failed: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Efficiency core binding failed: {ex.Message}");
            }
        }

        private static void RefreshProcessCache()
        {
            var snapshot = Interop.Win32Api.CreateToolhelp32Snapshot(
                Interop.Win32Api.TH32CS_SNAPPROCESS, 0);

            if (!Interop.Win32Api.IsValidSnapshotHandle(snapshot))
            {
                AppLogger.Error($"Failed to create process snapshot: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                var entry = new Interop.Win32Api.PROCESSENTRY32W
                {
                    dwSize = (uint)Marshal.SizeOf<Interop.Win32Api.PROCESSENTRY32W>()
                };

                if (!Interop.Win32Api.Process32FirstW(snapshot, ref entry))
                {
                    AppLogger.Error($"Process32FirstW failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                var activeIds = new HashSet<int>();

                do
                {
                    var pid = entry.th32ProcessID;
                    if (pid == 0)
                        continue;

                    // 只关心当前会话的进程
                    if (!Interop.Win32Api.ProcessIdToSessionId(pid, out var sessionId) ||
                        sessionId != (uint)CurrentSessionId)
                    {
                        continue;
                    }

                    activeIds.Add((int)pid);

                    // 快照中的名称已包含 .exe 扩展名
                    var procName = entry.szExeFile;
                    if (string.IsNullOrEmpty(procName))
                        continue;

                    if (!ProcessCache.ContainsKey((int)pid))
                    {
                        ProcessCache.TryAdd((int)pid, new ProcessState((int)pid, procName));
                    }
                    else
                    {
                        var existing = ProcessCache[(int)pid];
                        if (!existing.Name.Equals(procName, StringComparison.OrdinalIgnoreCase))
                        {
                            existing.Name = procName;
                        }
                    }
                }
                while (Interop.Win32Api.Process32NextW(snapshot, ref entry));

                foreach (var kvp in ProcessCache)
                {
                    if (!activeIds.Contains(kvp.Key))
                    {
                        ProcessCache.TryRemove(kvp.Key, out _);
                    }
                }
            }
            finally
            {
                Interop.Win32Api.CloseHandle(snapshot);
            }
        }

        /// <summary>
        /// 对实测空闲的后台进程裁剪工作集（EmptyWorkingSet）。
        /// 实测：一个空转但占 514MB 常驻内存的进程可被 100% 释放，且不会回流。
        /// 需要单独的句柄权限（PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA），
        /// 打不开就静默跳过，不影响节流本身。
        /// </summary>
        private static void TrimWorkingSetIfIdle(ProcessState state, double coreFraction)
        {
            if (!_workingSetTrimEnabled || state.Tier == ThrottleTier.None)
                return;

            if (coreFraction < WorkingSetTrimIdleThreshold)
            {
                state.ConsecutiveIdleSamples++;
            }
            else
            {
                // 重新活跃后允许下次空闲时再次裁剪
                state.ConsecutiveIdleSamples = 0;
                state.WorkingSetTrimmed = false;
                return;
            }

            if (state.WorkingSetTrimmed || state.ConsecutiveIdleSamples < WorkingSetTrimIdleSamples)
                return;

            var hQuota = Interop.Win32Api.OpenProcess(
                (uint)(Interop.Win32Api.ProcessAccessFlags.QueryInformation |
                       Interop.Win32Api.ProcessAccessFlags.SetQuota),
                false,
                (uint)state.Id);

            if (hQuota == IntPtr.Zero)
                return;

            try
            {
                uint size = (uint)Marshal.SizeOf<Interop.Win32Api.PROCESS_MEMORY_COUNTERS_EX>();
                if (!Interop.Win32Api.GetProcessMemoryInfo(hQuota, out var mem, size))
                    return;

                ulong minBytes = (ulong)_workingSetTrimMinMB * 1024 * 1024;
                if ((ulong)mem.WorkingSetSize < minBytes)
                    return;

                if (Interop.Win32Api.EmptyWorkingSet(hQuota))
                {
                    state.WorkingSetTrimmed = true;
                    AppLogger.Info(
                        $"[TRIM] {state.Name} (PID: {state.Id}) working set {(ulong)mem.WorkingSetSize / 1024 / 1024} MB released (idle at ~{coreFraction:P1} of a core)");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"TrimWorkingSet failed for {state.Name}: {ex.Message}");
            }
            finally
            {
                Interop.Win32Api.CloseHandle(hQuota);
            }
        }

        /// <summary>
        /// 反馈升级：测量已被节流进程的 CPU 占用，仍然偏高的晋升到 Tier2，
        /// 持续降低的回退到 Tier1。每轮刷新（约 30s）执行一次。
        /// </summary>
        private static void EvaluateEscalation()
        {
            if (CurrentMode == PowerMode.Paused)
                return;

            var cpuSets = ProcessContext.GetEfficiencyCoreCpuSets();
            bool canEscalate = cpuSets is { Length: > 0 };

            foreach (var state in ProcessCache.Values)
            {
                if (state.Tier == ThrottleTier.None)
                    continue;

                var hProcess = Interop.Win32Api.OpenProcess(
                    (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation |
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation),
                    false,
                    (uint)state.Id);

                if (hProcess == IntPtr.Zero)
                    continue;

                try
                {
                    if (!Interop.Win32Api.QueryProcessCycleTime(hProcess, out var cycles))
                        continue;

                    var now = DateTime.UtcNow;
                    var elapsed = now - state.LastMeasuredAt;
                    double coreFraction = 0;
                    bool hasMeasurement = false;

                    if (state.LastCycles > 0 && elapsed > TimeSpan.FromSeconds(5))
                    {
                        double seconds = elapsed.TotalSeconds;
                        double cyclesPerSecond = (cycles - state.LastCycles) / seconds;
                        // 以 3GHz 为基准估算等效满核占用比例
                        coreFraction = cyclesPerSecond / 3_000_000_000.0;
                        hasMeasurement = true;

                        if (coreFraction > EscalationCpuThreshold)
                        {
                            state.ConsecutiveHighCpu++;
                            state.ConsecutiveLowCpu = 0;
                        }
                        else if (coreFraction < DeescalationCpuThreshold)
                        {
                            state.ConsecutiveLowCpu++;
                            state.ConsecutiveHighCpu = 0;
                        }

                        if (canEscalate && state.Tier == ThrottleTier.Tier1 &&
                            state.ConsecutiveHighCpu >= EscalationConsecutiveSamples)
                        {
                            AppLogger.Info($"[ESCALATE] {state.Name} still using ~{coreFraction:P0} of a core, moving to efficiency cores");
                            ToggleEfficiencyCoreBinding(hProcess, true);
                            state.Tier = ThrottleTier.Tier2;
                            state.ConsecutiveHighCpu = 0;
                        }
                        else if (state.Tier == ThrottleTier.Tier2 &&
                                 state.ConsecutiveLowCpu >= DeescalationConsecutiveSamples)
                        {
                            AppLogger.Info($"[DEESCALATE] {state.Name} down to ~{coreFraction:P0} of a core, releasing efficiency cores");
                            ToggleEfficiencyCoreBinding(hProcess, false);
                            state.Tier = ThrottleTier.Tier1;
                            state.ConsecutiveLowCpu = 0;
                        }
                    }

                    state.LastCycles = cycles;
                    state.LastMeasuredAt = now;

                    // 工作集裁剪：实测空闲的后台进程，把常驻内存立即还给系统。
                    // 只处理已被节流的进程，且必须连续多次测得空闲，
                    // 否则反复换入换出反而增加磁盘活动、更耗电
                    if (hasMeasurement)
                    {
                        TrimWorkingSetIfIdle(state, coreFraction);
                    }
                }
                finally
                {
                    Interop.Win32Api.CloseHandle(hProcess);
                }
            }
        }

        /// <summary>
        /// 分层节流策略：为每个进程决定目标档位并应用差异更新。
        /// 保护顺序：系统/用户白名单 &gt; 播音中 &gt; 前台/待处理 &gt; LRU 最近使用
        /// &gt; 可见窗口（深度模式下豁免失效）
        /// </summary>
        private static void ApplyThrottlePolicy(uint excludePid1, uint excludePid2 = 0)
        {
            var processSnapshot = ProcessCache.Values.ToList();

            // 一次性快照保护列表：避免逐个加锁，也避免重载配置时的竞态
            HashSet<string> bypassSnapshot;
            lock (_bypassListLock)
            {
                bypassSnapshot = new HashSet<string>(_bypassProcessList, StringComparer.OrdinalIgnoreCase);
            }

            bool deepMode = IsDeepModeActive();
            HashSet<int> visiblePids = deepMode ? new HashSet<int>() : ProcessContext.GetVisibleProcessIds();
            bool fullscreen = !deepMode && ProcessContext.IsFullscreenOrPresentation();
            uint idleMs = ProcessContext.GetIdleTimeMs();

            foreach (var state in processSnapshot)
            {
                uint pid = (uint)state.Id;

                if (pid == excludePid1 || pid == excludePid2)
                {
                    ApplyTier(state, ThrottleTier.None);
                    continue;
                }

                // 保护列表（系统进程 + 用户配置）
                if (bypassSnapshot.Contains(state.Name))
                {
                    ApplyTier(state, ThrottleTier.None);
                    continue;
                }

                // 可能正在播音的进程
                if (ProcessContext.IsLikelyAudible(state.Name))
                {
                    ApplyTier(state, ThrottleTier.None);
                    continue;
                }

                // LRU 中最近使用过的应用保持不节流
                if (IsAppInRecentList(pid))
                {
                    ApplyTier(state, ThrottleTier.None);
                    continue;
                }

                // 有可见窗口的进程不节流（深度模式下豁免失效）
                if (visiblePids.Contains(state.Id))
                {
                    ApplyTier(state, ThrottleTier.None);
                    continue;
                }

                // 深度模式（熄屏 / 长时间空闲 / 低电量）：直接上 Tier2
                var target = deepMode ? ThrottleTier.Tier2 : ThrottleTier.Tier1;

                // 低电量时同样提升一档
                if (!deepMode && _isBatteryPowered &&
                    _batteryLevelPercent > 0 && _batteryLevelPercent < LowBatteryEscalatePercent)
                {
                    target = ThrottleTier.Tier2;
                }

                ApplyTier(state, target);

                if (AppLogger.Enabled && fullscreen && state.Tier != ThrottleTier.None)
                {
                    // 全屏/演示模式下仍会节流后台进程，但这里不做额外处理，
                    // 仅用于日志可观测性
                }
            }

            if (AppLogger.Enabled && deepMode)
            {
                AppLogger.Info($"[DEEP] Deep throttling active (screen={(ProcessContext.IsScreenOn ? "on" : "off")}, idle={idleMs / 1000}s, battery={_batteryLevelPercent}%).");
            }
        }

        /// <summary>
        /// 把进程调整到目标档位（幂等，档位未变化时不会重复调用 API）
        /// </summary>
        private static void ApplyTier(ProcessState state, ThrottleTier target)
        {
            // 电源档位变化时（如拔掉电源）需要重新施加内存优先级
            bool memoryPriorityChanged =
                state.Tier != ThrottleTier.None && state.MemoryPriorityApplied != ShouldApplyLowMemoryPriority;

            if (state.Tier == target && !memoryPriorityChanged)
                return;

            var hProcess = Interop.Win32Api.OpenProcess(
                (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation |
                       Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation),
                false,
                (uint)state.Id);

            if (hProcess == IntPtr.Zero)
                return;

            try
            {
                if (target == ThrottleTier.None)
                {
                    ToggleEfficiencyMode(hProcess, false);
                    ToggleEfficiencyCoreBinding(hProcess, false);
                    state.MemoryPriorityApplied = false;
                    AppLogger.Info($"[RESTORE] {state.Name} (PID: {state.Id})");
                }
                else
                {
                    ToggleEfficiencyMode(hProcess, true);
                    if (target == ThrottleTier.Tier2)
                    {
                        ToggleEfficiencyCoreBinding(hProcess, true);
                    }
                    else
                    {
                        ToggleEfficiencyCoreBinding(hProcess, false);
                    }

                    state.MemoryPriorityApplied = ShouldApplyLowMemoryPriority;
                    AppLogger.Info($"[THROTTLE:{target}] {state.Name} (PID: {state.Id})");
                }

                state.Tier = target;
                state.LastCycles = 0;
                state.LastMeasuredAt = DateTime.UtcNow;
                state.ConsecutiveHighCpu = 0;
                state.ConsecutiveLowCpu = 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"ApplyTier failed for {state.Name} (PID: {state.Id}): {ex.Message}");
            }
            finally
            {
                Interop.Win32Api.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// 深度模式：熄屏、用户长时间空闲或低电量时启用更激进的节流
        /// </summary>
        private static bool IsDeepModeActive()
        {
            if (!ProcessContext.IsScreenOn)
                return true;

            if (ProcessContext.GetIdleTimeMs() > DeepModeIdleMinutes * 60_000)
                return true;

            // 低电量只在电池供电下触发（交流电上电量正在回升）
            if (_isBatteryPowered && _batteryLevelPercent > 0 && _batteryLevelPercent < LowBatteryEscalatePercent)
                return true;

            return false;
        }

        public static void ThrottleAllUserBackgroundProcesses()
        {
            if (CurrentMode == PowerMode.Paused || !_enabled)
                return;

            uint currentPendingPid;
            lock (_pendingProcLock)
            {
                currentPendingPid = _pendingProcPid;
            }

            ApplyThrottlePolicy(currentPendingPid);
        }

        public static async Task RestoreAllProcessesToNormalAsync()
        {
            var processSnapshot = ProcessCache.Values.ToList();

            await Task.Run(() =>
            {
                foreach (var state in processSnapshot)
                {
                    ApplyTier(state, ThrottleTier.None);
                }
            });
        }

        [Obsolete("Use RestoreAllProcessesToNormalAsync instead")]
        public static void RestoreAllProcessesToNormal()
        {
            RestoreAllProcessesToNormalAsync().GetAwaiter().GetResult();
        }

        private static string GetProcessNameFromHandle(IntPtr hProcess)
        {
            var sb = _threadLocalStringBuilder.Value!;
            sb.Clear();

            int capacity = sb.Capacity;
            if (Interop.Win32Api.QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
            {
                return Path.GetFileName(sb.ToString());
            }

            return string.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBypassProcess(ReadOnlySpan<char> processName)
        {
            // 首先检查硬编码保护列表（最高优先级）- O(1) HashSet 查找
            var processNameStr = processName.ToString();
            if (_hardcodedBypassList.Contains(processNameStr))
            {
                return true;
            }

            // 然后检查配置列表 - O(1) HashSet 查找
            lock (_bypassListLock)
            {
                return _bypassProcessList.Contains(processNameStr, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void HandleForegroundEvent(IntPtr hwnd)
        {
            if (CurrentMode == PowerMode.Paused || !_enabled)
                return;

            var windowThreadId = Interop.Win32Api.GetWindowThreadProcessId(hwnd, out uint procId);
            if (windowThreadId == 0 || procId == 0) return;

            var procHandle = Interop.Win32Api.OpenProcess(
                (uint)(Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation | Interop.Win32Api.ProcessAccessFlags.SetInformation), false, procId);
            if (procHandle == IntPtr.Zero) return;

            uint actualProcId = procId;
            IntPtr actualProcHandle = procHandle;

            try
            {
                var appName = GetProcessNameFromHandle(procHandle);

                // UWP needs to be handled in a special case
                if (appName.Equals(UWPFrameHostApp, StringComparison.OrdinalIgnoreCase))
                {
                    UwpChildProcessInfo? childProcessInfo = null;

                    Interop.Win32Api.EnumChildWindows(hwnd, (innerHwnd, lparam) =>
                    {
                        if (childProcessInfo.HasValue) return false;

                        if (Interop.Win32Api.GetWindowThreadProcessId(innerHwnd, out uint innerProcId) > 0)
                        {
                            if (actualProcId == innerProcId) return true;

                            var innerProcHandle = Interop.Win32Api.OpenProcess(
                                (uint)(Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation | Interop.Win32Api.ProcessAccessFlags.SetInformation),
                                false, innerProcId);
                            if (innerProcHandle == IntPtr.Zero) return true;

                            var innerAppName = GetProcessNameFromHandle(innerProcHandle);
                            childProcessInfo = new UwpChildProcessInfo(innerProcId, innerProcHandle, innerAppName);
                            return false;
                        }

                        return true;
                    }, IntPtr.Zero);

                    if (childProcessInfo.HasValue)
                    {
                        // 原句柄已关闭，actualProcHandle 改持子进程句柄，由 finally 统一释放
                        Interop.Win32Api.CloseHandle(actualProcHandle);
                        actualProcHandle = childProcessInfo.Value.Handle;
                        actualProcId = childProcessInfo.Value.Pid;
                        appName = childProcessInfo.Value.Name;
                    }
                }

                var currentTime = DateTime.UtcNow;
                var timeSinceLastSwitch = currentTime - _lastSwitchTime;

                // 检查是否为受保护进程（系统进程）
                var isProtected = IsBypassProcess(appName.AsSpan());

                // 只有非受保护的用户应用才记录到 LRU 缓存
                if (!isProtected)
                {
                    AddOrUpdateApp(actualProcId, appName);

                    // Boost the current foreground app
                    AppLogger.Info($"[FOREGROUND] Boost {appName}");
                    ToggleEfficiencyMode(actualProcHandle, false);
                }
                else
                {
                    AppLogger.Info($"[FOREGROUND] Skip protected process: {appName}");
                }

                // 防抖：距离上次切换太近则跳过
                // （AddOrUpdateApp 已刷新该应用的访问时间，无需重复处理）
                if (timeSinceLastSwitch < TimeSpan.FromMilliseconds(_switchDebounceMs))
                {
                    return;
                }

                // 对之前的前台应用应用效率限制
                uint prevPid;
                lock (_pendingProcLock)
                {
                    prevPid = _pendingProcPid;
                }

                if (prevPid != 0 && prevPid != actualProcId)
                {
                    var prevProcHandle = Interop.Win32Api.OpenProcess(
                        (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation | Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation),
                        false, prevPid);
                    if (prevProcHandle != IntPtr.Zero)
                    {
                        try
                        {
                            var prevAppName = GetProcessNameFromHandle(prevProcHandle);
                            if (!string.IsNullOrEmpty(prevAppName) && !IsBypassProcess(prevAppName.AsSpan()))
                            {
                                AppLogger.Info($"[BACKGROUND] Throttle {prevAppName}");
                                ToggleEfficiencyMode(prevProcHandle, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error($"Failed to throttle previous process: {ex.Message}");
                        }
                        finally
                        {
                            Interop.Win32Api.CloseHandle(prevProcHandle);
                        }
                    }
                }

                // 对其他后台进程应用效率限制
                // 优先恢复 LRU 缓存中的应用
                ApplyThrottlePolicy(actualProcId, prevPid);

                _lastSwitchTime = currentTime;
                lock (_pendingProcLock)
                {
                    _pendingProcPid = actualProcId;
                }
            }
            finally
            {
                // actualProcHandle 始终指向当前持有的句柄：未替换时即原句柄；
                // 替换为 UWP 子进程句柄后，原句柄已在上面关闭
                Interop.Win32Api.CloseHandle(actualProcHandle);
            }
        }

        private readonly record struct UwpChildProcessInfo(uint Pid, IntPtr Handle, string Name);

        /// <summary>
        /// 节流档位：None=不节流，Tier1=EcoQoS+后台模式+低内存优先级，Tier2=额外绑定能效核
        /// </summary>
        public enum ThrottleTier
        {
            None = 0,
            Tier1 = 1,
            Tier2 = 2,
        }

        /// <summary>
        /// 进程的运行时节流状态（含反馈升级所需的 CPU 测量值）
        /// </summary>
        public sealed class ProcessState
        {
            public ProcessState(int id, string name)
            {
                Id = id;
                Name = name;
            }

            public int Id { get; }
            public string Name { get; set; }
            public ThrottleTier Tier { get; set; } = ThrottleTier.None;
            public bool MemoryPriorityApplied { get; set; }
            public bool WorkingSetTrimmed { get; set; }
            public int ConsecutiveIdleSamples { get; set; }
            public ulong LastCycles { get; set; }
            public DateTime LastMeasuredAt { get; set; } = DateTime.UtcNow;
            public int ConsecutiveHighCpu { get; set; }
            public int ConsecutiveLowCpu { get; set; }
        }
    }
}
