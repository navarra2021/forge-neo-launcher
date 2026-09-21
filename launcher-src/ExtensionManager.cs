using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// Forge <b>扩展（插件）</b>的扫描、检测、更新、安装、卸载、启用禁用。
    ///
    /// <para>内核那套在 <see cref="CoreUpdater"/>，两边不再是同一个类里靠
    /// <c>IsCore</c> 布尔分叉的两种配置（原委见 <see cref="ExtensionInfo"/> 的注释）。</para>
    ///
    /// <para><b>v0.31 补齐的四件事</b>（v0.30 及以前只有「列出 + 检测更新 + 更新」）：
    /// <list type="number">
    ///   <item><b>非 git 的扩展也列出来</b> —— 老实现遇到没有 <c>.git</c> 的目录直接
    ///     <c>continue</c>，手工解压装的扩展因此在列表里根本不出现，用户会以为没装上；</item>
    ///   <item><b>启用 / 禁用</b> —— 两处都写（扩展目录的 <c>disabled</c> 文件 +
    ///     WebUI <c>config.json</c> 的 <c>disabled_extensions</c>）；</item>
    ///   <item><b>安装</b> —— 贴地址即 <c>git clone</c> 到 <c>extensions/</c>；</item>
    ///   <item><b>卸载</b> —— 走<b>回收站</b>（见 <see cref="RecycleBin"/>），
    ///     且"能不能删"的判断一律<b>排在删除之前</b>（v0.26 的老教训）。</item>
    /// </list></para>
    ///
    /// <para><b>v0.32 又补的一条：改动之后只刷新"受影响的那一项"</b>。
    /// 更新一个插件、装一个插件之后，界面要的是那一行的新状态，而不是把
    /// 几十个插件的远程都重问一遍 —— 见 <see cref="MarkUpdated"/> 与
    /// <see cref="ScanOne"/>。<b>"重扫全部"只能是用户明确点的那个按钮</b>。</para>
    /// </summary>
    public static class ExtensionManager
    {
        /// <summary>
        /// Forge 根目录。默认随 <see cref="AppPaths.Root"/> 自适应；
        /// 保留 setter 是为了让测试把整条链路指到临时目录上（不碰真实环境）。
        /// </summary>
        public static string Root { get; set; } = AppPaths.Root;

        /// <summary>扩展目录</summary>
        public static string ExtensionsDir => Path.Combine(Root, "extensions");

        /// <summary>WebUI 的配置文件（启用状态在它里面有第二份记录）</summary>
        public static string ConfigJsonPath => Path.Combine(Root, "config.json");

        /// <summary>扩展目录下那个空标记文件的文件名（Forge 认这个）</summary>
        public const string DisabledFileName = "disabled";

        /// <summary>WebUI 配置里禁用名单的键名</summary>
        public const string DisabledKey = "disabled_extensions";

        /// <summary>需要 clone 的时长上限（国内拉 GitHub 常常要一两分钟）</summary>
        private const int CloneTimeoutMs = 180000;

        // ==================================================================
        //  扫描
        // ==================================================================

        /// <summary>扫描扩展目录。默认扫真实包目录；测试请用带参数的版本。</summary>
        public static List<ExtensionInfo> Scan() => Scan(ExtensionsDir, ConfigJsonPath);

        /// <summary>
        /// 扫描指定目录下的扩展。
        ///
        /// <para><b>两处都读才算准</b>：<c>disabled</c> 文件（Forge 认）与
        /// <c>config.json</c> 的 <c>disabled_extensions</c>（WebUI 设置页认）——
        /// 任一处在名单里就算禁用。只在一边写、只在一边读，都会让界面与实际行为对不上。</para>
        /// </summary>
        public static List<ExtensionInfo> Scan(string extDir, string configPath)
        {
            var list = new List<ExtensionInfo>();
            try
            {
                if (!Directory.Exists(extDir)) return list;

                var disabledNames = ReadDisabledNames(configPath);

                foreach (var dir in Directory.GetDirectories(extDir)
                                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var info = ReadOne(dir, disabledNames);
                    if (info != null) list.Add(info);
                }
            }
            catch { /* 扫不动就返回已有的，不把界面带崩 */ }
            return list;
        }

        /// <summary>
        /// 只扫<b>一个</b>扩展目录。<paramref name="name"/> 是目录名（= <see cref="InstallAsync"/>
        /// 用的那个仓库名）。扫不到 / 点开头的目录 → <c>null</c>。
        ///
        /// <para>存在的理由：装完一个插件之后要把这一项加进列表，而全量 <see cref="Scan"/>
        /// 会对<b>每一个</b>扩展各起几条 git 进程（几十个扩展就是上百个进程），
        /// 只为了显示一行新记录 —— 太亏。<b>但它读的仍是同一个 <see cref="ReadOne"/>，
        /// 不是"简化的另一份扫描逻辑"</b>：两份逻辑各自演化的话，新装的那一项
        /// 与刷新后的它就会长得不一样（例如禁用状态、来源文案）。</para>
        /// </summary>
        public static ExtensionInfo? ScanOne(string extDir, string configPath, string name)
        {
            if (string.IsNullOrWhiteSpace(extDir) || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                string dir = Path.Combine(extDir, name);
                if (!Directory.Exists(dir)) return null;
                return ReadOne(dir, ReadDisabledNames(configPath));
            }
            catch { return null; }
        }

        /// <summary>
        /// 读<b>一个</b>扩展目录的状态。<see cref="Scan"/> 与 <see cref="ScanOne"/> 都走它 ——
        /// 扫描规则只维护这一份。
        ///
        /// <para><b>两处都读才算准</b>：<c>disabled</c> 文件（Forge 认）与
        /// <c>config.json</c> 的 <c>disabled_extensions</c>（WebUI 设置页认）——
        /// 任一处在名单里就算禁用。只在一边写、只在一边读，都会让界面与实际行为对不上。</para>
        ///
        /// <para>点开头的目录不是扩展（<c>.git</c> / <c>.github</c> / <c>.disabled</c> …），
        /// 返回 <c>null</c> 让调用方跳过。</para>
        /// </summary>
        public static ExtensionInfo? ReadOne(string dir, List<string> disabledNames)
        {
            string name = Path.GetFileName(dir);

            // 点开头的目录不是扩展（.git / .github / .disabled …）
            if (name.StartsWith(".")) return null;

            var info = new ExtensionInfo
            {
                Name = name,
                Path = dir,
                IsGit = GitRunner.IsRepo(dir)
            };

            bool byFile = File.Exists(Path.Combine(dir, DisabledFileName));
            bool byConfig = disabledNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            info.Enabled = !(byFile || byConfig);

            if (info.IsGit)
            {
                info.RemoteUrl = GitRunner.RemoteUrlOf(dir);
                info.Branch = GitRunner.BranchOf(dir);
                var (sha, date) = GitRunner.HeadOf(dir);
                info.CurrentVersion = GitRunner.ShortSha(sha);
                info.CurrentDate = date;

                if (string.IsNullOrEmpty(sha))
                {
                    info.State = UpdateState.Error;
                    info.Message = "读取本地提交失败";
                }
                else
                {
                    info.State = UpdateState.Unknown;
                    info.Message = "未检测";
                }
            }
            else
            {
                // ⚠ 不是错误状态 —— 只是"这类扩展没有远程版本可比"
                info.State = UpdateState.Unknown;
                info.Message = "本地目录";
            }

            return info;
        }

        /// <summary>读 config.json 里的禁用名单（读不到返回空列表）。</summary>
        public static List<string> ReadDisabledNames(string configPath)
        {
            var names = new List<string>();
            try
            {
                if (!File.Exists(configPath)) return names;
                if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root) return names;
                if (root[DisabledKey] is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        // ⚠ 用 TryGetValue 而不是 GetValue：名单里混进非字符串时
                        //   不会把整个读取打断（手工编辑过的 config.json 很常见）
                        if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                            names.Add(s);
                    }
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// 按关键字过滤插件列表。**纯函数**，不改动传进来的列表。
        ///
        /// <para>为什么强调"不改动"：界面上「列表显示过滤后的子集」与
        /// 「一键更新读全集」这两件事必须同时成立，否则用户搜两个字再点
        /// 「更新全部」，就只更新了看得见的那几个。所以过滤只产出新列表，
        /// 全集始终留在调用方手里。</para>
        ///
        /// <para>匹配范围：名称 / 来源 / 状态 / 分支；大小写不敏感。</para>
        /// </summary>
        public static List<ExtensionInfo> Filter(List<ExtensionInfo> all, string keyword)
        {
            var result = new List<ExtensionInfo>();
            if (all == null) return result;

            if (string.IsNullOrWhiteSpace(keyword))
            {
                result.AddRange(all);
                return result;
            }

            string k = keyword.Trim();
            foreach (var e in all)
            {
                if (Hit(e.Name, k) || Hit(e.SourceText, k) || Hit(e.Message, k) || Hit(e.Branch, k))
                    result.Add(e);
            }
            return result;

            static bool Hit(string s, string k) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ==================================================================
        //  检测更新
        // ==================================================================

        /// <summary>
        /// 逐个查询远程最新提交（每个扩展一次 <c>ls-remote</c>，需要网络）。
        ///
        /// <para>用 <c>ls-remote</c> 而不是 <c>fetch</c>：它<b>只读、不改本地仓库</b> ——
        /// 检测更新不该顺手动用户的本地状态。</para>
        /// </summary>
        public static async Task<List<ExtensionInfo>> CheckAsync(
            List<ExtensionInfo> list, Action<string>? onProgress = null)
        {
            var result = new List<ExtensionInfo>(list.Count);

            foreach (var item in list)
            {
                if (!item.IsGit)
                {
                    result.Add(item);       // 非 git 扩展没有"远程最新"可比
                    continue;
                }

                onProgress?.Invoke($"正在检测：{item.Name} ...");

                string refSpec = string.IsNullOrEmpty(item.Branch) || item.Branch == "HEAD"
                    ? "HEAD"
                    : $"refs/heads/{item.Branch}";

                var (code, o, _) = await Task.Run(
                    () => GitRunner.Run($"ls-remote origin {refSpec}", item.Path, 60000));

                if (code != 0)
                {
                    item.State = UpdateState.Error;
                    item.Message = "无法连接远程仓库";
                    result.Add(item);
                    continue;
                }

                string remoteSha = o.Trim().Split('\n').FirstOrDefault()
                                    ?.Split('\t').FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(remoteSha))
                {
                    item.State = UpdateState.Error;
                    item.Message = "远程无对应分支";
                    result.Add(item);
                    continue;
                }

                item.LatestVersion = GitRunner.ShortSha(remoteSha);

                if (string.Equals(item.CurrentVersion, item.LatestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    item.State = UpdateState.UpToDate;
                    item.Message = "已是最新";
                }
                else
                {
                    item.State = UpdateState.UpdateAvailable;
                    item.Message = "有新提交可更新";
                    item.LatestMessage = await TryGetRemoteCommitMessageAsync(item.RemoteUrl, item.Branch);
                }

                result.Add(item);
            }

            return result;
        }

        /// <summary>通过 GitHub API 取某分支最新提交的标题（失败返回空串，不抛异常）。</summary>
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
                using var doc = JsonDocument.Parse(await UpdateHttp.Client.GetStringAsync(api));
                var msg = doc.RootElement.GetProperty("commit").GetProperty("message").GetString() ?? "";
                return msg.Split('\n')[0];
            }
            catch { return ""; }
        }

        // ==================================================================
        //  更新
        // ==================================================================

        /// <summary>
        /// 把扩展强制对齐到远程最新。
        ///
        /// <para>⚠ 用的是 <c>reset --hard</c>：扩展的本地未提交改动会被覆盖。
        /// 这件事必须由界面在确认框里明说（调用方负责），因为它是<b>不可逆</b>的。
        /// 另外，本启动器的「扩展补丁」（<see cref="ExtensionPatches"/>）也会被这次
        /// <c>reset</c> 抹掉 —— 那正是补丁机制存在的理由，拉起 Forge 之前会重新贴上。</para>
        /// </summary>
        public static async Task<(bool ok, string msg)> UpdateAsync(ExtensionInfo item)
        {
            if (!item.IsGit)
                return (false, "这不是 git 管理的扩展，无法更新（可以先卸载，再用地址重新安装）");
            if (!GitRunner.Available)
                return (false, "未找到 git.exe");

            var (c1, _, e1) = await Task.Run(() => GitRunner.Run("fetch origin --prune", item.Path, 180000));
            if (c1 != 0) return (false, "拉取失败：" + GitRunner.CleanErr(e1));

            string target = string.IsNullOrEmpty(item.Branch) || item.Branch == "HEAD"
                ? "FETCH_HEAD"
                : $"origin/{item.Branch}";

            var (c2, _, e2) = await Task.Run(() => GitRunner.Run($"reset --hard {target}", item.Path, 30000));
            if (c2 != 0) return (false, "更新失败：" + GitRunner.CleanErr(e2));

            // ⚠ 别拿 `reset --hard` 的输出当提交号：它是
            //   `HEAD is now at abc1234 提交主题`，截前 8 位得到的是「HEAD is 」——
            //   于是日志会写「已更新到 HEAD is 」。看着像成功、内容是垃圾（不报错）。
            //   重新读一次 HEAD 才准（本地只读，一条命令）。
            var (sha, _) = GitRunner.HeadOf(item.Path);
            return (true, "已更新到 " + (string.IsNullOrEmpty(sha) ? target : GitRunner.ShortSha(sha)));
        }

        /// <summary>
        /// 刚更新完的那一项：<b>本地只读</b>地把它刷成"已是最新"。
        ///
        /// <para>为什么需要它：更新完界面总得刷新那一行。老写法是收尾再调一次
        /// 「检测插件更新」—— 那会对<b>全部</b>插件各发一次 <c>git ls-remote</c>，
        /// 几十个插件就是几十次网络往返，而用户只是想更新其中<b>一个</b>。
        /// 这里只读本仓库的 HEAD（<c>git log -1</c>，不联网）。</para>
        ///
        /// <para>⚠ <b>只在更新确实成功之后调</b>：它会把状态直接写成
        /// <see cref="UpdateState.UpToDate"/>（依据是"刚刚 fetch + reset 到远程那份"）。
        /// 更新失败时调它，界面就会撒谎说"已是最新"。</para>
        ///
        /// <para>同一个理由也适用于"一键更新"：每更新完一个就刷那一个，
        /// 全程不发网络请求。要重问远程，那是「检测插件更新」那个按钮的事。</para>
        /// </summary>
        public static bool MarkUpdated(ExtensionInfo item)
        {
            if (item == null || !item.IsGit || string.IsNullOrWhiteSpace(item.Path)) return false;

            var (sha, date) = GitRunner.HeadOf(item.Path);
            if (string.IsNullOrEmpty(sha))
            {
                // 更新命令报了成功、却读不到 HEAD —— 如实说"读不到、请自己核一下"，
                // 而不是留一个"已是最新"的假象（界面按它决定按钮能不能点）。
                item.State = UpdateState.Error;
                item.Message = "更新完成，但读不到本地提交号，请点「检测插件更新」核实";
                return false;
            }

            item.CurrentVersion = GitRunner.ShortSha(sha);
            item.CurrentDate = date;
            item.LatestVersion = item.CurrentVersion;   // 刚对齐过，两边就是同一个提交
            item.LatestMessage = "";
            item.State = UpdateState.UpToDate;
            item.Message = "已是最新";
            return true;
        }

        // ==================================================================
        //  安装
        // ==================================================================

        /// <summary>
        /// 校验一个安装地址。**纯函数**，不碰文件系统、不起进程 —— 便于单测。
        ///
        /// <para>⚠ 仓库名会被拼进路径，所以这里必须挡住目录穿越（<c>..</c>、分隔符、
        /// 非法字符）—— 否则一个精心构造的地址就能把 clone 目标指到 <c>extensions/</c>
        /// 外面去。</para>
        ///
        /// <para>⚠ <b>必须要求"主机之后至少两段"</b>。老写法是"取最后一个 <c>/</c> 之后那段"，
        /// 于是 <c>https://github.com/</c> 这种只有域名的地址会把 <c>github.com</c> 当成
        /// 仓库名放行，clone 到一个叫 <c>github.com</c> 的目录里并失败 —— <b>地址是错的，
        /// 校验却报"没问题"</b>。现在改成先剥掉主机、再看路径段数，域名段永远进不了
        /// "仓库名"这个位置。</para>
        /// </summary>
        public static (bool ok, string name, string reason) ValidateInstallUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (false, "", "请输入仓库地址");

            string t = url.Trim();

            bool httpish = t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            bool sshish = t.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
            if (!httpish && !sshish) return (false, "", "地址要以 https:// 或 git@ 开头");

            // ⚠ 引号与换行必须在**原串**上就挡掉，不能等到取出名字再查：
            //   名字只取最后一段，而 owner 段里的一个 `"` 就能把 clone 的参数引号提前闭合，
            //   后面的内容会被当成新参数（地址是用户从浏览器里复制来的，不值得信）。
            foreach (var bad in new[] { '"', '\r', '\n', '\0' })
                if (t.IndexOf(bad) >= 0)
                    return (false, "", "地址里有非法字符（引号或换行）");

            // 先去掉查询串 / 锚点 / 结尾斜杠，免得它们混进名字里
            string s = t;
            int cut = s.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);
            s = s.TrimEnd('/');

            // 取出"主机之后"的那一段 —— 这一段才是 owner/repo
            string pathPart;
            if (httpish)
            {
                // 交给 Uri 解析，主机名由它切出来（顺带把 `a/..` 这类点段规范化掉）
                if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                    return (false, "", "地址格式不正确");
                if (string.IsNullOrEmpty(uri.Host))
                    return (false, "", "地址里没有主机名");
                pathPart = uri.AbsolutePath.Trim('/');
            }
            else
            {
                // git@github.com:owner/repo.git —— 冒号后面才是路径
                int colon = s.IndexOf(':');
                if (colon < 0)
                    return (false, "", "git@ 形式的地址缺冒号，应写成 git@主机:owner/仓库.git");
                pathPart = s.Substring(colon + 1).Trim('/');
            }

            var segs = pathPart.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2)
                return (false, "", "地址里要含 owner/仓库名（例如 https://github.com/owner/repo.git）");

            string name = segs[segs.Length - 1];
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            if (name.Length == 0) return (false, "", "无法从地址里取出仓库名");
            if (name.Contains("..")) return (false, "", "仓库名里有非法字符（..）");
            if (name.Contains('/') || name.Contains('\\')) return (false, "", "仓库名里有非法字符（路径分隔符）");

            foreach (var ch in Path.GetInvalidFileNameChars())
                if (name.IndexOf(ch) >= 0) return (false, "", "仓库名里有非法字符");

            return (true, name, "");
        }

        /// <summary>从地址克隆一个新扩展到 <paramref name="extDir"/>。</summary>
        public static async Task<(bool ok, string msg)> InstallAsync(string url, string extDir)
        {
            var (vok, name, reason) = ValidateInstallUrl(url);
            if (!vok) return (false, reason);

            if (!GitRunner.Available) return (false, "未找到 git.exe（请先安装 Git for Windows）");

            try { Directory.CreateDirectory(extDir); } catch { }

            string dest = Path.Combine(extDir, name);
            if (Directory.Exists(dest))
                return (false, $"已经存在同名目录「{name}」—— 若要重装，请先卸载它");

            var (code, _, err) = await Task.Run(
                () => GitRunner.Run($"clone \"{url.Trim()}\" \"{dest}\"", extDir, CloneTimeoutMs));

            if (code != 0)
            {
                // clone 失败常常留下半个目录。这是我们**刚刚自己建的**、且注定不可用的
                // 残留（不是用户的扩展），所以在这里直接清掉 —— 与"卸载走回收站"不相关。
                try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
                return (false, "克隆失败：" + GitRunner.CleanErr(err));
            }

            return (true, $"已安装「{name}」");
        }

        // ==================================================================
        //  卸载
        // ==================================================================

        /// <summary>
        /// 判断某个扩展<b>能不能</b>卸载。**只读，不删任何东西。**
        ///
        /// <para>⚠ 这个判断必须<b>先于</b>删除执行 —— 这是 v0.26 的教训：
        /// 「重新部署」当年是先删 venv 再检查随包运行时在不在，于是报错时
        /// 4 GB 已经没了。<b>能提前问的（只读判断）要先问，不可逆的（删除）要后做。</b></para>
        /// </summary>
        public static (bool ok, string reason) CanUninstall(ExtensionInfo item, string extDir)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return (false, "条目没有路径");
            if (string.IsNullOrWhiteSpace(extDir) || !Directory.Exists(extDir))
                return (false, "扩展目录不存在");

            string full, baseFull;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
                baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extDir));
            }
            catch (Exception ex)
            {
                return (false, "路径无法解析：" + ex.Message);
            }

            // ⚠ 只允许删 extensions 的**直接子目录**。
            //   这一条同时挡住了 `extensions\..\venv`（父目录不是 extensions）、
            //   `extensions\a\b`（父目录是 extensions\a）这类越界写法。
            string parent = Path.GetDirectoryName(full) ?? "";
            if (!string.Equals(parent, baseFull, StringComparison.OrdinalIgnoreCase))
                return (false, "只允许卸载 extensions 下的直接子目录");

            if (!Directory.Exists(full))
                return (false, "目录已经不存在");

            // 链接（junction / symlink）会被删成"目标一起没"或"只少一个链接"，
            // 两种都不是用户想要的。拒绝并让他手工处理。
            try
            {
                var attrs = File.GetAttributes(full);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return (false, "该目录是一个链接（junction / symlink），为避免误删链接目标，请手工处理");
            }
            catch (Exception ex)
            {
                return (false, "读目录属性失败：" + ex.Message);
            }

            return (true, "");
        }

        /// <summary>
        /// 卸载扩展 —— <b>移入回收站</b>，不是硬删。
        ///
        /// <para>回收站不可用（网络盘 / 超长路径 / 组策略禁用）时返回失败，
        /// <b>不降级为永久删除</b>：把选择权还给用户，好过替他决定"那就算了直接删掉"。</para>
        /// </summary>
        public static (bool ok, string msg) Uninstall(ExtensionInfo item, string extDir)
        {
            var (can, reason) = CanUninstall(item, extDir);
            if (!can) return (false, "不能卸载：" + reason);

            if (!RecycleBin.TryDelete(item.Path, out string err))
                return (false, "删除失败（目录未改动）：" + err + "。可以到资源管理器里手动删除。");

            return (true, "已移入回收站");
        }

        // ==================================================================
        //  启用 / 禁用
        // ==================================================================

        /// <summary>
        /// 设置扩展的启用状态。**两处都写**，缺一处就会出现"界面显示已禁用，
        /// 重启后它又跑起来了"或反过来的怪象。
        ///
        /// <list type="bullet">
        ///   <item>① 扩展目录下的空文件 <c>disabled</c> —— Forge 启动时据此决定是否加载；</item>
        ///   <item>② WebUI <c>config.json</c> 的 <c>disabled_extensions</c> 数组 ——
        ///     设置页里看到的就是它。</item>
        /// </list>
        ///
        /// <para>⚠ <c>config.json</c> 不存在（WebUI 从没跑过）时<b>不静默跳过</b> ——
        /// 会在结果里说明"这次只写了 disabled 标记"，让用户知道为什么设置页里还看不到。</para>
        /// </summary>
        public static (bool ok, string msg) SetEnabled(ExtensionInfo item, bool enabled, string configPath)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return (false, "条目没有路径");

            var notes = new List<string>();
            bool ok = true;

            // ① disabled 标记文件
            string flag = Path.Combine(item.Path, DisabledFileName);
            try
            {
                if (enabled)
                {
                    if (File.Exists(flag)) File.Delete(flag);
                    notes.Add("已清除 disabled 标记");
                }
                else
                {
                    File.WriteAllText(flag, "");
                    notes.Add("已写入 disabled 标记");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                notes.Add("写 disabled 标记失败：" + ex.Message);
            }

            // ② config.json 的禁用名单
            var (cok, cmsg) = WriteDisabledInConfig(configPath, item.Name, !enabled);
            if (!cok) ok = false;
            notes.Add(cmsg);

            return (ok, string.Join("；", notes));
        }

        /// <summary>
        /// 写 config.json 用的序列化选项。
        ///
        /// <para>⚠ <b><c>TypeInfoResolver</c> 不能省</b>：<c>JsonNode.ToJsonString(options)</c>
        /// 会把传进来的 options <b>就地标记为只读</b>，而"没有解析器的 options"在这一步会直接抛
        /// <c>InvalidOperationException: ... must specify a TypeInfoResolver setting before being
        /// marked as read-only</c>。显式给一个反射解析器即可（这里只序列化 <c>JsonObject</c>，
        /// 走的是内置转换器，解析器本身并不会被真正用到）。</para>
        ///
        /// <para>代价说明：WebUI 自己写 config.json 是 4 空格缩进，这里是 2 空格 —— 整份文件会被
        /// 重排一次。内容一个字段不少（JsonNode 原样保留其它键），WebUI 下次保存又会按它自己的
        /// 风格写回去，所以只是"样子变了"，不影响功能。</para>
        /// </summary>
        private static readonly JsonSerializerOptions ConfigWriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        /// <summary>把某个扩展名加入 / 移出 config.json 的禁用名单（尽量保留文件里的其它字段）。</summary>
        private static (bool ok, string msg) WriteDisabledInConfig(string configPath, string name, bool disable)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return (false, "config.json 尚未生成（WebUI 还没跑过），本次只写了 disabled 标记");

            try
            {
                if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root)
                    return (false, "config.json 顶层不是对象，未改动禁用名单");

                var names = new List<string>();
                if (root[DisabledKey] is JsonArray arr)
                {
                    foreach (var n in arr)
                        if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                            names.Add(s);
                }

                names = names.Where(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (disable) names.Add(name);

                var fresh = new JsonArray();
                foreach (var s in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    fresh.Add(s);
                root[DisabledKey] = fresh;

                File.WriteAllText(
                    configPath,
                    root.ToJsonString(ConfigWriteOptions),
                    new UTF8Encoding(false));

                return (true, disable ? "已加入 config.json 的禁用名单" : "已移出 config.json 的禁用名单");
            }
            catch (Exception ex)
            {
                return (false, "写 config.json 失败：" + ex.Message);
            }
        }
    }
}
