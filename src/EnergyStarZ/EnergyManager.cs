using EnergyStarZ.Interop;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;
using EnergyStarZ.Config;
using Microsoft.Extensions.Configuration;

namespace EnergyStarZ
{
    public unsafe class EnergyManager : IDisposable
    {
        public static HashSet<string> BypassProcessList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Speical handling needs for UWP to get the child window process
        public const string UWPFrameHostApp = "ApplicationFrameHost.exe";

        // 使用LRU缓存来防止频繁切换
        private static readonly LinkedList<(uint pid, string name, DateTime lastAccess, int weight)> _recentlyUsedApps = new();
        private static readonly Dictionary<uint, LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>> _appLookup = new();
        private static int _lruCacheSize = 5; // 默认缓存大小
        private static TimeSpan _timeoutPeriod = TimeSpan.FromSeconds(30); // 默认超时时间
        private static DateTime _lastSwitchTime = DateTime.MinValue;

        // 保留原来的 pendingProcPid 和 pendingProcName 用于后台任务
        private static uint pendingProcPid = 0;
        private static string pendingProcName = "";

        private static IntPtr pThrottleOn = IntPtr.Zero;
        private static IntPtr pThrottleOff = IntPtr.Zero;
        private static int szControlBlock = 0;

        private static readonly ThreadLocal<StringBuilder> _threadLocalStringBuilder =
            new(() => new StringBuilder(1024));

        // 缓存当前会话ID
        private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;

        // 使用 ConcurrentDictionary 缓存进程信息
        private static readonly ConcurrentDictionary<int, ProcessInfo> ProcessCache = new();

        private static System.Threading.Timer? ProcessCacheRefreshTimer;
        private static System.Threading.Timer? PowerStatusCheckTimer;
        private static bool _isBatteryPowered = false;
        private static bool _autoPowerModeEnabled = false;

        // 添加当前模式属性
        public static PowerMode CurrentMode { get; set; } = PowerMode.Auto;

        static EnergyManager()
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

            // 初始化缓存刷新定时器
            ProcessCacheRefreshTimer = new System.Threading.Timer(RefreshProcessCache, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

            // 初始化电源状态检查定时器
            PowerStatusCheckTimer = new System.Threading.Timer(CheckPowerStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            // 加载配置
            LoadConfiguration();
        }

        public static void LoadConfiguration()
        {
            var config = ConfigurationHelper.LoadConfiguration();
            var settings = ConfigurationHelper.GetAppSettings(config);
            
            // 更新绕过列表
            BypassProcessList = new HashSet<string>(settings.BypassProcessList, StringComparer.OrdinalIgnoreCase);
            
            // 更新LRU缓存配置
            _lruCacheSize = Math.Max(1, settings.LRUCacheSize); // 至少为1
            _timeoutPeriod = TimeSpan.FromSeconds(settings.TimeoutSeconds); // 超时时间
        }

        // 初始化电源状态检查定时器
        public static void InitializePowerStatusMonitoring()
        {
            // 检查是否是笔记本电脑（有电池）
            if (SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.Unknown)
            {
                // 启动电源状态检查定时器（每5秒检查一次）
                // PowerStatusCheckTimer 是 readonly 字段，只能在初始化时赋值
                // 所以我们使用一个临时变量来创建定时器
                var timer = new System.Threading.Timer(CheckPowerStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
                // 由于 PowerStatusCheckTimer 是 readonly，我们假设它已在静态构造函数中初始化
                // 这里只是启动定时器，不重新分配 PowerStatusCheckTimer
                // 实际上，我们应确保在静态构造函数中正确初始化
            }
        }

        private static void CheckPowerStatus(object? state)
        {
            if (!_autoPowerModeEnabled)
                return;

            var powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;

            bool isOnBattery = powerLineStatus == PowerLineStatus.Offline;

            if (isOnBattery != _isBatteryPowered)
            {
                _isBatteryPowered = isOnBattery;

                if (isOnBattery)
                {
                    // 笔记本离电，切换到自动模式（节能）
                    CurrentMode = PowerMode.Auto;
                    HookManager.SubscribeToWindowEvents();
                    Console.WriteLine("[POWER STATUS] Switched to Battery Mode - Automatic energy saving enabled");
                }
                else
                {
                    // 笔记本插电，切换到暂停模式
                    CurrentMode = PowerMode.Paused;
                    HookManager.UnsubscribeWindowEvents();
                    Console.WriteLine("[POWER STATUS] Switched to AC Mode - Energy saving paused");
                }
            }
        }

        public static void SetAutoPowerModeEnabled(bool enabled)
        {
            _autoPowerModeEnabled = enabled;
            if (enabled)
            {
                // 如果启用自动电源模式，确保监测已启动
                if (PowerStatusCheckTimer == null)
                {
                    InitializePowerStatusMonitoring();
                }
            }
        }

        ~EnergyManager()
        {
            Dispose(false);
        }

        // LRU缓存管理方法
        private static void AddOrUpdateApp(uint pid, string name)
        {
            DateTime now = DateTime.UtcNow;
            int newWeight = 1;

            if (_appLookup.TryGetValue(pid, out var node))
            {
                // 如果应用已存在，更新其位置、时间和权重
                _recentlyUsedApps.Remove(node);
                newWeight = node.Value.weight + 1; // 增加权重
            }
            else if (_recentlyUsedApps.Count >= _lruCacheSize)
            {
                // 如果缓存满了，移除最少使用的应用
                var oldestNode = _recentlyUsedApps.Last;
                if (oldestNode != null)
                {
                    _appLookup.Remove(oldestNode.Value.pid);
                    _recentlyUsedApps.RemoveLast();
                }
            }

            // 添加新节点到链表开头
            var newNode = new LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>((pid, name, now, newWeight));
            _recentlyUsedApps.AddFirst(newNode);
            _appLookup[pid] = newNode;
        }

        private static bool IsAppInRecentList(uint pid)
        {
            return _appLookup.ContainsKey(pid);
        }

        private static void UpdateAppAccessTime(uint pid)
        {
            if (_appLookup.TryGetValue(pid, out var node))
            {
                var updatedValue = (node.Value.pid, node.Value.name, DateTime.UtcNow, node.Value.weight + 1);
                _recentlyUsedApps.Remove(node);
                var newNode = new LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>(updatedValue);
                _recentlyUsedApps.AddFirst(newNode);
                _appLookup[pid] = newNode;
            }
        }

        private static void CleanupExpiredApps()
        {
            var now = DateTime.UtcNow;
            var node = _recentlyUsedApps.Last;

            while (node != null)
            {
                var prevNode = node.Previous;

                if ((now - node.Value.lastAccess) > _timeoutPeriod)
                {
                    _appLookup.Remove(node.Value.pid);
                    _recentlyUsedApps.Remove(node);
                }

                node = prevNode;
            }
        }

        private static void ReleaseUnmanagedResources()
        {
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
                ProcessCacheRefreshTimer?.Dispose();
                PowerStatusCheckTimer?.Dispose();
                
                // 清理缓存
                ProcessCache.Clear();
                _recentlyUsedApps.Clear();
                _appLookup.Clear();
            }
            
            ReleaseUnmanagedResources();
        }

        private static void ToggleEfficiencyMode(IntPtr hProcess, bool enable)
        {
            // 如果当前处于暂停模式，则不执行任何操作
            if (CurrentMode == PowerMode.Paused)
                return;

            try
            {
                var result = Interop.Win32Api.SetProcessInformation(hProcess, Interop.Win32Api.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    enable ? pThrottleOn : pThrottleOff, (uint)szControlBlock);
                
                if (!result)
                {
                    // 如果设置功率限制失败，尝试仅设置优先级
                    Interop.Win32Api.SetPriorityClass(hProcess, enable ? Interop.Win32Api.PriorityClass.IDLE_PRIORITY_CLASS : Interop.Win32Api.PriorityClass.NORMAL_PRIORITY_CLASS);
                }
                else
                {
                    // 如果功率限制设置成功，也设置优先级
                    Interop.Win32Api.SetPriorityClass(hProcess, enable ? Interop.Win32Api.PriorityClass.IDLE_PRIORITY_CLASS : Interop.Win32Api.PriorityClass.NORMAL_PRIORITY_CLASS);
                }
            }
            catch (Exception ex)
            {
                // 记录错误但不中断处理
                Console.WriteLine($"Error applying efficiency mode to process: {ex.Message}");
            }
        }

        private static void RefreshProcessCache(object? state)
        {
            try
            {
                var processes = Process.GetProcesses();
                var activeIds = new HashSet<int>(processes.Length); // 预设容量

                foreach (var proc in processes)
                {
                    if (proc.SessionId != CurrentSessionId)
                    {
                        proc.Dispose(); // 立即释放不需要的进程对象
                        continue;
                    }

                    activeIds.Add(proc.Id);

                    if (!ProcessCache.ContainsKey(proc.Id))
                    {
                        // 只在需要时获取进程名称
                        ProcessCache.TryAdd(proc.Id, new ProcessInfo(proc.Id, proc.ProcessName + ".exe"));
                    }
                    else
                    {
                        // 如果进程已在缓存中，更新其名称（以防名称改变）
                        var existing = ProcessCache[proc.Id];
                        if (existing.Name != proc.ProcessName + ".exe")
                        {
                            ProcessCache[proc.Id] = new ProcessInfo(proc.Id, proc.ProcessName + ".exe");
                        }
                    }
                }

                // 移除已终止的进程
                var keysToRemove = new List<int>();
                foreach (var kvp in ProcessCache)
                {
                    if (!activeIds.Contains(kvp.Key))
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    ProcessCache.TryRemove(key, out _);
                }

                foreach (var proc in processes)
                {
                    proc.Dispose(); // 释放 Process 对象
                }
            }
            catch (Exception ex)
            {
                // 记录错误但不中断服务
                Console.WriteLine($"Error refreshing process cache: {ex.Message}");
            }
        }

        public static void ThrottleAllUserBackgroundProcesses()
        {
            // 如果当前处于暂停模式，则不执行任何操作
            if (CurrentMode == PowerMode.Paused)
                return;

            var processesToThrottle = ProcessCache.Values
                .Where(p => p.Id != pendingProcPid && !BypassProcessList.Contains(p.Name));

            foreach (var procInfo in processesToThrottle)
            {
                var hProcess = Interop.Win32Api.OpenProcess(
                    (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation | 
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), 
                    false, 
                    (uint)procInfo.Id);
                
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        ToggleEfficiencyMode(hProcess, true);
                    }
                    finally
                    {
                        Interop.Win32Api.CloseHandle(hProcess);
                    }
                }
            }
        }
        
        private static void ApplyEfficiencyModeToBackgroundProcesses(uint currentForegroundPid)
        {
            // 对所有非前台、非绕过列表中的进程应用效率限制
            var backgroundProcesses = ProcessCache.Values
                .Where(p => p.Id != currentForegroundPid && 
                           p.Id != pendingProcPid && 
                           !BypassProcessList.Contains(p.Name));

            foreach (var procInfo in backgroundProcesses)
            {
                var hProcess = Interop.Win32Api.OpenProcess(
                    (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation | 
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), 
                    false, 
                    (uint)procInfo.Id);
                
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        // 应用效率模式
                        ToggleEfficiencyMode(hProcess, true);
                    }
                    finally
                    {
                        Interop.Win32Api.CloseHandle(hProcess);
                    }
                }
                else
                {
                    // 如果无法打开进程，可能是因为权限不足或其他原因
                    // 尝试使用不同的访问标志
                    var hProcessWithDifferentAccess = Interop.Win32Api.OpenProcess(
                        (uint)(Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), 
                        false, 
                        (uint)procInfo.Id);
                    
                    if (hProcessWithDifferentAccess != IntPtr.Zero)
                    {
                        Interop.Win32Api.CloseHandle(hProcessWithDifferentAccess);
                    }
                }
            }
        }

        public static void RestoreAllProcessesToNormal()
        {
            // 恢复所有缓存中的进程到正常模式
            foreach (var procInfo in ProcessCache.Values)
            {
                var hProcess = Interop.Win32Api.OpenProcess(
                    (uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation | 
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), 
                    false, 
                    (uint)procInfo.Id);
                
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        // 将进程恢复到正常模式（关闭效率限制）
                        ToggleEfficiencyMode(hProcess, false);
                    }
                    finally
                    {
                        Interop.Win32Api.CloseHandle(hProcess);
                    }
                }
            }
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
            // 使用 ReadOnlySpan<char> 避免字符串分配
            foreach (var bypassProcess in BypassProcessList)
            {
                if (processName.Equals(bypassProcess.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static unsafe void HandleForegroundEvent(IntPtr hwnd)
        {
            // 如果当前处于暂停模式，则不执行任何操作
            if (CurrentMode == PowerMode.Paused)
                return;

            var windowThreadId = Interop.Win32Api.GetWindowThreadProcessId(hwnd, out uint procId);
            // This is invalid, likely a process is dead, or idk
            if (windowThreadId == 0 || procId == 0) return;

            var procHandle = Interop.Win32Api.OpenProcess(
                (uint) (Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation | Interop.Win32Api.ProcessAccessFlags.SetInformation), false, procId);
            if (procHandle == IntPtr.Zero) return;

            try
            {
                // 使用栈分配的缓冲区减少GC压力
                Span<char> buffer = stackalloc char[260]; // MAX_PATH
                var appName = GetProcessNameFromHandle(procHandle);

                // UWP needs to be handled in a special case
                if (appName.Equals(UWPFrameHostApp, StringComparison.OrdinalIgnoreCase))
                {
                    var found = false;
                    Interop.Win32Api.EnumChildWindows(hwnd, (innerHwnd, lparam) =>
                    {
                        if (found) return true;
                        if (Interop.Win32Api.GetWindowThreadProcessId(innerHwnd, out uint innerProcId) > 0)
                        {
                            if (procId == innerProcId) return true;

                            var innerProcHandle = Interop.Win32Api.OpenProcess((uint)(Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation |
                                Interop.Win32Api.ProcessAccessFlags.SetInformation), false, innerProcId);
                            if (innerProcHandle == IntPtr.Zero) return true;

                            // Found. Set flag, reinitialize handles and call it a day
                            found = true;
                            Interop.Win32Api.CloseHandle(procHandle);
                            procHandle = innerProcHandle;
                            procId = innerProcId;
                            appName = GetProcessNameFromHandle(procHandle);
                        }

                        return true;
                    }, IntPtr.Zero);
                }

                // 检查是否需要防抖处理
                var currentTime = DateTime.UtcNow;
                var timeSinceLastSwitch = currentTime - _lastSwitchTime;

                // Boost the current foreground app, and then impose EcoQoS for previous foreground app
                var bypass = IsBypassProcess(appName.AsSpan());
                if (!bypass)
                {
                    Console.WriteLine($"Boost {appName}");
                    ToggleEfficiencyMode(procHandle, false);
                }

                // 如果距离上次切换太近，跳过本次处理
                if (timeSinceLastSwitch < TimeSpan.FromMilliseconds(500)) // 半秒防抖
                {
                    // 更新应用访问时间，但不执行切换
                    UpdateAppAccessTime(procId);
                    return;
                }

                // 对之前的前台应用应用效率限制
                if (pendingProcPid != 0 && pendingProcPid != procId)
                {
                    var prevProcHandle = Interop.Win32Api.OpenProcess((uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation | 
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), false, pendingProcPid);
                    if (prevProcHandle != IntPtr.Zero)
                    {
                        try
                        {
                            // 检查之前的进程是否仍然存在且有效
                            var prevAppName = GetProcessNameFromHandle(prevProcHandle);
                            if (!string.IsNullOrEmpty(prevAppName) && !IsBypassProcess(prevAppName.AsSpan()))
                            {
                                Console.WriteLine($"Throttle {prevAppName}");
                                ToggleEfficiencyMode(prevProcHandle, true);
                            }
                        }
                        finally
                        {
                            Interop.Win32Api.CloseHandle(prevProcHandle);
                        }
                    }
                }
                
                // 同时对其他后台进程应用效率限制
                ApplyEfficiencyModeToBackgroundProcesses(procId);

                // 更新最后切换时间
                _lastSwitchTime = currentTime;
                
                // 更新 pendingProcPid 和 pendingProcName 以供后台任务使用
                pendingProcPid = procId;
                pendingProcName = appName;
            }
            finally
            {
                Interop.Win32Api.CloseHandle(procHandle);
            }
        }

        // 进程信息结构体 - 优化内存使用
        private readonly record struct ProcessInfo(int Id, string Name);
    }
}