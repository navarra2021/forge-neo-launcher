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
