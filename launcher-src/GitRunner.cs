using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// git 调用的唯一收口 —— 内核侧与插件侧共用这一份。
    ///
    /// <para><b>为什么单独抽出来</b>：v0.30 之前这些全都长在 <c>Updater</c> 里 ——
    /// 那个类一个文件装着 6 件事（git 定位、git 执行、内核检测、插件检测、内核更新、
    /// 插件更新），两侧业务混在一处，连数据模型都得靠一个 <c>IsCore</c> 布尔来区分
    /// 「这条记录是谁」。拆开之后 <c>CoreUpdater</c> 与 <c>ExtensionManager</c>
    /// 各管各的业务，「去哪找 git」「超时多少」「输出怎么解码」这类共性只维护一份。</para>
    ///
    /// <para><b>行为与原实现完全一致</b> —— v0.31 是「拆职责 + 补插件功能」，
    /// 不顺手改既有语义，出货才敢说「内核那边一个字节的行为都没变」。</para>
    /// </summary>
    public static class GitRunner
    {
        /// <summary>
        /// git.exe 的位置。<c>null</c> = 没找到 —— 此时所有操作都返回一条明确的
        /// 错误信息，<b>不抛异常</b>（调用方拿到的永远是 (false, 原因)）。
        ///
        /// <para>只搜一次并缓存：每次调用都起一个 <c>where</c> 进程是白花钱。</para>
        /// </summary>
        public static string? Exe
        {
            get
            {
                if (_searched) return _exe;
                _searched = true;
                _exe = Locate();
                return _exe;
            }
        }

        /// <summary>git 是否可用（界面据此决定要不要禁用「检测更新」）</summary>
        public static bool Available => Exe != null;

        private static string? _exe;
        private static bool _searched;

        /// <summary>
        /// 找 git.exe：先看标准安装路径，再退回 PATH。
        ///
        /// <para>⚠ 从 PATH 里挑结果时要<b>排掉 workbuddy 自带的那个</b> ——
        /// 它是给工具链用的，不保证带上 Windows 侧的凭据助手，
        /// 用它去 <c>fetch</c> 私有仓库会卡在认证上。</para>
        /// </summary>
        private static string? Locate()
        {
            // 优先用系统标准安装的 Git（独立 exe 运行时不依赖其它环境的 PATH）
            foreach (var c in new[]
            {
                @"C:\Program Files\Git\cmd\git.exe",
                @"C:\Program Files (x86)\Git\cmd\git.exe",
                @"C:\Program Files\Git\bin\git.exe"
            })
            {
                if (File.Exists(c)) return c;
            }

            // 再退回 PATH 查找
            try
            {
                var psi = new ProcessStartInfo("where", "git")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return outp.Split('\n')
                        .Select(s => s.Trim())
                        .FirstOrDefault(s => s.Length > 0 && File.Exists(s)
                                             && s.IndexOf("workbuddy", StringComparison.OrdinalIgnoreCase) < 0);
                }
            }
            catch { }

            return null;
        }

        /// <summary>跑一条 git 命令。返回 (退出码, stdout, stderr)；失败时退出码为 -1、stderr 写原因。</summary>
        public static (int code, string stdout, string stderr) Run(
            string args, string workDir, int timeoutMs = 30000)
        {
            var git = Exe;
            if (git == null) return (-1, "", "未找到 git.exe（请先安装 Git for Windows）");
            if (!Directory.Exists(workDir)) return (-1, "", "目录不存在: " + workDir);
            try
            {
                var psi = new ProcessStartInfo(git, args)
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // 禁止弹窗索要账号密码
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "无法启动 git 进程");

                // 两个流都要**并发**读：只读一个的话，另一个写满管道缓冲区就会
                // 把子进程堵死，表现为"命令超时"（而它其实只是没人在读它的输出）。
                var soTask = p.StandardOutput.ReadToEndAsync();
                var seTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return (-1, soTask.Result, seTask.Result + "  (命令超时)");
                }
                return (p.ExitCode, soTask.Result, seTask.Result);
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }

        /// <summary>把完整 sha 截成 8 位短号（界面显示用）。</summary>
        public static string ShortSha(string sha) =>
            string.IsNullOrEmpty(sha) ? "" : sha.Substring(0, Math.Min(8, sha.Length));

        /// <summary>
        /// 从 git 的输出里挑一句能给人看的。
        ///
        /// <para>git 报错常常以一大段 usage / hint 收尾，把整段塞进日志等于没有信息 ——
        /// 取<b>最后一行有内容的</b>才是真正描述问题的那句。</para>
        /// </summary>
        public static string CleanErr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(无详细信息)";
            var lines = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            return lines.Length > 0 ? lines[lines.Length - 1] : s.Trim();
        }

        /// <summary>读某个仓库的 origin 地址（读不到返回空串）。</summary>
        public static string RemoteUrlOf(string dir)
        {
            var (c, o, _) = Run("remote get-url origin", dir, 10000);
            return c == 0 ? o.Trim() : "";
        }

        /// <summary>读某个仓库的当前分支（读不到返回空串）。</summary>
        public static string BranchOf(string dir)
        {
            var (c, o, _) = Run("rev-parse --abbrev-ref HEAD", dir, 10000);
            return c == 0 ? o.Trim() : "";
        }

        /// <summary>
        /// 读某个仓库的 HEAD 全号与提交日期（读不到返回两个空串）。
        ///
        /// <para>用一条 <c>log -1 --format=%H|%ci</c> 拿全两个值 —— 拆成
        /// <c>rev-parse</c> + <c>log</c> 两条命令会让「刷新扩展列表」多花一倍进程开销，
        /// 而那个操作要对几十个扩展各来一次。</para>
        /// </summary>
        public static (string sha, string date) HeadOf(string dir)
        {
            var (c, o, _) = Run("log -1 --format=%H|%ci", dir, 10000);
            if (c != 0) return ("", "");

            string t = o.Trim();
            int bar = t.IndexOf('|');
            if (bar < 0) return ("", "");

            return (t.Substring(0, bar).Trim(), t.Substring(bar + 1).Trim());
        }

        /// <summary>
        /// 某个目录是不是 git 仓库。
        ///
        /// <para>只看 <c>.git</c> 在不在 —— 够用且<b>不起进程</b>。
        /// 扫描 extensions 目录时要对几十个目录问这个问题，
        /// 每个都跑一次 <c>rev-parse</c> 会让「刷新列表」慢到没法用。</para>
        /// </summary>
        public static bool IsRepo(string dir)
        {
            try { return Directory.Exists(System.IO.Path.Combine(dir, ".git")); }
            catch { return false; }
        }
    }
}
