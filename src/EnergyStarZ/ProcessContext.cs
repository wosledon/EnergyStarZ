using System.Diagnostics;
using EnergyStarZ.Interop;
using EnergyStarZ.Utilities;

namespace EnergyStarZ
{
    /// <summary>
    /// 节流决策所需的上下文信息：进程是否有可见界面、是否在播音/全屏、
    /// 用户是否空闲，以及混合架构 CPU 的能效核集合。
    /// 所有探测都带 TTL 缓存，且任何失败都安全降级。
    /// </summary>
    public static class ProcessContext
    {
        private static readonly object _visibleLock = new();
        private static HashSet<int> _visiblePids = new();
        private static DateTime _visibleRefreshedAt = DateTime.MinValue;
        private static readonly TimeSpan VisibleCacheTtl = TimeSpan.FromSeconds(2);

        private static readonly object _audioLock = new();
        private static HashSet<string> _audibleProcessNames = new(StringComparer.OrdinalIgnoreCase);

        // 能效核 CPU set：null 表示未探测/不可用
        private static uint[]? _efficiencyCoreCpuSets;
        private static bool _cpuSetsProbed;
        private static bool _hybridCpuAvailable;
        private static readonly object _cpuSetLock = new();

        /// <summary>
        /// 屏幕是否点亮（收到 GUID_MONITOR_POWER_ON 通知时为 true）
        /// </summary>
        public static volatile bool IsScreenOn = true;

        /// <summary>
        /// 是否允许能效核绑定（可在配置中关闭）
        /// </summary>
        public static volatile bool EfficiencyCoreBindingEnabled = true;

        public static void SetAudibleProcessList(IEnumerable<string>? names)
        {
            lock (_audioLock)
            {
                _audibleProcessNames = new HashSet<string>(
                    names?.Where(n => !string.IsNullOrWhiteSpace(n)) ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 返回当前拥有可见顶层窗口的进程 ID 集合（2 秒缓存）
        /// </summary>
        public static HashSet<int> GetVisibleProcessIds()
        {
            lock (_visibleLock)
            {
                if (DateTime.UtcNow - _visibleRefreshedAt < VisibleCacheTtl && _visiblePids.Count > 0)
                    return _visiblePids;

                var result = new HashSet<int>();

                bool EnumProc(IntPtr hwnd, IntPtr lparam)
                {
                    if (Interop.Win32Api.IsWindowVisible(hwnd) && !Interop.Win32Api.IsIconic(hwnd))
                    {
                        if (Interop.Win32Api.GetWindowThreadProcessId(hwnd, out uint pid) > 0 && pid != 0)
                        {
                            result.Add((int)pid);
                        }
                    }
                    return true;
                }

                try
                {
                    Interop.Win32Api.EnumWindows(EnumProc, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"EnumWindows failed: {ex.Message}");
                }

                _visiblePids = result;
                _visibleRefreshedAt = DateTime.UtcNow;
                return result;
            }
        }

        public static bool HasVisibleWindow(int pid)
        {
            return GetVisibleProcessIds().Contains(pid);
        }

        /// <summary>
        /// 启发式判断进程是否可能正在播音（配置的媒体/通讯进程名单）
        /// </summary>
        public static bool IsLikelyAudible(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;

            lock (_audioLock)
            {
                return _audibleProcessNames.Contains(processName);
            }
        }

        /// <summary>
        /// 是否处于全屏播放/演示模式（此时连前台应用都不应被节流）
        /// </summary>
        public static bool IsFullscreenOrPresentation()
        {
            try
            {
                int hr = Interop.Win32Api.SHQueryUserNotificationState(
                    out Interop.Win32Api.QUERY_USER_NOTIFICATION_STATE state);

                if (hr != 0)
                    return false;

                return state is Interop.Win32Api.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
                    or Interop.Win32Api.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_DISPLAY_REQUIRED;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 用户空闲毫秒数（无键鼠输入）
        /// </summary>
        public static uint GetIdleTimeMs()
        {
            try
            {
                var info = new Interop.Win32Api.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.Win32Api.LASTINPUTINFO>() };
                if (Interop.Win32Api.GetLastInputInfo(ref info))
                {
                    return unchecked((uint)Environment.TickCount64 - info.dwTime);
                }
            }
            catch
            {
                // 忽略
            }

            return 0;
        }

        /// <summary>
        /// 探测混合架构 CPU 的能效核集合。非混合架构或探测失败时返回 null（禁用 E 核绑定）
        /// </summary>
        public static uint[]? GetEfficiencyCoreCpuSets()
        {
            lock (_cpuSetLock)
            {
                if (_cpuSetsProbed)
                    return _efficiencyCoreCpuSets;

                _cpuSetsProbed = true;
                _efficiencyCoreCpuSets = null;
                _hybridCpuAvailable = false;

                try
                {
                    uint required = 0;
                    if (!EfficiencyCoreBindingEnabled)
                    {
                        AppLogger.Info("Efficiency-core binding disabled by configuration.");
                        return null;
                    }

                    // 先查询所需长度；部分环境会直接返回 0，则退回到固定缓冲区
                    Interop.Win32Api.GetSystemCpuSetInformation(IntPtr.Zero, 0, ref required);

                    uint bufferSize = required > 0 ? required : 64 * 1024;
                    IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bufferSize);
                    try
                    {
                        if (!Interop.Win32Api.GetSystemCpuSetInformation(buffer, bufferSize, ref required) || required == 0)
                        {
                            AppLogger.Warn("CPU set enumeration unavailable; efficiency-core binding disabled.");
                            return null;
                        }

                        int stride = System.Runtime.InteropServices.Marshal.SizeOf<Interop.Win32Api.SYSTEM_CPU_SET_INFORMATION>();
                        int count = (int)required / stride;
                        var sets = new List<(uint id, byte efficiencyClass)>(count);

                        for (int i = 0; i < count; i++)
                        {
                            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Interop.Win32Api.SYSTEM_CPU_SET_INFORMATION>(
                                buffer + i * stride);
                            if (info.Size != (uint)stride)
                                continue;

                            sets.Add((info.Union.CpuSet.Id, info.Union.CpuSet.EfficiencyClass));
                        }

                        if (sets.Count == 0)
                            return null;

                        var distinctClasses = sets.Select(s => s.efficiencyClass).Distinct().ToList();
                        if (distinctClasses.Count < 2)
                        {
                            AppLogger.Info($"Uniform CPU efficiency class ({distinctClasses[0]}); efficiency-core binding disabled.");
                            return null;
                        }

                        byte lowest = distinctClasses.Min();
                        _efficiencyCoreCpuSets = sets.Where(s => s.efficiencyClass == lowest).Select(s => s.id).ToArray();
                        _hybridCpuAvailable = true;

                        AppLogger.Info(
                            $"Hybrid CPU detected: {_efficiencyCoreCpuSets.Length} efficiency-core CPU sets (classes: {string.Join("/", distinctClasses.OrderBy(c => c))}).");
                        return _efficiencyCoreCpuSets;
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"CPU set detection failed ({ex.Message}); efficiency-core binding disabled.");
                    _efficiencyCoreCpuSets = null;
                    return null;
                }
            }
        }

        public static bool IsHybridCpu => _hybridCpuAvailable;
    }
}
