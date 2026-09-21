using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// Forge Neo <b>内核</b>的检测与更新。
    ///
    /// <para>与插件侧（<see cref="ExtensionManager"/>）是两件不同的事，理由都写在
    /// <see cref="CoreUpdateInfo"/> 的注释里。这里只补一条最容易踩的：
    /// <b>内核的更新方式与插件不一样，而且不能改成一样</b> ——</para>
    ///
    /// <para><b>为什么内核要用 <c>checkout -f FETCH_HEAD -- .</c> 而不是 <c>reset --hard</c></b>：
    /// 整合包是「拷贝部署」来的，仓库里所有文件都已存在但<b>未被 git 跟踪</b>；
    /// 这种状态下 <c>reset --hard</c> <b>不会覆盖已存在的未跟踪文件</b>，
    /// 于是「更新」看起来成功了、实际一个字节都没变（实测验证过）。
    /// <c>checkout -f</c> 才是真的拿远端内容覆盖工作区。</para>
    ///
    /// <para>而插件的仓库是 <c>git clone</c> 出来的、文件本来就被跟踪，
    /// 所以那边 <c>reset --hard</c> 是对的。两条路各自成立，别互相抄。</para>
    /// </summary>
    public static class CoreUpdater
    {
        // ---- 上游仓库（Forge Neo 官方）----
        private const string GitUrl = "https://github.com/Haoming02/sd-webui-forge-classic.git";
        private const string BranchName = "neo";

        /// <summary>只读的版本文件 —— 拿它判版本不占 GitHub API 配额</summary>
        private const string RawVersionUrl =
            "https://raw.githubusercontent.com/Haoming02/sd-webui-forge-classic/neo/modules_forge/forge_version.py";

        /// <summary>上游最新提交（best-effort，限流时静默降级）</summary>
        private const string ApiCommitUrl =
            "https://api.github.com/repos/Haoming02/sd-webui-forge-classic/commits/neo";

        /// <summary>
        /// Forge Neo 安装根目录。默认随 <see cref="AppPaths.Root"/> 自适应，
        /// 保留 setter 是为了让测试能把整条链路指到临时目录上。
        /// </summary>
        public static string Root { get; set; } = AppPaths.Root;

        /// <summary>上游地址（界面上显示「远程地址」用）</summary>
        public static string RemoteUrl => GitUrl;

        /// <summary>跟踪的分支（界面上显示用）</summary>
        public static string Branch => BranchName;

        /// <summary>检测内核是否有新版本。任何失败都变成 <see cref="UpdateState.Error"/>，不抛异常。</summary>
        public static async Task<CoreUpdateInfo> CheckAsync()
        {
            var item = new CoreUpdateInfo
            {
                Path = Root,
                RemoteUrl = GitUrl,
                Branch = BranchName
            };

            string localRel = ReadLocalRelease();
            item.CurrentVersion = string.IsNullOrEmpty(localRel) ? "未知" : localRel;

            bool isRepo = GitRunner.IsRepo(Root);

            // 远程最新版本号（raw 文件，不占 API 配额）
            string upRel = "";
            try
            {
                string txt = await UpdateHttp.Client.GetStringAsync(RawVersionUrl);
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
                using var doc = JsonDocument.Parse(await UpdateHttp.Client.GetStringAsync(ApiCommitUrl));
                var root = doc.RootElement;
                upSha = root.GetProperty("sha").GetString() ?? "";
                upMsg = root.GetProperty("commit").GetProperty("message").GetString() ?? "";
                upMsg = upMsg.Split('\n')[0];
                upDate = root.GetProperty("commit").GetProperty("committer").GetProperty("date").GetString() ?? "";
            }
            catch { /* API 限流时静默降级 */ }

            if (!string.IsNullOrEmpty(upSha)) item.LatestVersion = GitRunner.ShortSha(upSha);
            else if (!string.IsNullOrEmpty(upRel)) item.LatestVersion = upRel;
            item.LatestMessage = string.IsNullOrEmpty(upMsg) && !string.IsNullOrEmpty(upDate)
                ? upDate.Replace("T", " ").Replace("Z", "")
                : upMsg;

            if (isRepo)
            {
                // 是 git 仓库：用提交号精确对比
                var (c1, o1, _) = GitRunner.Run("rev-parse HEAD", Root, 15000);
                var (c2, o2, _) = GitRunner.Run($"ls-remote origin refs/heads/{BranchName}", Root, 60000);
                if (c1 == 0 && c2 == 0)
                {
                    string localSha = o1.Trim();
                    string remoteSha = o2.Trim().Split('\t').FirstOrDefault() ?? "";
                    item.CurrentVersion = GitRunner.ShortSha(localSha);
                    var (c3, o3, _) = GitRunner.Run("log -1 --format=%ci", Root, 15000);
                    if (c3 == 0) item.CurrentDate = o3.Trim();
                    item.State = (localSha == remoteSha) ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
                    item.Message = item.State == UpdateState.UpToDate ? "已是最新" : "有新提交可更新";
                    return item;
                }
            }

            // 非 git 仓库（或 git 不可用）：退回按 release 版本号对比
            item.CurrentVersion = string.IsNullOrEmpty(localRel) ? "未知" : localRel;
            item.LatestVersion = string.IsNullOrEmpty(upRel) ? item.LatestVersion : upRel;
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

        /// <summary>
        /// 从上游拉取并覆盖内核源码。返回 (是否成功, 说明)。
        ///
        /// <para>只覆盖<b>上游跟踪的文件</b>：<c>config.json</c> / 模型 / 插件 / venv /
        /// 启动器 exe 都不在仓库里，不会被动到。</para>
        /// </summary>
        public static async Task<(bool ok, string msg)> UpdateAsync()
        {
            if (!GitRunner.Available) return (false, "未找到 git.exe");

            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                var (ci, _, ei) = await Task.Run(() => GitRunner.Run("init", Root, 30000));
                if (ci != 0) return (false, "初始化仓库失败：" + GitRunner.CleanErr(ei));
            }

            // 设置/修正 remote
            var (cs, _, _) = await Task.Run(() => GitRunner.Run($"remote set-url origin {GitUrl}", Root, 15000));
            if (cs != 0)
            {
                await Task.Run(() => GitRunner.Run($"remote add origin {GitUrl}", Root, 15000));
            }

            var (cf, _, ef) = await Task.Run(() => GitRunner.Run($"fetch origin {BranchName} --depth=1", Root, 300000));
            if (cf != 0) return (false, "拉取上游失败：" + GitRunner.CleanErr(ef));

            // 关键：用 checkout -f 强制覆盖仓库内文件。
            // 不能用 reset --hard —— 对于「拷贝部署」目录（所有文件已存在但未被跟踪），
            // reset --hard 不会覆盖已存在的未跟踪文件，会导致更新完全失效（实测验证）。
            var (cch, _, ech) = await Task.Run(() => GitRunner.Run("checkout -f FETCH_HEAD -- .", Root, 300000));
            if (cch != 0) return (false, "写入更新失败：" + GitRunner.CleanErr(ech));

            // 对齐本地分支与 HEAD，便于下次检测（检测用的是 rev-parse HEAD）
            await Task.Run(() => GitRunner.Run($"update-ref refs/heads/{BranchName} FETCH_HEAD", Root, 30000));
            await Task.Run(() => GitRunner.Run($"symbolic-ref HEAD refs/heads/{BranchName}", Root, 30000));
            await Task.Run(() => GitRunner.Run("reset --mixed FETCH_HEAD", Root, 60000));

            var (ch, oh, _) = await Task.Run(() => GitRunner.Run("rev-parse HEAD", Root, 15000));
            string sha = ch == 0 ? GitRunner.ShortSha(oh.Trim()) : "";
            return (true, "内核已更新到 " + (string.IsNullOrEmpty(sha) ? BranchName : sha));
        }

        /// <summary>
        /// 刚更新完的内核：<b>本地只读</b>地把它刷成"已是最新"（不联网）。
        ///
        /// <para>与 <see cref="ExtensionManager.MarkUpdated"/> 是对称的两件事，
        /// 理由也一样：更新完只需要刷新<b>受影响的那一项</b>。老写法是收尾再跑一次
        /// 完整检测（要打 GitHub 的 raw 文件 + API + <c>ls-remote</c> 三个网络请求），
        /// 只为了把一行字从"可更新"改成"已是最新"。</para>
        ///
        /// <para>⚠ 同样<b>只在更新成功之后调</b> —— 它直接断言
        /// <see cref="UpdateState.UpToDate"/>。</para>
        /// </summary>
        public static void MarkUpdated(CoreUpdateInfo item)
        {
            if (item == null) return;

            item.Path = Root;
            item.RemoteUrl = GitUrl;
            item.Branch = BranchName;

            string rel = ReadLocalRelease();

            // 优先用提交号：界面上两边显示的是同一种东西，才好对比
            if (GitRunner.IsRepo(Root))
            {
                var (sha, date) = GitRunner.HeadOf(Root);
                if (!string.IsNullOrEmpty(sha))
                {
                    item.CurrentVersion = GitRunner.ShortSha(sha);
                    item.CurrentDate = date;
                }
                else if (!string.IsNullOrEmpty(rel))
                {
                    item.CurrentVersion = rel;
                }
            }
            else if (!string.IsNullOrEmpty(rel))
            {
                item.CurrentVersion = rel;
            }

            item.LatestVersion = item.CurrentVersion;   // 刚对齐过，两边就是同一个提交
            item.LatestMessage = "";
            item.State = UpdateState.UpToDate;
            item.Message = "已是最新";
        }

        /// <summary>读本地 <c>modules_forge/forge_version.py</c> 里的 release 号（读不到返回空串）</summary>
        public static string ReadLocalRelease()
        {
            try
            {
                string f = Path.Combine(Root, "modules_forge", "forge_version.py");
                if (!File.Exists(f)) return "";
                return ParseRelease(File.ReadAllText(f));
            }
            catch { return ""; }
        }

        /// <summary>
        /// 从 <c>forge_version.py</c> 的内容里抓 <c>release = "..."</c> 那一行。
        ///
        /// <para>⚠ 必须用 <c>^release\s*=</c> 这种<b>行首锚定 + 等号</b>的判据，
        /// 不能只判 <c>StartsWith("release")</c> —— 同文件里还有
        /// <c>release_codename = "..."</c> 这个键，只判前缀的话，
        /// 只要它排在上面就会被取成版本号（值恰好也是一串带引号的文本，不报错）。</para>
        /// </summary>
        public static string ParseRelease(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";

            foreach (var line in content.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*release\s*=\s*(.+?)\s*$");
                if (m.Success) return m.Groups[1].Value.Trim().Trim('"', '\'', ' ');
            }
            return "";
        }
    }
}
