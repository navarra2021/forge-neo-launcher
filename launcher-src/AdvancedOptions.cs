using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 高级选项的数据模型 + 命令行拼装引擎。
    ///
    /// 设计原则：**界面不直接拼命令行，命令行也不反向解析界面**。
    /// 两边都读写这一个对象，界面只是它的投影。
    /// 这样「预览的命令行」和「真正启动用的命令行」必然一致，
    /// 不会出现勾了没生效、或者预览与实际不符的坑。
    ///
    /// 所有参数名都核对过本机 Forge Neo（v0.x）的 argparse 定义：
    ///   - modules/cmd_args.py    （--no-hashing / --api / --listen / --gradio-auth / --ckpt-dirs ...）
    ///   - backend/args.py        （--cuda-malloc / --cuda-stream / --pin-shared-memory / --autotune ...）
    /// 没有把握的参数一律不做进界面——写错的 flag 会让启动直接崩在 argparse。
    /// </summary>
    internal sealed class AdvancedOptions
    {
        // ================= 性能 =================
        /// <summary>禁用 ckpt 的 sha256 哈希，加快模型加载</summary>
        public bool NoHashing;
        /// <summary>cudnn benchmark 自动调优（换分辨率/换模型后首次会变慢）</summary>
        public bool Autotune;
        /// <summary>锁页内存，改善内存与显存间的搬运效率</summary>
        public bool PinSharedMemory;
        /// <summary>可扩展显存段（实验性）</summary>
        public bool ExpandableSegments;

        // ================= 服务与网络 =================
        /// <summary>
        /// 服务就绪后由**启动器**自动打开界面（默认开）。
        ///
        /// <para><b>打开界面这件事必须只有一个执行者。</b>上游 WebUI 自己也会开：
        /// <c>modules/shared_options.py</c> 里 <c>auto_launch_browser</c> 的默认值是
        /// <c>"Local"</c>，只要没加 <c>--share</c>/<c>--listen</c> 就算 True，
        /// <c>webui.py</c> 便以 <c>inbrowser=True</c> 调 <c>demo.launch()</c>，
        /// gradio 内部 <c>webbrowser.open()</c> 一次 —— 启动器再开一次就是两个相同标签页。</para>
        ///
        /// <para>而且它是**时有时无**的：<c>launch_utils.prepare_environment()</c> 里
        /// <c>os.remove(tmp/restart)</c> 与 <c>setdefault("SD_WEBUI_RESTARTING","1")</c>
        /// 共用一个 try，<c>tmp\restart</c> 不存在时前者抛 OSError，后者就不执行了。</para>
        ///
        /// <para>所以启动器改成：**无条件**给子进程设 <c>SD_WEBUI_RESTARTING=1</c>
        /// 关掉 WebUI 那一份（见 <c>MainWindow.ApplyChildEnv</c>），再由本开关决定
        /// 启动器开不开。<b>不再传 <c>--autolaunch</c></b> —— 它被那个环境变量一票否决，
        /// 留着就是"UI 能勾、实际没用"的静默失效。</para>
        /// </summary>
        public bool AutoOpenBrowser = true;
        /// <summary>随 WebUI 一起开启 API</summary>
        public bool Api;
        /// <summary style="color:red">⚠ 对局域网开放</summary>
        public bool Listen;
        /// <summary>访问认证 username:password</summary>
        public string GradioAuth = "";

        /// <summary>
        /// 启动时检查并补装扩展依赖（默认<b>开</b>）。
        ///
        /// <para><b>它控制 <c>--skip-install</c> 发不发</b>，而不是普通的启动参数 ——
        /// 所以不进 <see cref="AppendTo"/>，由 <c>MainWindow.BuildFinalArgs()</c> 统一裁决。
        /// 键名仍落在 <c>Adv.</c> 前缀下，便于和其余高级选项一起读写。</para>
        ///
        /// <para><b>为什么需要这个开关</b>：<c>--skip-install</c> 在 Forge 里是<b>一票制</b>，
        /// 它同时关掉三件事：</para>
        /// <list type="number">
        ///   <item><c>launch_utils.run_pip()</c> 的第一行 <c>if args.skip_install: return</c>
        ///         —— 所有在线安装（onnxruntime 等）</item>
        ///   <item><c>prepare_environment()</c> 里的 <c>install_requirements()</c></item>
        ///   <item><c>run_extensions_installers()</c> —— 逐个执行 <c>extensions/*/install.py</c></item>
        /// </list>
        ///
        /// <para><b>踩过的坑</b>：原先的判据是 <c>if (HasTorch) 加 --skip-install</c>。
        /// 看着合理（"依赖装好了就不用再装"），实则<b>只对第 1、2 件成立</b> ——
        /// 第 3 件（扩展依赖）与 torch 在不在毫无关系。
        /// 于是只要 torch 装好，<b>以后再装任何扩展，它的 <c>install.py</c> 永远跑不到</b>，
        /// 表现为「扩展报 <c>ModuleNotFoundError: 某个第三方包</c>」——
        /// 症状离原因极远，排查起来非常绕（wd14-tagger 缺 <c>jsonschema</c> 就是这么来的）。
        /// </para>
        ///
        /// <para>⚠ <b>判据不能兼职</b>：<c>HasTorch</c> 回答的是"torch 在不在"，
        /// 它回答不了"扩展依赖要不要补"。这两件事必须分开问。</para>
        ///
        /// <para><b>代价</b>：开着时每次启动会跑一遍各扩展的 <c>install.py</c>。
        /// 但 <c>requirements_met()</c> / <c>is_installed()</c> 的守卫会让已装好的依赖
        /// 直接跳过，实测只多几秒。关掉可换回最快启动。</para>
        /// </summary>
        public bool InstallExtDeps = true;

        /// <summary>
        /// 依赖下载源档位：<c>cn</c>（国内加速，默认）/ <c>official</c>（官方源）。
        ///
        /// <para>名字不叫 DownloadSource 是为了避开与 <see cref="ForgeNeoLauncher.DownloadSource"/>
        /// 这个类名同名带来的解析歧义。落盘键为 <c>Adv.MirrorSource</c>，
        /// 与 <c>deploy.json</c> 里的 <c>mirrorProfile</c> 是同一个概念。</para>
        ///
        /// <para>⚠ 它<b>不是启动参数</b>，不会出现在 <see cref="AppendTo"/> 里 ——
        /// 它影响的是子进程的环境变量（首装、装 PyTorch 时的下载地址），
        /// 由 <see cref="DeployManager.ApplySourceEnv"/> 统一翻译。</para>
        /// </summary>
        public string MirrorSource = ForgeNeoLauncher.DownloadSource.Cn;

        // ================= 模型目录 =================
        /// <summary>
        /// 复用已有 A1111 / 秋叶包安装的模型目录（--forge-ref-a1111-home）。
        ///
        /// <para><b>⚠ 默认关闭，这是刻意设计的。</b>
        /// 整合包发到别人电脑上时，对方可能根本没有 A1111；
        /// 指向一个不存在的目录会让 Forge 启动时直接报错。
        /// 只有<b>自己这台机器确实有</b>时才打开，并填写真实路径。</para>
        /// </summary>
        public bool UseA1111Home;
        /// <summary>A1111 / 秋叶包安装根目录（其下应有 models 子目录）</summary>
        public string A1111Home = "";

        /// <summary>额外的模型目录（--ckpt-dirs，可多条）</summary>
        public List<string> CkptDirs = new List<string>();
        /// <summary>额外的 LoRA 目录（--lora-dirs，可多条）</summary>
        public List<string> LoraDirs = new List<string>();
        /// <summary>额外的 VAE 目录（--vae-dirs，可多条）</summary>
        public List<string> VaeDirs = new List<string>();

        /// <summary>把选项追加到启动参数里（不修改传入数组）</summary>
        public void AppendTo(List<string> args)
        {
            // ---- 性能 ----
            if (NoHashing) args.Add("--no-hashing");
            if (Autotune) args.Add("--autotune");
            if (PinSharedMemory) args.Add("--pin-shared-memory");
            if (ExpandableSegments) args.Add("--expandable-segments");

            // ---- 服务与网络 ----
            // 刻意不发 --autolaunch：子进程带着 SD_WEBUI_RESTARTING=1，它一定是空转，
            // 传了只会让人以为"这个开关在管事"。开界面由启动器自己负责（见 AutoOpenBrowser）。
            if (Api) args.Add("--api");
            // --listen 本身就会把 gradio_server_name 变成 0.0.0.0
            // （见 modules/initialize_util.py），无需再补 --server-name
            if (Listen) args.Add("--listen");
            if (Listen && !string.IsNullOrWhiteSpace(GradioAuth))
            {
                args.Add("--gradio-auth");
                args.Add(GetAuthValue());
            }

            // ---- 模型目录 ----
            // 复用已有 A1111 安装的模型（仅本机有 A1111 时才该开）
            if (UseA1111Home && !string.IsNullOrWhiteSpace(A1111Home))
            {
                args.Add("--forge-ref-a1111-home");
                args.Add(A1111Home.Trim());
            }
            foreach (var d in CkptDirs) { args.Add("--ckpt-dirs"); args.Add(d); }
            foreach (var d in LoraDirs) { args.Add("--lora-dirs"); args.Add(d); }
            foreach (var d in VaeDirs) { args.Add("--vae-dirs"); args.Add(d); }
        }

        /// <summary>认证串：允许多组，用逗号分隔；单组时规范化为 u:p</summary>
        public string GetAuthValue()
        {
            var groups = (GradioAuth ?? "").Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (groups.Count == 0) return "";
            return string.Join(",", groups.Select(NormalizeOneAuth));
        }

        /// <summary>u:p 原样返回；只写了 u 则补成 u:1234（否则 WebUI 会拒绝启动）</summary>
        private static string NormalizeOneAuth(string s)
        {
            return s.Contains(':') ? s : s + ":1234";
        }

        // ================= 校验 =================

        /// <summary>
        /// 返回阻止启动的「致命问题」列表。空列表 = 可以启动。
        /// 致命 = 会让 WebUI 直接崩掉或产生安全风险，必须拦。
        /// </summary>
        public List<string> ValidateFatal()
        {
            var list = new List<string>();

            // --listen 暴露到局域网却没设密码：任何同网设备都能操作你的 WebUI
            if (Listen && string.IsNullOrWhiteSpace(GradioAuth))
                list.Add("已勾选【对局域网开放】但未填写访问认证，服务将无密码暴露给同网络的所有设备。请填写 用户名:密码，或取消勾选。");

            // 认证串里出现中文/空格基本是手滑，argparse 会吃进去但浏览器端会认证失败
            foreach (var g in (GradioAuth ?? "").Split(','))
            {
                var t = g.Trim();
                if (t.Length == 0) continue;
                if (t.Any(c => c > 127))
                {
                    list.Add($"访问认证「{t}」包含非 ASCII 字符，请改用英文、数字与符号。");
                    break;
                }
            }

            // 复用 A1111：开了就必须给有效路径，否则 Forge 启动时才报错，排查起来很绕
            if (UseA1111Home)
            {
                var home = (A1111Home ?? "").Trim();
                if (home.Length == 0)
                    list.Add("已勾选【复用已有 A1111 模型目录】但未填写路径，请填写具体目录或取消勾选。");
                else if (!Directory.Exists(home))
                    list.Add($"A1111 目录不存在：{home}");
            }

            // 模型目录必须存在，否则 Forge 启动时才报错，排查起来很绕
            foreach (var p in CkptDirs.Concat(LoraDirs).Concat(VaeDirs))
            {
                if (!Directory.Exists(p))
                    list.Add($"模型目录不存在：{p}");
            }

            return list.Distinct().ToList();
        }

        /// <summary>非致命的提醒（不影响启动，但值得让用户知道）</summary>
        public List<string> ValidateWarnings()
        {
            var list = new List<string>();

            if (Autotune)
                list.Add("已开启 cudnn 自动调优：更换模型或分辨率后的第一次生成会明显变慢，属正常现象。");

            if (ExpandableSegments)
                list.Add("可扩展显存段为实验性选项，偶发不稳定，如遇生成异常可先关掉它。");

            if (UseA1111Home && !string.IsNullOrWhiteSpace(A1111Home) && Directory.Exists(A1111Home.Trim()))
            {
                var modelsSub = Path.Combine(A1111Home.Trim(), "models");
                if (!Directory.Exists(modelsSub))
                    list.Add("所填 A1111 目录下没有 models 子目录，Forge 可能加载不到模型——请确认路径指向的是 A1111 安装根目录。");
                else
                    list.Add("已开启 A1111 模型复用：这是本机专属设置，把包拷到没有 A1111 的电脑上会启动失败，届时清空此项即可。");
            }

            if (!AutoOpenBrowser)
                list.Add("已关闭「就绪后自动打开界面」：服务起来后不会再弹浏览器，需要时点主界面的【打开界面】。");

            if (!InstallExtDeps)
                list.Add("已关闭「启动时补装扩展依赖」：新装扩展自身的依赖不会被自动安装，"
                       + "该扩展可能因缺少第三方包而加载失败。装完新扩展后建议临时打开一次。");

            return list;
        }

        // ================= 持久化 =================

        public void Save()
        {
            LauncherConfig.SetBool("Adv.NoHashing", NoHashing);
            LauncherConfig.SetBool("Adv.Autotune", Autotune);
            LauncherConfig.SetBool("Adv.PinSharedMemory", PinSharedMemory);
            LauncherConfig.SetBool("Adv.ExpandableSegments", ExpandableSegments);

            // ⚠ 换了键名（旧 Adv.AutoLaunch 不再读）：旧键在很多人的 cfg 里是 "0"，
            //   沿用会让他们升级后"突然不再自动开界面"；新键缺省即「开」，行为保持不变。
            LauncherConfig.SetBool("Adv.AutoOpenBrowser", AutoOpenBrowser);
            LauncherConfig.SetBool("Adv.Api", Api);
            LauncherConfig.SetBool("Adv.Listen", Listen);
            LauncherConfig.Set("Adv.GradioAuth", GradioAuth ?? "");
            // ⚠ 默认值必须与 Load() 的兜底一致（都是 true）：老配置文件里没这一项时，
            //   读到的是"开" —— 这样升级上来的用户立刻获得"扩展依赖会被自动补装"，
            //   而他们之前恰恰卡在这件事上。
            LauncherConfig.SetBool("Adv.InstallExtDeps", InstallExtDeps);
            // ⚠ 下载源必须落盘：不存的话重启就退回默认的「国内加速」，
            //   用户明明切到了官方源、下次打开又变回去 —— 属于静默篡改用户选择
            LauncherConfig.Set("Adv.MirrorSource", ForgeNeoLauncher.DownloadSource.Normalize(MirrorSource));

            LauncherConfig.SetBool("Adv.UseA1111Home", UseA1111Home);
            LauncherConfig.Set("Adv.A1111Home", A1111Home ?? "");
            LauncherConfig.SetList("Adv.CkptDirs", CkptDirs);
            LauncherConfig.SetList("Adv.LoraDirs", LoraDirs);
            LauncherConfig.SetList("Adv.VaeDirs", VaeDirs);
        }

        public static AdvancedOptions Load()
        {
            return new AdvancedOptions
            {
                NoHashing = LauncherConfig.GetBool("Adv.NoHashing"),
                Autotune = LauncherConfig.GetBool("Adv.Autotune"),
                PinSharedMemory = LauncherConfig.GetBool("Adv.PinSharedMemory"),
                ExpandableSegments = LauncherConfig.GetBool("Adv.ExpandableSegments"),

                AutoOpenBrowser = LauncherConfig.GetBool("Adv.AutoOpenBrowser", true),
                Api = LauncherConfig.GetBool("Adv.Api"),
                Listen = LauncherConfig.GetBool("Adv.Listen"),
                GradioAuth = LauncherConfig.Get("Adv.GradioAuth"),
                // 默认 true：老配置文件缺这一项时升级即生效（与 Save() 的默认值一致）
                InstallExtDeps = LauncherConfig.GetBool("Adv.InstallExtDeps", true),
                // Normalize 兜底：老配置文件里没这一项时读到空串，会退化成默认值
                MirrorSource = ForgeNeoLauncher.DownloadSource.Normalize(LauncherConfig.Get("Adv.MirrorSource")),

                UseA1111Home = LauncherConfig.GetBool("Adv.UseA1111Home"),
                A1111Home = LauncherConfig.Get("Adv.A1111Home"),
                CkptDirs = LauncherConfig.GetList("Adv.CkptDirs"),
                LoraDirs = LauncherConfig.GetList("Adv.LoraDirs"),
                VaeDirs = LauncherConfig.GetList("Adv.VaeDirs")
            };
        }

        public void Reset()
        {
            NoHashing = false; Autotune = false; PinSharedMemory = false; ExpandableSegments = false;
            AutoOpenBrowser = true; Api = false; Listen = false; GradioAuth = "";
            InstallExtDeps = true;
            MirrorSource = ForgeNeoLauncher.DownloadSource.Cn;
            UseA1111Home = false; A1111Home = "";
            CkptDirs.Clear(); LoraDirs.Clear(); VaeDirs.Clear();
        }
    }

    /// <summary>
    /// 命令行拼装：含空格的参数必须加引号，否则会被拆成多个参数。
    /// （F:\Stable Diffusion 不加引号会被拆成 ...\Stable 和 Diffusion 两个参数）
    /// </summary>
    internal static class ArgLine
    {
        public static string Build(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (a.Contains(' ') || a.Contains('&'))
                    sb.Append('"').Append(a).Append('"');
                else
                    sb.Append(a);
                sb.Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
