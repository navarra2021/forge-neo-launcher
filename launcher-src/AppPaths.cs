using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 包内路径解析器 —— 整合包「换目录就能跑」的地基。
    ///
    /// 为什么需要它：原先把 <c>F:\sd-webui-forge-neo</c> 直接写死在代码里，
    /// 解压到 D 盘、换个用户名、或者放在含空格的路径下，立刻找不到 Python。
    /// 整合包要在别人的电脑上跑，这个前提必须先解决。
    ///
    /// <para><b>根目录解析按四级回落</b>（前者不成立就退到下一级）：</para>
    /// <list type="number">
    ///   <item>环境变量 <c>FORGE_NEO_ROOT</c> —— 脚本 / 快捷方式指定（多盘、网络盘场景）</item>
    ///   <item>exe 所在目录本身就是包根 —— 整合包的标准形态（exe 与 launch.py 同级）</item>
    ///   <item>从 exe 目录向上逐级查找含 launch.py 的祖先 —— exe 被塞进 bin\ 之类子目录时</item>
    ///   <item>都找不到 → <see cref="HasCore"/> = false，Root 退化为 ExeDir（仅供拼默认路径展示）</item>
    /// </list>
    ///
    /// <para>第 4 级不只是兜底 —— 它同时就是「一键部署」的入口条件：
    /// 判定为未部署时，上层才知道该拉起部署向导。</para>
    ///
    /// <para><b>⚠ 单文件发布的坑</b>：本项目是 <c>PublishSingleFile</c>，
    /// 这种形态下 <c>Assembly.Location</c> 返回空串，
    /// 而 <c>AppContext.BaseDirectory</c> 在 .NET 5+ 才改为指向 exe 真实位置。
    /// 这里优先用 .NET 6+ 的 <see cref="Environment.ProcessPath"/>（最直接可靠），
    /// 退回 <c>AppContext.BaseDirectory</c>，最后才用当前工作目录。</para>
    /// </summary>
    internal static class AppPaths
    {
        // ==================== 解析结果 ====================

        /// <summary>exe 所在目录。单文件发布下指向 exe 的真实位置，不是 %TEMP% 解压目录。</summary>
        public static string ExeDir { get; }

        /// <summary>Forge Neo 安装根目录。始终非空；未部署时退化为 <see cref="ExeDir"/>。</summary>
        public static string Root { get; }

        /// <summary>Root 的确定方式（显示在界面 / 写进日志，排查时能一眼看出认到哪去了）</summary>
        public static string RootSource { get; }

        /// <summary>Root 下存在 launch.py —— 视为源码已就位</summary>
        public static bool HasCore { get; }

        /// <summary>
        /// 内核关键文件中缺失的那些（空 = 齐备）。
        /// 比 <see cref="HasCore"/> 细 —— 后者只看 <c>launch.py</c>，
        /// 而解压中断常常是「入口在、modules 目录空了一半」这种半残形态。
        /// </summary>
        public static List<string> MissingCoreFiles { get; }

        /// <summary>内核关键文件是否齐备 —— 跑起来的前提条件</summary>
        public static bool CoreOk => MissingCoreFiles.Count == 0;

        static AppPaths()
        {
            ExeDir = ResolveExeDir();
            var (root, src, hasCore) = ResolveRoot(ExeDir);
            Root = root;
            RootSource = src;
            HasCore = hasCore;

            // 内核体检只做一次 —— Root 定了就不会再变
            var miss = new List<string>();
            foreach (var f in CoreFiles)
                if (!File.Exists(Path.Combine(root, f))) miss.Add(f);
            MissingCoreFiles = miss;
        }

        // ==================== 派生路径 ====================

        /// <summary>Forge Neo 启动入口</summary>
        public static string LaunchPy => Path.Combine(Root, "launch.py");

        /// <summary>虚拟环境目录</summary>
        public static string VenvDir => Path.Combine(Root, "venv");

        /// <summary>venv 里的解释器 —— 真正用来跑 launch.py 的那个</summary>
        public static string PythonExe => Path.Combine(VenvDir, "Scripts", "python.exe");

        /// <summary>随包携带的运行时目录（Python + uv）</summary>
        public static string RuntimeDir => Path.Combine(Root, "runtime");

        /// <summary>随包携带的 Python standalone 的安装根（其下还有一个按版本命名的子目录）</summary>
        public static string BundlePyDir => Path.Combine(RuntimeDir, "python");

        /// <summary>随包携带的 uv 所在目录</summary>
        public static string UvDir => Path.Combine(RuntimeDir, "uv");

        /// <summary>随包携带的 uv.exe</summary>
        public static string UvExe => Path.Combine(UvDir, "uv.exe");

        /// <summary>模型目录（用户自己往里放 ckpt）</summary>
        public static string ModelsDir => Path.Combine(Root, "models");

        // ---- 首页那几个「一键跳目录」的快捷入口 ----
        // 目录名的依据全部来自上游源码，不靠记忆：
        //   extensions        modules/paths_internal.py: extensions_dir
        //   output            modules/paths_internal.py: default_output_dir = data_path + "output"
        //   <五个输出子目录>   modules/shared_options.py: outdir_* 默认值（txt2img-images 等）
        //
        // ⚠ 输出根目录是 <b>output（单数）</b>，不是 A1111 的 outputs —— 写错只是"打开一个
        //   空目录"，不报错，所以特别容易一直错下去。上游换版本时要重新核这几个名字。

        /// <summary>扩展目录（第三方插件都装在这儿）</summary>
        public static string ExtensionsDir => Path.Combine(Root, "extensions");

        /// <summary>临时目录（缓存/临时文件，删了不影响环境）</summary>
        public static string TmpDir => Path.Combine(Root, "tmp");

        /// <summary>输出根目录 —— 注意是 output，单数</summary>
        public static string OutputDir => Path.Combine(Root, "output");

        /// <summary>超分（Extras）输出</summary>
        public static string ExtrasImagesDir => Path.Combine(OutputDir, "extras-images");

        /// <summary>文生图 · 网格图</summary>
        public static string Txt2ImgGridsDir => Path.Combine(OutputDir, "txt2img-grids");

        /// <summary>文生图 · 单图</summary>
        public static string Txt2ImgImagesDir => Path.Combine(OutputDir, "txt2img-images");

        /// <summary>图生图 · 网格图</summary>
        public static string Img2ImgGridsDir => Path.Combine(OutputDir, "img2img-grids");

        /// <summary>图生图 · 单图</summary>
        public static string Img2ImgImagesDir => Path.Combine(OutputDir, "img2img-images");

        /// <summary>部署状态文件</summary>
        public static string DeployStateFile => Path.Combine(Root, "deploy.json");

        /// <summary>
        /// 启动器配置文件。**固定在 exe 目录** —— 它比 Root 更早被需要
        /// （Root 的解析本身就可能要读它），放 Root 下会形成循环依赖。
        /// </summary>
        public static string ConfigFile => Path.Combine(ExeDir, "launcher.cfg");

        // ==================== 定位逻辑 ====================

        /// <summary>确定 exe 所在目录</summary>
        private static string ResolveExeDir()
        {
            // ① Environment.ProcessPath（.NET 6+）—— 单文件发布下也返回真实 exe 路径
            try
            {
                var proc = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(proc))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(proc));
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { /* 下列方式兜底 */ }

            // ② AppContext.BaseDirectory（.NET 5+ 单文件下同样指向 exe 目录）
            try
            {
                var b = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(b))
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            }
            catch { }

            // ③ 最后才用当前工作目录（理论上到不了这里）
            return Directory.GetCurrentDirectory();
        }

        /// <summary>四级回落确定包根目录</summary>
        private static (string root, string source, bool hasCore) ResolveRoot(string exeDir)
        {
            // ① 环境变量显式指定（必须真的含 launch.py 才采纳，避免误指空目录）
            var env = Environment.GetEnvironmentVariable("FORGE_NEO_ROOT");
            if (!string.IsNullOrWhiteSpace(env))
            {
                try
                {
                    var full = Path.GetFullPath(env.Trim().Trim('"'));
                    if (File.Exists(Path.Combine(full, "launch.py")))
                        return (full, "环境变量 FORGE_NEO_ROOT 指定", true);
                }
                catch { /* 非法路径就忽略，走下面的回落 */ }
            }

            // ② exe 目录本身就是包根（整合包标准形态）
            if (File.Exists(Path.Combine(exeDir, "launch.py")))
                return (exeDir, "exe 所在目录", true);

            // ③ 向上逐级找（最多 4 层）—— exe 被放在 bin\ 之类子目录时的形态
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 4 && dir.Parent != null; i++)
            {
                dir = dir.Parent;
                if (File.Exists(Path.Combine(dir.FullName, "launch.py")))
                    return (dir.FullName, $"exe 上级目录（上溯 {i + 1} 层）", true);
            }

            // ④ 未部署
            return (exeDir, "未找到 Forge Neo（缺 launch.py）", false);
        }

        // ==================== 就位检查 ====================

        /// <summary>
        /// 在随包 Python 目录下自动发现解释器。
        ///
        /// uv 落地 standalone 后会套一层按平台命名的子目录
        /// （形如 <c>cpython-3.13.12-windows-x86_64-none\python.exe</c>），
        /// 版本号会随上游变化，所以**不能写死子目录名**，必须搜出来。
        /// </summary>
        public static string? FindBundledPython()
        {
            try
            {
                if (!Directory.Exists(BundlePyDir)) return null;

                // 先看有没有直接放平的（手工裁剪过的包）
                var direct = Path.Combine(BundlePyDir, "python.exe");
                if (File.Exists(direct)) return direct;

                // 再搜一层子目录（uv 的标准落地形态）
                foreach (var d in Directory.GetDirectories(BundlePyDir))
                {
                    var exe = Path.Combine(d, "python.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
            return null;
        }

        /// <summary>venv 是否已建好（部署完成的标志之一）</summary>
        public static bool HasVenv => File.Exists(PythonExe);

        /// <summary>
        /// 现在<b>能不能</b>把 venv 建出来 —— 即随包 uv 与随包 Python 是否都在位。
        ///
        /// <para><b>为什么单独抽成一个只读判断</b>：它只有一类调用方，但那一类很要命 ——
        /// 【重新部署环境】是<b>先删后建</b>的。随包运行时不在时（开发目录就是这种形态：
        /// 有 venv、没 runtime；或用户照着 <see cref="Inspect"/> 的注释
        /// 把"功成身退"的 runtime 删了），旧代码会在删完之后才发现建不了 ——
        /// 用户手里只剩一个空目录和一句「包不完整，请重新解压整合包」，
        /// 而代价是几 GB 已装依赖现场蒸发。</para>
        ///
        /// <para>所以判据必须<b>先于删除</b>求值：能不能重建是只读的、可以提前问；
        /// 删除是不可逆的、问晚了就来不及。</para>
        ///
        /// <para>注意它与 <see cref="PrerequisitesOk"/> 的区别：那个回答「<i>现在能不能用</i>」
        /// （venv 在就算能用，runtime 缺了也无所谓），这个回答「<i>重建得起来吗</i>」。
        /// <b>两个问题，别拿一个的答案去答另一个</b> —— 这正是 v0.25 那个坑的形态。</para>
        /// </summary>
        public static bool CanCreateVenv => File.Exists(UvExe) && FindBundledPython() != null;

        /// <summary>
        /// venv 里装没装 torch。只看 site-packages 下的目录存在性 —— 轻量、只读、不发网络；
        /// 能否真正 import（ABI 是否匹配、CUDA 是否可用）交由 PyTorch 环境页的探测回答。
        /// </summary>
        public static bool HasTorch
        {
            get
            {
                try
                {
                    var sp = Path.Combine(VenvDir, "Lib", "site-packages");
                    return Directory.Exists(Path.Combine(sp, "torch"));
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 内核关键文件 —— 缺任何一个都说明包不完整（解压中断 / 被杀软吞了文件 / 手工裁剪过）。
        /// 只查「缺了就必然起不来」的那几个，不去逐个核对几百个源码文件：
        /// 目的是判断「有无」，不是校验完整性（那该由哈希清单做，成本高得多）。
        /// </summary>
        private static readonly string[] CoreFiles =
        {
            "launch.py",
            "webui.py",
            "requirements.txt",
            Path.Combine("modules", "launch_utils.py"),
            Path.Combine("modules", "cmd_args.py")
        };

        /// <summary>
        /// 体检清单 —— 供部署向导 / 状态区显示「还差什么」。
        /// 只做本地文件级检查（快、只读、不发网络），
        /// torch 能否真正 import 交由 v0.10 的 TorchManager 去探测。
        /// </summary>
        public static List<PathCheck> Inspect()
        {
            // ⚠ 随包运行时的「必要性」取决于 venv 建没建：
            //   runtime\python 与 runtime\uv 的唯一用途就是建 venv。
            //   venv 一旦建好，这两样东西就功成身退 —— 删掉也不影响运行。
            //   （开发目录就是这种形态：有 venv、没 runtime，不该被误判成「包坏了」）
            bool needRuntime = !HasVenv;
            var py = FindBundledPython();
            bool uvOk = File.Exists(UvExe);

            var list = new List<PathCheck>
            {
                new PathCheck("Forge Neo 内核", CoreOk,
                    CoreOk
                        ? $"关键文件齐备（{CoreFiles.Length} 项）"
                        : "缺少：" + string.Join("、", MissingCoreFiles), true),

                new PathCheck("Python 运行时", py != null,
                    py ?? (needRuntime
                        ? "未随包携带（runtime\\python 下找不到 python.exe）"
                        : "未随包携带；当前虚拟环境已建好，不影响使用"), needRuntime),

                new PathCheck("uv 包管理器", uvOk,
                    uvOk ? UvExe : (needRuntime
                        ? $"未随包携带 —— {UvExe}"
                        : "未随包携带；当前虚拟环境已建好，不影响使用"), needRuntime),

                new PathCheck("虚拟环境", HasVenv,
                    HasVenv ? PythonExe : "尚未创建（一键部署可以现场建好）", false)
            };

            if (File.Exists(DeployStateFile))
                list.Add(new PathCheck("部署状态文件", true, DeployStateFile, false));

            return list;
        }

        /// <summary>
        /// 当前状态下「必不可少」的东西是否齐备。
        ///
        /// <para><b>它的含义随 venv 状态而变</b>，这正是关键：</para>
        /// <list type="bullet">
        ///   <item>venv <b>已建好</b> —— 只核内核关键文件（运行时已功成身退）</item>
        ///   <item>venv <b>未建</b> —— 还要核随包 Python 与 uv（缺了就没法建 venv）</item>
        /// </list>
        ///
        /// <para>早先的版本无条件要求随包 Python 与 uv 齐备，结果把「有 venv、无 runtime」
        /// 的开发目录误判成「包不完整，请重新解压」—— 用户手上明明是一个能正常跑的环境。</para>
        /// </summary>
        public static bool PrerequisitesOk
        {
            get
            {
                foreach (var c in Inspect())
                    if (c.Required && !c.Ok) return false;
                return true;
            }
        }

        /// <summary>缺了哪些「当前必须有」的项（空列表 = 都齐备）。界面据此逐条列缺什么</summary>
        public static List<PathCheck> MissingRequired()
        {
            var list = new List<PathCheck>();
            foreach (var c in Inspect())
                if (c.Required && !c.Ok) list.Add(c);
            return list;
        }

        /// <summary>一句话概括当前状态，写日志用</summary>
        public static string Describe()
        {
            return $"包根目录 {Root}（{RootSource}）"
                 + (HasCore ? "" : " —— ⚠ 未找到 Forge Neo，可能需要先部署");
        }
    }

    /// <summary>单项就位检查的结果</summary>
    internal sealed class PathCheck
    {
        public string Name = "";
        public bool Ok;
        public string Detail = "";
        /// <summary>缺失时是否算「未部署」（false = 缺失也能跑，只是不完整）</summary>
        public bool Required = true;

        public PathCheck() { }

        public PathCheck(string name, bool ok, string detail, bool required)
        {
            Name = name;
            Ok = ok;
            Detail = detail;
            Required = required;
        }
    }
}
