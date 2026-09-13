using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ForgeNeoLauncher
{
    public enum UpdateState
    {
        Unknown,          // 未检测
        UpToDate,         // 已是最新
        UpdateAvailable,  // 有新版本
        Error             // 检测出错
    }

    /// <summary>一条可检测/可更新的条目（内核或插件）</summary>
    public class UpdateItem
    {
        public bool IsCore { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string RemoteUrl { get; set; } = "";
        public string Branch { get; set; } = "";
        public string CurrentVersion { get; set; } = "";   // 本地版本/提交
        public string LatestVersion { get; set; } = "";    // 远程最新
        public string CurrentDate { get; set; } = "";      // 本地提交日期
        public string LatestMessage { get; set; } = "";    // 远程最新提交说明
        public UpdateState State { get; set; } = UpdateState.Unknown;
        public string Message { get; set; } = "";

        public bool CanUpdate => State == UpdateState.UpdateAvailable;
    }

    /// <summary>
    /// 更新检测器：内核走「本地版本文件 + GitHub 上游」或 git；插件走标准 git。
    /// </summary>
    public static class Updater
    {
        // ---- 上游仓库（Forge Neo 官方）----
        private const string CoreOwner = "Haoming02";
        private const string CoreRepo = "sd-webui-forge-classic";
        private const string CoreBranch = "neo";
        private const string CoreGitUrl = "https://github.com/Haoming02/sd-webui-forge-classic.git";
        private const string CoreRawVersionUrl =
            "https://raw.githubusercontent.com/Haoming02/sd-webui-forge-classic/neo/modules_forge/forge_version.py";
        private const string CoreApiCommitUrl =
            "https://api.github.com/repos/Haoming02/sd-webui-forge-classic/commits/neo";

        /// <summary>Forge Neo 安装根目录（由 AppPaths 解析，随 exe 位置自适应）</summary>
        public static string RepoDir { get; set; } = AppPaths.Root;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        static Updater()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeNeoLauncher/1.0");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        // ==================== git 定位 ====================
        private static string? _gitPath;
        private static bool _gitSearched;

        public static string? GitPath
        {
            get
            {
                if (_gitSearched) return _gitPath;
                _gitSearched = true;

                // 优先用系统标准安装的 Git（独立 exe 运行时不依赖其它环境的 PATH）
                foreach (var c in new[]
                {
                    @"C:\Program Files\Git\cmd\git.exe",
                    @"C:\Program Files (x86)\Git\cmd\git.exe",
                    @"C:\Program Files\Git\bin\git.exe"
                })
                {
                    if (File.Exists(c)) { _gitPath = c; return _gitPath; }
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
                        var first = outp.Split('\n')
                            .Select(s => s.Trim())
                            .FirstOrDefault(s => s.Length > 0 && File.Exists(s)
                                                 && s.IndexOf("workbuddy", StringComparison.OrdinalIgnoreCase) < 0);
                        if (first != null) { _gitPath = first; return _gitPath; }
                    }
                }
                catch { }

                return null;
            }
        }

        public static bool GitAvailable => GitPath != null;

        // ==================== git 执行 ====================
        private static (int code, string stdout, string stderr) RunGit(string args, string workDir, int timeoutMs = 30000)
        {
            var git = GitPath;
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

        private static string ShortSha(string sha) =>
            string.IsNullOrEmpty(sha) ? "" : sha.Substring(0, Math.Min(8, sha.Length));

        // ==================== 内核检测 ====================
        public static async Task<UpdateItem> CheckCoreAsync()
        {
            var item = new UpdateItem
            {
                IsCore = true,
                Name = "Forge Neo 内核",
                Path = RepoDir,
                RemoteUrl = CoreGitUrl,
                Branch = CoreBranch
            };

            string localRel = ReadLocalCoreRelease();
            item.CurrentVersion = string.IsNullOrEmpty(localRel) ? "未知" : localRel;

            bool isRepo = Directory.Exists(System.IO.Path.Combine(RepoDir, ".git"));

            // 远程最新版本号（raw 文件，不占 API 配额）
            string upRel = "";
            try
            {
                string txt = await Http.GetStringAsync(CoreRawVersionUrl);
                upRel = ParseRelease(txt);
            }
            catch (Exception ex)
            {
                item.State = UpdateState.Error;
                item.Message = "无法访问上游版本文件：" + ex.Message;
                return item;
            }

            // 远程最新提交信息（best-effort）
            string upSha = "", upMsg = "", upDate = "";
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(CoreApiCommitUrl));
                var root = doc.RootElement;
                upSha = root.GetProperty("sha").GetString() ?? "";
                upMsg = root.GetProperty("commit").GetProperty("message").GetString() ?? "";
                upMsg = upMsg.Split('\n')[0];
                upDate = root.GetProperty("commit").GetProperty("committer").GetProperty("date").GetString() ?? "";
            }
            catch { /* API 限流时静默降级 */ }

            if (!string.IsNullOrEmpty(upSha)) item.LatestVersion = ShortSha(upSha);
            else if (!string.IsNullOrEmpty(upRel)) item.LatestVersion = upRel;
            item.LatestMessage = string.IsNullOrEmpty(upMsg) && !string.IsNullOrEmpty(upDate)
                ? upDate.Replace("T", " ").Replace("Z", "")
                : upMsg;

            if (isRepo)
            {
                // 是 git 仓库：用提交号精确对比
                var (c1, o1, _) = RunGit("rev-parse HEAD", RepoDir, 15000);
                var (c2, o2, _) = RunGit($"ls-remote origin refs/heads/{CoreBranch}", RepoDir, 60000);
                if (c1 == 0 && c2 == 0)
                {
                    string localSha = o1.Trim();
                    string remoteSha = o2.Trim().Split('\t').FirstOrDefault() ?? "";
                    item.CurrentVersion = ShortSha(localSha);
                    var (c3, o3, _) = RunGit("log -1 --format=%ci", RepoDir, 15000);
                    if (c3 == 0) item.CurrentDate = o3.Trim();
                    item.State = (localSha == remoteSha) ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
                    item.Message = item.State == UpdateState.UpToDate ? "已是最新" : "有新提交可更新";
                    return item;
                }
            }

            // 非 git 仓库：按 release 版本号对比
            item.CurrentVersion = string.IsNullOrEmpty(localRel) ? "未知" : localRel;
            item.LatestVersion = (string.IsNullOrEmpty(upRel) ? item.LatestVersion : upRel);
            if (!string.IsNullOrEmpty(localRel) && !string.IsNullOrEmpty(upRel))
            {
                item.State = (localRel == upRel) ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
                item.Message = item.State == UpdateState.UpToDate
                    ? "已是最新"
                    : $"上游已发布 {upRel}";
            }
            else
            {
                item.State = UpdateState.Unknown;
                item.Message = "无法确定版本";
            }
            return item;
        }

        private static string ReadLocalCoreRelease()
        {
            try
            {
                string f = System.IO.Path.Combine(RepoDir, "modules_forge", "forge_version.py");
                if (!File.Exists(f)) return "";
                return ParseRelease(File.ReadAllText(f));
            }
            catch { return ""; }
        }

        private static string ParseRelease(string content)
        {
            foreach (var line in content.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("release"))
                {
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    return t.Substring(eq + 1).Trim().Trim('"', '\'', ' ');
                }
            }
            return "";
        }

        // ==================== 插件检测 ====================
        public static async Task<List<UpdateItem>> CheckExtensionsAsync(Action<string>? onProgress = null)
        {
            var list = new List<UpdateItem>();
            string extDir = System.IO.Path.Combine(RepoDir, "extensions");
            if (!Directory.Exists(extDir)) return list;

            foreach (var dir in Directory.GetDirectories(extDir))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (!Directory.Exists(System.IO.Path.Combine(dir, ".git"))) continue;

                var item = new UpdateItem
                {
                    IsCore = false,
                    Name = name,
                    Path = dir
                };

                // remote url
                var (cr, or_, _) = RunGit("remote get-url origin", dir, 10000);
                item.RemoteUrl = cr == 0 ? or_.Trim() : "";

                // 当前分支
                var (cb, ob, _) = RunGit("rev-parse --abbrev-ref HEAD", dir, 10000);
                item.Branch = (cb == 0) ? ob.Trim() : "";

                // 本地 HEAD
                var (ch, oh, _) = RunGit("rev-parse HEAD", dir, 10000);
                if (ch != 0)
                {
                    item.State = UpdateState.Error;
                    item.Message = "读取本地提交失败";
                    list.Add(item);
                    continue;
                }
                string localSha = oh.Trim();
                item.CurrentVersion = ShortSha(localSha);

                var (cd, od, _) = RunGit("log -1 --format=%ci", dir, 10000);
                if (cd == 0) item.CurrentDate = od.Trim();

                onProgress?.Invoke($"正在检测：{name} ...");

                // 远程 HEAD（ls-remote 只读，不改动仓库）
                string refSpec = string.IsNullOrEmpty(item.Branch) || item.Branch == "HEAD"
                    ? "HEAD"
                    : $"refs/heads/{item.Branch}";
                var (cl, ol, el) = RunGit($"ls-remote origin {refSpec}", dir, 60000);
                if (cl != 0)
                {
                    item.State = UpdateState.Error;
                    item.Message = "无法连接远程仓库";
                    list.Add(item);
                    continue;
                }
                string remoteSha = ol.Trim().Split('\n').FirstOrDefault()?.Split('\t').FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(remoteSha))
                {
                    item.State = UpdateState.Error;
                    item.Message = "远程无对应分支";
                    list.Add(item);
                    continue;
                }
                item.LatestVersion = ShortSha(remoteSha);

                if (localSha == remoteSha)
                {
                    item.State = UpdateState.UpToDate;
                    item.Message = "已是最新";
                }
                else
                {
                    item.State = UpdateState.UpdateAvailable;
                    item.Message = "有新提交可更新";
                    // best-effort：取远程最新提交说明（仅 GitHub 仓库）
                    item.LatestMessage = await TryGetRemoteCommitMessageAsync(item.RemoteUrl, item.Branch);
                }

                list.Add(item);
            }
            return list;
        }

        // 通过 GitHub API 取某分支最新提交标题（失败返回空，不抛异常）
        private static async Task<string> TryGetRemoteCommitMessageAsync(string remoteUrl, string branch)
        {
            try
            {
                if (string.IsNullOrEmpty(remoteUrl) || string.IsNullOrEmpty(branch)) return "";
                if (!remoteUrl.Contains("github.com")) return "";
                string slug = remoteUrl
                    .Replace("https://github.com/", "")
                    .Replace("http://github.com/", "");
                if (slug.EndsWith(".git")) slug = slug.Substring(0, slug.Length - 4);
                slug = slug.Trim('/');
                if (slug.Count(ch => ch == '/') != 1) return "";

                string api = $"https://api.github.com/repos/{slug}/commits/{branch}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(api));
                var msg = doc.RootElement.GetProperty("commit").GetProperty("message").GetString() ?? "";
                return msg.Split('\n')[0];
            }
            catch { return ""; }
        }

        // ==================== 执行更新 ====================
        public static async Task<(bool ok, string msg)> UpdateExtensionAsync(UpdateItem item)
        {
            if (GitPath == null) return (false, "未找到 git.exe");
            var (c1, _, e1) = await Task.Run(() => RunGit("fetch origin --prune", item.Path, 180000));
            if (c1 != 0) return (false, "拉取失败：" + CleanErr(e1));

            string target = string.IsNullOrEmpty(item.Branch) || item.Branch == "HEAD"
                ? "FETCH_HEAD"
                : $"origin/{item.Branch}";
            var (c2, o2, e2) = await Task.Run(() => RunGit($"reset --hard {target}", item.Path, 30000));
            if (c2 != 0) return (false, "更新失败：" + CleanErr(e2));
            return (true, "已更新到 " + ShortSha(o2.Trim()));
        }

        public static async Task<(bool ok, string msg)> UpdateCoreAsync()
        {
            if (GitPath == null) return (false, "未找到 git.exe");

            if (!Directory.Exists(System.IO.Path.Combine(RepoDir, ".git")))
            {
                var (ci, _, ei) = await Task.Run(() => RunGit("init", RepoDir, 30000));
                if (ci != 0) return (false, "初始化仓库失败：" + CleanErr(ei));
            }

            // 设置/修正 remote
            var (cs, _, _) = await Task.Run(() => RunGit($"remote set-url origin {CoreGitUrl}", RepoDir, 15000));
            if (cs != 0)
            {
                await Task.Run(() => RunGit($"remote add origin {CoreGitUrl}", RepoDir, 15000));
            }

            var (cf, _, ef) = await Task.Run(() => RunGit($"fetch origin {CoreBranch} --depth=1", RepoDir, 300000));
            if (cf != 0) return (false, "拉取上游失败：" + CleanErr(ef));

            // 关键：用 checkout -f 强制覆盖仓库内文件。
            // 不能用 reset --hard —— 对于「拷贝部署」目录（所有文件已存在但未被跟踪），
            // reset --hard 不会覆盖已存在的未跟踪文件，会导致更新完全失效（实测验证）。
            var (cch, _, ech) = await Task.Run(() => RunGit("checkout -f FETCH_HEAD -- .", RepoDir, 300000));
            if (cch != 0) return (false, "写入更新失败：" + CleanErr(ech));

            // 对齐本地分支与 HEAD，便于下次检测（检测用的是 rev-parse HEAD）
            await Task.Run(() => RunGit($"update-ref refs/heads/{CoreBranch} FETCH_HEAD", RepoDir, 30000));
            await Task.Run(() => RunGit($"symbolic-ref HEAD refs/heads/{CoreBranch}", RepoDir, 30000));
            await Task.Run(() => RunGit("reset --mixed FETCH_HEAD", RepoDir, 60000));

            var (ch, oh, _) = await Task.Run(() => RunGit("rev-parse HEAD", RepoDir, 15000));
            string sha = ch == 0 ? ShortSha(oh.Trim()) : "";
            return (true, "内核已更新到 " + (string.IsNullOrEmpty(sha) ? CoreBranch : sha));
        }

        private static string CleanErr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(无详细信息)";
            var lines = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            return lines.Length > 0 ? lines[lines.Length - 1] : s.Trim();
        }
    }
}
