using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 某个端口此刻的占用情况快照。
    /// </summary>
    internal sealed class PortProbe
    {
        /// <summary>被查的端口</summary>
        public int Port;
        /// <summary>正在 LISTEN 它的进程号；<b>0 = 端口空闲</b></summary>
        public int Pid;
        /// <summary>
        /// 是否可以确认是<b>本项目</b>的 Forge。
        /// <para>⚠ 只有拿到明确证据才是 true；<b>拿不准一律 false</b>。</para>
        /// </summary>
        public bool IsOurs;
        /// <summary>给人看的占用者描述（"空闲" / "chrome.exe（PID 1234，C:\...）"）</summary>
        public string OwnerDesc = "";

        public bool Free => Pid <= 0;
    }

    /// <summary>
    /// 端口探测 + 「占用者到底是不是我们自己的 Forge」的判定。
    ///
    /// <para><b>为什么必须分开问这两个问题</b>：修 v0.29 时发现，启动器原先只问
    /// "7860 上有没有人在听"，然后就把那个 PID 当成自己的 —— 状态栏显示它、
    /// 【打开界面】打开它、停止服务时 <c>taskkill</c> 它。可"端口有人在听"
    /// 和"那是我们的 Forge"完完全全是两件事：</para>
    ///
    /// <list type="bullet">
    ///   <item>gradio 的 <c>http_server.start_server()</c> 在<b>没给 --port</b> 时会
    ///         从 7860 起<b>静默往上找 100 个端口</b>（<c>TRY_NUM_PORTS</c>）。
    ///         于是 7860 被别人占着时，我们的 Forge 其实好好跑在 7861，
    ///         而启动器还盯着 7860 —— 从头到尾指的都是别人。</item>
    ///   <item>所以"停止服务"会<b>杀掉别人正在用的程序</b>。这是本类存在的理由。</item>
    /// </list>
    ///
    /// <para><b>为什么不用 HTTP 探测</b>（比如请求 <c>/internal/ping</c>）来判身份：
    /// 端口上坐着的可能是任何 web 服务，随便一个返回 200 的程序都会给出
    /// "是自己人"的<b>假阳性</b> —— 而假阳性的后果正是误杀，方向错不得。
    /// 现在的判据（本启动器记着的 PID、<c>forge.pid</c>、进程路径）假阳性风险低得多；
    /// 它们的<b>假阴性</b>（明明是自己人却判成外人）代价只是"另起一个实例在新端口"，无害。</para>
    /// </summary>
    internal static class PortGuard
    {
        /// <summary>默认端口。发 `--port` 的基准，也是配置项的空缺值。</summary>
        public const int DefaultPort = 7860;

        /// <summary>
        /// 换端口时最多往后试多少个。<b>与 gradio 的 <c>TRY_NUM_PORTS</c>（默认 100）对齐</b> ——
        /// 启动器自己找端口时若只试了几个，会在 gradio 本来能用的范围内误报"没有空闲端口"。
        /// </summary>
        public const int ScanCount = 100;

        // =====================================================================
        //  端口是否空闲
        // =====================================================================

        /// <summary>
        /// 当前所有处于 LISTEN 状态的 TCP 端口。取一次可以反复查询，比逐个端口
        /// 跑 <c>netstat</c> 快得多（扫描 100 个端口时尤其明显）。
        /// </summary>
        public static HashSet<int> ListeningPorts()
        {
            var set = new HashSet<int>();
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                    set.Add(ep.Port);
            }
            catch { }
            return set;
        }

        /// <summary>端口是否空闲（每次自行取一遍监听表，零散调用用这个）</summary>
        public static bool IsPortFree(int port) => IsPortFree(port, ListeningPorts());

        /// <summary>
        /// 端口是否空闲。<paramref name="listening"/> 用
        /// <see cref="ListeningPorts"/> 预先取好，避免在循环里重复枚举。
        ///
        /// <para><b>两道独立判据，任一为"忙"即判忙</b>（保守方向 ——
        /// 误判成"忙"只是往后换一个端口，无副作用；误判成"闲"会让 Forge 起来就崩）：</para>
        /// <list type="number">
        ///   <item>系统监听表里有它 —— 最直接；</item>
        ///   <item>按 <b>gradio 自己的方式</b>再试一次 bind（见
        ///         <c>gradio/http_server.py</c> 的 <c>start_server()</c>：
        ///         <c>SO_REUSEADDR</c> + <c>bind(127.0.0.1, port)</c>）。
        ///         第二道是为了那些"绑了但监听表里看不出来"的边缘情况。</item>
        /// </list>
        ///
        /// <para>⚠ Windows 上 <c>SO_REUSEADDR</c> 允许后到者绑到已被占用的端口，
        /// 单靠 bind 会漏判 —— 这正是必须有第一道的原因。反之亦然。</para>
        /// </summary>
        public static bool IsPortFree(int port, HashSet<int> listening)
        {
            if (port < 1 || port > 65535) return false;
            if (listening != null && listening.Contains(port)) return false;
            return CanBind(port);
        }

        private static bool CanBind(int port)
        {
            Socket? s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { s?.Close(); } catch { }
            }
        }

        /// <summary>
        /// 从 <paramref name="start"/> 起向上找第一个空闲端口。
        /// 找不到（全被占、或越界）返回 <b>-1</b>。
        /// </summary>
        public static int FindFreePort(int start, int count)
        {
            if (count <= 0) count = ScanCount;
            var listening = ListeningPorts();
            for (int i = 0; i < count; i++)
            {
                int p = start + i;
                if (p > 65535) break;
                if (IsPortFree(p, listening)) return p;
            }
            return -1;
        }

        // =====================================================================
        //  占用者是谁
        // =====================================================================

        /// <summary>
        /// 谁在 LISTEN 这个端口（解析 <c>netstat -ano</c>）。找不到返回 <b>-1</b>。
        ///
        /// <para>为什么不用 .NET 的 API：<c>IPGlobalProperties</c> 只给端点不给 PID，
        /// 要 PID 就得走 <c>GetExtendedTcpTable</c> 那套 P/Invoke。netstat 已经够用，
        /// 而且这是**已经跑通的老路径**，不轻易换。</para>
        /// </summary>
        public static int GetPortPid(int port)
        {
            if (port < 1 || port > 65535) return -1;
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);

                string suffix = ":" + port.ToString();
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains("LISTENING")) continue;
                    // TCP  127.0.0.1:7860  0.0.0.0:0  LISTENING  12345
                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    // parts[1] 形如 127.0.0.1:7860 / [::1]:7860 / [::]:7860。
                    // 用 ":" 前缀比较可以避开"端口是另一端口后缀"的假匹配
                    // （查 786 不会命中 1786）。
                    if (parts.Length >= 5 && parts[1].EndsWith(suffix) && parts[3] == "LISTENING")
                    {
                        if (int.TryParse(parts[4], out int pid)) return pid;
                    }
                }
            }
            catch { }
            return -1;
        }

        /// <summary>进程是否还活着（PID 可能已被复用，所以这只是一半证据）</summary>
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>
        /// 进程的可执行文件路径。读不到（越权、已退出、受保护进程）返回 <b>null</b> ——
        /// 调用方必须把 null 当成"不知道"，而不是"不是"。
        /// </summary>
        public static string? GetProcessPath(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.MainModule?.FileName;
            }
            catch { return null; }
        }

        /// <summary>进程的可执行文件名（不含扩展名）。拿不到返回空串。</summary>
        public static string GetProcessName(int pid)
        {
            if (pid <= 0) return "";
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName ?? "";
            }
            catch { return ""; }
        }

        /// <summary>可读的占用者描述，直接进日志与状态行。</summary>
        public static string Describe(int pid)
        {
            if (pid <= 0) return "空闲";
            string name = GetProcessName(pid);
            if (name.Length == 0) name = "未知进程";
            var path = GetProcessPath(pid);
            return string.IsNullOrWhiteSpace(path)
                ? $"{name}（PID {pid}，路径不可读）"
                : $"{name}.exe（PID {pid}，{path}）";
        }

        /// <summary>
        /// 光看进程路径，像不像<b>本项目</b>的 Forge：可执行文件落在包根目录之下，
        /// 且是 python。
        ///
        /// <para>为什么要加"且是 python"：只比路径前缀的话，用户放在包目录里的任何
        /// exe 都会被认成自己人。而真正监听 7860 的一定是那个 python 子进程
        /// （gradio 的 uvicorn 跑在它的线程里）。</para>
        ///
        /// <para>⚠ 用 uv 建的 venv 里 <c>Scripts\python.exe</c> 只是个转发壳，
        /// 真正干活的是 <c>runtime\python\python.exe</c> —— 两者都在包根下，
        /// 所以按"前缀在包根内 + 是 python"判断对两种情况都成立。</para>
        /// </summary>
        public static bool LooksLikeOurForge(int pid, string root, Func<int, string?>? pathOf = null)
        {
            if (pid <= 0) return false;
            if (string.IsNullOrWhiteSpace(root)) return false;

            var path = (pathOf ?? GetProcessPath)(pid);
            if (string.IsNullOrWhiteSpace(path)) return false;

            string full;
            try { full = Path.GetFullPath(path!); } catch { return false; }

            var rootFull = root.TrimEnd('\\', '/');
            var prefix = rootFull + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var exe = Path.GetFileName(full);
            return exe.StartsWith("python", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 已知 <paramref name="pid"/> 时的归属判定（不再跑 netstat）。
        ///
        /// <para>证据分级，<b>任意一级成立即为"自己人"，都不成立则为"外人"</b>：</para>
        /// <list type="number">
        ///   <item><b>本启动器记着的 PID</b>（正在跑的子进程句柄 / <c>forge.pid</c> 文件）——
        ///         最强证据，因为那份记录是我们自己写的；</item>
        ///   <item><b>进程路径在包根下且是 python</b> —— 上一次会话留下的实例，
        ///         或用户自己用包里的 <c>webui.bat</c> 起的。</item>
        /// </list>
        /// </summary>
        public static PortProbe Classify(int pid, IEnumerable<int>? ourPids, string root,
                                         Func<int, string?>? pathOf = null)
        {
            var probe = new PortProbe { Pid = pid, Port = 0 };
            if (pid <= 0)
            {
                probe.Pid = 0;
                probe.OwnerDesc = "空闲";
                return probe;
            }

            probe.OwnerDesc = Describe(pid);

            if (ourPids != null && ourPids.Any(x => x > 0 && x == pid))
            {
                probe.IsOurs = true;
                return probe;
            }

            probe.IsOurs = LooksLikeOurForge(pid, root, pathOf);
            return probe;
        }

        /// <summary>
        /// 查一个端口并判定归属。<paramref name="ourPids"/> 传"本启动器认为属于自己"
        /// 的进程号（正在跑的子进程、<c>forge.pid</c> 里的值）。
        /// </summary>
        public static PortProbe Inspect(int port, IEnumerable<int>? ourPids, string root,
                                        Func<int, string?>? pathOf = null)
        {
            var probe = Classify(GetPortPid(port), ourPids, root, pathOf);
            probe.Port = port;
            return probe;
        }
    }
}
