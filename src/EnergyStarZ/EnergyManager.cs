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
    public class EnergyManager : IDisposable
    {
        // 配置常量
        private const int DefaultSwitchDebounceMs = 500;
        private const int MinWindowsBuild = 26100;
        private const int ExitErrorCode = 120;

        public static int RequiredWindowsBuild => MinWindowsBuild;
        public static int ExitErrorCodeValue => ExitErrorCode;

        public static IReadOnlyCollection<string> BypassProcessList
        {
            get
            {
                lock (_bypassListLock)
                {
                    return _bypassProcessList.ToList().AsReadOnly();
                }
            }
        }

        private static readonly object _bypassListLock = new();
        private static HashSet<string> _bypassProcessList = new(StringComparer.OrdinalIgnoreCase);

        // Speical handling needs for UWP to get the child window process
        public const string UWPFrameHostApp = "ApplicationFrameHost.exe";

        // 使用LRU缓存来防止频繁切换，通过 _lruLock 保证线程安全
        private static readonly object _lruLock = new();
        private static readonly LinkedList<(uint pid, string name, DateTime lastAccess, int weight)> _recentlyUsedApps = new();
        private static readonly Dictionary<uint, LinkedListNode<(uint pid, string name, DateTime lastAccess, int weight)>> _appLookup = new();
        private static int _lruCacheSize = 5;
        private static TimeSpan _timeoutPeriod = TimeSpan.FromSeconds(30);

        private static readonly object _pendingProcLock = new();
        private static uint _pendingProcPid = 0;
        private static string _pendingProcName = "";

        private static IntPtr pThrottleOn = IntPtr.Zero;
        private static IntPtr pThrottleOff = IntPtr.Zero;
        private static int szControlBlock = 0;
        private static bool _unmanagedResourcesInitialized = false;

        private static readonly ThreadLocal<StringBuilder> _threadLocalStringBuilder =
            new(() => new StringBuilder(1024));

        private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;

        private static readonly ConcurrentDictionary<int, ProcessInfo> ProcessCache = new();

        // 后台任务
        private static CancellationTokenSource? _backgroundTasksCts;
        private static Task? _processCacheRefreshTask;
        private static Task? _powerStatusCheckTask;
        
        private static volatile bool _isBatteryPowered = false;
        private static volatile bool _autoPowerModeEnabled = false;

        private static volatile PowerMode _currentMode = PowerMode.Auto;
        public static PowerMode CurrentMode
        {
            get => _currentMode;
            set => _currentMode = value;
        }

        private static DateTime _lastSwitchTime = DateTime.MinValue;
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
            try
            {
                var config = ConfigurationHelper.LoadConfiguration();
                var settings = ConfigurationHelper.GetAppSettings(config);

                lock (_bypassListLock)
                {
                    _bypassProcessList = new HashSet<string>(settings.BypassProcessList ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                }

                lock (_lruLock)
                {
                    _lruCacheSize = Math.Max(1, settings.LRUCacheSize);
                    _timeoutPeriod = TimeSpan.FromSeconds(settings.TimeoutSeconds);
                }

                _switchDebounceMs = settings.SwitchDebounceMs > 0 ? settings.SwitchDebounceMs : DefaultSwitchDebounceMs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to load configuration: {ex.Message}");
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
                Console.WriteLine($"[ERROR] Failed to initialize unmanaged resources: {ex.Message}");
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
            Console.WriteLine($"[{DateTime.UtcNow:O}] Process cache refresh loop started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    RefreshProcessCache();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] RefreshProcessCache: {ex.Message}");
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

            Console.WriteLine($"[{DateTime.UtcNow:O}] Process cache refresh loop stopped.");
        }

        private static async Task PowerStatusCheckLoop(CancellationToken cancellationToken)
        {
            Console.WriteLine($"[{DateTime.UtcNow:O}] Power status check loop started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    CheckPowerStatus();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] CheckPowerStatus: {ex.Message}");
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

            Console.WriteLine($"[{DateTime.UtcNow:O}] Power status check loop stopped.");
        }

        private static void CheckPowerStatus()
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
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [POWER STATUS] Switched to Battery Mode - Automatic energy saving enabled");
                }
                else
                {
                    CurrentMode = PowerMode.Paused;
                    HookManager.UnsubscribeWindowEvents();
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [POWER STATUS] Switched to AC Mode - Energy saving paused");
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
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [WARNING] Error waiting for background tasks: {ex.Message}");
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

        private static void RefreshProcessCache()
        {
            Process[]? processes = null;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] Failed to get processes: {ex.Message}");
                return;
            }

            var activeIds = new HashSet<int>(processes.Length);

            try
            {
                foreach (var proc in processes)
                {
                    try
                    {
                        if (proc.SessionId != CurrentSessionId)
                        {
                            continue;
                        }

                        activeIds.Add(proc.Id);

                        var procName = proc.ProcessName + ".exe";
                        if (!ProcessCache.ContainsKey(proc.Id))
                        {
                            ProcessCache.TryAdd(proc.Id, new ProcessInfo(proc.Id, procName));
                        }
                        else
                        {
                            var existing = ProcessCache[proc.Id];
                            if (!existing.Name.Equals(procName, StringComparison.OrdinalIgnoreCase))
                            {
                                ProcessCache[proc.Id] = new ProcessInfo(proc.Id, procName);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略单个进程的异常
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            finally
            {
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }

            foreach (var kvp in ProcessCache)
            {
                if (!activeIds.Contains(kvp.Key))
                {
                    ProcessCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// 对指定进程列表应用效率模式，排除白名单和指定的前台/待处理进程
        /// </summary>
        private static void ApplyEfficiencyMode(IEnumerable<ProcessInfo> processes, uint excludePid1, uint excludePid2 = 0)
        {
            var processSnapshot = processes.ToList();

            foreach (var procInfo in processSnapshot)
            {
                if (procInfo.Id == excludePid1 || procInfo.Id == excludePid2)
                    continue;

                lock (_bypassListLock)
                {
                    if (_bypassProcessList.Contains(procInfo.Name))
                        continue;
                }

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
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] ApplyEfficiencyMode failed for {procInfo.Name} (PID: {procInfo.Id}): {ex.Message}");
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

            uint currentPendingPid;
            lock (_pendingProcLock)
            {
                currentPendingPid = _pendingProcPid;
            }

            ApplyEfficiencyMode(ProcessCache.Values, currentPendingPid);
        }

        public static void RestoreAllProcessesToNormal()
        {
            var processSnapshot = ProcessCache.Values.ToList();

            foreach (var procInfo in processSnapshot)
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
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] RestoreAllProcessesToNormal failed for {procInfo.Name} (PID: {procInfo.Id}): {ex.Message}");
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
            lock (_bypassListLock)
            {
                foreach (var bypassProcess in _bypassProcessList)
                {
                    if (processName.Equals(bypassProcess.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static void HandleForegroundEvent(IntPtr hwnd)
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
                uint actualProcId = procId;
                IntPtr actualProcHandle = procHandle;

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
                        Interop.Win32Api.CloseHandle(actualProcHandle);
                        actualProcHandle = childProcessInfo.Value.Handle;
                        actualProcId = childProcessInfo.Value.Pid;
                        appName = childProcessInfo.Value.Name;
                    }
                }

                var currentTime = DateTime.UtcNow;
                var timeSinceLastSwitch = currentTime - _lastSwitchTime;

                // Boost the current foreground app
                var bypass = IsBypassProcess(appName.AsSpan());
                if (!bypass)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:O}] [FOREGROUND] Boost {appName}");
                    ToggleEfficiencyMode(actualProcHandle, false);
                }

                // 防抖：距离上次切换太近则跳过
                if (timeSinceLastSwitch < TimeSpan.FromMilliseconds(_switchDebounceMs))
                {
                    UpdateAppAccessTime(actualProcId);
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
                                Console.WriteLine($"[{DateTime.UtcNow:O}] [BACKGROUND] Throttle {prevAppName}");
                                ToggleEfficiencyMode(prevProcHandle, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] Failed to throttle previous process: {ex.Message}");
                        }
                        finally
                        {
                            Interop.Win32Api.CloseHandle(prevProcHandle);
                        }
                    }
                }

                // 对其他后台进程应用效率限制
                ApplyEfficiencyMode(ProcessCache.Values, actualProcId, prevPid);

                _lastSwitchTime = currentTime;
                lock (_pendingProcLock)
                {
                    _pendingProcPid = actualProcId;
                    _pendingProcName = appName;
                }
            }
            finally
            {
                Interop.Win32Api.CloseHandle(procHandle);
            }
        }

        private readonly record struct UwpChildProcessInfo(uint Pid, IntPtr Handle, string Name);

        private readonly record struct ProcessInfo(int Id, string Name);
    }
}
