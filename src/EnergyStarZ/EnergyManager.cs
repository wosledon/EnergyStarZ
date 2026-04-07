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

        // 使用LRU缓存来防止频繁切换，通过 _lruLock 保证线程安全
        private static readonly object _lruLock = new();
        private static readonly LinkedList<(uint pid, string name, DateTime lastAccess, int weight)> _recentlyUsedApps = new();
        private static readonly Dictionary<uint, LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>> _appLookup = new();
        private static int _lruCacheSize = 5;
        private static TimeSpan _timeoutPeriod = TimeSpan.FromSeconds(30);

        private static volatile uint _pendingProcPid = 0;
        private static volatile string _pendingProcName = "";

        private static IntPtr pThrottleOn = IntPtr.Zero;
        private static IntPtr pThrottleOff = IntPtr.Zero;
        private static int szControlBlock = 0;

        private static readonly ThreadLocal<StringBuilder> _threadLocalStringBuilder =
            new(() => new StringBuilder(1024));

        private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;

        private static readonly ConcurrentDictionary<int, ProcessInfo> ProcessCache = new();

        private static System.Threading.Timer? ProcessCacheRefreshTimer;
        private static System.Threading.Timer? PowerStatusCheckTimer;
        private static volatile bool _isBatteryPowered = false;
        private static volatile bool _autoPowerModeEnabled = false;

        private static volatile PowerMode _currentMode = PowerMode.Auto;
        public static PowerMode CurrentMode
        {
            get => _currentMode;
            set => _currentMode = value;
        }

        private static DateTime _lastSwitchTime = DateTime.MinValue;

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

            ProcessCacheRefreshTimer = new System.Threading.Timer(RefreshProcessCache, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            PowerStatusCheckTimer = new System.Threading.Timer(CheckPowerStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            LoadConfiguration();
        }

        public static void LoadConfiguration()
        {
            var config = ConfigurationHelper.LoadConfiguration();
            var settings = ConfigurationHelper.GetAppSettings(config);

            BypassProcessList = new HashSet<string>(settings.BypassProcessList, StringComparer.OrdinalIgnoreCase);

            lock (_lruLock)
            {
                _lruCacheSize = Math.Max(1, settings.LRUCacheSize);
                _timeoutPeriod = TimeSpan.FromSeconds(settings.TimeoutSeconds);
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
                    CurrentMode = PowerMode.Auto;
                    HookManager.SubscribeToWindowEvents();
                    Console.WriteLine("[POWER STATUS] Switched to Battery Mode - Automatic energy saving enabled");
                }
                else
                {
                    CurrentMode = PowerMode.Paused;
                    HookManager.UnsubscribeWindowEvents();
                    Console.WriteLine("[POWER STATUS] Switched to AC Mode - Energy saving paused");
                }
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

        // LRU缓存管理方法
        private static void AddOrUpdateApp(uint pid, string name)
        {
            lock (_lruLock)
            {
                DateTime now = DateTime.UtcNow;
                int newWeight = 1;

                if (_appLookup.TryGetValue(pid, out var node))
                {
                    _recentlyUsedApps.Remove(node);
                    newWeight = node.Value.weight + 1;
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

                var newNode = new LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>((pid, name, now, newWeight));
                _recentlyUsedApps.AddFirst(newNode);
                _appLookup[pid] = newNode;
            }
        }

        private static bool IsAppInRecentList(uint pid)
        {
            lock (_lruLock)
            {
                return _appLookup.ContainsKey(pid);
            }
        }

        private static void UpdateAppAccessTime(uint pid)
        {
            lock (_lruLock)
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

                    if ((now - node.Value.lastAccess) > _timeoutPeriod)
                    {
                        _appLookup.Remove(node.Value.pid);
                        _recentlyUsedApps.Remove(node);
                    }

                    node = prevNode;
                }
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

                ProcessCache.Clear();
                lock (_lruLock)
                {
                    _recentlyUsedApps.Clear();
                    _appLookup.Clear();
                }
            }

            ReleaseUnmanagedResources();
        }

        private static void ToggleEfficiencyMode(IntPtr hProcess, bool enable)
        {
            if (CurrentMode == PowerMode.Paused)
                return;

            try
            {
                Interop.Win32Api.SetProcessInformation(hProcess, Interop.Win32Api.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    enable ? pThrottleOn : pThrottleOff, (uint)szControlBlock);

                // 无论 SetProcessInformation 成功与否，都尝试设置优先级作为补充
                Interop.Win32Api.SetPriorityClass(hProcess, enable
                    ? Interop.Win32Api.PriorityClass.IDLE_PRIORITY_CLASS
                    : Interop.Win32Api.PriorityClass.NORMAL_PRIORITY_CLASS);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying efficiency mode to process: {ex.Message}");
            }
        }

        private static void RefreshProcessCache(object? state)
        {
            try
            {
                var processes = Process.GetProcesses();
                var activeIds = new HashSet<int>(processes.Length);

                foreach (var proc in processes)
                {
                    if (proc.SessionId != CurrentSessionId)
                    {
                        proc.Dispose();
                        continue;
                    }

                    activeIds.Add(proc.Id);

                    if (!ProcessCache.ContainsKey(proc.Id))
                    {
                        ProcessCache.TryAdd(proc.Id, new ProcessInfo(proc.Id, proc.ProcessName + ".exe"));
                    }
                    else
                    {
                        var existing = ProcessCache[proc.Id];
                        if (existing.Name != proc.ProcessName + ".exe")
                        {
                            ProcessCache[proc.Id] = new ProcessInfo(proc.Id, proc.ProcessName + ".exe");
                        }
                    }
                }

                foreach (var kvp in ProcessCache)
                {
                    if (!activeIds.Contains(kvp.Key))
                    {
                        ProcessCache.TryRemove(kvp.Key, out _);
                    }
                }

                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing process cache: {ex.Message}");
            }
        }

        /// <summary>
        /// 对指定进程列表应用效率模式，排除白名单和指定的前台/待处理进程
        /// </summary>
        private static void ApplyEfficiencyMode(IEnumerable<ProcessInfo> processes, uint excludePid1, uint excludePid2 = 0)
        {
            foreach (var procInfo in processes)
            {
                if (procInfo.Id == excludePid1 || procInfo.Id == excludePid2)
                    continue;
                if (BypassProcessList.Contains(procInfo.Name))
                    continue;

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

        public static void ThrottleAllUserBackgroundProcesses()
        {
            if (CurrentMode == PowerMode.Paused)
                return;

            ApplyEfficiencyMode(ProcessCache.Values, _pendingProcPid);
        }

        public static void RestoreAllProcessesToNormal()
        {
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
            if (CurrentMode == PowerMode.Paused)
                return;

            var windowThreadId = Interop.Win32Api.GetWindowThreadProcessId(hwnd, out uint procId);
            if (windowThreadId == 0 || procId == 0) return;

            var procHandle = Interop.Win32Api.OpenProcess(
                (uint)(Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation | Interop.Win32Api.ProcessAccessFlags.SetInformation), false, procId);
            if (procHandle == IntPtr.Zero) return;

            try
            {
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

                            found = true;
                            Interop.Win32Api.CloseHandle(procHandle);
                            procHandle = innerProcHandle;
                            procId = innerProcId;
                            appName = GetProcessNameFromHandle(procHandle);
                        }

                        return true;
                    }, IntPtr.Zero);
                }

                var currentTime = DateTime.UtcNow;
                var timeSinceLastSwitch = currentTime - _lastSwitchTime;

                // Boost the current foreground app
                var bypass = IsBypassProcess(appName.AsSpan());
                if (!bypass)
                {
                    Console.WriteLine($"Boost {appName}");
                    ToggleEfficiencyMode(procHandle, false);
                }

                // 防抖：距离上次切换太近则跳过
                if (timeSinceLastSwitch < TimeSpan.FromMilliseconds(500))
                {
                    UpdateAppAccessTime(procId);
                    return;
                }

                // 对之前的前台应用应用效率限制
                var prevPid = _pendingProcPid;
                if (prevPid != 0 && prevPid != procId)
                {
                    var prevProcHandle = Interop.Win32Api.OpenProcess((uint)(Interop.Win32Api.ProcessAccessFlags.SetInformation |
                           Interop.Win32Api.ProcessAccessFlags.QueryLimitedInformation), false, prevPid);
                    if (prevProcHandle != IntPtr.Zero)
                    {
                        try
                        {
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

                // 对其他后台进程应用效率限制
                ApplyEfficiencyMode(ProcessCache.Values, procId, prevPid);

                _lastSwitchTime = currentTime;
                _pendingProcPid = procId;
                _pendingProcName = appName;
            }
            finally
            {
                Interop.Win32Api.CloseHandle(procHandle);
            }
        }

        private readonly record struct ProcessInfo(int Id, string Name);
    }
}
