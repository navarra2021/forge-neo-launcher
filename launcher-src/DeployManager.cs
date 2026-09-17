using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 首装逻辑 —— 把「一个解压出来的空壳」变成「能跑的 Forge Neo」。
    ///
    /// <para><b>薄包首装的边界在哪</b>：本类只做一件事 —— <b>建 venv</b>。
    /// 依赖（torch 等约 4~5 GB）不在这里装，而是交给 Forge 自己的
    /// <c>launch.py → prepare_environment()</c>。</para>
    ///
    /// <para>这不是偷懒，是刻意的：Forge 的 <c>prepare_environment()</c> 里写着
    /// 「torch 该配哪个 torchvision」「xformers / sageattention / flash-attn 该配哪个版本」
    /// 这一整套对应关系（见 <c>modules/launch_utils.py</c>），而且会<b>按版本变化</b>。
    /// 启动器另起一套安装逻辑，等于把这套对应关系抄第二遍，
    /// 上游一升级两边就对不上 —— 这类 bug 极难查。所以：<b>venv 我们建，依赖让它自己装。</b></para>
    ///
    /// <para>原先 <c>一键启动</c> 只会在 venv 不存在时打一行「找不到 Python」就退出，
    /// 因为那时还没有这个类 —— 首装是二期的事。现在补上了。</para>
    /// </summary>
    internal static class DeployManager
    {
        /// <summary>建 venv 的超时。实测 38 秒（含从镜像拉 pip），给足余量。</summary>
        private const int VenvTimeoutMs = 600_000;

        // ==================== 环境变量注入 ====================

        /// <summary>
        /// 把「下载源」翻译成子进程的环境变量。
        /// <b>只注入子进程</b>（<see cref="ProcessStartInfo.Environment"/>），
        /// 绝不碰系统环境 —— 这是整合包「不污染本机」承诺的一部分。
        /// </summary>
        public static void ApplySourceEnv(ProcessStartInfo psi, string? source)
        {
            string pypi = DownloadSource.PyPiIndex(source);

            // Forge 的 launch_utils.run_pip() 读这个，拼成 pip install ... --index-url <v>
            psi.Environment["INDEX_URL"] = pypi;
            // uv 自己的默认索引 —— uv venv --seed / uv pip install 都认它
            psi.Environment["UV_DEFAULT_INDEX"] = pypi;
            // 兜底：万一某条路径走的是真 pip（venv 里带 pip，没经过 uv_hook）
            psi.Environment["PIP_INDEX_URL"] = pypi;

            // Forge 的 prepare_environment() 读这个，拼成 torch 的 --extra-index-url
            // 默认装的是 cu130 三件套，故这里按 cu130 给；用户在 PyTorch 页选了别的支线时，
            // TorchManager 会显式传 --index-url，不依赖这一个变量。
            psi.Environment["TORCH_INDEX_URL"] = DownloadSource.TorchIndex(source, "cu130");
        }

        /// <summary>
        /// uv 的隔离三件套 + 缓存目录。
        /// 目标是「uv 只在包内活动」：不往 AppData 装 Python、不改 PATH、缓存也留在包里。
        /// </summary>
        public static void ApplyUvEnv(ProcessStartInfo psi)
        {
            psi.Environment["UV_PYTHON_INSTALL_DIR"] = AppPaths.BundlePyDir;
            psi.Environment["UV_PYTHON_PREFERENCE"] = "only-managed";
            psi.Environment["UV_CACHE_DIR"] = Path.Combine(AppPaths.RuntimeDir, "uvcache");
            psi.Environment["UV_NO_MODIFY_PATH"] = "1";
        }

        /// <summary>pip 日志超过这个大小就轮转（留一代，磁盘占用上限 ≈ 2×）</summary>
        private const long PipLogRotateBytes = 8L * 1024 * 1024;

        /// <summary>
        /// 让「装依赖」这件事<b>在日志里看得见</b>。
        ///
        /// <para><b>要解决的问题</b>：扩展自带的 <c>install.py</c> 里写死了 <c>pip install -q</c>
        /// （如 wd14-tagger 的 <c>install.py:11</c>）—— <c>-q</c> 是「完全静默」，
        /// 于是日志窗口<b>一个字都不动</b>，看上去像卡死，其实在龟速下载。
        /// 而下载发生在 webui 起来之前：端口不监听、进度条没有依据只能一直转圈，
        /// 使用者完全无从判断"它在干活还是在死等"。</para>
        ///
        /// <para><b>两个互补的手段</b>（2026-09-17 实测，见下）：</para>
        /// <list type="number">
        ///   <item><c>PIP_VERBOSE=1</c> —— <b>把 <c>-q</c> 抵消掉，实时输出进日志窗口</b>。
        ///         pip 的 <c>-q</c> 与 <c>-v</c> 共用<b>同一个计数器</b>，
        ///         命令行给了 <c>-q</c>（−1），环境变量再给 <c>+1</c> → 净 0 = 正常输出。
        ///         实测（同一份 <c>cowsay==6.1</c>，唯一变量是这个变量）：
        ///         不加时输出 <b>0 行</b>；加了之后下面 5 行全部出现 ——
        ///         <c>Looking in indexes</c> / <c>Collecting</c> /
        ///         <c>Downloading X.whl (25 kB)</c> / <c>Installing collected packages</c> /
        ///         <c>Successfully installed</c>。
        ///         <b>这是本项目第一条能击穿别人写死的 <c>-q</c> 的手段 —— 不需要改扩展的
        ///         任何文件</b>（改了下游一更新就没了）。</item>
        ///   <item><c>PIP_LOG=&lt;file&gt;</c> —— <b>命令行能被 <c>-q</c> 关掉，日志文件关不掉</b>。
        ///         实测：<c>-q</c> 下 stdout 仍是 0 行，但日志文件里拿到了完整记录
        ///         （5500 字节：连 <c>Starting new HTTPS connection (1): host:443</c>、
        ///         <c>"GET /simple/xxx/ HTTP/1.1" 200</c> 这类 HTTP 级细节都在）。
        ///         用途是<b>事后取证</b>：出问题时把这个文件发出来就能复盘。</item>
        /// </list>
        ///
        /// <para><b>⚠ 刻意不设 <c>PIP_PROGRESS_BAR</c></b>：它在管道（非 TTY）下<b>实测无效</b> ——
        /// 设 <c>on</c> / 设 <c>off</c> / 完全不设，三种情况输出<b>一字不差</b>。
        /// 而大文件下载时那行进度快照（<c>-------- 14.3/14.3 MB 65.9 MB/s 0:00:00</c>）
        /// 是 <c>PIP_VERBOSE=1</c> 自己带来的：只给 <c>PIP_PROGRESS_BAR=on</c>
        /// 而不给 <c>PIP_VERBOSE</c> 时，输出仍是 <b>0 行</b>。
        /// 设一个不起作用的变量只会让人误以为它在起作用，故不留。</para>
        ///
        /// <para><b>还实测了安全性</b>（怕 verbose 把日志刷爆 / 灌 ANSI 转义码）：
        /// 打开 <c>PIP_VERBOSE</c> 后一个包只多 5~6 字节的 <c>\r</c>，
        /// <b>ANSI 转义序列为 0</b> —— 非 TTY 下 pip 自己降级成纯文本，
        /// 进启动器的日志窗口是干净的。（对比：WPF 里显示 <c>rich</c> 的彩色进度条会变成一堆乱码。）</para>
        ///
        /// <para><b>顺带一个巧合</b>：pip 的 <c>Downloading X.whl (25 kB)</c> 与 uv 的
        /// <c>Downloading X (25.0KiB)</c> 形状一致，正好被启动器既有的
        /// <c>RxDownloading</c> 正则认出来 → 进度条能据此统计"已下载多少体积"。</para>
        ///
        /// <para>⚠ <b>uv 没有对应手段</b>：实测 <c>UV_VERBOSE=1</c> 打不穿 uv 的 <c>-q</c>。
        /// 但影响不大 —— Forge 自己调 uv 的那条路（<c>run_pip</c>）本来就不加 <c>-q</c>，
        /// 只有扩展自己写死的 pip 调用才静默，而它们走的是真 pip（<c>sys.executable -m pip</c>），
        /// 正好被上面两条覆盖。</para>
        /// </summary>
        public static void ApplyDependencyVisibilityEnv(ProcessStartInfo psi)
        {
            // ① 实时：抵消扩展 install.py 里那个写死的 -q
            psi.Environment["PIP_VERBOSE"] = "1";

            // ② 取证：全量日志落文件。
            // ⚠ 目录必须先建好：pip 的 FileHandler 只建文件不建目录，
            //   路径不存在时它会报错 → **宁可不要这个变量，也不能让它把启动带崩**。
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);

                var cur = new FileInfo(AppPaths.PipLogFile);
                if (cur.Exists && cur.Length > PipLogRotateBytes)
                    File.Move(AppPaths.PipLogFile, AppPaths.PipLogPrevFile, overwrite: true);

                psi.Environment["PIP_LOG"] = AppPaths.PipLogFile;
            }
            catch
            {
                // 日志落盘失败（目录只读 / 磁盘满 / 杀软拦截）不影响启动：
                // 实时输出那条路已经够用，不能为了诊断能力把主流程搭进去。
            }
        }

        // ==================== 钉版约束（保护 Forge 的依赖不被扩展顶掉） ====================

        /// <summary>只认「包名==版本」这种钉死写法；裸包名不收（那正是我们要防的东西）</summary>
        private static readonly System.Text.RegularExpressions.Regex PinnedRequirementRe =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.\-]+==",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private const string ConstraintsHeader =
            "# 本文件由 Forge Neo 启动器自动生成，请勿手工编辑（每次启动会按 requirements.txt 重算）。\n" +
            "#\n" +
            "# 内容：Forge 的 requirements.txt 里所有「包名==版本」行。\n" +
            "# 用途：通过 PIP_CONSTRAINT / UV_CONSTRAINT 交给 pip 与 uv。\n" +
            "#       constraint 只限制版本、不会主动安装任何包 ——\n" +
            "#       所以它等于在说「这些包只能选这些版本」。\n" +
            "#\n" +
            "# 为什么需要：扩展自带的 install.py 通常是 `pip install -r requirements.txt`，\n" +
            "#   而多数扩展的 requirements 只写裸包名（wd14-tagger 的 14 行里 13 行是裸的），\n" +
            "#   pip 对裸包名一律「装最新」，于是把 Forge 钉死的版本顶掉。\n" +
            "#   ⚠ 被顶掉还不致命（Forge 启动时会把钉版修回来），\n" +
            "#     致命的是被装进来的那个包本身：tensorflow 需要 protobuf>=6.31.1，\n" +
            "#     与本文件钉的 protobuf==4.25.9 不可兼得 ——\n" +
            "#     而 tensorflow 只要躺在 site-packages 里，Forge 导入 transformers 时就会崩。\n" +
            "#   所以本文件会让这类扩展判定无解并退出（ERROR: ResolutionImpossible）。\n" +
            "#   这是**刻意的**：宁可那个扩展的依赖装不上，也不能让 Forge 起不来。\n" +
            "#\n";

        /// <summary>
        /// 生成/刷新 <see cref="AppPaths.PipConstraintsFile"/>：把 Forge 在
        /// <c>requirements.txt</c> 里<b>钉死的版本</b>抄成 pip 的 constraint 文件。
        /// 返回写进去的条目数（0 表示没生成）。
        ///
        /// <para>⚠ <b>只从 requirements.txt 自动提取</b>，不手工追加猜测的包名 ——
        /// 「该保护什么」的唯一权威来源就是 Forge 自己钉的那份清单。</para>
        ///
        /// <para>⚠ <b>已知代价</b>：pip 判定"无解"时会逐一回溯候选版本，可能很慢
        /// （实测一次完整的 <c>--ignore-installed</c> dry-run 约 7 分钟）。
        /// 所以它只是<b>兜底</b>，主防线是 <c>AdvancedOptions.InstallExtDeps</c> 默认关。</para>
        /// </summary>
        public static int RefreshPipConstraints()
        {
            try
            {
                var req = Path.Combine(AppPaths.Root, "requirements.txt");
                if (!File.Exists(req)) return 0;

                var lines = new List<string>();
                foreach (var raw in File.ReadAllLines(req))
                {
                    var ln = raw.Trim();
                    if (ln.Length == 0 || ln[0] == '#') continue;
                    if (!PinnedRequirementRe.IsMatch(ln)) continue;
                    lines.Add(ln);
                }
                if (lines.Count == 0) return 0;

                var text = ConstraintsHeader + string.Join("\n", lines) + "\n";

                // 内容没变就不重写 —— 保留 mtime 的诊断价值（"这文件什么时候变的"）
                var same = File.Exists(AppPaths.PipConstraintsFile) &&
                           File.ReadAllText(AppPaths.PipConstraintsFile) == text;
                if (!same)
                {
                    Directory.CreateDirectory(AppPaths.RuntimeDir);
                    File.WriteAllText(AppPaths.PipConstraintsFile, text, new UTF8Encoding(false));
                }

                return lines.Count;
            }
            catch
            {
                // 约束是**兜底**，不是主流程：生成失败也不该影响启动
                return 0;
            }
        }

        /// <summary>
        /// 把钉版约束挂到子进程上。
        ///
        /// <para>两个变量名都已实测确认：pip 认 <c>PIP_CONSTRAINT</c>，
        /// uv 认 <c>UV_CONSTRAINT</c>（见 <c>uv pip install --help</c> 里的
        /// <c>[env: UV_CONSTRAINT=]</c>）。</para>
        ///
        /// <para>约束文件生成失败（或 requirements.txt 读不到）时<b>什么都不设</b> ——
        /// 一个指向不存在文件的 constraint 会让 pip 直接报错退出，
        /// 那等于把"兜底"变成"主流程故障"。</para>
        /// </summary>
        public static void ApplyConstraintEnv(ProcessStartInfo psi)
        {
            try
            {
                if (RefreshPipConstraints() == 0) return;
                if (!File.Exists(AppPaths.PipConstraintsFile)) return;

                psi.Environment["PIP_CONSTRAINT"] = AppPaths.PipConstraintsFile;
                psi.Environment["UV_CONSTRAINT"] = AppPaths.PipConstraintsFile;
            }
            catch
            {
                // 同上：兜底失败就退化成"没有兜底"，不能被它带崩
            }
        }

        // ==================== 建 venv ====================

        /// <summary>
        /// 用包内 uv + 包内 Python 建出 <c>venv\</c>，成功后写 <c>deploy.json</c>。
        ///
        /// <para>为什么带 <c>--seed</c>：uv 默认建的 venv 里<b>没有 pip</b>。
        /// Forge 启动时带 <c>--uv</c> 走 uv 装包没问题，但
        /// 「版本管理 → PyTorch 环境」页装 torch 用的是 <c>python -m pip</c>，
        /// 没有 pip 会直接失败。带上 --seed 一次装齐 pip/setuptools，两条路都通。</para>
        ///
        /// <para><b>两个回调分工不同</b>：<paramref name="log"/> 进日志区（可以长、可以带路径），
        /// <paramref name="stage"/> 只给部署页那一行「当前在干什么」用 ——
        /// 所以给的是短句 + uv 原样输出，不带我们自己的路径/参数铺陈。</para>
        /// </summary>
        public static async Task<(bool ok, string msg)> CreateVenvAsync(string? source, Action<string> log,
                                                                        Action<string>? stage = null)
        {
            void Stage(string s) => stage?.Invoke(s);

            log("检查随包运行时 ...");
            Stage("检查随包运行时");

            if (!File.Exists(AppPaths.UvExe))
                return (false, $"找不到随包 uv：{AppPaths.UvExe}（包不完整，请重新解压整合包）");

            var py = AppPaths.FindBundledPython();
            if (py == null)
                return (false, $"找不到随包 Python：{AppPaths.BundlePyDir} 下没有 python.exe（包不完整，请重新解压整合包）");

            if (File.Exists(AppPaths.PythonExe))
            {
                Stage("虚拟环境已存在");
                return (true, "虚拟环境已存在，无需重建");
            }

            log($"uv        : {AppPaths.UvExe}");
            log($"Python    : {py}");
            log($"目标 venv : {AppPaths.VenvDir}");
            log($"下载源    : {DownloadSource.Title(source)}（{DownloadSource.PyPiIndex(source)}）");

            var psi = new ProcessStartInfo(AppPaths.UvExe)
            {
                WorkingDirectory = AppPaths.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("venv");
            psi.ArgumentList.Add(AppPaths.VenvDir);
            psi.ArgumentList.Add("--python");
            psi.ArgumentList.Add(py);
            psi.ArgumentList.Add("--seed");

            ApplyUvEnv(psi);
            ApplySourceEnv(psi, source);

            log("执行：uv venv ... --seed（首次需要下载 pip，约几十秒）");
            Stage($"uv venv --seed（{DownloadSource.Title(source)}）");

            var sw = Stopwatch.StartNew();
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return (false, "无法启动 uv.exe");

                // uv 把进度写到 stderr，两边都收，按原样喂给日志区；
                // 同时把每一行也送去部署页，那里只显示最新一行 —— 静默期这就是"活着"的证据
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { log("  " + e.Data); Stage(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { log("  " + e.Data); Stage(e.Data); } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = await Task.Run(() => p.WaitForExit(VenvTimeoutMs));
                if (!exited)
                {
                    try { p.Kill(true); } catch { }
                    return (false, $"建 venv 超时（超过 {VenvTimeoutMs / 60000} 分钟），已中断");
                }

                // 等异步输出读完，否则最后几行（通常正是错误信息）会丢
                await Task.Delay(300);
                sw.Stop();

                if (p.ExitCode != 0)
                {
                    string hint = Directory.Exists(AppPaths.VenvDir)
                        ? $"　如果 venv 目录是上次失败留下的残骸，手动删掉 {AppPaths.VenvDir} 再试一次即可。"
                        : "";
                    return (false, $"uv venv 退出码 {p.ExitCode}{hint}");
                }
            }
            catch (Exception ex)
            {
                return (false, "建 venv 失败：" + ex.Message);
            }

            Stage("校验虚拟环境 ...");
            if (!File.Exists(AppPaths.PythonExe))
                return (false, $"uv 报告成功，但没找到 {AppPaths.PythonExe}（venv 结构异常）");

            // 落地部署记录 —— deploy.json 不进发布包（含绝对路径），只在本地有意义
            Stage("写入部署记录 ...");
            string venvPyVer = DetectPythonVersion(AppPaths.PythonExe);
            var st = DeployState.Capture("", "", DownloadSource.Normalize(source), venvPyVer);
            bool saved = st.Save(AppPaths.DeployStateFile);

            log($"venv 解释器 : Python {venvPyVer}");
            log(saved ? $"已写入部署记录：{AppPaths.DeployStateFile}"
                      : $"⚠ 部署记录写入失败（不影响使用）：{AppPaths.DeployStateFile}");

            return (true, $"虚拟环境创建完成（Python {venvPyVer}，耗时 {sw.Elapsed.TotalSeconds:F1} 秒）");
        }

        /// <summary>读 venv 里 python 的真实版本号；失败返回「未知」</summary>
        private static string DetectPythonVersion(string pythonExe)
        {
            try
            {
                var psi = new ProcessStartInfo(pythonExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import sys;print('%d.%d.%d'%sys.version_info[:3])");

                using var p = Process.Start(psi);
                if (p == null) return "未知";
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15_000);
                return string.IsNullOrWhiteSpace(outp) ? "未知" : outp;
            }
            catch { return "未知"; }
        }
    }
}
