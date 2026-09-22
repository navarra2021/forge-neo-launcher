using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ForgeNeoLauncher
{
    public partial class MainWindow : Window
    {
        // ---- 配置 ----
        // 路径一律由 AppPaths 解析，不再写死盘符 —— 整合包要能换目录运行
        private static string NeoToolDir => AppPaths.Root;
        private static string PythonPath => AppPaths.PythonExe;

        /// <summary>
        /// 本次运行<b>实际</b>使用的服务端口。
        ///
        /// <para>⚠ 它<b>不是常量</b>：目标端口被别的程序占用时会改用其他端口
        /// （见 <see cref="PortGuard"/>）。原先这里写死 7860，后果是"端口上那个 PID"
        /// 从头到尾都可能是<b>别人</b>的 —— 状态栏显示别人的 PID、【打开界面】打开别人的
        /// 页面、【终止进程】更是直接杀掉别人的程序。修法不是"把 7860 挪个位置"，
        /// 而是<b>先分清端口上坐的到底是谁</b>。</para>
        /// </summary>
        private int currentPort = PortGuard.DefaultPort;

        /// <summary>服务地址。跟着 <see cref="currentPort"/> 走，<b>绝不写死</b>。</summary>
        private string WebUiUrl => $"http://127.0.0.1:{currentPort}";

        // ---- 基础启动参数（固定不变的骨架）----
        // 可变部分来自「高级选项」页，见 AdvancedOptions.AppendTo
        //
        // 说明两处**刻意移除**的旧参数：
        //   --forge-ref-a1111-home F:\Stable Diffusion
        //       原先是硬编码，等于强制复用本机 A1111 的模型。整合包发到别人电脑上
        //       会因目录不存在而启动失败，现改为高级选项里的可选开关（默认关）。
        //   --skip-install
        //       并非删除，而是改为按部署状态动态添加 —— 见 BuildFinalArgs()。
        private static readonly string[] BaseLaunchArgs = new[]
        {
            "launch.py",
            "--skip-version-check",
            "--cuda-malloc",
            "--cuda-stream"
        };

        /// <summary>高级选项（性能 / 服务 / 模型目录），由配置载入、随界面实时更新</summary>
        private readonly AdvancedOptions advOpts = new AdvancedOptions();

        /// <summary>基础参数 + 高级选项，得到最终命令行参数表</summary>
        private List<string> BuildFinalArgs()
        {
            var list = new List<string>(BaseLaunchArgs);

            // --uv：让 Forge 用随包的 uv 装包（见 modules_forge/uv_hook.py）。
            // 那是个猴子补丁，会把 Forge 内部所有 pip 调用改写成 uv pip；
            // 它**只在 PATH 里找得到 uv 时才生效**，而 PATH 由 ApplyChildEnv 注入。
            // 好处：下载快、可并发，且首装时 venv 里即使没有 pip 也能装东西。
            // --uv 与 --uv-symlink / --uv-local-cache 是 argparse 里的互斥组，只能给一个。
            if (File.Exists(AppPaths.UvExe))
                list.Add("--uv");

            // --skip-install：**一票制**，一旦发出，三件事全关：
            //   ① launch_utils.run_pip() 第一行的 `if args.skip_install: return`（所有在线安装）
            //   ② install_requirements()（Forge 主依赖）
            //   ③ run_extensions_installers()（各扩展自己的 install.py）
            //
            // ⚠ 这里原先的判据是 `if (AppPaths.HasTorch)` —— **判据兼职，错在这**：
            //   它只对 ①② 成立（torch 在就不必重装主依赖），对 ③ 完全不成立。
            //   扩展依赖与 torch 毫无关系，于是结果是「torch 一装好，
            //   以后装任何扩展都不会再执行它的 install.py」——
            //   症状是扩展报某个第三方包 ModuleNotFoundError，离原因极远。
            //
            // 现在拆成两个独立的问题：
            //   ① torch 缺失（薄包首装）→ **必须**放开安装，否则缺 torch 直接崩
            //   ② advOpts.InstallExtDeps（默认开）→ 用户选择要不要顺带补扩展依赖
            // 任一为真就不发 --skip-install。
            //
            // 代价可控：Forge 侧 requirements_met() / is_installed() 会挡住已装好的依赖，
            // 只有各扩展的 install.py 会真跑一遍 pip（几秒）。
            if (AppPaths.HasTorch && !advOpts.InstallExtDeps)
                list.Add("--skip-install");

            advOpts.AppendTo(list);
            return list;
        }

        /// <summary>
        /// 给所有子进程注入环境变量。收在一处，避免「启动用一套、装 PyTorch 用另一套」。
        /// <b>只改子进程的环境块，不碰系统环境</b> —— 整合包的零污染承诺靠这条守住。
        /// </summary>
        private void ApplyChildEnv(ProcessStartInfo psi)
        {
            // python 输出是 UTF-8；Windows 默认按 ANSI(GBK) 解码会把 tqdm 方块字符弄成乱码
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            // 把包内 uv 塞进子进程 PATH 的最前面。
            // Forge 的 uv_hook 是个猴子补丁，会把内部的 pip 调用换成 uv pip（部署提速关键），
            // 但它**只在 PATH 里能找到 uv 时才生效**。只注入子进程，绝不改系统 PATH。
            if (File.Exists(AppPaths.UvExe))
                psi.Environment["PATH"] = AppPaths.UvDir + ";" +
                    (Environment.GetEnvironmentVariable("PATH") ?? "");

            // 缓存落进包内：一是便携（拷走包时缓存跟着走，换机不必重下），
            // 二是避免往用户 AppData 写东西。缓存可随时手动删除，不影响环境。
            psi.Environment["UV_CACHE_DIR"] = Path.Combine(AppPaths.RuntimeDir, "uvcache");

            // 告诉 uv「目标环境就是这个 venv」。
            // uv pip install 不带 --python 时靠 VIRTUAL_ENV / 当前目录下的 .venv 猜，
            // 不设的话它会报「找不到虚拟环境」。
            psi.Environment["VIRTUAL_ENV"] = AppPaths.VenvDir;

            // ⚠ 抑制 WebUI 自己打开浏览器 —— 不设这个，就会**偶尔同时弹出两个相同页面**。
            //
            // 上游（modules/shared_options.py）里 auto_launch_browser 的默认值是 "Local"，
            // 而 webui.py 里 `elif auto_launch_browser == "Local": auto_launch_browser =
            // not cmd_opts.webui_is_non_local` —— 只要没加 --share/--listen，它就算 True，
            // 于是 gradio 的 demo.launch(inbrowser=True) 自己 webbrowser.open() 一次；
            // 同时启动器在探测到端口 LISTENING 后也会开一次 → 两个标签页。
            //
            // 更麻烦的是它**时有时无**：launch_utils.prepare_environment() 里那段
            //     os.remove(tmp/restart)  ← 文件不存在会抛 OSError
            //     os.environ.setdefault("SD_WEBUI_RESTARTING", "1")
            // 被同一个 try 包着 —— tmp\restart 在不在，决定了 WebUI 那份到底开不开。
            // 所以现象是"偶尔"两个页面，看着毫无规律。
            //
            // 这里无条件写 "1"：webui.py 的判断在**外层**，一票否决（连 --autolaunch 一起），
            // 打开界面这件事就只剩启动器一个执行者，恒定一个页面。
            // 该变量的读取点全库仅 webui.py 一处，写点都是 setdefault（不会覆盖这里），
            // 所以额外影响为零 —— 只关掉"自动开浏览器"，不动别的启动流程。
            psi.Environment["SD_WEBUI_RESTARTING"] = "1";

            // 本机地址不走系统代理。
            // 装了 Clash / Steam++ 这类全局代理时，不设这个会让 WebUI 把 localhost 判成
            // 「不可达」并打出 When localhost is not accessible...（报错里一个"代理"字都没有），
            // 服务明明就在本机却起不来。合并语义与理由见 ProxyEnv.cs。
            ProxyEnv.Apply(psi);

            // uv 的其余隔离项 + 下载源
            DeployManager.ApplyUvEnv(psi);
            DeployManager.ApplySourceEnv(psi, advOpts.MirrorSource);

            // 让「装依赖在干什么」看得见（抵消扩展 install.py 里写死的 pip -q + 全量日志落文件）
            DeployManager.ApplyDependencyVisibilityEnv(psi);

            // 钉版约束（兜底）：把 Forge requirements.txt 里钉死的版本交给 pip / uv，
            // 使扩展的 install.py 顶不掉它们 —— 否则像 wd14-tagger 的裸 `tensorflow`
            // 会把 protobuf 顶到 7.x，Forge 就永久起不来了。
            // 详见 AdvancedOptions.InstallExtDeps 的「踩过的坑（二）」。
            DeployManager.ApplyConstraintEnv(psi);
        }

        // ---- 状态 ----
        private Process? proc;
        // 端口曾经 LISTENING 过 = 服务真的起来过。用于区分「进程退出」的两种含义：
        // 起过又退出 = 正常停止；从没起过 = 启动就崩了（要报错给用户看）。
        // ⚠ 不要拿 autoOpened 兼职这件事：用户关掉「就绪后自动打开界面」时它恒为 false，
        //   于是每次正常退出都会被判成"启动失败"。
        private bool portEverUp;

        // ---- 空闲时端口占用者的缓存 ----
        // 监控线程每秒跑一次，而 Describe() 要读进程路径（两次进程查询）。
        // 只在"端口换人了"时才重算，别每秒刷同一条状态。
        private int idlePid = -1;
        private string idleOwnerDesc = "";
        private bool idleOwnerIsOurs;
        private bool autoOpened;              // 就绪这一刻已处理过（含"用户主动关闭自动打开"的情况）
        private bool childExitNoted;          // 子进程退出只收尾一次（监控循环每秒都会看到"没在跑"）
        private CancellationTokenSource? monitorCts;

        // 日志颜色（恒定的内容色，不随主题切换——日志区固定黑色）
        private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        private static readonly Brush TimeBrush = new SolidColorBrush(Color.FromRgb(0x78, 0x9C, 0x9C));
        private static readonly Brush DefaultBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5));

        // ================= 主题 =================
        // 用于菜单高亮（选中项），蓝紫渐变，两套主题共用
        private static readonly Brush MenuActiveBg = new LinearGradientBrush(
            Color.FromRgb(0x5D, 0x9C, 0xF5), Color.FromRgb(0x7C, 0x66, 0xF0), 45.0);
        private static readonly Brush MenuIdleForeDefault = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x72));

        // 当前主题是否深色
        private bool isDark = false;
        // 当前高亮的菜单按钮（切主题时重挂高亮，不跳回第一个）
        private System.Windows.Controls.Button? activeMenuBtn;
        // 版本管理 / 插件管理：是否正在检测/更新（防重复点击）。
        // ⚠ **一个锁、两页共用** —— 内核与插件虽然分了两页，但更新动作都动同一份磁盘，
        //    两把锁就会出现"内核正在写文件、插件更新同时也开跑"。
        private bool versionBusy = false;
        // 版本管理：内核检测结果（内核与插件是**两个独立模型**，不再共用一个带 IsCore 的类）
        private CoreUpdateInfo? coreItem;
        // 插件管理：上一次扫描到的插件全集。搜索只改**显示**，不动这一份
        private List<ExtensionInfo> extItems = new List<ExtensionInfo>();
        // 插件管理：搜索关键字（空 = 全显示）
        private string extFilter = "";
        // 版本管理：内核是否已检测过至少一次（决定显示引导卡片还是内核卡片）
        // ⚠ 只管内核。v0.31 及以前它同时管着插件区的显隐，于是"进插件页看看有哪些插件"
        //   都要先点一次检测（要发网络请求）。拆页之后插件区是常显的，各自管各自的。
        private bool coreCheckedOnce = false;
        // 插件管理：是否已扫过一次本地目录（决定进页面时要不要自动扫一遍）
        private bool extScannedOnce = false;
        // 模型管理：上一次扫到的目录全集。搜索只改**显示**，不动这一份
        private List<ModelCatalog.ModelFolder> modelFolders = new List<ModelCatalog.ModelFolder>();
        // 模型管理：搜索关键字（空 = 全显示）
        private string modelFilter = "";
        // 模型管理：是否已扫过一次（决定进页面时要不要自动扫一遍）
        private bool modelScannedOnce = false;

        // PyTorch 环境：是否已探测过（本地命令，不发网络）
        private bool torchProbedOnce = false;
        // PyTorch 环境：当前 venv 的真实环境
        private TorchEnvInfo? torchEnv;
        // PyTorch 环境：是否正在安装
        private bool torchInstalling = false;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += OnStateChanged;
            // 入场动效的放行时机（首帧渲染完成）—— 见 ArmEntranceForStartup
            ContentRendered += OnContentRendered;
            // 窗口句柄一建好就向 DWM 申请圆角
            SourceInitialized += (_, _) => ApplyWindowCorner();
        }

        // ================= 自绘标题栏：窗口按钮 =================
        private void WinMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void WinMax_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void WinClose_Click(object sender, RoutedEventArgs e)
        {
            // 与系统关闭按钮行为一致：走 Closing 事件（会询问是否停止服务）
            Close();
        }

        // ---- 自绘标题栏：最大化边界补偿 ----
        // Windows 最大化时会把窗口外扩，把本该隐藏的调整边框推到屏幕外。
        // 自绘标题栏（WindowChrome）必须自己把这个溢出量补回来，否则内容四边会被裁掉。
        // ⚠️ 不用 SystemParameters.WindowResizeBorderThickness —— 它自 .NET 4.5 起
        //    少算了 SM_CXPADDEDBORDER（实测 100% 缩放下返回 4px，正确值是 8px），
        //    故直接读 GetSystemMetrics 自行换算。
        private const int SM_CXSIZEFRAME = 32;
        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static Thickness MaxCompensation(Visual visual)
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            double px = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            double py = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            return new Thickness(px / dpi.DpiScaleX, py / dpi.DpiScaleY,
                                 px / dpi.DpiScaleX, py / dpi.DpiScaleY);
        }

        private void ApplyMaxCompensation()
        {
            RootBorder.Padding = WindowState == WindowState.Maximized
                ? MaxCompensation(this)
                : new Thickness(0);
        }

        // ================= 窗口圆角 =================
        // 圆角分两层，缺一不可：
        //   ① **DWM**（这里）—— 真正裁掉窗口四角。不透明窗口里 WPF 画的圆角
        //      会被窗口自身的矩形背景盖住，所以四角必须由系统来裁。
        //   ② **RootBorder.CornerRadius**（XAML）—— 决定那圈描边走成圆角还是直角。
        // 半径取 8（WPF 逻辑单位）与 Win11 的系统圆角一致：200% 缩放下都是 16 物理px，
        // 两者重合才不会在角上露出缝隙。
        //
        // Win10 上没有这个 attribute，DwmSetWindowAttribute 会返回失败 —— 忽略即可，
        // 窗口保持方角，其余功能不受影响。
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>窗口圆角半径（逻辑单位）。与 DWM 的 8px 保持一致</summary>
        private const double WindowCornerRadius = 8;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyWindowCorner()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                // 最大化时用系统的"不圆角"（Windows 自己最大化后也是方角），
                // 还原时再要回圆角
                int pref = WindowState == WindowState.Maximized ? 1 : DWMWCP_ROUND;
                DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch
            {
                // 没有 dwmapi / 系统不认这个 attribute：保持方角，不影响使用
            }
        }

        /// <summary>
        /// 让 RootBorder 按圆角裁剪子元素。
        ///
        /// <para><b>为什么必须做</b>：WPF 的 <c>CornerRadius</c> 只作用于 Border 自己的
        /// 背景与描边，<b>不裁剪子元素</b>。子元素里最上面是自绘标题栏、首页是横幅图，
        /// 都是直角矩形 —— 不裁的话它们会把四角的圆角直接盖掉，看起来还是方角。</para>
        /// </summary>
        private void UpdateRootClip()
        {
            if (RootBorder == null) return;

            double w = RootBorder.ActualWidth, h = RootBorder.ActualHeight;
            double r = RootBorder.CornerRadius.TopLeft;
            RootBorder.Clip = (r > 0 && w > 0 && h > 0)
                ? new RectangleGeometry(new Rect(0, 0, w, h), r, r)
                : null;
        }

        private void RootBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateRootClip();
        }

        // 最大化 / 还原时切换按钮图标 + 补偿边界
        private void OnStateChanged(object? sender, EventArgs e)
        {
            bool max = WindowState == WindowState.Maximized;

            WinMaxBtn.ToolTip = max ? "还原" : "最大化";
            MaxIcon.Data = Geometry.Parse(max
                ? "M2,0 H10 V8 M0,2 H8 V10 H0 Z"   // 还原：双层框
                : "M0,0 H10 V10 H0 Z");            // 最大化：单层框

            ApplyMaxCompensation();

            // 最大化后是方角（与系统一致），还原时把圆角要回来
            RootBorder.CornerRadius = max ? new CornerRadius(0) : new CornerRadius(WindowCornerRadius);
            UpdateRootClip();
            ApplyWindowCorner();
        }

        // ================= 启动 =================
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 载入配置（主题 + 高级选项），随后的所有保存都写回同一个文件
            // 路径固定在 exe 目录 —— 它必须比包根目录更早确定，否则会形成循环依赖
            LauncherConfig.Load(AppPaths.ConfigFile);
            LoadAdvancedOptions();

            // 主题（默认浅色）
            isDark = LauncherConfig.GetBool("DarkTheme");
            ApplyTheme(isDark);

            // 窗口若以最大化状态启动，补一次边界补偿
            ApplyMaxCompensation();

            // 版本管理：指向 Forge Neo 安装目录
            // 内核与插件各自拿自己的根目录（拆成两个模块后，谁也不该去读别人的静态字段）
            CoreUpdater.Root = NeoToolDir;
            ExtensionManager.Root = NeoToolDir;

            // 显示启动器版本号
            LogoVersionText.Text = AppInfo.Short;
            VerLauncherVerText.Text = "启动器 " + AppInfo.Short;
            TitleVersionText.Text = AppInfo.Short;
            Title = $"Forge Neo 启动器 {AppInfo.Short}";

            // 版本管理页初始状态：显示「尚未检测」引导卡片，而不是空白列表
            // （检测更新改为用户手动触发，进页面不再自动跑网络检测）
            ShowUpdateResult();
            ApplyVerTabVisibility();

            AddLog($"= Forge Neo 启动器 {AppInfo.Full} 已就绪 ==", SuccessBrush);

            // 路径与部署状态 —— 换目录运行时，这里是唯一的线索
            AddLog(AppPaths.Describe(), InfoBrush);

            // 落地页 = 首页（banner + 目录快捷入口 + 一键启动）。
            // 必须排在体检之前 —— 体检若判定未部署，会把人切到【一键部署】页（那是刻意的）
            ShowView(ViewHome);
            if (MenuHome != null) SetMenuActive(MenuHome);

            // 启动即体检：缺必需文件就自动落到「一键部署」页
            AutoRouteByDeployStatus();

            AddLog("在【首页】点【一键启动】运行。关闭窗口时可选择是否停止后台服务。", InfoBrush);

            // 初始接管运行中的实例
            //
            // ⚠ 判据是「端口上坐的是不是**本包**的 Forge」，不是「端口上有没有人」。
            //   后者会把别人的 PID 当自己的接管过来 —— 界面显示成"运行中"、
            //   点【终止进程】还会把对方杀掉。分清归属的理由详见 PortGuard。
            if (advOpts.TryPortArg(out var initPort)) currentPort = initPort;
            var initProbe = PortGuard.Inspect(currentPort, new[] { ReadPid() }, NeoToolDir);
            if (initProbe.IsOurs)
            {
                AddLog($"检测到 Forge Neo 已在运行 (PID: {initProbe.Pid})，已自动进入监控模式。", InfoBrush);
                UpdateStatus("运行中", $"PID {initProbe.Pid} · {WebUiUrl}");
                OpenBtn.IsEnabled = true;
                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled = true;
                WritePid(initProbe.Pid);

                // 服务是**之前**就起来的，不是这次点出来的：别再替他弹一次浏览器。
                // 少了这两行，重开启动器（或它自己重启）就会平白多一个 127.0.0.1:7860 标签页。
                portEverUp = true;
                autoOpened = true;
            }
            else if (!initProbe.Free)
            {
                // 端口有人占着，但不是本包的 Forge。现在就把话说清楚，
                // 免得到时候用户看见"明明起了却打不开这个地址"一头雾水。
                AddLog($"提示：端口 {currentPort} 已被其他程序占用：{initProbe.OwnerDesc}", WarnBrush);
                AddLog("它**不是**本启动器启动的 Forge。点【一键启动】时会按【服务与网络】里的设置"
                     + "自动改用其他空闲端口，绝不会去结束那个进程。", WarnBrush);
            }

            StartMonitor();

            // 窗口外框：初始（非最大化）是圆角，裁剪范围得跟着尺寸算一次
            UpdateRootClip();

            // 入场动效放在最后：上面这些步骤会改落地页（可能切到【一键部署】），
            // 等界面状态定下来再动，免得动画演的是"还没决定的那个页面"。
            // 只在这一处播 —— 之后切页面不再重复动，那是切换、不是启动。
            ArmEntranceForStartup();
        }

        // ================= 启动入场动效：挂上 / 放行 =================
        private UiFx.EntranceHandle? entrance;
        private DispatcherTimer? entranceFallback;
        private bool entranceFired;

        /// <summary>
        /// 把入场动效**挂上但不放行**（见 <see cref="UiFx.ArmEntrance"/> 里为什么必须两段式）。
        ///
        /// <para>挂上之后内容就停在起点（偏下 34px 且透明）——窗口画出来的第一帧本身就是
        /// "还没浮上来"的样子，不会先闪一下成品再倒回去。放行交给
        /// <see cref="OnContentRendered"/>，那一刻窗口才真的把首帧画到屏幕上。</para>
        /// </summary>
        private void ArmEntranceForStartup()
        {
            entrance = UiFx.ArmEntrance(ContentRoot, ContentShift);

            // 兜底：万一 ContentRendered 不来（例如窗口以最小化状态启动），
            // 挂着的暂停时钟会把内容永久停在"偏下且透明"，也就是**界面整个空白**。
            // 那比没有动效严重得多，所以到点还没放行就摘掉，退化成"不动"。
            entranceFallback = new DispatcherTimer(
                TimeSpan.FromSeconds(1.5), DispatcherPriority.Background,
                (_, _) =>
                {
                    StopEntranceFallback();
                    if (entranceFired) return;
                    UiFx.CancelEntrance(entrance);
                    AddLog("入场动效未能在首帧放行，已跳过（不影响使用）。", InfoBrush);
                }, Dispatcher);
            entranceFallback.Start();
        }

        /// <summary>首帧渲染完成 —— 这才是"窗口真的出现在屏幕上"的时刻，此刻放行动效</summary>
        private void OnContentRendered(object? sender, EventArgs e)
        {
            if (entranceFired) return;
            entranceFired = true;
            StopEntranceFallback();
            UiFx.FireEntrance(entrance);
        }

        private void StopEntranceFallback()
        {
            entranceFallback?.Stop();
            entranceFallback = null;
        }

        /// <summary>
        /// 启动时按部署状态决定落到哪一页。
        ///
        /// <para>判据来自 <see cref="DeployState.Inspect"/>，三态各有去处：</para>
        /// <list type="bullet">
        ///   <item><b>就绪</b> —— 停在主视图（日志页），一键启动就能用</item>
        ///   <item><b>缺 venv</b> —— 切到一键部署页，侧栏高亮、角标点亮</item>
        ///   <item><b>包不完整</b> —— 也切过去，但那页会直说「部署也救不了，得重解压」</item>
        /// </list>
        ///
        /// <para>刻意不弹 MessageBox 拦截：让用户看到体检清单比弹个对话框信息量大，
        /// 而且他仍可以切去【高级选项】改下载源 —— 拦死会挡住正当操作。</para>
        /// </summary>
        private void AutoRouteByDeployStatus()
        {
            // 服务已在运行 = 环境本来就是好的。这时把人拽到部署页纯属添乱。
            // ⚠ 用 IsOurForgeRunning() 而不是"端口上有人"：别人占着 7860 时，
            //   后者会让本该显示"包不完整、要重解压"的严重问题被白白跳过。
            if (IsOurForgeRunning())
            {
                RefreshDeployDot();
                return;
            }

            var st = DeployState.Inspect();

            if (st == DeployStatus.Ready)
            {
                RefreshDeployDot();
                if (!AppPaths.HasTorch)
                    AddLog("⚠ 依赖尚未就绪，首次启动会自动安装（约 4~5 GB，耗时较长，请确保磁盘空间充足）。", WarnBrush);
                return;
            }

            if (MenuDeploy != null) SetMenuActive(MenuDeploy);
            ShowDeployView();

            if (st == DeployStatus.NeedsVenv)
            {
                AddLog("检测到尚未部署（缺虚拟环境）—— 已自动打开【一键部署】页。", WarnBrush);
                AddLog("点页面上的【开始部署】即可创建，通常几十秒。", InfoBrush);
            }
            else
            {
                AddLog("⚠ 检测到包内缺少必需文件 —— 已自动打开【一键部署】页，上面列了缺哪些。", ErrorBrush);
                AddLog("这类问题一键部署修不了（它依赖的随包文件本身就缺了），请重新解压整合包。", WarnBrush);
            }
        }

        // ================= 一键启动 =================
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            // ① 自己起的进程还活着 —— 已经在跑了
            if (proc != null && !proc.HasExited)
            {
                AddLog("服务已在运行中，无需重复启动。", WarnBrush);
                return;
            }

            // 未部署就先别启动 —— 否则 python 找不到，报错信息会让人一头雾水
            if (!AppPaths.HasCore)
            {
                AddLog($"未找到 Forge Neo 内核：{AppPaths.LaunchPy}", ErrorBrush);
                AddLog("请把启动器放进 Forge Neo 目录（与 launch.py 同级），或用环境变量 FORGE_NEO_ROOT 指定位置。", WarnBrush);
                return;
            }

            // ② 端口决策 —— 先弄清目标端口上坐着谁，再决定「接管 / 直接用 / 换一个」
            //
            // ⚠ 刻意排在 python 检查**之前**：端口没得用就该立刻失败，
            //   而不是让用户等完几十分钟的首次部署才被告知端口冲突。
            //
            // ⚠ 也刻意不把"端口有人在听"当成"服务已在运行"：那可能是**任何**程序。
            //   分辨归属的完整理由见 PortGuard。
            if (!advOpts.TryPortArg(out int wantPort))
            {
                AddLog($"服务端口「{advOpts.Port}」无效，无法启动。", ErrorBrush);
                AddLog($"请到【高级选项】→【服务与网络】填 1~65535 之间的整数（默认 {PortGuard.DefaultPort}）。", WarnBrush);
                return;
            }

            var probe = PortGuard.Inspect(wantPort, new[] { ReadPid() }, NeoToolDir);

            if (probe.IsOurs)
            {
                // 端口上是**本包**的 Forge（上次没退干净 / 用户自己用 webui.bat 起的）
                // —— 服务其实已经在跑，接管监控即可，别再开一个实例
                AddLog($"检测到 Forge Neo 已在运行 (PID: {probe.Pid})，进入监控模式。", SuccessBrush);
                currentPort = wantPort;
                advOpts.EffectivePort = currentPort;
                UpdateStatus("运行中", $"PID {probe.Pid} · {WebUiUrl}");
                OpenBtn.IsEnabled = true;
                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled = true;
                WritePid(probe.Pid);
                RefreshPreview();
                return;
            }

            if (probe.Free)
            {
                currentPort = wantPort;
            }
            else if (!advOpts.AutoSwitchPort)
            {
                // 关掉了自动切换：**只报错，绝不去动那个进程**
                AddLog($"端口 {wantPort} 已被其他程序占用：{probe.OwnerDesc}", ErrorBrush);
                AddLog("该程序不是本启动器启动的 Forge —— 启动器不会去结束它，本次启动已取消。", WarnBrush);
                AddLog("请二选一：① 到【高级选项】→【服务与网络】换一个端口；"
                     + "② 打开「端口被占用时自动改用其他端口」。", WarnBrush);
                return;
            }
            else
            {
                int alt = PortGuard.FindFreePort(wantPort + 1, PortGuard.ScanCount);
                if (alt <= 0)
                {
                    AddLog($"端口 {wantPort} 已被其他程序占用：{probe.OwnerDesc}", ErrorBrush);
                    AddLog($"其后 {PortGuard.ScanCount} 个端口也都不可用，没有可换的端口。"
                         + "请到【高级选项】→【服务与网络】手动指定一个。", WarnBrush);
                    return;
                }
                AddLog($"端口 {wantPort} 已被其他程序占用：{probe.OwnerDesc}", WarnBrush);
                AddLog($"它**不是**本启动器启动的 Forge（启动器不会去结束它），本次自动改用端口 {alt}。", WarnBrush);
                currentPort = alt;
            }

            advOpts.EffectivePort = currentPort;
            RefreshPreview();

            if (!File.Exists(PythonPath))
            {
                // 薄包首装：venv 还没建。以前这里只打一行「找不到 Python」就退出，
                // 使用者会以为是包缺文件 —— 其实缺的是这一步。现在真的去建。
                if (!await DeployAsync()) return;

                // DeployAsync 成功时会自己校验一遍，这里是双保险。
                // 到这一步还缺就是真异常（磁盘满了 / 杀软隔离了 venv），得说话不能闷着。
                if (!File.Exists(PythonPath))
                {
                    AddLog($"部署完成后仍未找到 Python：{PythonPath}", ErrorBrush);
                    AddLog("若是杀软拦截或磁盘写满导致，处理后删掉 venv 目录再点一次【一键启动】。", WarnBrush);
                    return;
                }
            }

            AddLog($"正在启动 Forge Neo（端口 {currentPort}）...", InfoBrush);
            // 装依赖那段最容易被误判成「卡死」（扩展的 install.py 原本带 -q，全程静默）。
            // 这里先把「去哪儿看」说清楚：实时看本窗口，事后翻这个文件。
            AddLog($"依赖安装的完整日志会写在：{AppPaths.PipLogFile}", InfoBrush);

            // 扩展补丁必须赶在拉起 Forge **之前**：Forge 一起手就会跑各扩展的 install.py，
            // 那时再去改 requirements 已经晚了。绝大多数启动里文件本来就是对的，
            // 这里连一次写盘都不会发生（幂等）。详见 ExtensionPatches.cs。
            ExtensionPatches.ApplyAll(AppPaths.Root,
                (msg, isWarn) => AddLog(msg, isWarn ? WarnBrush : InfoBrush));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = PythonPath,
                    Arguments = ArgLine.Build(BuildFinalArgs()),
                    WorkingDirectory = NeoToolDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // python 输出是 UTF-8；Windows 默认 ANSI(GBK) 解码会把 tqdm 方块字符弄成乱码
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // 环境变量统一收口：UTF-8、包内 uv 进 PATH、缓存与下载源、VIRTUAL_ENV
                ApplyChildEnv(psi);
                proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (s, ev) => { if (ev.Data != null) AddLog(ev.Data, DefaultBrush); };
                proc.ErrorDataReceived += (s, ev) => { if (ev.Data != null) AddLog(ev.Data, ErrorBrush); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AddLog($"启动失败：{ex.Message}", ErrorBrush);
                return;
            }

            UpdateStatus("启动中", "正在加载模型，请稍候...");
            AddLog($"进程已启动 (PID: {proc.Id})，正在加载模型...", InfoBrush);

            // 进「在动」状态：这一步之后通常要装依赖（首次约 4~5 GB），
            // 期间 uv 不报百分比 —— 用滑动 + 走秒 + 包计数表示还活着
            // 一次启动 = 一份全新的状态：端口还没起来、就绪动作还没做过。
            // ⚠ 必须重置 portEverUp：不重置的话"上一轮起过、这一轮秒崩"会被判成正常停止，
            //   报错就被吞了（HandleChildExit 靠它区分"起过又退出"和"没起来"）。
            childExitNoted = false;
            portEverUp = false;
            autoOpened = false;
            idlePid = -1;                 // 端口占用者的缓存跟着清零，下次重算
            BeginMainBusy("正在启动 Forge");

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            WritePid(proc.Id);
        }

        // ================= 首次部署 =================

        /// <summary>
        /// 部署是否正在进行（防重复点击）。
        ///
        /// <para><b>这把锁由 <see cref="DeployAsync"/> 独占持有</b>：只有它会置位与复位。
        /// 调用方（<see cref="RunDeployAsync"/> / <c>Start_Click</c>）只能<b>读</b>它，
        /// 不许自己置位 —— v0.15 的致命 bug 就是这么来的：<c>RunDeployAsync</c> 先置
        /// <c>deploying = true</c> 再调 <c>DeployAsync()</c>，而后者开头判 <c>if (deploying)</c>
        /// 必然为真，于是点一下【开始部署】必定失败，日志还只剩一句「部署已在进行中」，
        /// 连包根路径都打不出来。**加"入口先占锁"这种写法前，先确认锁的持有者唯一。**</para>
        /// </summary>
        private bool deploying;

        /// <summary>
        /// 本轮部署失败的原因（已截断）。部署页状态行直接显示它，
        /// 免得用户看到那句无信息量的「上面的清单和日志里有原因」还得自己去翻日志。
        /// 每轮开始时清空。
        /// </summary>
        private string deployLastError = "";

        /// <summary>
        /// 统一登记一次部署失败：把原因写到**部署页状态行**，而不是只丢进日志。
        /// 两条入口（部署按钮 / 一键启动里的自动首装）都会经过这里，
        /// 所以从哪条路失败，用户切到【一键部署】页都能看到原因。
        /// </summary>
        private void FailDeploy(string reason)
        {
            deployLastError = (reason ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            SetDeployStatus("部署失败：" + Shorten(deployLastError), error: true);
        }

        /// <summary>状态行只占一行，长文案会撑破布局 —— 截断显示，全文进悬浮提示</summary>
        private static string Shorten(string s, int max = 64)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s[..max] + " …";
        }

        /// <summary>
        /// 补上「薄包首装」缺的那一步：<b>建 venv</b>。
        ///
        /// <para>依赖本体不在这一步装 —— 建完 venv 后照常启动 Forge，
        /// 由它自己的 <c>prepare_environment()</c> 按自己的版本对应关系去装。
        /// 理由见 <see cref="DeployManager"/> 的注释。</para>
        /// </summary>
        private async Task<bool> DeployAsync()
        {
            // 重入检查在**置位之前** —— 这里是这把锁唯一的持有者（见 deploying 字段的注释）
            if (deploying)
            {
                AddLog("部署已在进行中，请稍候 ...", WarnBrush);
                return false;
            }
            if (!AppPaths.HasCore)
            {
                string why = $"未找到内核文件 launch.py：{AppPaths.LaunchPy}";
                AddLog("无法部署：" + why, ErrorBrush);
                FailDeploy(why);
                return false;
            }

            deploying = true;
            StartBtn.IsEnabled = false;
            try
            {
                AddLog("========== 首次部署：创建虚拟环境 ==========", InfoBrush);
                AddLog($"包根目录：{AppPaths.Root}（{AppPaths.RootSource}）", InfoBrush);
                AddLog($"下载源　：{DownloadSource.Title(advOpts.MirrorSource)} —— {DownloadSource.Detail(advOpts.MirrorSource)}", InfoBrush);
                AddLog("这一步只建 venv，通常几十秒；依赖本体由 Forge 接着自己装。", InfoBrush);

                // 计时从真正开始干活算起（自动首装那条路径没经过 RunDeployAsync）
                deployStartAt = DateTime.Now;
                if (DeployStepList != null) RefreshDeploySteps();

                var (ok, msg) = await DeployManager.CreateVenvAsync(
                    advOpts.MirrorSource,
                    s => AddLog(s),
                    s => SetDeployLive(s));

                if (!ok)
                {
                    AddLog("部署失败：" + msg, ErrorBrush);
                    AddLog("可在【一键部署】或【高级选项 → 服务与网络】里换一个下载源再试一次。", WarnBrush);
                    SetDeployLive(msg);
                    FailDeploy(msg);          // ← 部署页状态行也写出原因，不用去翻日志
                    return false;
                }

                // 成功时清掉上一轮的失败痕迹
                deployLastError = "";

                // 「venv 本来就在」和「刚建好」是两回事：只有真建了才敢报耗时
                if (msg.Contains("创建完成")) deployVenvSeconds = (DateTime.Now - deployStartAt).TotalSeconds;

                AddLog("✅ " + msg, SuccessBrush);
                AddLog("接下来 Forge 会安装依赖（torch 等，约 4~5 GB，视网速可能几十分钟）。", WarnBrush);
                AddLog("安装进度会持续输出在下方日志里，请不要关闭启动器。", WarnBrush);
                return true;
            }
            finally
            {
                deploying = false;
                if (proc == null || proc.HasExited) StartBtn.IsEnabled = true;
                // 部署后侧栏角标要跟着灭掉（部署页自己也会重刷，这里覆盖从主视图启动的那条路径）
                RefreshDeployDot();
            }
        }

        // ================= 终止进程 =================
        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopForge("用户手动停止");
        }

        private void StopForge(string reason)
        {
            // ---- 只停「我们自己的」进程 ----
            //
            // ⚠ 这里**绝不再按端口杀**。旧代码最后是 `KillTree(GetPortPid())`，
            //   而端口上那一位完全可能是**别人的程序** —— gradio 在没拿到 --port 时会
            //   自己往上找端口，于是我们的 Forge 在 7861，而 7860 上坐着的是别人。
            //   那一刀就是误杀无关进程。理由详见 PortGuard。
            if (proc != null && !proc.HasExited)
            {
                AddLog($"正在停止服务 ({reason}) ...", WarnBrush);
                try
                {
                    Task.Run(() => KillTree(proc.Id));
                    if (!proc.WaitForExit(6000))
                    {
                        AddLog("主进程未响应，强制结束。", WarnBrush);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"停止过程出错：{ex.Message}", ErrorBrush);
                }
            }
            else
            {
                // 监控模式：进程不是我们起的，靠 forge.pid 认领。
                // 同样要过身份判据 —— PID 会被系统复用，只看号码有可能杀错人。
                int adopted = ReadPid();
                if (adopted > 0 && PortGuard.IsAlive(adopted) && PortGuard.LooksLikeOurForge(adopted, NeoToolDir))
                {
                    AddLog($"正在停止服务 (PID {adopted}，{reason}) ...", WarnBrush);
                    KillTree(adopted);
                }
                else if (adopted > 0)
                {
                    AddLog($"forge.pid 里记的进程 {adopted} 已不在运行、或不是本包的 Forge，跳过。", InfoBrush);
                }
            }

            Thread.Sleep(600);

            // 端口还占着的话**只报告，不动手**。
            // 坐在那儿的可能是别人的程序，杀它就是误杀；真正需要清理时用户自己会处理。
            int portPid = PortGuard.GetPortPid(currentPort);
            if (portPid > 0)
            {
                var left = PortGuard.Classify(portPid, new[] { proc?.Id ?? -1, ReadPid() }, NeoToolDir);
                if (left.IsOurs)
                    AddLog($"端口 {currentPort} 仍被本包的 Forge (PID {portPid}) 占用，可再点一次【终止进程】。", WarnBrush);
                else
                    AddLog($"端口 {currentPort} 仍被 {left.OwnerDesc} 占用 —— 那不是本包的 Forge，启动器不会去结束它。", WarnBrush);
            }

            try { File.Delete(Path.Combine(NeoToolDir, "forge.pid")); } catch { }

            // 本次运行用的端口回到配置值。预览也跟着回到"下次启动会用到的参数"，
            // 否则界面会一直显示上次自动切换后的端口，让用户以为配置被改了。
            advOpts.EffectivePort = null;
            idlePid = -1;
            RefreshPreview();

            UpdateStatus("已停止", "");
            AddLog("服务已停止。", SuccessBrush);
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
        }

        // ================= 打开界面 =================
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            OpenBrowser();
        }

        private void OpenBrowser()
        {
            try { Process.Start(new ProcessStartInfo(WebUiUrl) { UseShellExecute = true }); }
            catch (Exception ex) { AddLog($"打开浏览器失败：{ex.Message}", ErrorBrush); }
        }

        // ================= 生成诊断包 =================
        private void Diag_Click(object sender, RoutedEventArgs e)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"ForgeNeo诊断_{ts}.txt");
            try
            {
                var header = new StringBuilder();
                header.AppendLine("================ Forge Neo 诊断包 ================");
                header.AppendLine($"启动器版本 : {AppInfo.Full}");
                header.AppendLine($"程序集版本 : {AppInfo.AssemblyVersionText}");
                header.AppendLine($"导出时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"Forge 目录 : {NeoToolDir}");
                header.AppendLine($"Python     : {PythonPath}");
                header.AppendLine($"服务地址   : {WebUiUrl}");
                header.AppendLine($"端口       : {currentPort}"
                    + (advOpts.EffectivePort.HasValue && advOpts.TryPortArg(out var cfgP) && cfgP != currentPort
                        ? $"（配置为 {cfgP}，本次自动切换）" : ""));
                // 端口上坐的是谁 —— 自动换端口、打开界面、停止服务全看它，排障第一眼要看的
                var dProbe = PortGuard.Inspect(currentPort, new[] { proc?.Id ?? -1, ReadPid() }, NeoToolDir);
                header.AppendLine($"端口占用   : {(dProbe.Free
                    ? "空闲"
                    : dProbe.OwnerDesc + (dProbe.IsOurs ? "（本包 Forge）" : "（不是本包的 Forge）"))}");
                // 排查「localhost is not accessible」时第一眼要看的东西
                header.AppendLine($"代理绕过   : {ProxyEnv.MergeNoProxy(Environment.GetEnvironmentVariable("NO_PROXY"))}");
                header.AppendLine($"当前状态   : {StateText.Text} {StateDetail.Text}");
                header.AppendLine("=================================================");
                header.AppendLine();

                File.WriteAllText(path, header + Console.Text, Encoding.UTF8);
                AddLog($"诊断包已导出: {path}", SuccessBrush);
            }
            catch (Exception ex)
            {
                AddLog($"导出诊断包失败：{ex.Message}", ErrorBrush);
            }
        }

        // ================= 菜单点击 =================
        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
            {
                SetMenuActive(btn);
                string tag = (btn.Tag as string) ?? "";

                // 各页 → 各自的视图；「控制台」「疑难解答」→ 日志视图
                if (tag == "版本管理")
                {
                    ShowVersionView();
                }
                else if (tag == "插件管理")
                {
                    ShowExtView();
                }
                else if (tag == "模型管理")
                {
                    ShowModelView();
                }
                else if (tag == "高级选项")
                {
                    ShowAdvancedView();
                }
                else if (tag == "一键部署")
                {
                    ShowDeployView();
                }
                else if (tag == "首页")
                {
                    ShowView(ViewHome);
                }
                else
                {
                    ShowMainView();
                }

                // ⚠ 删侧栏菜单项时，**这个 switch 里的分支要一起删**：只删分支不删按钮，
                //   按钮照样点得动、只是悄悄走上面的 else → 切到控制台（看着"有功能"、其实
                //   什么也没干）；反过来只删按钮、留下死分支更该删 —— 它会让下一个人以为这里
                //   还有功能。v0.33 删掉的「交流群」「设置」就是这种从没接上页面的占位项：
                //   第 27 节 + 反例 55/56 同时盯着 XAML 与这里。
                switch (tag)
                {
                    case "一键部署":
                        AddLog(DeployState.Inspect() switch
                        {
                            DeployStatus.Ready => "已打开【一键部署】。当前环境已就绪，可在这里重新部署虚拟环境。",
                            DeployStatus.NeedsVenv => "已打开【一键部署】。点【开始部署】创建虚拟环境。",
                            _ => "⚠ 包内缺少必需文件，一键部署无法继续 —— 请重新解压整合包。"
                        }, DeployState.Inspect() == DeployStatus.Broken ? WarnBrush : InfoBrush);
                        break;
                    case "高级选项": AddLog("已打开【高级选项】。切换后点【保存并应用】，下次启动生效（当前运行中的实例不受影响）。", InfoBrush); break;
                    case "疑难解答": AddLog("[提示] 若启动异常，点击顶部【生成诊断包】导出日志。", InfoBrush); break;
                    case "版本管理": AddLog("已打开【版本管理】。点右上角【检测内核更新】查看 Forge Neo 内核是否有新版本；插件请去【插件管理】。", InfoBrush); break;
                    case "插件管理": AddLog("已打开【插件管理】。可以在这里安装 / 启用禁用 / 卸载 / 更新插件。", InfoBrush); break;
                    case "模型管理":
                        AddLog(advOpts.UseA1111Home && !string.IsNullOrWhiteSpace(advOpts.A1111Home)
                            ? $"已打开【模型管理】。这一页只读：列出每个模型目录里有什么、多大；模型目录当前复用已有 A1111 安装 {advOpts.A1111Home}。"
                            : $"已打开【模型管理】。这一页只读：列出每个模型目录里有什么、多大；要追加其他位置的目录请去【高级选项 → 模型目录】。",
                            InfoBrush);
                        break;
                }
            }
        }

        /// <summary>
        /// 视图切换的<b>唯一入口</b>。
        ///
        /// <para>以前每个 <c>ShowXxxView</c> 各自把「另外两个」置为 Collapsed ——
        /// 加一个视图就得改三处，漏一处会出现两个页面叠着显示
        /// （新加的盖在上面，看起来像按钮点了没反应）。现在集中在这里。</para>
        /// </summary>
        private void ShowView(string view)
        {
            HomeView.Visibility      = view == ViewHome      ? Visibility.Visible : Visibility.Collapsed;
            MainView.Visibility      = view == ViewMain      ? Visibility.Visible : Visibility.Collapsed;
            DeployView.Visibility    = view == ViewDeploy    ? Visibility.Visible : Visibility.Collapsed;
            VersionView.Visibility   = view == ViewVersion   ? Visibility.Visible : Visibility.Collapsed;
            ExtensionView.Visibility = view == ViewExt       ? Visibility.Visible : Visibility.Collapsed;
            ModelView.Visibility     = view == ViewModel     ? Visibility.Visible : Visibility.Collapsed;
            AdvancedView.Visibility  = view == ViewAdvanced  ? Visibility.Visible : Visibility.Collapsed;

            // 顶栏**整条**在首页收起，把顶部那 64px 全让给题图
            // （Grid.Row 0 已改成 Auto，收起后不会留空白）。
            // 理由：状态文字 / 打开界面 / 一键启动，首页底栏那张卡片里都有；
            // 剩下的生成诊断包、终止进程属维护类动作 —— 本页的设计只负责
            // 「把人送进某个目录」或「把人送进一键启动」，维护一律去控制台。
            // 注：StartBtn 的 Visibility 不再单独管，父级收起即可（若哪天要让顶栏
            //     重新出现在首页，得把「首页不并排两颗一键启动」那条守卫补回来）。
            TopBarRow.Visibility = view == ViewHome ? Visibility.Collapsed : Visibility.Visible;

            // 顶部那条进度栏同样只在非首页出现：控制台里已经有同一条进度了，
            // 首页再摆一条纯属重复，收掉还能把纵向空间让给横幅。
            // （轨道宽归 0 时 PlotMainFill 会走 UiFx 的兜底值；回到控制台时
            //   SizeChanged 会自动按真实宽度重投影，不用在这里手动补一刀。）
            ProgressRow.Visibility = view == ViewHome ? Visibility.Collapsed : Visibility.Visible;

            // 各视图的「进页面动作」——只在该视图真要显示时才跑
            if (view == ViewHome)
            {
                RefreshHome();
            }
            else if (view == ViewDeploy)
            {
                RenderDeployCheck();
                FillDeploySourceBox();
                RefreshDeployUi();
            }
            else if (view == ViewAdvanced)
            {
                LoadAdvancedIntoUi();
            }
            else if (view == ViewVersion && !torchProbedOnce)
            {
                // 刻意不自动检测更新：会打网络请求（github API + 逐个插件 git ls-remote），
                // 用户只是想进来看看时不该替他跑一遍。这里只读一次 PyTorch 环境（纯本地 import）。
                _ = ProbeTorchEnvAsync();
            }
            else if (view == ViewExt && !extScannedOnce)
            {
                // 插件页进来先扫一遍**本地**目录（每个扩展几条 git 进程，**不发网络请求**）——
                // 否则列表是空的，而用户进这一页本来就是为了看有哪些插件。
                // 要问"有没有新版本"是「检测插件更新」那个按钮的事。
                _ = RescanExtensionsAsync();
            }
            else if (view == ViewModel && !modelScannedOnce)
            {
                // 模型页同理：进来先扫一遍本地目录（只枚举一层，不发网络）——
                // 用户进这一页就是想看"我放的模型到底在不在"。
                _ = RescanModelsAsync();
            }
        }

        private const string ViewHome      = "home";
        private const string ViewMain      = "main";
        private const string ViewDeploy    = "deploy";
        private const string ViewVersion   = "version";
        private const string ViewExt       = "ext";
        private const string ViewAdvanced  = "advanced";
        // ⚠ 与控件名 ModelView 的字母顺序**不同**（照 ViewExt / ExtensionView 的旧例），
        //   读代码时别看错：ViewModel 是"模型这一页"的视图标识。
        private const string ViewModel     = "models";

        private void ShowVersionView()  => ShowView(ViewVersion);
        private void ShowExtView()      => ShowView(ViewExt);
        private void ShowModelView()    => ShowView(ViewModel);
        private void ShowAdvancedView() => ShowView(ViewAdvanced);
        private void ShowMainView()     => ShowView(ViewMain);

        // =====================================================================
        //  一键部署
        //
        //  与「高级选项」共用同一个下载源档位（advOpts.MirrorSource），
        //  两个下拉框只是同一份配置的两个视图 —— 改哪边都生效，不会打架。
        // =====================================================================

        /// <summary>部署页的下载源下拉框正在被代码同步时为 true，用于防事件回环</summary>
        private bool deploySourceSyncing;

        private void ShowDeployView() => ShowView(ViewDeploy);

        /// <summary>
        /// 逐项渲染体检结果。
        ///
        /// 用代码生成而不是 XAML 写死：项数本身是动态的（部署完成后会多出「部署状态文件」一项），
        /// 详情里还要放真实路径 —— 写死四行的话，将来加一项检查就得同时改 XAML 和代码。
        /// </summary>
        private void RenderDeployCheck()
        {
            if (DeployCheckList == null) return;
            DeployCheckList.Children.Clear();

            var headBrush = (Brush)FindResource("TopBtnText");
            var subBrush  = (Brush)FindResource("StateDetail");

            foreach (var c in AppPaths.Inspect())
            {
                var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // 状态图标：√ / ×
                var icon = new TextBlock
                {
                    Text = c.Ok ? "\uE73E" : "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    // 必需项缺失标红（包坏了，得重解压）；可选项缺失标黄（部署能补救）
                    Foreground = c.Ok ? SuccessBrush : (c.Required ? ErrorBrush : WarnBrush)
                };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var name = new TextBlock
                {
                    Text = c.Name,
                    FontSize = 12.5,
                    Foreground = headBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);

                // 详情可能很长（绝对路径），裁断显示 + 悬浮看全文
                var detail = new TextBlock
                {
                    Text = c.Detail,
                    FontSize = 11.5,
                    Foreground = subBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = c.Detail
                };
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);

                DeployCheckList.Children.Add(grid);
            }
        }

        /// <summary>按三态刷新徽章、按钮与说明文字。三态的含义见 <see cref="DeployStatus"/></summary>
        private void RefreshDeployUi()
        {
            if (DeployBadge == null) return;

            switch (DeployState.Inspect())
            {
                case DeployStatus.Ready:
                    DeployBadge.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                    DeployBadgeText.Text = "已就绪";
                    DeployCheckHint.Text = "必需文件全部就位，环境可用。";
                    DeployBtn.Content = "重新部署环境";
                    DeployBtn.IsEnabled = !deploying;
                    DeployGoBtn.Visibility = Visibility.Visible;
                    DeployNextHint.Text = "点【进入一键启动】运行 Forge Neo（会自动切到控制台看输出）。" +
                        "若运行异常（例如换了显卡），可在这里重新部署一次虚拟环境。";
                    break;

                case DeployStatus.NeedsVenv:
                    DeployBadge.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xA2, 0x2E));
                    DeployBadgeText.Text = "未部署";
                    DeployCheckHint.Text = "包内文件齐备，只差虚拟环境 —— 点下面的按钮建好它。";
                    DeployBtn.Content = "开始部署";
                    DeployBtn.IsEnabled = !deploying;
                    DeployGoBtn.Visibility = Visibility.Collapsed;
                    DeployNextHint.Text = "虚拟环境只用几十秒。建好后 Forge 会自己安装依赖" +
                        "（torch 等约 4~5 GB，视网速可能几十分钟）——" +
                        "这一段也会显示在上面的进度条上，详细日志在【控制台】。";
                    break;

                default:
                    DeployBadge.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F));
                    DeployBadgeText.Text = "包不完整";
                    DeployCheckHint.Text = "缺少随包文件，一键部署也无从下手 —— 请重新解压整合包。";
                    DeployBtn.Content = "无法部署";
                    DeployBtn.IsEnabled = false;
                    DeployGoBtn.Visibility = Visibility.Collapsed;
                    DeployNextHint.Text = "上面标红的就是缺的东西。常见原因：解压中断、" +
                        "杀软把 runtime 目录里的文件隔离了、或者复制时漏了文件。" +
                        "建议整个目录删掉重解压一次。";
                    break;
            }

            RefreshDeployDot();
            RefreshDeploySteps();   // 步骤清单 / 进度条 / 计时器都从现实推导，这里一起刷
        }

        /// <summary>侧栏「一键部署」上的橙色角标：未就绪时亮起，否则收起</summary>
        private void RefreshDeployDot()
        {
            if (DeployDot == null) return;
            DeployDot.Visibility = DeployState.Inspect() == DeployStatus.Ready
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>把下载源填进部署页的下拉框（数据源与高级选项页是同一份 <c>DownloadSource.Options()</c>）</summary>
        private void FillDeploySourceBox()
        {
            if (DeploySourceBox == null) return;

            var opts = DownloadSource.Options();
            DeploySourceBox.ItemsSource = opts;

            deploySourceSyncing = true;
            try
            {
                DeploySourceBox.SelectedItem =
                    opts.FirstOrDefault(o => o.Id == advOpts.MirrorSource) ?? opts[0];
            }
            finally { deploySourceSyncing = false; }

            UpdateDeploySourceHint();
        }

        private void DeploySource_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (deploySourceSyncing) return;
            if (DeploySourceBox?.SelectedItem is not SourceOption o) return;
            if (!DownloadSource.IsOfficial(o.Id) && o.Id != DownloadSource.Cn) return;

            // 唯一真值写回模型 —— 然后两个视图各自刷新
            advOpts.MirrorSource = o.Id;

            // 高级选项页的下拉框是同一份配置的另一个视图，得跟着走
            deploySourceSyncing = true;
            try
            {
                if (SourceBox != null)
                    SourceBox.SelectedItem = DownloadSource.Options()
                        .FirstOrDefault(x => x.Id == o.Id);
            }
            finally { deploySourceSyncing = false; }

            UpdateDeploySourceHint();
            UpdateSourceHint();
            UpdateTorchPreview();

            // 立刻落盘：部署页是首装入口，用户在这里选完源可能直接点部署就走，
            // 不该要求他再跑去高级选项页点一次「保存」
            advOpts.Save();
            LauncherConfig.Save();
        }

        /// <summary>把档位落成具体域名显示 —— 镜像站哪天挂了，能一眼看出在用哪个地址</summary>
        private void UpdateDeploySourceHint()
        {
            if (DeploySourceHint == null) return;

            string src = advOpts.MirrorSource;
            DeploySourceHint.Text =
                $"PyPI 包　：{DownloadSource.PyPiIndex(src)}\n" +
                $"PyTorch　：{DownloadSource.TorchIndex(src, "cu130")}";
        }

        /// <summary>「开始部署 / 重新部署」按钮</summary>
        private async void DeployBtn_Click(object sender, RoutedEventArgs e)
        {
            if (deploying) return;

            // ⚠ 顺序即安全：**先问"能不能重建"，再删**。
            //   删除不可逆，而"能不能重建"是个只读判断 —— 问晚了就来不及。
            //   开发目录（有 venv、没 runtime）里点这个按钮，旧代码会先把 4 GB 依赖删掉，
            //   之后才轮到 CreateVenvAsync 报「找不到随包 uv，请重新解压整合包」——
            //   用户手里只剩一个空目录和一句"包不完整"。拦截必须发生在这里。
            if (AppPaths.HasVenv && !AppPaths.CanCreateVenv)
            {
                bool uvMissing = !File.Exists(AppPaths.UvExe);
                bool pyMissing = AppPaths.FindBundledPython() == null;
                string what = (uvMissing ? "  · " + AppPaths.UvExe + "\n" : "")
                            + (pyMissing ? "  · " + AppPaths.BundlePyDir + " 下没有 python.exe\n" : "");

                AddLog("========== 重新部署：已中止（没有删除任何文件） ==========", WarnBrush);
                AddLog("这个目录缺少重建虚拟环境所需的随包运行时，无法重建。", ErrorBrush);
                AddLog(what.TrimEnd(), ErrorBrush);
                AddLog("现有虚拟环境保持原样 —— 它仍然可用。", InfoBrush);
                SetDeployStatus("缺少随包运行时，无法重建（现有环境未改动）", error: true);

                MessageBox.Show(
                    "没有删除任何文件。\n\n" +
                    "【重新部署】是先删旧环境、再建新的；而这个目录缺少重建所需的随包运行时，\n" +
                    "所以本次操作被提前拦下，现有虚拟环境完好无损。\n\n" +
                    "缺少：\n" + what +
                    "\n如果你确实要重建（例如换了显卡），有两条路：\n" +
                    "  · 备齐随包运行时（runtime\\uv\\uv.exe 与 runtime\\python\\…\\python.exe）\n" +
                    "  · 或解压一份新的整合包，在别的目录里做首次部署\n\n" +
                    "注意：重建之后 torch 等依赖要重新装（约 4~5 GB）。\n" +
                    "如果只是想换个 Python 解释器，不必走这一条。",
                    "无法重新部署（未删除任何文件）", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 重新部署要先清掉现有 venv —— 这是删除操作，必须让用户明确知道删的是什么
            if (AppPaths.HasVenv)
            {
                var r = MessageBox.Show(
                    "将删除现有的虚拟环境并重新创建：\n\n" + AppPaths.VenvDir + "\n\n" +
                    "影响范围：\n" +
                    "  · 只删 venv 目录（里面是 Python 环境和已装的依赖）\n" +
                    "  · 不影响源码、模型、输出图片、启动器配置\n" +
                    "  · 重建后需要重新安装依赖（torch 等约 4~5 GB）\n\n" +
                    "确定要重新部署吗？",
                    "重新部署环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;

                AddLog("========== 重新部署：清理旧虚拟环境 ==========", WarnBrush);
                if (!DeleteVenv())
                {
                    AddLog("删除失败 —— 通常是有进程正占用它。请先停止 Forge Neo，或关闭后重试。", ErrorBrush);
                    SetDeployStatus("删除旧环境失败，请先停止服务", error: true);
                    return;
                }
                AddLog($"已删除：{AppPaths.VenvDir}", InfoBrush);
            }

            await RunDeployAsync();
        }

        /// <summary>真正执行部署。与「一键启动」里的自动首装走同一条路径，避免两套逻辑分叉</summary>
        private async Task<bool> RunDeployAsync()
        {
            if (deploying) return false;

            // ⚠ 这里**只做 UI 层防连点，不要置 deploying** ——
            //   那把锁归 DeployAsync 独占。曾在 860 行写成 deploying = true 再 await DeployAsync()，
            //   结果 DeployAsync 开头的重入检查必然为真 → 点一下必失败（v0.15 的 bug）。
            //   防连点靠禁用按钮就够了：WPF 的 Click 在 UI 线程串行派发，
            //   IsEnabled=false 之后同一次连点不会产生第二个事件。
            DeployBtn.IsEnabled = false;
            DeployGoBtn.Visibility = Visibility.Collapsed;
            SetDeployStatus("正在创建虚拟环境 ...", error: false);

            // 重新开一轮：把上一轮的残留（计时 / 百分比 / 最近动作 / 失败原因）清掉
            deployStartAt = DateTime.Now;
            deployVenvSeconds = -1;
            deployPercent = -1;
            deployLiveAction = "";
            deployLastError = "";
            RefreshDeploySteps();

            try
            {
                bool ok = await DeployAsync();

                if (ok)
                    SetDeployStatus(deployVenvSeconds > 0
                        ? $"虚拟环境已就绪（耗时 {deployVenvSeconds:F1} 秒）"
                        : "虚拟环境已就绪", error: false);
                else if (deployLastError.Length == 0)
                    // DeployAsync 没能给出具体原因时的兜底（正常不会走到）
                    SetDeployStatus("部署失败 —— 详细输出见【控制台】的日志", error: true);
                // 有具体原因时**不要覆盖** FailDeploy 写好的那句

                return ok;
            }
            finally
            {
                deployLiveAction = "";
                RenderDeployCheck();
                RefreshDeployUi();
            }
        }

        private void SetDeployStatus(string text, bool error)
        {
            if (DeployStatusText == null) return;
            DeployStatusText.Text = text;
            DeployStatusText.Foreground = error ? ErrorBrush : SuccessBrush;
            // 状态行只有一行的位置，长文案会被 TextTrimming 截掉 —— 全文放悬浮提示里，
            // 省得用户为了看完整原因还得去翻日志
            DeployStatusText.ToolTip = !string.IsNullOrEmpty(text) && text.Length > 40 ? text : null;
        }

        // =====================================================================
        //  部署页的进度区
        //
        //  两步：① 建虚拟环境（启动器干，秒级）② 装依赖 torch 等（Forge 干，几十分钟）
        //
        //  显示策略（刻意不编造百分比）：
        //    · 步骤清单的两步状态**从现实推导**（venv / torch 在不在、Forge 在不在跑），
        //      不另记一套状态变量 —— 所以重开启动器、或从首页【一键启动】自动首装，
        //      这里显示的照样是对的。
        //    · 建 venv 阶段 uv 不给百分比 → 进度条来回滑动 +「已用 xx 秒」自己走，
        //      静默期也能看出没卡死。
        //    · 装依赖阶段日志里有 tqdm 的真实百分比 → 直接把那条进度镜像过来。
        // =====================================================================

        /// <summary>一步的状态</summary>
        private enum StepState { Pending, Running, Done, Failed }

        private sealed class DeployStepInfo
        {
            public string Title { get; set; } = "";
            public string Note { get; set; } = "";
            public StepState State { get; set; } = StepState.Pending;
        }

        /// <summary>本次建 venv 的起始时刻</summary>
        private DateTime deployStartAt;

        /// <summary>本次建 venv 的耗时（秒）；负数表示这次没真跑（venv 本来就在）</summary>
        private double deployVenvSeconds = -1;

        /// <summary>那一行「最近动作」：uv 的原样输出，或我们自己的短句</summary>
        private string deployLiveAction = "";

        /// <summary>从日志 tqdm 解析到的真实百分比；负数表示还没有</summary>
        private double deployPercent = -1;

        /// <summary>进度条填充比例（0~100），窗口尺寸变化 / 重新布局后按它重算像素宽</summary>
        private double deployFillRatio;

        /// <summary>部署页每秒一刷的计时器（只在真有活干时开着）</summary>
        private DispatcherTimer? deployTick;

        /// <summary>建 venv 阶段的来回滑动动画是否正在跑（用 <see cref="UiFx"/> 直接控 Transform）</summary>
        private bool deployMarqueeOn;

        /// <summary>
        /// 按现实推导两步状态。
        /// 判据都是「文件在不在 / 进程在不在跑」这类只读、零成本的检查。
        /// </summary>
        private List<DeployStepInfo> BuildDeploySteps()
        {
            bool hasVenv  = AppPaths.HasVenv;
            bool hasTorch = AppPaths.HasTorch;
            bool forgeUp  = proc != null && !proc.HasExited;

            var steps = new List<DeployStepInfo>();

            // ① 建虚拟环境（本启动器负责）
            var s1 = new DeployStepInfo { Title = "创建虚拟环境" };
            if (hasVenv)
            {
                s1.State = StepState.Done;
                s1.Note = deployVenvSeconds > 0
                    ? $"已完成 · 本次耗时 {deployVenvSeconds:F1} 秒"
                    : "已完成";
            }
            else if (deploying)
            {
                s1.State = StepState.Running;
                s1.Note = "进行中 · 已用 " + ElapsedText();
            }
            else
            {
                s1.Note = "待运行 · 国内源通常几秒，官方源最长约 40 秒";
            }
            steps.Add(s1);

            // ② 装依赖（Forge 负责，启动器只观察）
            var s2 = new DeployStepInfo { Title = "安装依赖（torch 等约 4~5 GB）" };
            if (!hasVenv)
            {
                s2.Note = "等虚拟环境建好之后开始";
            }
            else if (hasTorch)
            {
                s2.State = StepState.Done;
                s2.Note = "已完成 · torch 已在虚拟环境里";
            }
            else if (forgeUp)
            {
                s2.State = StepState.Running;
                s2.Note = "进行中 · 详细日志在【控制台】";
            }
            else
            {
                s2.Note = "待运行 · 点【进入一键启动】后由 Forge 自己装";
            }
            steps.Add(s2);

            return steps;
        }

        /// <summary>当前这一轮的已用时间（人话格式）</summary>
        private string ElapsedText()
        {
            if (!deploying) return "0 秒";
            var t = DateTime.Now - deployStartAt;
            if (t.TotalSeconds < 1) return "不到 1 秒";
            if (t.TotalMinutes < 1) return $"{t.TotalSeconds:F0} 秒";
            return $"{t.Minutes} 分 {t.Seconds:00} 秒";
        }

        /// <summary>渲染两步清单 —— 排版与上面的「环境体检」清单保持一致</summary>
        private void RenderDeploySteps()
        {
            if (DeployStepList == null) return;
            DeployStepList.Children.Clear();

            var headBrush = (Brush)FindResource("TopBtnText");
            var subBrush  = (Brush)FindResource("StateDetail");
            var accent    = (Brush)FindResource("AccentBrush");

            foreach (var st in BuildDeploySteps())
            {
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // 图标：完成 √ / 失败 × / 进行中实心圆 / 待运行空心圆。
                // 圆点用 Ellipse 画，不去赌字体里有没有那个字形。
                // （Ellipse 写全限定名：本文件顶部不能加 using System.Windows.Shapes，
                //   否则 Path 会与 System.IO.Path 二义，CS0104 —— 与项目其他地方的写法一致）
                FrameworkElement icon;
                if (st.State == StepState.Running)
                {
                    icon = new System.Windows.Shapes.Ellipse
                    {
                        Width = 11, Height = 11, Fill = accent,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(3, 0, 0, 0)
                    };
                }
                else if (st.State == StepState.Pending)
                {
                    icon = new System.Windows.Shapes.Ellipse
                    {
                        Width = 11, Height = 11, StrokeThickness = 1.5, Stroke = subBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(3, 0, 0, 0)
                    };
                }
                else
                {
                    icon = new TextBlock
                    {
                        Text = st.State == StepState.Done ? "\uE73E" : "\uE711",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = st.State == StepState.Done ? SuccessBrush : ErrorBrush
                    };
                }
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var title = new TextBlock
                {
                    Text = st.Title,
                    FontSize = 12.5,
                    Foreground = headBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(title, 1);
                grid.Children.Add(title);

                var note = new TextBlock
                {
                    Text = st.Note,
                    FontSize = 11.5,
                    Foreground = subBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(note, 2);
                grid.Children.Add(note);

                DeployStepList.Children.Add(grid);
            }
        }

        /// <summary>
        /// 进度区总刷新：步骤清单 + 进度条 + 最近动作 + 计时器开关。
        /// 所有触发点（进页面 / 开始部署 / 每秒轮询 / 日志解析）都走这一个口子，
        /// 免得出现"某个入口漏刷新导致界面停在旧状态"。
        /// </summary>
        private void RefreshDeploySteps()
        {
            if (DeployStepList == null) return;

            bool forgeUp = proc != null && !proc.HasExited;
            if (!forgeUp && !deploying) deployPercent = -1;   // 进程没了，旧百分比作废

            RenderDeploySteps();

            bool venvRunning = deploying && !AppPaths.HasVenv;
            bool depsRunning = AppPaths.HasVenv && !AppPaths.HasTorch && forgeUp;
            bool show        = deploying || depsRunning;

            DeployBarRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                bool real = depsRunning && deployPercent >= 0;   // 有真实百分比才画填充
                SetDeployMarquee(!real);
                if (real)
                {
                    SetDeployFill(deployPercent);
                    DeployBarText.Text = $"{deployPercent:F0}%";
                }
                else
                {
                    SetDeployFill(0);
                    DeployBarText.Text = venvRunning ? "已用 " + ElapsedText() : "进行中 ...";
                }
            }
            else
            {
                SetDeployMarquee(false);
                SetDeployFill(0);
                DeployBarText.Text = "";
            }

            bool liveShow = show && deployLiveAction.Length > 0;
            DeployLiveBox.Visibility = liveShow ? Visibility.Visible : Visibility.Collapsed;
            DeployLiveText.Text = deployLiveAction;

            if (show) StartDeployTick(); else StopDeployTick();
        }

        /// <summary>记录「现在在干什么」——部署页那一行深色条</summary>
        /// <summary>
        /// 把一段界面操作切回 UI 线程执行 —— <b>后台线程碰控件前必须过这一道</b>。
        ///
        /// <para>为什么非有不可：子进程的输出是在 <see cref="Process"/> 的
        /// <c>AsyncStreamReader</c> 线程上回调的，直接在里面写控件会抛
        /// <c>InvalidOperationException（调用线程无法访问此对象，因为另一个线程拥有该对象）</c>。
        /// 这是**未处理异常**，WPF 不会兜住：进程直接消失（闪退），
        /// 日志里连一句提示都没有。v0.16 的部署闪退就是这么来的
        /// （uv 说的每一行都会喂给「当前在干什么」那一行，而那一行没做线程切换）。</para>
        ///
        /// <para>已在 UI 线程时直接执行，不绕 <c>Invoke</c> —— 既省一次调度，
        /// 也避免"UI 线程等自己"造成的死锁。</para>
        /// </summary>
        private void UiRun(Action act)
        {
            if (act == null) return;
            try
            {
                if (Dispatcher.CheckAccess()) act();
                else Dispatcher.Invoke(act);
            }
            catch { }
        }

        /// <summary>部署页那一行「当前在干什么」（uv 的最新一行）。可从任意线程调用。</summary>
        private void SetDeployLive(string action)
        {
            if (action == null) return;
            deployLiveAction = action.Trim();          // 字段：任意线程可写
            UiRun(() =>
            {
                if (DeployLiveBox == null) return;
                if (deployLiveAction.Length == 0) return;
                DeployLiveBox.Visibility = Visibility.Visible;
                DeployLiveText.Text = deployLiveAction;
            });
        }

        /// <summary>
        /// 设定部署页进度条的填充比例（0~100）。与顶部那条同款：
        /// 比例是模型，像素在轨道宽度变化时由 <see cref="BarTrack_SizeChanged"/> 重投影
        /// （这行右侧的「96%　剩余 2m35s · 3.89it/s」文字宽度变化和窗口缩放都会改轨道宽）。
        /// </summary>
        private void SetDeployFill(double percent)
        {
            deployFillRatio = percent;
            PlotDeployFill();
        }

        /// <summary>把比例画到当前轨道上</summary>
        private void PlotDeployFill()
        {
            if (DeployBarFill == null || DeployBarBack == null) return;
            DeployBarFill.Width = UiFx.FillWidth(deployFillRatio, DeployBarBack.ActualWidth);
        }

        /// <summary>建 venv 阶段的"在动"效果：一个色块在轨道里来回滑</summary>
        private void SetDeployMarquee(bool on)
        {
            if (DeployBarMarquee == null) return;

            if (on)
            {
                DeployBarMarquee.Visibility = Visibility.Visible;
                if (!deployMarqueeOn)
                {
                    UiFx.StartMarquee(DeployMarqueeShift, -96, 900, 1.5);
                    deployMarqueeOn = true;
                }
            }
            else
            {
                DeployBarMarquee.Visibility = Visibility.Collapsed;
                if (deployMarqueeOn)
                {
                    UiFx.StopMarquee(DeployMarqueeShift);
                    deployMarqueeOn = false;
                }
            }
        }

        /// <summary>
        /// 把日志里解析出来的真实进度镜像到部署页（装依赖阶段）。
        /// 这里走的是"快路径"：tqdm 行来得密（每秒好几条），只改进度条，不重建清单。
        /// </summary>
        private void MirrorPercentToDeploy(int pct, string detail)
        {
            deployPercent = pct;

            if (DeployView == null || DeployView.Visibility != Visibility.Visible) return;
            if (!AppPaths.HasVenv) return;              // 还没到装依赖阶段，别抢建 venv 那条滑动条

            DeployBarRow.Visibility = Visibility.Visible;
            SetDeployMarquee(false);
            SetDeployFill(pct);
            DeployBarText.Text = string.IsNullOrEmpty(detail) ? $"{pct}%" : $"{pct}%　{detail}";
        }

        private void StartDeployTick()
        {
            if (deployTick == null)
            {
                deployTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                deployTick.Tick += (s, e) =>
                {
                    // 没活干了就自己停 —— 步骤状态那一秒的延迟差无所谓，
                    // 但别让一个每秒醒来的计时器一直挂着
                    if (deploying || (proc != null && !proc.HasExited)) RefreshDeploySteps();
                    else StopDeployTick();
                };
            }
            if (!deployTick.IsEnabled) deployTick.Start();
        }

        private void StopDeployTick()
        {
            if (deployTick != null && deployTick.IsEnabled) deployTick.Stop();
        }

        /// <summary>
        /// 删除 venv 目录。范围严格限制在 <see cref="AppPaths.VenvDir"/> 这一个目录内，
        /// 不做任何通配或上级目录操作 —— 这个目录就在包根下，删错了就是删用户的安装。
        /// </summary>
        private bool DeleteVenv()
        {
            try
            {
                if (!Directory.Exists(AppPaths.VenvDir)) return true;

                // 保险丝：路径必须真的以 venv 结尾，避免任何拼接失误导致误删包根
                var full = Path.GetFullPath(AppPaths.VenvDir).TrimEnd('\\', '/');
                if (!string.Equals(Path.GetFileName(full), "venv", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"⚠ 安全检查未通过，拒绝删除：{full}", ErrorBrush);
                    return false;
                }

                Directory.Delete(full, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                AddLog("删除 venv 失败：" + ex.Message, ErrorBrush);
                return false;
            }
        }

        /// <summary>
        /// 跳到【控制台】。日志区、顶部进度条、状态行都在这一页 ——
        /// 凡是「启动 / 维护环境」这类会产出大量输出的动作，都把执行者送到这里看结果。
        /// </summary>
        private void GotoConsole()
        {
            ShowMainView();
            if (MenuConsole != null) SetMenuActive(MenuConsole);
        }

        /// <summary>部署页的「进入一键启动」：切到控制台，顺便把启动动作带过去</summary>
        private void DeployGoStart_Click(object sender, RoutedEventArgs e)
        {
            GotoConsole();
            Start_Click(sender, e);
        }

        // ================= 菜单高亮 =================
        private void SetMenuActive(System.Windows.Controls.Button active)
        {
            activeMenuBtn = active;
            var activeBg = MenuActiveBg;
            var idleFore = (Brush)FindResource("MenuIdleFore");
            var buttons = new[] { MenuHome, MenuConsole, MenuDeploy, MenuAdvanced, MenuFix, MenuVersion, MenuExt, MenuModel };
            // ⚠ 这里引用的控件必须真实存在（编译期就挡得住），但**漏一个**是静默的：
            //   漏掉的按钮不会被重置成 idle 色，切主题后会留着上一套主题的灰。
            //   `_hometest` 的「切主题后菜单文字跟着换色」那条断言用的就是同一份清单。
            foreach (var b in buttons)
            {
                if (b == null) continue;
                if (b == active)
                {
                    b.Background = activeBg;
                    b.Foreground = Brushes.White;
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.Foreground = idleFore;
                }
            }
        }

        // ================= 主题切换 =================
        // 切换黑白主题；日志区恒定黑色不受影响
        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            isDark = !isDark;
            ApplyTheme(isDark);
            SaveDarkTheme(isDark);
            AddLog(isDark ? "已切换到黑色主题。日志区始终为黑色。" : "已切换到白色主题。日志区始终为黑色。", InfoBrush);
        }

        // 主题选择存到 exe 同目录的 launcher.cfg（统一由 LauncherConfig 维护，
        // 避免与高级选项各写各的、互相覆盖）
        private static void SaveDarkTheme(bool dark)
        {
            LauncherConfig.SetBool("DarkTheme", dark);
            LauncherConfig.Save();
        }

        private void ApplyTheme(bool dark)
        {
            if (dark) Theme.ApplyDark(this); else Theme.ApplyLight(this);

            // 主题切换后，未激活菜单文字/状态灯空闲色要从新资源读取
            // 重挂当前高亮的菜单（若未高亮过则默认第一个）
            SetMenuActive(activeMenuBtn ?? MenuHome);
            if (StateText.Text == "未运行")
            {
                StateDot.Fill = (Brush)FindResource("StatusDotIdle");
                StateText.Foreground = (Brush)FindResource("StatusTextIdle");
            }

            ThemeIcon.Text = dark ? "☾" : "☀";
            ThemeLabel.Text = dark ? "黑夜" : "白天";
        }

        // ================= 版本管理（内核）=================
        //  ⚠ v0.32 起这一页只管**内核**。插件那一整套（扫描 / 检测 / 装 / 卸 / 启停 / 更新）
        //    搬到了【插件管理】页 —— 于是"检测"与"一键更新全部"在每一页上都只有一个意思。
        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckCoreAsync();
        }

        private async Task CheckCoreAsync()
        {
            if (versionBusy) return;
            versionBusy = true;
            CheckUpdateBtn.IsEnabled = false;
            VerStatusText.Text = "正在检测内核 ...";

            try
            {
                // git 与 HTTP 都是同步等待，放线程池里跑，别卡 UI
                var ci = await Task.Run(() => CoreUpdater.CheckAsync());
                coreItem = ci;

                Dispatcher.Invoke(() =>
                {
                    FillCore(ci);

                    // 首次检测完成 → 收起引导卡片，露出内核卡片
                    coreCheckedOnce = true;
                    ShowUpdateResult();
                    VerStatusText.Text = $"检测完成（{DateTime.Now:HH:mm:ss}）· {StateTextOf(ci.State)}";
                    AddLog($"内核检测完成：{StateTextOf(ci.State)}。", InfoBrush);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => VerStatusText.Text = "检测失败：" + ex.Message);
                AddLog("内核检测失败：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    CheckUpdateBtn.IsEnabled = true;
                    CoreUpdateBtn.IsEnabled = coreItem?.CanUpdate == true;
                });
            }
        }

        /// <summary>
        /// 把 <see cref="extItems"/> 按当前搜索词过滤后送进列表。
        ///
        /// <para>列表显示的是<b>过滤后的子集</b>，而「一键更新」读的是
        /// <see cref="extItems"/> 全集 —— 免得用户搜了两个字之后，
        /// 「更新全部」就只更新了看得见的那几个。</para>
        /// </summary>
        private void ApplyExtFilter()
        {
            var shown = ExtensionManager.Filter(extItems, extFilter);
            ExtList.ItemsSource = shown;

            int upd = extItems.Count(x => x.State == UpdateState.UpdateAvailable);
            ExtCountText.Text = $"共 {extItems.Count} 个插件"
                                + (shown.Count != extItems.Count ? $"，显示 {shown.Count} 个" : "")
                                + (upd > 0 ? $" · {upd} 项可更新" : "");
        }

        // ================= 版本管理：选项卡 =================
        private void VerTab_Checked(object sender, RoutedEventArgs e)
        {
            ApplyVerTabVisibility();
        }

        /// <summary>按选中项显示「组件更新」或「PyTorch 环境」面板</summary>
        private void ApplyVerTabVisibility()
        {
            if (PaneUpdate == null || PaneTorch == null) return;

            bool showUpdate = VTabUpdate == null || VTabUpdate.IsChecked != false;
            bool showTorch = VTabTorch != null && VTabTorch.IsChecked == true;
            if (!showUpdate && !showTorch) showUpdate = true;   // 兜底，避免空白页

            PaneUpdate.Visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;
            PaneTorch.Visibility = showTorch ? Visibility.Visible : Visibility.Collapsed;

            // 「检测内核更新」只属于内核那个面板。
            // （以前还要跟着切「一键更新全部」—— 那个按钮已随插件区搬到【插件管理】页，
            //   它不属于这一页的任何 tab，在这里管它只会把它错关掉。）
            CheckUpdateBtn.Visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;

            if (showTorch && !torchProbedOnce) _ = ProbeTorchEnvAsync();
        }

        /// <summary>
        /// 版本管理页：引导卡片 ⇄ 内核卡片 的显隐。
        ///
        /// <para>⚠ <b>只管内核</b>。v0.31 及以前它还管着插件区（ExtHeader / ExtToolRow /
        /// ExtCard）的显隐 —— 那套"没检测过就别看列表"的闸门，在插件独立成页之后
        /// 已经没有意义了：进【插件管理】就是要看有哪些插件。</para>
        /// </summary>
        private void ShowUpdateResult()
        {
            CoreCard.Visibility = coreCheckedOnce ? Visibility.Visible : Visibility.Collapsed;
            VerIdleHint.Visibility = coreCheckedOnce ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string StateTextOf(UpdateState st) => st switch
        {
            UpdateState.UpToDate => "已是最新",
            UpdateState.UpdateAvailable => "可更新",
            UpdateState.Error => "检测出错",
            _ => "未知"
        };

        private void FillCore(CoreUpdateInfo it)
        {
            CoreRemoteText.Text = "远程地址：" + (string.IsNullOrEmpty(it.RemoteUrl) ? "—" : it.RemoteUrl);
            CoreCurrentText.Text = "当前版本：" + (string.IsNullOrEmpty(it.CurrentVersion) ? "未知" : it.CurrentVersion);
            CoreLatestText.Text = "  最新版本：" + (string.IsNullOrEmpty(it.LatestVersion) ? "—" : it.LatestVersion);

            // 这里是内核卡片 —— 传进来的必定是内核信息，不必再问一次「是不是内核」
            // （那个 IsCore 判断正是拆模型要去掉的东西）
            string msg = it.Message;
            if (!string.IsNullOrEmpty(it.LatestMessage))
                msg += "  ·  " + it.LatestMessage;
            CoreMsgText.Text = msg;

            switch (it.State)
            {
                case UpdateState.UpdateAvailable:
                    CoreBadge.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x2E));
                    CoreBadgeText.Text = "可更新";
                    CoreUpdateBtn.IsEnabled = true;
                    break;
                case UpdateState.UpToDate:
                    CoreBadge.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                    CoreBadgeText.Text = "已是最新";
                    CoreUpdateBtn.IsEnabled = false;
                    break;
                case UpdateState.Error:
                    CoreBadge.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F));
                    CoreBadgeText.Text = "检测出错";
                    CoreUpdateBtn.IsEnabled = false;
                    break;
                default:
                    CoreBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                    CoreBadgeText.Text = "未检测";
                    CoreUpdateBtn.IsEnabled = false;
                    break;
            }
        }

        private async void CoreUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy || coreItem == null) return;

            // 前置检查：服务运行中时文件被占用，更新可能失败
            // ⚠ 只看"我们自己的进程"，**不看端口** —— 端口上那一位可能是别人的程序，
            //   认成自己就会在下面的「是否先停止服务」里把对方杀掉。
            bool svcRunning = IsOurForgeRunning();
            if (svcRunning)
            {
                var r0 = MessageBox.Show(
                    "检测到 Forge Neo 服务正在运行。\n\n" +
                    "更新内核需要写入源码文件，建议先停止服务，否则可能更新失败。\n\n" +
                    "是否先停止服务再更新？",
                    "服务运行中", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r0 == MessageBoxResult.Cancel) return;
                if (r0 == MessageBoxResult.Yes)
                {
                    StopForge("更新内核前停止");
                    await Task.Delay(1500);
                }
            }

            string warn = coreItem.State == UpdateState.UpdateAvailable
                ? $"确定要把 Forge Neo 内核更新到最新（{coreItem.LatestVersion}）吗？\n\n" +
                  "• 从上游 Haoming02/sd-webui-forge-classic 的 neo 分支拉取最新代码\n" +
                  "• 仅覆盖上游仓库跟踪的文件（源码、内置模块等）\n" +
                  "• config.json / 模型 / 插件 / venv / 启动器 exe 不受影响\n" +
                  "• 若你手动改过内核源码，改动会被上游版本覆盖\n" +
                  "• 更新后需重启启动器与服务生效"
                : "当前未检测到更新。仍要强制从上游同步一次吗？";

            var rs = MessageBox.Show(warn, "更新内核确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (rs != MessageBoxResult.OK) return;

            versionBusy = true;
            CoreUpdateBtn.IsEnabled = false;
            CheckUpdateBtn.IsEnabled = false;
            VerStatusText.Text = "正在更新内核 ...";
            AddLog("开始更新 Forge Neo 内核 ...", WarnBrush);

            bool updated = false;
            try
            {
                var (ok, msg) = await CoreUpdater.UpdateAsync();
                updated = ok;
                AddLog((ok ? "内核更新完成：" : "内核更新失败：") + msg, ok ? SuccessBrush : ErrorBrush);
                Dispatcher.Invoke(() => VerStatusText.Text = (ok ? "内核已更新：" : "内核更新失败：") + msg);
            }
            catch (Exception ex)
            {
                AddLog("内核更新异常：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    CheckUpdateBtn.IsEnabled = true;

                    // ⚠ 只刷新**内核这一项**，而且是本地只读的（不联网）。
                    //   老写法是收尾再跑一次完整检测 —— 那要打 GitHub 的 raw 文件 + API
                    //   + 每个插件一条 git ls-remote，只为了把一行字改成"已是最新"。
                    //   只在成功时刷新：MarkUpdated 会直接断言"已是最新"。
                    if (updated && coreItem != null)
                    {
                        CoreUpdater.MarkUpdated(coreItem);
                        FillCore(coreItem);
                    }
                });
            }
        }

        // ================= 插件管理（v0.32 从「版本管理」独立成页）=================
        //
        //  ⚠ 这一节里所有"改完刷新"的动作都遵守同一条规矩：
        //    **只刷新受影响的那一项**（本地只读），不许顺手把全部插件重问一遍远程。
        //    老写法是收尾调一次「检测插件更新」—— 点一个插件的「更新」，
        //    会对几十个插件各发一次 git ls-remote，用户等半天只为看一行字变色。

        private void ExtCheck_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckExtsAsync();
        }

        /// <summary>
        /// 检测插件更新：先本地扫描，再逐个问远程（<c>git ls-remote</c>，只读不改本地仓库）。
        ///
        /// <para>这是**唯一**会发网络请求的插件动作。更新/安装完之后的界面刷新都不走它。</para>
        /// </summary>
        private async Task CheckExtsAsync()
        {
            if (versionBusy) return;
            versionBusy = true;
            BtnExtCheck.IsEnabled = false;
            UpdateAllBtn.IsEnabled = false;
            ExtStatusText.Text = "正在检测 ...";

            try
            {
                // 扫描本身也要起 git 进程（每个扩展几条），几十个扩展时不能放在 UI 线程上做
                var exts = await Task.Run(async () =>
                {
                    var scanned = ExtensionManager.Scan();
                    Dispatcher.Invoke(() =>
                    {
                        extItems = scanned;
                        extScannedOnce = true;
                        ApplyExtFilter();
                        ExtStatusText.Text = $"正在检测 {scanned.Count} 个插件 ...";
                    });
                    return await ExtensionManager.CheckAsync(scanned, msg =>
                    {
                        Dispatcher.Invoke(() => ExtStatusText.Text = msg);
                    });
                });

                Dispatcher.Invoke(() =>
                {
                    extItems = exts;
                    ApplyExtFilter();

                    int upd = exts.Count(x => x.State == UpdateState.UpdateAvailable);
                    UpdateAllBtn.IsEnabled = upd > 0;
                    ExtStatusText.Text = $"检测完成（{DateTime.Now:HH:mm:ss}）"
                                         + (upd > 0 ? $" · 有 {upd} 项可更新" : " · 全部已是最新");
                    AddLog($"插件检测完成：{exts.Count} 个"
                           + (upd > 0 ? $"，{upd} 项可更新。" : "，全部已是最新。"), InfoBrush);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ExtStatusText.Text = "检测失败：" + ex.Message);
                AddLog("插件检测失败：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    BtnExtCheck.IsEnabled = true;
                    UpdateAllBtn.IsEnabled = extItems.Any(x => x.State == UpdateState.UpdateAvailable);
                });
            }
        }

        /// <summary>搜索框：输入即筛（只改显示，不动 extItems 全集）</summary>
        private void ExtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            extFilter = ExtSearchBox.Text ?? "";
            ApplyExtFilter();
        }

        /// <summary>
        /// 本地重扫一遍插件目录 —— <b>不发任何网络请求</b>。
        /// 首次进页面、点「刷新列表」、装完插件（改走 <see cref="AddExtItem"/>）都会用到它。
        ///
        /// <para>返回扫到的列表，调用方自己决定要不要报个数（状态栏文案属于界面的事）。</para>
        /// </summary>
        private async Task<List<ExtensionInfo>> RescanExtensionsAsync()
        {
            var list = await Task.Run(() => ExtensionManager.Scan());
            Dispatcher.Invoke(() =>
            {
                extItems = list;
                extScannedOnce = true;
                ApplyExtFilter();
            });
            return list;
        }

        /// <summary>
        /// 刷新列表：只重扫本地目录，**不做网络检测**（那是「检测插件更新」的事）。
        /// 扫描每个扩展都要起几条 git 进程，所以放到线程池。
        /// </summary>
        private async void ExtRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;
            versionBusy = true;
            BtnExtRefresh.IsEnabled = false;
            ExtStatusText.Text = "正在刷新插件列表 ...";

            try
            {
                var list = await RescanExtensionsAsync();
                int localOnly = list.Count(x => !x.IsGit);
                Dispatcher.Invoke(() => ExtStatusText.Text =
                    $"列表已刷新（{list.Count} 个"
                    + (localOnly > 0 ? $"，含 {localOnly} 个本地目录" : "")
                    + "）；点「检测插件更新」查有无新版本");
                AddLog($"插件列表已刷新：{list.Count} 个。", InfoBrush);
            }
            catch (Exception ex)
            {
                AddLog("刷新插件列表失败：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() => BtnExtRefresh.IsEnabled = true);
            }
        }

        /// <summary>
        /// 装完之后把<b>这一项</b>加进列表（按目录名归位，跟扫描的顺序一致）。
        ///
        /// <para>⚠ 不做全量重扫：那会对每个扩展各起几条 git 进程，只为了多显示一行。
        /// 但它读的是 <see cref="ExtensionManager.ScanOne"/> —— 与全量扫描<b>同一个
        /// <c>ReadOne</c></b>，不是另写一份"简化的扫描"，否则刚装上的那一行
        /// 与刷新之后的它会长得不一样（禁用状态、来源文案都可能不同）。</para>
        /// </summary>
        private void AddExtItem(string name)
        {
            var one = ExtensionManager.ScanOne(
                ExtensionManager.ExtensionsDir, ExtensionManager.ConfigJsonPath, name);
            if (one == null) return;

            extItems.RemoveAll(x => string.Equals(x.Path, one.Path, StringComparison.OrdinalIgnoreCase));
            extItems.Add(one);
            extItems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            ApplyExtFilter();
        }

        /// <summary>从仓库地址安装一个新扩展</summary>
        private async void ExtInstall_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;

            string url = ExtInstallBox.Text ?? "";
            var (vok, name, reason) = ExtensionManager.ValidateInstallUrl(url);
            if (!vok)
            {
                AddLog("安装地址无效：" + reason, WarnBrush);
                MessageBox.Show(reason, "地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string dest = Path.Combine(ExtensionManager.ExtensionsDir, name);
            var rs = MessageBox.Show(
                "将从下面的地址克隆一个新插件：\n\n" +
                $"{url.Trim()}\n\n" +
                $"目录名：{name}\n" +
                $"目标位置：{dest}\n\n" +
                "装好后需要重启 Forge 才会加载它。是否继续？",
                "安装插件确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (rs != MessageBoxResult.OK) return;

            versionBusy = true;
            BtnExtInstall.IsEnabled = false;
            ExtStatusText.Text = $"正在安装：{name} ...";
            AddLog($"开始安装插件：{url.Trim()}", WarnBrush);

            bool installed = false;
            try
            {
                var (ok, msg) = await ExtensionManager.InstallAsync(url, ExtensionManager.ExtensionsDir);
                installed = ok;
                AddLog((ok ? "安装完成：" : "安装失败：") + msg, ok ? SuccessBrush : ErrorBrush);
                if (ok) ExtInstallBox.Text = "";
            }
            catch (Exception ex)
            {
                AddLog("安装异常：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    BtnExtInstall.IsEnabled = true;
                    // 只把刚装上的这一项加进列表（不是全量重扫）
                    if (installed) AddExtItem(name);
                    ExtStatusText.Text = installed
                        ? $"「{name}」已安装；重启 Forge 后生效"
                        : $"「{name}」安装失败，详见日志";
                });
            }
        }

        /// <summary>勾选 / 取消勾选 = 启用 / 禁用（两处都写，见 <see cref="ExtensionManager.SetEnabled"/>）</summary>
        private void ExtToggle_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;
            if (sender is not System.Windows.Controls.CheckBox chk) return;
            if (chk.Tag is not ExtensionInfo item) return;

            bool want = chk.IsChecked == true;
            if (item.Enabled == want) return;      // 与当前一致 —— 什么都不做

            var (ok, msg) = ExtensionManager.SetEnabled(item, want, ExtensionManager.ConfigJsonPath);

            if (ok)
            {
                item.Enabled = want;
                AddLog($"插件「{item.Name}」{(want ? "已启用" : "已禁用")}：{msg}。重启 Forge 后生效。",
                       SuccessBrush);
            }
            else
            {
                // 只成功了一半时（例如 disabled 文件写了、config.json 没写），
                // 磁盘与模型可能不一致 —— 把勾恢复成模型值并让用户去核对，
                // 而不是留一个"看着对、其实不对"的勾。
                chk.IsChecked = item.Enabled;
                AddLog($"插件「{item.Name}」{(want ? "启用" : "禁用")}未完全成功：{msg}"
                       + "　建议点「刷新列表」核对实际状态。", ErrorBrush);
            }
        }

        /// <summary>卸载：只读判断 → 用户确认 → 移入回收站。顺序不能反。</summary>
        private void ExtUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;
            if (sender is not System.Windows.Controls.Button b) return;
            if (b.Tag is not ExtensionInfo item) return;

            string extDir = ExtensionManager.ExtensionsDir;

            // ⚠ 先做**只读**判断（是不是 extensions 的直接子目录、是不是链接……），
            //   再问，最后才动手。不可逆的动作排在最后 —— 见 CanUninstall 的注释。
            var (can, reason) = ExtensionManager.CanUninstall(item, extDir);
            if (!can)
            {
                AddLog($"不能卸载「{item.Name}」：{reason}", ErrorBrush);
                MessageBox.Show($"不能卸载：{reason}", "无法卸载", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var rs = MessageBox.Show(
                $"确定要卸载插件「{item.Name}」吗？\n\n" +
                $"{item.Path}\n\n" +
                "• 整个目录会被移入回收站（不是永久删除）—— 误删可以从回收站还原\n" +
                "• 它自己装的依赖包不会被清理\n" +
                "• 需要重启 Forge 之后界面上的入口才会消失",
                "卸载插件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (rs != MessageBoxResult.OK) return;

            var (ok, msg) = ExtensionManager.Uninstall(item, extDir);
            if (ok)
            {
                extItems.Remove(item);
                ApplyExtFilter();
                AddLog($"插件「{item.Name}」{msg}。", SuccessBrush);
            }
            else
            {
                AddLog($"插件「{item.Name}」卸载失败：{msg}", ErrorBrush);
                MessageBox.Show(msg, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 刚更新完的那一项：本地只读地刷成"已是最新"，并重挂一次列表。
        ///
        /// <para>⚠ <b>只在更新确实成功之后调</b> —— <see cref="ExtensionManager.MarkUpdated"/>
        /// 会直接断言"已是最新"；更新失败时调它，界面就开始撒谎。</para>
        ///
        /// <para><b>这是本版核心诉求的落点</b>：点一个插件的「更新」，只刷新这一个，
        /// 不发任何网络请求。要重问远程是「检测插件更新」那个按钮的事。</para>
        /// </summary>
        private void RefreshOneExt(ExtensionInfo item)
        {
            ExtensionManager.MarkUpdated(item);
            // 列表项是普通 POCO（没实现 INotifyPropertyChanged），改字段不会自己重绘 ——
            // 重挂一次 ItemsSource 才是"刷新"（ApplyExtFilter 顺带把计数也更新了）。
            ApplyExtFilter();
        }

        private async void ExtUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;
            if (sender is not System.Windows.Controls.Button b) return;
            if (b.Tag is not ExtensionInfo item) return;

            if (!item.IsGit)
            {
                MessageBox.Show(
                    $"「{item.Name}」不是 git 管理的扩展，没有「远程最新提交」可以对齐。\n\n" +
                    "它是手工解压 / 拷贝进 extensions 目录的。若想换成能更新的版本：\n" +
                    "先卸载它，再用仓库地址重新安装。",
                    "无法更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var rs = MessageBox.Show(
                $"确定要更新插件「{item.Name}」吗？\n\n" +
                $"• 当前：{item.CurrentVersion}  →  最新：{item.LatestVersion}\n" +
                "• 使用 git 强制对齐到远程最新提交（本地未提交改动会被覆盖）\n" +
                "• 本启动器给扩展打的修正补丁同样会被覆盖 —— 下次启动会重新贴上",
                "更新插件确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (rs != MessageBoxResult.OK) return;

            versionBusy = true;
            BtnExtCheck.IsEnabled = false;
            UpdateAllBtn.IsEnabled = false;
            ExtStatusText.Text = $"正在更新：{item.Name} ...";
            AddLog($"开始更新插件：{item.Name} ...", WarnBrush);

            try
            {
                var (ok, msg) = await ExtensionManager.UpdateAsync(item);
                AddLog((ok ? $"插件 {item.Name} 更新完成：" : $"插件 {item.Name} 更新失败：") + msg,
                       ok ? SuccessBrush : ErrorBrush);
                // ⚠ 只有成功才刷新那一行：MarkUpdated 会直接断言"已是最新"，
                //   失败时调它等于让界面撒谎（用户会以为已经对齐了）。
                Dispatcher.Invoke(() =>
                {
                    if (ok) RefreshOneExt(item);
                    ExtStatusText.Text = $"「{item.Name}」" + (ok ? "更新完成" : "更新失败，详见日志");
                });
            }
            catch (Exception ex)
            {
                AddLog($"插件 {item.Name} 更新异常：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    BtnExtCheck.IsEnabled = true;
                    UpdateAllBtn.IsEnabled = extItems.Any(x => x.State == UpdateState.UpdateAvailable);
                });
            }
        }

        /// <summary>
        /// 一键更新全部<b>插件</b>。
        ///
        /// <para>⚠ 这一页上的「全部」= 全部插件。内核有自己的卡片和「更新内核」按钮
        /// （在【版本管理】页）—— 混在一起时，"全部"到底含不含内核只能靠猜。</para>
        /// </summary>
        private async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (versionBusy) return;

            // ⚠ 读 extItems（插件全集），**不是** ExtList.ItemsSource ——
            //   后者可能被搜索框过滤过，那样用户搜了两个字再点「一键更新全部」，
            //   就只会更新看得见的那几个，而按钮上写着「全部」。
            var updatable = extItems.Where(x => x.State == UpdateState.UpdateAvailable).ToList();
            if (updatable.Count == 0) return;

            // 前置检查：服务运行中会占用文件
            // ⚠ 只看"我们自己的进程"，**不看端口** —— 端口上那一位可能是别人的程序，
            //   认成自己就会在下面的「是否先停止服务」里把对方杀掉。
            bool svcRunning = IsOurForgeRunning();
            if (svcRunning)
            {
                var r0 = MessageBox.Show(
                    "检测到 Forge Neo 服务正在运行。\n\n" +
                    "更新会写入插件文件，建议先停止服务。\n\n是否先停止服务再更新？",
                    "服务运行中", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r0 == MessageBoxResult.Cancel) return;
                if (r0 == MessageBoxResult.Yes)
                {
                    StopForge("更新前停止");
                    await Task.Delay(1500);
                }
            }

            var rs = MessageBox.Show(
                $"将更新以下 {updatable.Count} 个插件：\n\n"
                + string.Join("\n", updatable.Select(x => "• " + x.Name)) +
                "\n\n插件使用 git 强制对齐到远程（本地未提交改动会被覆盖）。是否继续？",
                "一键更新全部插件", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (rs != MessageBoxResult.OK) return;

            versionBusy = true;
            BtnExtCheck.IsEnabled = false;
            UpdateAllBtn.IsEnabled = false;

            try
            {
                foreach (var it in updatable)
                {
                    ExtStatusText.Text = $"正在更新：{it.Name} ...";
                    AddLog($"【一键更新】{it.Name} ...", WarnBrush);
                    var (ok, msg) = await ExtensionManager.UpdateAsync(it);
                    AddLog((ok ? $"插件 {it.Name} 更新完成：" : $"插件 {it.Name} 更新失败：") + msg,
                           ok ? SuccessBrush : ErrorBrush);

                    // 每更新完一个就刷掉它自己那一行 —— **本地只读、不发网络请求**。
                    // 老写法是全部跑完再整体检测一次（对每个插件各发一条 git ls-remote），
                    // 于是"更新 3 个插件"实际会问远程 N+3 次。
                    if (ok)
                    {
                        var done = it;
                        Dispatcher.Invoke(() => RefreshOneExt(done));
                    }
                }

                // 更新会 git reset --hard 掉扩展目录里的改动 —— 包括我们给扩展打的补丁。
                // 补丁的重新应用在 Start_Click 里（拉起 Forge 之前），这里只是把话说清楚。
                AddLog("提示：被更新覆盖的扩展补丁，会在下次启动 Forge 之前自动重新贴上。", InfoBrush);
                Dispatcher.Invoke(() => ExtStatusText.Text = $"更新完成（{DateTime.Now:HH:mm:ss}）");
            }
            catch (Exception ex)
            {
                AddLog("一键更新异常：" + ex.Message, ErrorBrush);
            }
            finally
            {
                versionBusy = false;
                Dispatcher.Invoke(() =>
                {
                    BtnExtCheck.IsEnabled = true;
                    UpdateAllBtn.IsEnabled = extItems.Any(x => x.State == UpdateState.UpdateAvailable);
                });
            }
        }

        // ================= 模型管理页 =================

        /// <summary>
        /// 重扫模型目录。**纯本地只读**，不发网络请求。
        ///
        /// <para><b>两件事必须在 UI 线程先做完</b>：① 追加目录要经 <see cref="GetDirList"/> 取，
        /// 而它内部有一处 <c>AddLog</c> —— 从线程池调用就是跨线程碰控件；
        /// ② 取回来的是模型里那个 <c>List</c> 的引用，先拷一份再交给线程池，
        /// 否则扫描期间用户点一下「保存并应用」就是在枚举中改列表。</para>
        ///
        /// <para>扫描本身只枚举一层，但追加目录可能配在网络盘上 —— 那种目录一次
        /// <c>GetFiles</c> 也可能要几百毫秒，所以整体还是放线程池。</para>
        /// </summary>
        private async Task RescanModelsAsync()
        {
            string modelsDir = AppPaths.ModelsDir;

            // ① 在 UI 线程把追加目录取好（GetDirList 里有 AddLog，不能在线程池里调）
            var extras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in ModelCatalog.Known)
            {
                if (string.IsNullOrEmpty(k.Tag)) continue;
                extras[k.Tag] = new List<string>(GetDirList(k.Tag));   // ② 拷一份
            }

            var list = await Task.Run(() => ModelCatalog.Scan(
                modelsDir,
                tag => extras.TryGetValue(tag, out var v) ? v : new List<string>()));

            Dispatcher.Invoke(() =>
            {
                modelFolders = list;
                modelScannedOnce = true;
                ApplyModelFilter();
            });
        }

        /// <summary>
        /// 按关键字过滤，并把**计数**写进本页状态栏（计数文案只在这里拼，别处别自己拼）。
        ///
        /// <para>⚠ 每次都<b>重挂 <c>ItemsSource</c></b>：模型对象是普通 POCO、没实现
        /// <c>INotifyPropertyChanged</c>，换掉列表内容而不重挂的话界面不会变（**且不报错**）。</para>
        /// </summary>
        private void ApplyModelFilter(string prefix = "")
        {
            var shown = ModelCatalog.Filter(modelFolders, modelFilter);
            ModelList.ItemsSource = shown;

            string count = modelFolders.Count == 0
                ? "没有可显示的目录"
                : (string.IsNullOrWhiteSpace(modelFilter)
                    ? $"共 {modelFolders.Count} 个目录"
                    : $"匹配 {shown.Count} / {modelFolders.Count} 个目录");

            ModelStatusText.Text = string.IsNullOrEmpty(prefix) ? count : prefix + " · " + count;
        }

        /// <summary>搜索框：输入即筛（只改显示，不动 modelFolders 全集）</summary>
        private void ModelSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            modelFilter = ModelSearchBox.Text ?? "";
            ApplyModelFilter();
        }

        /// <summary>重新扫描模型目录（纯本地只读；不发任何网络请求）</summary>
        private async void ModelRefresh_Click(object sender, RoutedEventArgs e)
        {
            BtnModelRefresh.IsEnabled = false;
            ModelStatusText.Text = "正在扫描 ...";
            try
            {
                await RescanModelsAsync();

                int missing = modelFolders.Count(f => !f.Exists);
                ApplyModelFilter($"扫描完成（{DateTime.Now:HH:mm:ss}）"
                                 + (missing > 0 ? $"，其中 {missing} 个目录不存在" : ""));
                AddLog($"模型目录扫描完成：{modelFolders.Count} 个。", InfoBrush);
            }
            catch (Exception ex)
            {
                ModelStatusText.Text = "扫描失败：" + ex.Message;
                AddLog("模型目录扫描失败：" + ex.Message, ErrorBrush);
            }
            finally { BtnModelRefresh.IsEnabled = true; }
        }

        /// <summary>打开某一张卡片对应的目录（Tag 里存的是**路径字符串**，不是卡片对象）</summary>
        private void ModelOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is string path && !string.IsNullOrWhiteSpace(path)) OpenFolder(path);
        }

        /// <summary>打开 models 根目录</summary>
        private void ModelOpenRoot_Click(object sender, RoutedEventArgs e)
            => OpenFolder(AppPaths.ModelsDir);

        // ================= 端口与进程归属 =================
        // 端口探测统一走 PortGuard（纯函数、可单独跑测试）。
        // 这里只放"本启动器自己记着的 PID"这类**有状态**的事。

        /// <summary>读 <c>forge.pid</c> 里记的进程号（写它的是 <see cref="WritePid"/>）。读不到返回 -1。</summary>
        private static int ReadPid()
        {
            try
            {
                var f = Path.Combine(NeoToolDir, "forge.pid");
                if (!File.Exists(f)) return -1;
                return int.TryParse(File.ReadAllText(f).Trim(), out var pid) ? pid : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 本启动器<b>自己启动的</b> Forge 还在跑吗？
        ///
        /// <para>⚠ 判据里<b>不含端口</b>。端口上坐着谁，与"我们有没有在跑"是两件不同的事 ——
        /// 原先这里写着 <c>|| GetPortPid() &gt; 0</c>，于是别人占着 7860 时：
        /// 更新内核 / 装 PyTorch 前会弹「服务正在运行」，选「是」就把<b>别人的进程</b>杀掉；
        /// 关窗口时也会平白多问一次。<b>判据兼职，错在这。</b></para>
        /// </summary>
        private bool IsOurForgeRunning()
        {
            // ① 我们自己起的子进程 —— 最直接的证据
            if (proc != null && !proc.HasExited) return true;

            // ② forge.pid 里记的那个（上次会话留下的）。
            //    PID 会被系统复用，所以既要"活着"也要通过身份判据，
            //    不能只看"这个号有人用"。
            int pid = ReadPid();
            if (pid > 0 && PortGuard.IsAlive(pid) && PortGuard.LooksLikeOurForge(pid, NeoToolDir))
                return true;

            // ③ 没有记录时（forge.pid 被删过、或服务是用户自己用 webui.bat 起的），
            //    看配置端口上坐的是不是本包的 Forge
            if (advOpts.TryPortArg(out var p) && PortGuard.Inspect(p, null, NeoToolDir).IsOurs)
                return true;

            return false;
        }

        // ================= 进程树终止 =================
        private static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p != null) p.WaitForExit(5000);
            }
            catch { }
        }

        // ================= PID 文件 =================
        private static void WritePid(int pid)
        {
            try { File.WriteAllText(Path.Combine(NeoToolDir, "forge.pid"), pid.ToString()); } catch { }
        }

        // ================= 状态刷新线程 =================
        private void StartMonitor()
        {
            monitorCts = new CancellationTokenSource();
            var token = monitorCts.Token;
            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            bool running = proc != null && !proc.HasExited;
                            // 查的是**本次实际使用的那个端口**（可能是自动切换过的），
                            // 而不是写死的 7860 —— 否则自动换端口后就再也探测不到就绪了
                            int pidV = PortGuard.GetPortPid(currentPort);

                            if (running)
                            {
                                if (pidV > 0)
                                {
                                    portEverUp = true;
                                    UpdateStatus("运行中", $"PID {pidV} · {WebUiUrl}");
                                    if (!autoOpened)
                                    {
                                        autoOpened = true;
                                        // 开不开由「启动行为」那一栏的开关说了算。
                                        // WebUI 自己那份已在 ApplyChildEnv 里被抑制，这里不会被重复开。
                                        if (advOpts.AutoOpenBrowser)
                                        {
                                            AddLog("服务就绪，正在打开界面...", SuccessBrush);
                                            OpenBrowser();
                                        }
                                        else
                                        {
                                            AddLog($"服务已就绪：{WebUiUrl}（已关闭自动打开，可点【打开界面】）", InfoBrush);
                                        }
                                    }
                                    EndMainBusy("服务已就绪");
                                }
                                else
                                {
                                    // 首次启动可能正在装依赖（几分钟到几十分钟）。
                                    // 状态行跟着进度条上的阶段走 —— 只说"正在加载模型"会让人以为卡住了。
                                    UpdateStatus("启动中", mainBusy && mainPhase.Length > 0
                                        ? mainPhase + "，请稍候..."
                                        : "正在加载模型，请稍候...");
                                }
                            }
                            else
                            {
                                // 进程没了。只处理一次 —— 否则这段每秒都会重写进度条与日志。
                                if (!childExitNoted)
                                {
                                    childExitNoted = true;
                                    HandleChildExit();
                                }

                                // 占用者身份只在"端口换人了"时重算一次：
                                // Describe() 要读进程路径，每秒重算纯属浪费。
                                if (pidV != idlePid)
                                {
                                    idlePid = pidV;
                                    var op = pidV > 0
                                        ? PortGuard.Classify(pidV, new[] { ReadPid() }, NeoToolDir)
                                        : null;
                                    idleOwnerDesc = op?.OwnerDesc ?? "";
                                    idleOwnerIsOurs = op?.IsOurs == true;
                                }

                                if (pidV > 0)
                                {
                                    // ⚠ 只有确认是本包的 Forge 才"邀请清理"。
                                    //   别人占着端口时写"点击【终止进程】清理"，
                                    //   等于诱导用户误杀无关进程。
                                    UpdateStatus("已停止", idleOwnerIsOurs
                                        ? $"端口 {currentPort} 仍被本包 Forge (PID {pidV}) 占用，可点【终止进程】清理"
                                        : $"端口 {currentPort} 被其他程序占用：{idleOwnerDesc}");
                                }
                                else
                                {
                                    UpdateStatus("已停止", "");
                                }
                            }
                        });
                    }
                    catch { }
                    Thread.Sleep(1000);
                }
            }, token);
        }

        // ================= 状态更新 =================
        private void UpdateStatus(string state, string detail)
        {
            StateText.Text = state;
            StateDetail.Text = detail;
            switch (state)
            {
                case "运行中":
                    StateDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                    StateText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                    break;
                case "启动中":
                    StateDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                    StateText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                    break;
                default:
                    StateDot.Fill = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                    StateText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                    break;
            }
        }

        // =====================================================================
        //  顶部进度区：两种互斥的显示模式
        //
        //   · 填充模式 —— 日志里出现了 tqdm 的**真实百分比**（采样步数 / 加载进度）
        //   · 滑动模式 —— 只知道"在动"、没有百分比（首次启动装依赖就是这一段）
        //
        //  为什么装依赖阶段不给百分比：uv 不报总进度。能诚实给出的是**包计数** ——
        //  「Resolved 13 packages」是 uv 自己给的分母，「Downloaded <pkg>」有几次
        //  就是分子，再累加它自己报的体积。所以这里写「包 6/13 · 已下载 2.1 GB」，
        //  而不是编一个 37% —— 编出来的数字在卡住时比没有更糟。
        // =====================================================================

        private DispatcherTimer? mainTick;
        private DateTime mainBusySince;
        private bool mainBusy;

        /// <summary>
        /// 当前的填充比例（0~100）—— <b>这才是模型</b>，像素宽度只是它在当下轨道宽度上的投影
        /// （见 <see cref="PlotMainFill"/>）。只存像素是错的：轨道宽度会变。
        /// </summary>
        private double mainFillRatio;

        /// <summary>正在被日志里的真实百分比驱动（滑动块让位给填充条）</summary>
        private bool mainHasPct;

        /// <summary>
        /// <b>启动这一轮</b>已经收尾（服务就绪 / 进程退出）—— 一次启动只收尾一次。
        ///
        /// <para>⚠ 不能拿 <see cref="mainBusy"/> 当这个判据：真实百分比一来就进了百分比模式，
        /// 而"服务就绪"是之后才发生的；<see cref="EndMainBusy"/> 开头那句
        /// <c>if (!mainBusy) return;</c> 会把收尾整个吞掉 —— 状态行已经写着「运行中」，
        /// 进度条却永远停在上一个百分比上（v0.18 用户看到的就是这个）。</para>
        ///
        /// <para>⚠ 它<b>只</b>说明"启动那一段干完了"，<b>不代表"以后不会再有进度"</b>。
        /// 服务起来之后 Forge 每跑一张图都会重开一条 tqdm —— 那些行属于<b>作业期</b>
        /// （见 <see cref="BeginJob"/>）。v0.18 把这两件事混成一个标志，在解析处写成
        /// <c>if (mainSettled) return;</c>，顺手把跑图的进度全吞了。</para>
        /// </summary>
        private bool mainSettled;

        /// <summary>当前有一次作业（一次出图）正在被这条进度条跟踪</summary>
        private bool jobActive;

        /// <summary>作业起点 —— 收尾时用它算「本次用时」</summary>
        private DateTime jobStartAt;

        private string mainPhase = "";
        private long mainBytes;
        private int mainResolved;                                  // uv 报的待装包总数
        private readonly HashSet<string> dlSeen = new HashSet<string>();      // 见过下载动作的包
        private readonly HashSet<string> dlDone = new HashSet<string>();      // 下载完成的包
        private string lastChildError = "";                        // 子进程最后的报错行

        // uv / pip 装依赖时那几行 —— 只用来喂进度计数，日志照样原样写进去
        private static readonly System.Text.RegularExpressions.Regex RxResolved =
            new System.Text.RegularExpressions.Regex(@"^\s*Resolved\s+(\d+)\s+packages?",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// <c>Downloading &lt;文件&gt; (&lt;体积&gt;)</c> —— uv 与 pip <b>共用</b>这一条。
        ///
        /// <para>⚠ <b>体积单位必须同时认大小写，且要认 <c>bytes</c></b>。
        /// 这条正则原先是照着 <b>uv</b> 写的，只有 <c>[KMGT]?i?B</c>（大写 + 二进制单位）；
        /// 而 <b>pip 用的是小写 <c>kB</c></b>，并且极小包会写成 <c>512 bytes</c>。实测：</para>
        /// <list type="bullet">
        ///   <item><c>Downloading onnxruntime-...whl (14.3 MB)</c> → 老写法能认</item>
        ///   <item><c>Downloading cowsay-6.1-py3-none-any.whl (25 kB)</c> → <b>老写法认不出</b>
        ///         （<c>[KMGT]</c> 不含小写 k）</item>
        /// </list>
        /// <para>pip 的体积分档是 <c>format_size()</c>：<c>&gt;1e6 → MB</c>、<c>&gt;1e4 → kB</c>、
        /// <c>&gt;1e3 → x.x kB</c>、否则 <c>N bytes</c> —— 所以小写 k 与 bytes 都是常规情况，
        /// 不是边角。单位统一在 <see cref="ParseBytes"/> 里 <c>ToUpperInvariant</c> 后换算。</para>
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex RxDownloading =
            new System.Text.RegularExpressions.Regex(@"^\s*Downloading\s+(\S+)\s*\(([\d.]+)\s*([KMGTkmgt]?i?B|bytes)\)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxDownloaded =
            new System.Text.RegularExpressions.Regex(@"^\s*Downloaded\s+(\S+?)(?:\s*\(([\d.]+)\s*([KMGTkmgt]?i?B|bytes)\))?\s*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxPrepared =
            new System.Text.RegularExpressions.Regex(@"^\s*Prepared\s+\d+\s+packages?",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxInstalled =
            new System.Text.RegularExpressions.Regex(@"^\s*Installed\s+\d+\s+packages?",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // ---- pip 的对应版本 ----
        // 上面那几条是照着 **uv** 的措辞写的（Resolved / Prepared / Installed）。
        // 但扩展自带的 install.py 走的是**真 pip**（`sys.executable -m pip`），
        // 它的措辞完全不同 —— 不补这两条，pip 装依赖期间状态栏会一直停在上一句话上。
        //   pip: "Installing collected packages: foo, bar"  ↔  uv: "Prepared 2 packages"
        //   pip: "Successfully installed foo-1.0 bar-2.0"   ↔  uv: "Installed 2 packages"
        // 注：pip 没有 uv "Resolved N packages" 的等价输出（它是逐行 "Collecting X"），
        // 所以「正在解析依赖」这一段对 pip 不适用，直接从「正在下载依赖」开始 —— 不改。
        private static readonly System.Text.RegularExpressions.Regex RxPipInstalling =
            new System.Text.RegularExpressions.Regex(@"^\s*Installing collected packages:",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxPipInstalled =
            new System.Text.RegularExpressions.Regex(@"^\s*Successfully installed\s+\S",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>进入「在动」状态：滑动条 + 每秒刷新已用时间。必须在 UI 线程调用。</summary>
        private void BeginMainBusy(string phase)
        {
            mainBusy = true;
            mainSettled = false;
            mainHasPct = false;
            jobActive = false;                      // 新的一次启动，上一轮的作业跟着作废
            mainBusySince = DateTime.Now;
            mainPhase = phase;
            mainBytes = 0;
            mainResolved = 0;
            lastChildError = "";
            dlSeen.Clear();
            dlDone.Clear();

            SetMainFill(0);                         // 收掉上一轮留下的填充
            SetMainMarquee(true);
            RefreshMainBarText();

            EnsureMainTick();
            mainTick!.Start();
        }

        /// <summary>
        /// 「没有百分比」阶段的那条每秒计时器（走秒用）。
        /// 建一次就留着 —— 启动期和作业期都用它，谁需要谁 <c>Start()</c>。
        /// </summary>
        private void EnsureMainTick()
        {
            if (mainTick != null) return;
            mainTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            // 百分比模式由 tqdm 自己刷新（每秒好几条），这条计时器别去抢 ——
            // 它只在「没有百分比」的阶段负责走秒
            mainTick.Tick += (s, e) => { if (mainBusy && !mainHasPct) RefreshMainBarText(); };
        }

        /// <summary>
        /// 有真实百分比了：滑动块让位给填充条。
        ///
        /// <para>⚠ 这只是<b>显示模式</b>的切换，不代表这一轮启动结束。曾经这里连带把
        /// <c>mainBusy</c> 也清掉、把计时器也停了，于是后一步「服务就绪」的收尾被
        /// <see cref="EndMainBusy"/> 开头的判据吞掉，进度条就停在了那个百分比上。</para>
        /// </summary>
        private void EnterPercentMode()
        {
            mainHasPct = true;
            SetMainMarquee(false);
        }

        /// <summary>
        /// 百分比阶段结束（进到"没有百分比"的阶段了，比如下载完转安装）：
        /// 填充收掉，滑动块回来，文字切回阶段名 —— 比冻在上一个百分比上诚实。
        /// </summary>
        private void LeavePercentMode()
        {
            if (mainHasPct)
            {
                mainHasPct = false;
                SetMainFill(0);
                SetMainMarquee(true);
            }
            RefreshMainBarText();
        }

        /// <summary>
        /// 收尾：滑动条收掉、进度条打到满 —— 这一轮活干完了。
        /// <paramref name="text"/> 会留在进度文字上（如「服务已就绪」）。
        ///
        /// <para>幂等（靠 <see cref="mainSettled"/>）：监控循环每秒都会走到这里，
        /// 不收住的话「已用」会一秒一秒往上涨。</para>
        /// </summary>
        private void EndMainBusy(string text)
        {
            if (mainSettled) return;
            mainSettled = true;

            mainBusy = false;
            mainHasPct = false;
            mainTick?.Stop();
            SetMainMarquee(false);

            SetMainFill(100);
            if (!string.IsNullOrEmpty(text)) ProgressText.Text = text;
            ProgressDetail.Text = "已用 " + HumanElapsed(DateTime.Now - mainBusySince);
        }

        // =====================================================================
        //  作业期：服务就绪之后的一次「出图」
        //
        //  为什么单独一套：`mainSettled` 只说明**启动那一轮**干完了，不代表
        //  "以后不会再有进度"。Forge 每跑一张图都会重开一条 tqdm，那些行本来就
        //  该显示在这条进度条上（「填充模式」当初就是为"采样步数"写的）。
        //
        //  三个信号全部是 Forge 自己打出来的原文，都核过源码（不是猜的字符串）：
        //    · `Loading Model: {...}`  sd_models.py:328 `print(...)`，加载模型开始
        //    · `Model loaded in ...`   sd_models.py:388 `print(...)`，加载结束
        //    · `Total progress: ...`   shared_total_tqdm.py（写 stdout）+
        //                              sd_samplers_common.py:433 每一步 update 一次
        //
        //  ⚠ 模型加载那 30~60 秒里**一条百分比都没有**（Forge 只有 INFO 日志），
        //    进度条会停在上一次的态上、看着像死了 —— 那正是 v0.23 用户截图那一刻。
        //    所以「Loading Model」这行是有用的：它把那段空窗标成一个"准备态"。
        // =====================================================================

        /// <summary>
        /// 起一段作业。**只该在服务已就绪之后调用** —— 启动期有 <see cref="BeginMainBusy"/> 那套，
        /// 两边不能互相抢（所以这里不碰 <see cref="mainSettled"/>）。
        /// </summary>
        /// <param name="phase">还没有百分比时显示的文字（"正在生成" / "正在加载模型"）</param>
        private void BeginJob(string phase)
        {
            jobActive = true;
            jobStartAt = DateTime.Now;

            mainBusy = true;
            mainHasPct = false;
            mainBusySince = jobStartAt;
            mainPhase = phase;
            // 装依赖那几个计数是上一段的残留，不清的话这一行的右侧会冒出
            // 「已下载 2.1 GB」—— 跑图跟下载没有半点关系
            mainBytes = 0;
            mainResolved = 0;
            dlSeen.Clear();
            dlDone.Clear();

            // 还没有百分比就**不能编一个**：用"滑动 + 走秒"表示在动（与装依赖那段同一手法）
            SetMainFill(0);
            SetMainMarquee(true);
            // 文字走与启动期同一条刷新路径：右侧**立刻**显示「已用 0s」。
            // 别清成空 —— 模型加载那 30~60 秒里一条百分比都没有，"右侧那个走秒"
            // 就是"它还在动"的唯一证据，空着反而像死了。
            RefreshMainBarText();
            EnsureMainTick();
            mainTick!.Start();
        }

        /// <summary>
        /// 作业收尾（进度到 100%）：条子打满 + 写明本次用时。
        ///
        /// <para>这里**不退回「服务已就绪」** —— "刚跑完一张、用了 12 秒"比"服务闲着"
        /// 信息量大，而"服务还活着"这件事上面那行状态已经写了。下一次作业会把它顶掉。</para>
        /// </summary>
        private void FinishJob()
        {
            jobActive = false;
            mainBusy = false;
            mainHasPct = false;
            mainTick?.Stop();
            SetMainMarquee(false);
            SetMainFill(100);
            ProgressText.Text = "生成完成";
            ProgressDetail.Text = "本次用时 " + HumanElapsed(DateTime.Now - jobStartAt);
        }

        /// <summary>
        /// 模型加载完了、这一轮却还没报过任何百分比 → 它不是一次出图
        /// （在 WebUI 里手动换模型走的也是同一句 print），退回「服务已就绪」。
        ///
        /// <para>这一步不能省：不收尾就会留下一条<b>永远在滑的假进度</b>。</para>
        /// </summary>
        private void EndJobPrep()
        {
            if (!jobActive || mainHasPct) return;      // 已经在报百分比了 = 真的是出图，别收
            jobActive = false;
            mainBusy = false;
            mainTick?.Stop();
            SetMainMarquee(false);
            SetMainFill(100);
            ProgressText.Text = "服务已就绪";
            ProgressDetail.Text = "";
        }

        /// <summary>
        /// 认一认这行日志是不是"Forge 在加载模型"。<b>只认服务就绪之后出现的</b>：
        /// 启动期也可能加载模型，那属于启动那一轮（<see cref="BeginMainBusy"/> 在管）。
        /// </summary>
        private void NoteModelLoadLine(string t)
        {
            if (t.StartsWith("Loading Model:", StringComparison.Ordinal))
            {
                if (!jobActive) BeginJob("正在加载模型");
                else { mainPhase = "正在加载模型"; ProgressText.Text = mainPhase; }
                return;
            }
            if (t.StartsWith("Model loaded in ", StringComparison.Ordinal)) EndJobPrep();
        }

        // ================= 进度条填充：比例 → 像素 =================
        //
        //  填充宽度必须**每次都拿当下的轨道宽度现算**，不能只在写文字那一刻算一次。
        //  理由：这一行的轨道宽度 = 总宽 − 右侧「已用 4m20s」那列文字的宽度，
        //  而**改文字正是同一段代码紧接着要做的事**。WPF 的布局是延后一轮的，
        //  于是量到的是改文字**之前**的宽度 —— 差的就是那截。
        //
        //  v0.18 用户截图（状态已是「服务已就绪」、条子只到 92%）：
        //    收尾那一刻右侧是「已下载 4.0 GB · 已用 4m20s」，随后被换成「已用 4m20s」，
        //    右侧窄了 → 轨道宽了 79px（显示尺度）→ 填充是按旧的窄轨道算的，于是永远差 8%。
        //    实测：轨道 1960px、填充 1802px、差 158px，与 79px×2（2 倍缩放）严丝合缝。
        //

        /// <summary>设定顶部进度条的填充比例（0~100）。像素宽度会在布局后自动跟上。</summary>
        private void SetMainFill(double percent)
        {
            mainFillRatio = percent;
            PlotMainFill();
        }

        /// <summary>把比例画到当前轨道上（轨道宽度为 0 时由 UiFx 给兜底）</summary>
        private void PlotMainFill()
        {
            if (ProgressFill == null || ProgressBack == null) return;
            ProgressFill.Width = UiFx.FillWidth(mainFillRatio, ProgressBack.ActualWidth);
        }

        /// <summary>
        /// 轨道宽度一变就按比例重画。
        ///
        /// <para>这一个钩子同时修掉三件事：① 同行文字变长变短改了轨道宽（用户报的那个）；
        /// ② 窗口被拉大拉小；③ 首帧还没量到宽度时用的兜底值。都归到同一句话上：
        /// <b>像素是比例在当下轨道上的投影</b>，轨道变了就重投影。</para>
        /// </summary>
        private void BarTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ReferenceEquals(sender, ProgressBack)) PlotMainFill();
            else if (ReferenceEquals(sender, DeployBarBack)) PlotDeployFill();
        }

        private void SetMainMarquee(bool on)
        {
            if (ProgressMarquee == null || ProgressClip == null) return;
            // 判据用 Visibility 而不是 IsVisible：IsVisible 还要求所有祖先可见，
            // 窗口还没显示时它恒为 false —— 那会让"停止滑动"这一步被静默跳过
            if (on == (ProgressMarquee.Visibility == Visibility.Visible)) return;

            if (on)
            {
                ProgressMarquee.Visibility = Visibility.Visible;
                // 轨道宽度首帧可能还是 0 —— 给个兜底，下一秒不会重算（滑动本身不需要精确终点）
                double w = ProgressClip.ActualWidth > 0 ? ProgressClip.ActualWidth : UiFx.FallbackTrackWidth;
                UiFx.StartMarquee(ProgressMarqueeShift, -120, w + 12, 1.6);
            }
            else
            {
                UiFx.StopMarquee(ProgressMarqueeShift);
                ProgressMarquee.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>刷新进度条上的文字（没有百分比的阶段，每秒来一次）</summary>
        private void RefreshMainBarText()
        {
            // 百分比模式由 tqdm 自己刷新（每秒好几条），这条计时器别去抢它的文字
            if (!mainBusy || mainHasPct) return;

            string head = mainPhase;
            int total = Math.Max(mainResolved, dlSeen.Count);
            // 包计数只在**下载**阶段有意义；进了安装阶段还挂着「包 3/13」会让人以为
            // 才装了 3 个 —— 下载完成后这个比例就不再是进度的度量了
            if (mainPhase == "正在下载依赖" && dlSeen.Count > 0 && total > 0)
                head += $" · 包 {dlDone.Count}/{total}";

            ProgressText.Text = head;

            var parts = new List<string>();
            if (mainBytes > 0) parts.Add("已下载 " + HumanBytes(mainBytes));
            parts.Add("已用 " + HumanElapsed(DateTime.Now - mainBusySince));
            ProgressDetail.Text = string.Join(" · ", parts);
        }

        /// <summary>
        /// 认一认这行日志是不是「装依赖」的进度，以及「服务就绪之后 Forge 在加载模型」。
        /// 只做计数与状态切换，不改日志内容、不写界面
        /// （调用点已在 UI 线程的 Dispatcher 块里）。
        /// </summary>
        private void NoteBusyLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 子进程的报错行 —— 留一句，万一启动失败要能一句话说清原因
            string t = text.Trim();
            if (t.StartsWith("Traceback (most recent call last)") ||
                System.Text.RegularExpressions.Regex.IsMatch(t, @"^\w*(Error|Exception):\s*\S"))
            {
                if (lastChildError.Length == 0 || !t.Contains("Traceback"))
                    lastChildError = t;
            }

            // 作业期的信号（只在服务就绪后才认；启动期那一轮的模型加载归 BeginMainBusy 管）
            if (mainSettled) NoteModelLoadLine(t);

            if (!mainBusy) return;

            var m = RxResolved.Match(text);
            if (m.Success)
            {
                mainResolved = int.Parse(m.Groups[1].Value);
                mainPhase = "正在解析依赖";
                RefreshMainBarText();
                return;
            }

            m = RxDownloading.Match(text);
            if (m.Success) { NotePackage(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, false); return; }

            m = RxDownloaded.Match(text);
            if (m.Success) { NotePackage(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, true); return; }

            // 这两行意味着"下载那套百分比到此为止" —— 从百分比模式退回来，
            // 否则进度条会一直停在最后一个百分比上，跟状态行说的不是一回事
            // （uv 与 pip 两套措辞都认，见 RxPipInstalling 上方注释）
            if (RxPrepared.IsMatch(text) || RxPipInstalling.IsMatch(text))
            { mainPhase = "正在安装依赖"; LeavePercentMode(); return; }
            if (RxInstalled.IsMatch(text) || RxPipInstalled.IsMatch(text))
            { mainPhase = "依赖已装好，正在启动"; LeavePercentMode(); return; }
        }

        private void NotePackage(string name, string size, string unit, bool finished)
        {
            if (dlSeen.Add(name))                       // 只在第一次见到这个包时累加体积，避免重复计
            {
                mainBytes += ParseBytes(size, unit);
            }
            if (finished) dlDone.Add(name);
            mainPhase = "正在下载依赖";
            RefreshMainBarText();
        }

        private static long ParseBytes(string size, string unit)
        {
            if (!double.TryParse(size, out double v)) return 0;
            switch ((unit ?? "").ToUpperInvariant())
            {
                // pip 的极小包会写成 "N bytes"（见 RxDownloading 注释）—— 按 1 字节算
                case "BYTES": return (long)v;
                case "KIB": case "KB": return (long)(v * 1024);
                case "MIB": case "MB": return (long)(v * 1048576);
                case "GIB": case "GB": return (long)(v * 1073741824);
                case "TIB": case "TB": return (long)(v * 1099511627776);
                default: return 0;
            }
        }

        private static string HumanBytes(long bytes)
        {
            double b = bytes;
            if (b >= 1073741824) return (b / 1073741824).ToString("F1") + " GB";
            if (b >= 1048576) return (b / 1048576).ToString("F0") + " MB";
            if (b >= 1024) return (b / 1024).ToString("F0") + " KB";
            return bytes + " B";
        }

        private static string HumanElapsed(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
            return $"{t.TotalSeconds:F0}s";
        }

        /// <summary>
        /// 子进程退出时的收尾（只跑一次）。
        ///
        /// <para>区分两种退出：**从没等到端口** = 启动就没起来（比如缺文件、import 报错），
        /// 这时候把子进程最后那句报错摆到进度条上 —— 否则界面只会回到「已停止」，
        /// 使用者完全不知道发生了什么（日志区里那串 traceback 他也未必会看）。</para>
        /// </summary>
        private void HandleChildExit()
        {
            if (proc == null) return;              // 监控模式（进程不是我们起的），不掺和
            bool crashed = !portEverUp;            // 端口从未打开过

            mainBusy = false;
            mainHasPct = false;
            mainSettled = true;
            jobActive = false;                     // 进程都没了，作业态跟着作废
            mainTick?.Stop();
            SetMainMarquee(false);

            if (crashed)
            {
                SetMainFill(0);
                ProgressText.Text = lastChildError.Length > 0 ? "启动失败" : "进程已退出";
                ProgressDetail.Text = lastChildError.Length > 0 ? Shorten(lastChildError, 58) : "";
                if (lastChildError.Length > 0)
                    AddLog("启动失败：" + lastChildError, ErrorBrush);
            }
            else
            {
                SetMainFill(100);
                ProgressText.Text = "服务已停止";
                ProgressDetail.Text = "";
            }
        }

        // ================= 进度解析 =================
        // tqdm 格式示例：
        //   Total progress:  39%|████▌ | 25/64 [00:10<00:08,  4.37it/s]
        //   Total progress: 100%|██████████| 32/32 [00:08<00:00,  3.89it/s]
        //   Total progress: 0it [00:00, ?it/s]              ← 无界形态，见下
        // 返回 true 表示这是 tqdm 进度行（只更新进度条，不写日志）；false 表示普通日志

        /// <summary>
        /// Forge 采样 tqdm 的<b>无界</b>形态：<c>Total progress: 0it [00:00, ?it/s]</c>。
        ///
        /// <para>它在 <c>job_count * sampling_steps</c> 还是 0 时出现（<c>shared_total_tqdm.py</c>
        /// 建这条 tqdm 的那一刻），没有 <c>%|bar|n/N</c>，所以走不到上面那条正则上。
        /// 而 <c>n == 0</c> 恰好是"一条新 tqdm 刚建起来"= <b>一次出图的第一个信号</b>。</para>
        ///
        /// <para>⚠ 只认 Forge 这个前缀：pip / uv 也有无界进度条，别把装依赖当成"正在跑图"。
        /// 实测收到的行就是这样开头的（行尾还会跟着一个 tqdm 的光标回退转义）。</para>
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex RxForgeTotalUnbounded =
            new System.Text.RegularExpressions.Regex(@"^\s*Total progress:\s*(\d+)it\s*\[",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private bool TryParseProgress(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // 匹配 "nn%|...| nn/nn [mm:ss<mm:ss,  x.xxit/s]" 或 "nn%|...| nn/nn"
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"(\d{1,3})%\s*\|[^|]*\|\s*(\d+)/(\d+)(?:\s+\[[^\]]*\])?");
            if (!m.Success) return TryParseUnboundedProgress(text);

            int pct = int.Parse(m.Groups[1].Value);
            int cur = int.Parse(m.Groups[2].Value);
            int total = int.Parse(m.Groups[3].Value);
            if (pct > 100) pct = 100;
            if (pct < 0) pct = 0;
            if (total > 0 && cur > total) cur = total;

            double ratio = total > 0 ? (double)cur / total : pct / 100.0;
            if (ratio > 1.0) ratio = 1.0;
            if (ratio < 0.0) ratio = 0.0;

            // 解析剩余时间和速率
            string detail = "";
            var tm = System.Text.RegularExpressions.Regex.Match(text,
                @"\[(\d+):(\d+)<(\d+):(\d+),\s*([\d.]+)(it/s|s/it)\]");
            if (tm.Success)
            {
                int rm = int.Parse(tm.Groups[3].Value);
                int rs = int.Parse(tm.Groups[4].Value);
                string rate = tm.Groups[5].Value + " " + tm.Groups[6].Value;
                detail = $"剩余 {rm}m{rs:00}s · {rate}";
            }

            // 更新界面（在 UI 线程）
            Dispatcher.Invoke(() =>
            {
                // 这条百分比属于哪一段？
                //   · 启动期（服务还没起来）—— 属于这一次启动，照旧
                //   · 服务已就绪之后 —— 属于**一次作业**：Forge 在跑图
                //
                // ⚠ v0.18 这里是 `if (mainSettled) return;`（"迟到的进度行作废"）。
                //   它修好了"服务就绪覆盖不掉上一个百分比"，却顺手把跑图的进度全吞了 ——
                //   服务起来之后进度条就永远冻在「服务已就绪」，用户以为坏了（v0.23 反馈）。
                //   真正该作废的只是"进程已经退出"之后的行，那由 HandleChildExit 清零。
                bool isJob = mainSettled;

                if (isJob)
                {
                    if (!jobActive)
                    {
                        // 上一张已经收尾；tqdm 收尾时会再补画一条 100%，
                        // 别让那条重复行把「生成完成」顶成"又开始生成了"
                        if (pct >= 100) return;
                        BeginJob("正在生成");
                    }
                }

                // 有真实百分比了 —— 滑动模式让位，交给下面这条填充进度
                EnterPercentMode();

                SetMainFill(ratio * 100);
                ProgressText.Text = isJob
                    ? $"正在生成 {pct}%  ({cur}/{total})"
                    : $"{pct}%  ({cur}/{total})";
                ProgressDetail.Text = detail;

                // 同一条进度顺手镜像到【一键部署】页 —— 装 torch 那几十分钟里，
                // 用户停在部署页也能看到真实百分比，不必切到控制台。
                // 跑图的进度**不镜像**：那是"装依赖"那条进度条，两件事。
                if (isJob)
                {
                    if (pct >= 100) FinishJob();
                }
                else
                {
                    MirrorPercentToDeploy(pct, detail);
                }
            });

            return true; // 命中进度行，不写入日志区
        }

        /// <summary>
        /// 无界进度行（<c>Total progress: 0it [..]</c>）的处理。
        ///
        /// <para>只有 <c>n == 0</c> 有意义：那是 Forge 刚建起这条 tqdm = 一次出图开始，
        /// 此刻它还没有总数，只能先给个"在动"的准备态。<c>n &gt; 0</c> 的是它
        /// <c>close()</c> 时的补画（实测会打出一条 <c>Total progress: 21it [..]</c>），
        /// 当成新作业的话刚跑完就被顶回"生成中"。</para>
        ///
        /// <para>无论认不认得出含义，都返回 true —— 它是进度行，不该写进日志区
        /// （以前那行会连着 tqdm 的光标转义一起堆在日志里）。</para>
        /// </summary>
        private bool TryParseUnboundedProgress(string text)
        {
            var u = RxForgeTotalUnbounded.Match(text);
            if (!u.Success) return false;

            if (int.Parse(u.Groups[1].Value) == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    // 启动期由 BeginMainBusy 那套管，这里绝不抢
                    if (mainSettled && !jobActive) BeginJob("正在生成");
                });
            }
            return true;
        }

        // ================= 首页 =================
        //  仿绘世的落地页：横幅 + 目录快捷入口 + 一键启动。
        //  这一页刻意是「只读」的 —— 不在这儿显示日志与进度，那些统统在【控制台】。
        //  所以凡是要产出大量输出的动作（启动、维护环境），都先把人送到控制台去。

        /// <summary>首页卡片数据。只在第一次进首页时组一次 —— 路径不随运行变化</summary>
        private List<HomeFolders.HomeFolderItem>? homeFolders;

        /// <summary>
        /// 进首页时刷新。
        ///
        /// 状态一律<b>从现实推导</b>（文件在不在、进程在不在跑、控制台那颗按钮能不能点），
        /// 不另记一套状态变量 —— 这样重开启动器、或在别处切过状态，回到首页显示的都是对的。
        /// </summary>
        private void RefreshHome()
        {
            if (HomeFolderList == null) return;

            if (homeFolders == null)
            {
                homeFolders = HomeFolders.Build();

                // 勾了「复用已有 A1111 模型目录」时模型其实放在那边 ——
                // 卡片不跟着改就是骗人（点开是空的，用户会以为模型丢了）
                if (advOpts.UseA1111Home && !string.IsNullOrWhiteSpace(advOpts.A1111Home))
                {
                    var models = homeFolders.FirstOrDefault(f => f.Key == HomeFolders.KeyModels);
                    if (models != null)
                    {
                        var home = advOpts.A1111Home.Trim();
                        var sub = Path.Combine(home, "models");
                        models.FullPath = Directory.Exists(sub) ? sub : home;
                        models.Sub = models.FullPath;
                    }
                }

                // 额外模型目录（--ckpt-dirs / --lora-dirs / --vae-dirs / --text-encoder-dirs）
                // 也提一句，免得用户以为模型只可能在一个地方
                int extra = advOpts.CkptDirs.Count + advOpts.LoraDirs.Count
                          + advOpts.VaeDirs.Count + advOpts.TextEncoderDirs.Count;
                if (extra > 0 && homeFolders.FirstOrDefault(f => f.Key == HomeFolders.KeyModels) is { } mm)
                    mm.Sub += $"　·　另加 {extra} 个目录";

                HomeFolderList.ItemsSource = homeFolders;
            }

            string env = DeployState.Inspect() switch
            {
                DeployStatus.Ready when AppPaths.HasTorch => "环境已就绪（依赖已安装）",
                DeployStatus.Ready => "环境已就绪（依赖待首次启动时安装）",
                DeployStatus.NeedsVenv => "尚未部署（缺虚拟环境）",
                _ => "包不完整（缺必需文件）"
            };

            HomeVerText.Text = $"启动器 {AppInfo.Short} · {AppInfo.Codename}　|　{env}　|　服务 {StateText.Text}";
            HomeRootText.Text = $"包根目录 {AppPaths.Root}（{AppPaths.RootSource}）";

            // 两颗按钮的状态跟控制台保持一致：那边是权威（由进程/状态推导），这边只是投影
            HomeOpenBtn.IsEnabled = OpenBtn.IsEnabled;
            HomeStartBtn.IsEnabled = StartBtn.IsEnabled;
        }

        /// <summary>首页卡片被点：用资源管理器打开对应目录</summary>
        private void HomeFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is string path && !string.IsNullOrWhiteSpace(path)) OpenFolder(path);
        }

        /// <summary>
        /// 用资源管理器打开一个目录。
        ///
        /// <para>目录不存在时<b>先建出来再打开</b>：五个输出目录要等第一次生成才会出现，
        /// 而「点了一下没反应」比「建了个空目录」难解释得多。真建不出来（权限、盘满）
        /// 才报错 —— 那时把原因说清楚。</para>
        /// </summary>
        private void OpenFolder(string path)
        {
            bool existed = Directory.Exists(path);
            if (!EnsureFolder(path, out string error))
            {
                AddLog($"打开目录失败：{path} —— {error}", ErrorBrush);
                return;
            }
            if (!existed) AddLog($"目录原本不存在，已创建：{path}", InfoBrush);

            try
            {
                // UseShellExecute=true 才是交给系统按「目录」去关联打开（即资源管理器）
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                AddLog($"已打开目录：{path}", InfoBrush);
            }
            catch (Exception ex)
            {
                AddLog($"打开目录失败：{path} —— {ex.Message}", ErrorBrush);
            }
        }

        /// <summary>
        /// 保证目录存在（不存在就建）。失败时 <paramref name="error"/> 里是原因，不留空话。
        ///
        /// <para>与「打开」分开，是为了能被测试直接调用 —— 测试里不该真的弹出资源管理器窗口。</para>
        /// </summary>
        private static bool EnsureFolder(string path, out string error)
        {
            error = "";
            try
            {
                if (Directory.Exists(path)) return true;
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>首页那颗【一键启动】：先把人送到控制台，再启动 —— 输出全在那边</summary>
        private void HomeStart_Click(object sender, RoutedEventArgs e)
        {
            GotoConsole();
            Start_Click(sender, e);
        }

        // ================= 日志 =================
        private void AddLog(string text, Brush color)
        {
            // 1) 空行 / 纯空白行：跳过（tqdm 用 \r 重绘会拆出空行，不能写成光秃秃的时间戳）
            if (string.IsNullOrWhiteSpace(text)) return;

            // 2) 进度行：只更新顶部进度条，不写入日志区（避免 tqdm 字符刷屏）
            if (TryParseProgress(text)) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    // 顺手认一认装依赖的进度（uv 的 Resolved/Downloading/Downloaded/Installed）——
                    // 放在同一个 Dispatcher 块里，计数只被 UI 线程碰，不用再加锁
                    NoteBusyLine(text);

                    string ts = DateTime.Now.ToString("HH:mm:ss");
                    Console.AppendText($"[{ts}] ");
                    Console.AppendText(text + "\n");
                    Console.ScrollToEnd();
                });
            }
            catch { }
        }

        private void AddLog(string text) => AddLog(text, DefaultBrush);

        // =====================================================================
        //  高级选项（A 性能 / B 服务与网络 / C 模型目录）
        //
        //  数据流：界面控件 → advOpts 对象 → 预览 / 启动参数。
        //  界面永远只是 advOpts 的投影，不各自记一份状态，
        //  这样预览的命令行与实际启动用的命令行不可能不一致。
        // =====================================================================

        /// <summary>载入界面期间为 true，用于屏蔽控件事件，避免把默认值写回模型</summary>
        private bool advLoading;

        /// <summary>从配置读取高级选项（启动时调用）</summary>
        private void LoadAdvancedOptions()
        {
            var loaded = AdvancedOptions.Load();
            advOpts.NoHashing = loaded.NoHashing;
            advOpts.Autotune = loaded.Autotune;
            advOpts.PinSharedMemory = loaded.PinSharedMemory;
            advOpts.ExpandableSegments = loaded.ExpandableSegments;
            advOpts.AutoOpenBrowser = loaded.AutoOpenBrowser;
            advOpts.Api = loaded.Api;
            advOpts.Listen = loaded.Listen;
            advOpts.GradioAuth = loaded.GradioAuth;
            advOpts.Port = loaded.Port;
            advOpts.AutoSwitchPort = loaded.AutoSwitchPort;
            advOpts.MirrorSource = loaded.MirrorSource;
            advOpts.UseA1111Home = loaded.UseA1111Home;
            advOpts.A1111Home = loaded.A1111Home;
            advOpts.CkptDirs = new List<string>(loaded.CkptDirs);
            advOpts.LoraDirs = new List<string>(loaded.LoraDirs);
            advOpts.VaeDirs = new List<string>(loaded.VaeDirs);
            advOpts.TextEncoderDirs = new List<string>(loaded.TextEncoderDirs);
        }

        /// <summary>把 advOpts 刷进控件（每次进入页面都刷，保证与配置一致）</summary>
        private void LoadAdvancedIntoUi()
        {
            if (ArgPreview == null) return;

            advLoading = true;
            try
            {
                SwNoHashing.IsChecked = advOpts.NoHashing;
                SwAutotune.IsChecked = advOpts.Autotune;
                SwPinShared.IsChecked = advOpts.PinSharedMemory;
                SwExpandSeg.IsChecked = advOpts.ExpandableSegments;
                ReserveVramBox.Text = advOpts.ReserveVram;
                SwDisableSage.IsChecked = advOpts.DisableSage;
                SwDisableFlash.IsChecked = advOpts.DisableFlash;
                SwDisableXformers.IsChecked = advOpts.DisableXformers;

                SwAutoOpen.IsChecked = advOpts.AutoOpenBrowser;
                SwApi.IsChecked = advOpts.Api;
                SwListen.IsChecked = advOpts.Listen;
                AuthBox.Text = advOpts.GradioAuth;
                PortBox.Text = advOpts.Port;
                SwAutoSwitchPort.IsChecked = advOpts.AutoSwitchPort;
                SwInstallExtDeps.IsChecked = advOpts.InstallExtDeps;

                SwA1111Home.IsChecked = advOpts.UseA1111Home;
                A1111Box.Text = advOpts.A1111Home;

                FillDirList(CkptList, advOpts.CkptDirs);
                FillDirList(LoraList, advOpts.LoraDirs);
                FillDirList(VaeList, advOpts.VaeDirs);
                FillDirList(TextEncList, advOpts.TextEncoderDirs);
            }
            finally { advLoading = false; }

            ApplyTabVisibility();
            UpdateListenUi();
            UpdateA1111Ui();
            RefreshPreview();
        }

        private static void FillDirList(System.Windows.Controls.ListBox list, List<string> dirs)
        {
            if (list == null) return;
            list.Items.Clear();
            foreach (var d in dirs) list.Items.Add(d);
        }

        /// <summary>任一开关切换：回写模型 → 刷新依赖项 → 重绘预览</summary>
        private void AdvOption_Toggled(object sender, RoutedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            UpdateListenUi();
            UpdateA1111Ui();
            RefreshPreview();
        }

        private void AdvAuth_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            UpdateListenUi();
            RefreshPreview();
        }

        private void AdvA1111_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            RefreshPreview();
        }

        /// <summary>
        /// 预留显存输入变化。逐字符同步会顺手把「0.」「-」这类半成品也写进模型 ——
        /// 那是刻意的：预览要如实反映"现在填的是什么"，合法性由 ValidateFatal 提示，
        /// 而拼命令行时只有解析成正数的值才会真的发出去（AppendTo）。
        /// </summary>
        private void AdvReserveVram_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            RefreshPreview();
        }

        /// <summary>服务端口输入变化。同预留显存：半成品也照收，合法性由 ValidateFatal 说。</summary>
        private void AdvPort_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            RefreshPreview();
        }

        // ---- 下载源 ----

        /// <summary>把两档下载源填进下拉框，并选中当前档位</summary>
        private void FillSourceBox()
        {
            if (SourceBox == null) return;
            var opts = ForgeNeoLauncher.DownloadSource.Options();
            SourceBox.ItemsSource = opts;
            SourceBox.SelectedItem = opts.FirstOrDefault(o => o.Id == advOpts.MirrorSource) ?? opts[0];
        }

        private void AdvSource_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (advLoading) return;
            SyncFromUi();
            UpdateSourceHint();
            // PyTorch 环境页的命令行预览里也带 --index-url，得跟着换，
            // 否则会出现「预览写官方源、实际走镜像」这种不一致
            UpdateTorchPreview();

            // 部署页的下拉框是同一档位的另一个视图，同步过去免得两处显示打架
            if (DeploySourceBox != null && !deploySourceSyncing)
            {
                deploySourceSyncing = true;
                try
                {
                    DeploySourceBox.SelectedItem = DownloadSource.Options()
                        .FirstOrDefault(x => x.Id == advOpts.MirrorSource);
                }
                finally { deploySourceSyncing = false; }
                UpdateDeploySourceHint();
            }
        }

        /// <summary>
        /// 把抽象的「国内加速 / 官方源」落成具体域名显示出来。
        /// 镜像站哪天挂了，使用者能一眼看出当前正用哪个地址。
        /// </summary>
        private void UpdateSourceHint()
        {
            if (SourceHint == null) return;

            string src = advOpts.MirrorSource;
            var branch = (TorchBranchBox?.SelectedItem as TorchBranch)?.Id ?? "cu130";

            SourceHint.Text =
                $"PyPI 包　：{ForgeNeoLauncher.DownloadSource.PyPiIndex(src)}\n" +
                $"PyTorch　：{ForgeNeoLauncher.DownloadSource.TorchIndex(src, branch)}";

            if (SourceBox != null)
                SourceBox.ToolTip = ForgeNeoLauncher.DownloadSource.Detail(src);
        }

        /// <summary>A1111 目录输入区只在开关打开后才展开</summary>
        private void UpdateA1111Ui()
        {
            if (A1111Panel == null) return;
            A1111Panel.Visibility = (SwA1111Home != null && SwA1111Home.IsChecked == true)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>选一个 A1111 / 秋叶包安装根目录</summary>
        private void AdvA1111Browse_Click(object sender, RoutedEventArgs e)
        {
            // XAML 生成的字段在编译期被视为可空，这里先挡一道，后续即可放心直接访问
            if (A1111Box == null) return;

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 A1111 / 秋叶包安装根目录（其下应有 models 目录）",
                Multiselect = false
            };

            var cur = (A1111Box.Text ?? "").Trim();
            if (Directory.Exists(cur)) dlg.InitialDirectory = cur;

            if (dlg.ShowDialog(this) != true) return;
            string? picked = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(picked)) return;

            // 赋值即触发 TextChanged，同步与预览会在那里一并完成
            A1111Box.Text = picked.TrimEnd('\\', '/');
        }

        /// <summary>把界面上的勾选状态回写到模型</summary>
        private void SyncFromUi()
        {
            if (SwNoHashing == null) return;

            advOpts.NoHashing = SwNoHashing.IsChecked == true;
            advOpts.Autotune = SwAutotune.IsChecked == true;
            advOpts.PinSharedMemory = SwPinShared.IsChecked == true;
            advOpts.ExpandableSegments = SwExpandSeg.IsChecked == true;
            // 原样收下界面文本，合法与否交给 TryReserveVramArg / ValidateFatal 判
            advOpts.ReserveVram = ReserveVramBox?.Text ?? "";
            advOpts.DisableSage = SwDisableSage?.IsChecked == true;
            advOpts.DisableFlash = SwDisableFlash?.IsChecked == true;
            advOpts.DisableXformers = SwDisableXformers?.IsChecked == true;

            advOpts.AutoOpenBrowser = SwAutoOpen.IsChecked == true;
            advOpts.Api = SwApi.IsChecked == true;
            advOpts.Listen = SwListen.IsChecked == true;
            advOpts.GradioAuth = AuthBox.Text ?? "";
            // 同 ReserveVram：原样收下界面文本，合法与否交给 TryPortArg / ValidateFatal 判。
            // 收成 int 的话，"删光了准备重打"的中间态会被静默变成 0，界面与实际就对不上了。
            advOpts.Port = PortBox?.Text ?? "";
            advOpts.AutoSwitchPort = SwAutoSwitchPort == null || SwAutoSwitchPort.IsChecked == true;
            // 它不进 AppendTo —— 由 BuildFinalArgs() 裁决发不发 --skip-install
            advOpts.InstallExtDeps = SwInstallExtDeps == null || SwInstallExtDeps.IsChecked == true;
            advOpts.MirrorSource = (SourceBox?.SelectedItem as SourceOption)?.Id
                                   ?? ForgeNeoLauncher.DownloadSource.Cn;

            advOpts.UseA1111Home = SwA1111Home != null && SwA1111Home.IsChecked == true;
            advOpts.A1111Home = A1111Box?.Text ?? "";
        }

        /// <summary>认证输入框只在开启局域网访问后可编辑，并同步那条橙色警示</summary>
        private void UpdateListenUi()
        {
            if (AuthBox == null) return;

            bool listen = SwListen.IsChecked == true;
            AuthBox.IsEnabled = listen;
            if (AuthWarn != null)
            {
                AuthWarn.Visibility = listen && string.IsNullOrWhiteSpace(AuthBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        // ================= 选项卡 =================
        // 用 Visibility 切换，而不是每次重建内容：控件实例保持存活，
        // 切来切去不会丢掉用户已经填了一半的输入。
        private void AdvTab_Checked(object sender, RoutedEventArgs e)
        {
            ApplyTabVisibility();
        }

        /// <summary>
        /// 按当前选中的选项卡显示对应面板。
        /// 不只在 Checked 事件里调——XAML 解析期字段可能尚未赋值，
        /// 初始那一次 Checked 会被事件里的兜底挡掉，所以加载流程里还要主动调一次。
        /// </summary>
        private void ApplyTabVisibility()
        {
            if (PanePerf == null || PaneService == null || PaneModel == null) return;

            bool perf = TabPerf == null || TabPerf.IsChecked != false;
            bool svc = TabService != null && TabService.IsChecked == true;
            bool model = TabModel != null && TabModel.IsChecked == true;

            // 三个都没选中（理论上不会发生）时兜底到「性能」页，避免出现空白页
            if (!perf && !svc && !model) perf = true;

            PanePerf.Visibility = perf ? Visibility.Visible : Visibility.Collapsed;
            PaneService.Visibility = svc ? Visibility.Visible : Visibility.Collapsed;
            PaneModel.Visibility = model ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= 模型目录 =================
        private void AdvDirAdd_Click(object sender, RoutedEventArgs e)
        {
            string kind = (sender as System.Windows.Controls.Button)?.Tag as string ?? "";

            // OpenFolderDialog 是 .NET 8 新增的，不再依赖 WinForms 的 FolderBrowserDialog
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择额外的模型目录",
                Multiselect = false
            };

            // 从已配置的目录附近打开，省得每次从 C 盘翻
            var current = GetDirList(kind);
            if (current.Count > 0 && Directory.Exists(current[0])) dlg.InitialDirectory = current[0];

            if (dlg.ShowDialog(this) != true) return;
            string? picked = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(picked)) return;

            picked = picked.TrimEnd('\\', '/');
            var list = GetDirList(kind);

            if (list.Contains(picked, StringComparer.OrdinalIgnoreCase))
            {
                SetAdvMessage("该目录已在列表中。", false);
                return;
            }

            list.Add(picked);
            SyncDirListsToUi();
            RefreshPreview();
            SetAdvMessage($"已添加目录：{picked}", true);
        }

        private void AdvDirRemove_Click(object sender, RoutedEventArgs e)
        {
            string kind = (sender as System.Windows.Controls.Button)?.Tag as string ?? "";
            var box = GetDirBox(kind);
            if (box == null)
            {
                AddLog($"内部错误：未知的模型目录类型「{kind}」，本次操作已忽略。", WarnBrush);
                return;
            }

            int idx = box.SelectedIndex;
            if (idx < 0)
            {
                SetAdvMessage("请先在列表里选中要移除的目录。", false);
                return;
            }

            var list = GetDirList(kind);
            if (idx < list.Count) list.RemoveAt(idx);

            SyncDirListsToUi();
            RefreshPreview();
        }

        /// <summary>取某一类目录的模型列表引用（直接返回模型里的 List，改它就是改模型）</summary>
        private List<string> GetDirList(string kind)
        {
            switch (kind)
            {
                case "ckpt": return advOpts.CkptDirs;
                case "lora": return advOpts.LoraDirs;
                case "vae": return advOpts.VaeDirs;
                case "textenc": return advOpts.TextEncoderDirs;
                // ⚠ 未知 Tag 不许静默落回 ckpt：那会把「按钮 Tag 拼错」表现成
                //   「目录加进了 Checkpoint 列表」，界面上完全看不出异常。
                //   宁可什么都不做，并在日志里点名。
                default:
                    AddLog($"内部错误：未知的模型目录类型「{kind}」，本次操作已忽略。", WarnBrush);
                    return new List<string>();
            }
        }

        /// <summary>取某一类目录的列表控件（只用于读选中项；未知 Tag 返回 null）</summary>
        private System.Windows.Controls.ListBox? GetDirBox(string kind)
        {
            switch (kind)
            {
                case "ckpt": return CkptList;
                case "lora": return LoraList;
                case "vae": return VaeList;
                case "textenc": return TextEncList;
                default: return null;
            }
        }

        private void SyncDirListsToUi()
        {
            FillDirList(CkptList, advOpts.CkptDirs);
            FillDirList(LoraList, advOpts.LoraDirs);
            FillDirList(VaeList, advOpts.VaeDirs);
            FillDirList(TextEncList, advOpts.TextEncoderDirs);
        }

        // ================= 预览与保存 =================
        /// <summary>重算预览命令行，并把致命问题 / 提醒显示在下方</summary>
        private void RefreshPreview()
        {
            if (ArgPreview == null) return;

            var args = BuildFinalArgs();
            ArgPreview.Text = ArgLine.Build(args);
            if (ArgCountText != null) ArgCountText.Text = $"共 {args.Count} 个参数";

            var fatal = advOpts.ValidateFatal();
            var warn = advOpts.ValidateWarnings();

            if (fatal.Count > 0)
                SetAdvMessage("⛔ " + string.Join("\n⛔ ", fatal), false);
            else if (warn.Count > 0)
                SetAdvMessage("⚠ " + string.Join("\n⚠ ", warn), false);
            else
                SetAdvMessage("参数无冲突，可以启动。", true);
        }

        private void SetAdvMessage(string text, bool ok)
        {
            if (AdvMsg == null) return;
            AdvMsg.Text = text;
            AdvMsg.Foreground = ok
                ? (Brush)FindResource("StateDetail")
                : new SolidColorBrush(Color.FromRgb(0xE2, 0xA0, 0x3F));
        }

        private void AdvSave_Click(object sender, RoutedEventArgs e)
        {
            SyncFromUi();
            SyncDirListsToUi();

            var fatal = advOpts.ValidateFatal();
            if (fatal.Count > 0)
            {
                var rs = MessageBox.Show(
                    "以下问题尚未解决：\n\n" + string.Join("\n\n", fatal) +
                    "\n\n仍要保存吗？\n（保存后启动可能失败或存在安全风险）",
                    "高级选项",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (rs != MessageBoxResult.Yes) return;
            }

            advOpts.Save();
            LauncherConfig.Save();
            RefreshPreview();

            int extra = BuildFinalArgs().Count - BaseLaunchArgs.Length;
            AddLog($"高级选项已保存（附加 {extra} 个参数），下次启动生效。", SuccessBrush);
            SetAdvMessage("已保存到 launcher.cfg，下次启动生效。", true);
        }

        private void AdvReset_Click(object sender, RoutedEventArgs e)
        {
            var rs = MessageBox.Show(
                "把所有高级选项恢复为默认值？\n（模型目录列表也会被清空）",
                "恢复默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (rs != MessageBoxResult.Yes) return;

            advOpts.Reset();
            LoadAdvancedIntoUi();
            advOpts.Save();
            LauncherConfig.Save();

            AddLog("高级选项已恢复默认值。", WarnBrush);
            SetAdvMessage("已恢复默认值并保存。", true);
        }

        // =====================================================================
        //  PyTorch 环境（版本管理页第二个选项卡）
        //
        //  检测：调用 venv 里的 python 去 import torch 读真实版本。
        //  安装：pip install torch==X+cuY torchvision==... --index-url .../cuY
        //
        //  ⚠️ 界面上的版本清单不是凭空写的：每个组合都经过 pip --dry-run 实测可解析。
        //     尤其 torchaudio 在 2.12 之后官方停发 —— 2.12/2.13/2.14 的组合里没有它。
        // =====================================================================

        /// <summary>探测本机 venv 的 PyTorch 环境（纯本地，不发网络）</summary>
        private async Task ProbeTorchEnvAsync()
        {
            if (torchProbedOnce && torchEnv != null && torchEnv.HasTorch) return;
            torchProbedOnce = true;

            Dispatcher.Invoke(() =>
            {
                TorchEnvText.Text = "正在读取 ...";
                TorchBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                TorchBadgeText.Text = "检测中";
            });

            var env = await TorchManager.DetectAsync(PythonPath);
            Dispatcher.Invoke(() => ApplyTorchEnv(env));
        }

        private void ApplyTorchEnv(TorchEnvInfo env)
        {
            torchEnv = env;

            if (env.HasTorch)
            {
                TorchEnvText.Text = TorchManager.Describe(env);
                TorchPyText.Text = $"解释器：{PythonPath}   ·   Python {env.PythonVersion}";
                TorchBadge.Background = env.CudaAvailable
                    ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x2E));
                TorchBadgeText.Text = env.CudaAvailable ? "CUDA 可用" : "仅 CPU";
            }
            else
            {
                TorchEnvText.Text = string.IsNullOrEmpty(env.Error)
                    ? "未检测到 PyTorch。可从下方选择一个版本安装。"
                    : $"未检测到 PyTorch（{env.Error}）";
                TorchPyText.Text = $"解释器：{PythonPath}";
                TorchBadge.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F));
                TorchBadgeText.Text = "未安装";
            }

            RefreshTorchBranches();
        }

        /// <summary>重建支线下拉框，并尽量保持在用户当前选中的支线上</summary>
        private void RefreshTorchBranches()
        {
            var branches = TorchManager.GetBranches();
            TorchManager.MarkCurrent(torchEnv, branches);

            string keep = (TorchBranchBox.SelectedItem as TorchBranch)?.Id ?? "";
            // 没有保持项时，默认落到当前环境所在的支线（识别不出则第一条）
            if (keep.Length == 0) keep = torchEnv?.BranchId ?? "";
            if (keep.Length == 0) keep = branches[0].Id;

            TorchBranchBox.ItemsSource = branches;
            TorchBranchBox.SelectedItem = branches.FirstOrDefault(x => x.Id == keep) ?? branches[0];
        }

        private void TorchBranch_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RefreshTorchBuilds();
        }

        private void TorchBuild_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateTorchPreview();
        }

        private void ForceReinstall_Changed(object sender, RoutedEventArgs e)
        {
            UpdateTorchPreview();
        }

        /// <summary>填充该支线下的版本列表，并默认选中「当前环境所用的版本」</summary>
        private void RefreshTorchBuilds()
        {
            var br = TorchBranchBox.SelectedItem as TorchBranch;
            if (br == null) return;

            // 在这里一次性补齐 BranchId：BuildInstallArgs 依赖它拼 --index-url 和 wheel 后缀，
            // 漏了会拼出 torch==2.13.0+ 这种坏规格。不依赖调用方记得先设好。
            foreach (var b in br.Builds) b.BranchId = br.Id;

            TorchBuildBox.ItemsSource = br.Builds;

            // 优先选中当前正在用的版本，其次选带「当前环境所用」标注的，最后选最新的。
            // FirstOrDefault 可能返回 null（列表为空时），故用可空类型承接。
            TorchBuild? pick = br.Builds.FirstOrDefault(x => x.IsCurrent)
                               ?? br.Builds.FirstOrDefault(x => !string.IsNullOrEmpty(x.Note) && x.Note.Contains("当前"))
                               ?? br.Builds.FirstOrDefault();
            TorchBuildBox.SelectedItem = pick;

            UpdateTorchPreview();
        }

        private void TorchRefresh_Click(object sender, RoutedEventArgs e)
        {
            torchProbedOnce = false;
            torchEnv = null;
            _ = ProbeTorchEnvAsync();
        }

        /// <summary>把所选版本拼成安装命令显示出来——执行的就是这一条</summary>
        private void UpdateTorchPreview()
        {
            if (TorchCmdPreview == null) return;

            var build = TorchBuildBox.SelectedItem as TorchBuild;
            if (build == null)
            {
                TorchCmdPreview.Text = "（请先选择一个版本）";
                TorchInstallBtn.IsEnabled = false;
                if (TorchBuildHint != null) TorchBuildHint.Text = "";
                return;
            }
            build.BranchId = (TorchBranchBox.SelectedItem as TorchBranch)?.Id ?? build.BranchId;

            // 下拉框下方补一行完整说明（下拉框收起时只显示一行版本号，细节放这里）
            if (TorchBuildHint != null)
                TorchBuildHint.Text = $"{build.Subtitle} — 从{ForgeNeoLauncher.DownloadSource.Title(advOpts.MirrorSource)}安装。" +
                    (build.IsCurrent ? "与当前环境相同。" : "");

            bool force = ForceReinstallChk.IsChecked == true;
            TorchCmdPreview.Text = PythonPath + " " +
                TorchManager.BuildInstallCommandLine(build, force, advOpts.MirrorSource);

            TorchInstallBtn.IsEnabled = !torchInstalling;
            SetTorchMessage(build.IsCurrent
                ? "这与当前环境相同，如需修复可勾选「强制重装」。"
                : $"将把 PyTorch 环境切换到 {build.Title}。", build.IsCurrent);
        }

        private void SetTorchMessage(string text, bool neutral)
        {
            if (TorchMsg == null) return;
            TorchMsg.Text = text;
            TorchMsg.Foreground = neutral
                ? (Brush)FindResource("StateDetail")
                : new SolidColorBrush(Color.FromRgb(0xE2, 0xA0, 0x3F));
        }

        // ================= PyTorch 安装 =================
        private async void TorchInstall_Click(object sender, RoutedEventArgs e)
        {
            if (torchInstalling) return;

            var build = TorchBuildBox.SelectedItem as TorchBuild;
            if (build == null) return;
            build.BranchId = (TorchBranchBox.SelectedItem as TorchBranch)?.Id ?? build.BranchId;

            bool force = ForceReinstallChk.IsChecked == true;
            bool isDowngradeOrChange = !build.IsCurrent;

            // 服务运行中时装 torch 是灾难：正在被加载的 torch DLL 会被覆盖
            // ⚠ 只看"我们自己的进程"，**不看端口** —— 端口上那一位可能是别人的程序，
            //   认成自己就会在下面的「是否先停止服务」里把对方杀掉。
            bool svcRunning = IsOurForgeRunning();
            if (svcRunning)
            {
                var r0 = MessageBox.Show(
                    "检测到 Forge Neo 服务正在运行。\n\n" +
                    "替换 PyTorch 需要写入 venv 里正在被占用的 DLL，服务不停会导致安装失败或环境损坏。\n\n" +
                    "是否先停止服务再继续？",
                    "服务运行中", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r0 == MessageBoxResult.Cancel) return;
                if (r0 == MessageBoxResult.Yes)
                {
                    StopForge("安装 PyTorch 前停止");
                    await Task.Delay(1500);
                }
            }

            if (isDowngradeOrChange)
            {
                var r1 = MessageBox.Show(
                    $"即将把 PyTorch 环境切换为：\n\n{build.Title}\n{build.Subtitle}\n\n" +
                    "安装过程会下载约 2~3 GB 数据，视网速可能需要几分钟到数十分钟。\n" +
                    "期间请勿关闭启动器。\n\n确定继续？",
                    "安装 PyTorch", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (r1 != MessageBoxResult.OK) return;
            }

            torchInstalling = true;
            TorchInstallBtn.IsEnabled = false;
            TorchInstallBtn.Content = "安装中 ...";
            TorchRefreshBtn.IsEnabled = false;

            AddLog($"========== 开始安装 {build.Title} ==========", InfoBrush);
            AddLog(TorchManager.BuildInstallCommandLine(build, force, advOpts.MirrorSource), DefaultBrush);

            try
            {
                var (ok, msg) = await TorchManager.InstallAsync(PythonPath, build, force, advOpts.MirrorSource, line =>
                {
                    // pip 输出原样进日志区，出问题时用户能直接看到原因
                    AddLog(line, DefaultBrush);
                });

                AddLog(ok ? msg : "安装失败：" + msg, ok ? SuccessBrush : ErrorBrush);
                SetTorchMessage(msg, ok);

                if (ok)
                {
                    SetMenuActive(MenuVersion);
                    // 重新读一遍真实版本，确认装上了
                    torchProbedOnce = false;
                    await ProbeTorchEnvAsync();
                    SetTorchMessage($"{build.Title} 已安装。重启服务后生效。", true);
                }
            }
            catch (Exception ex)
            {
                AddLog("安装异常：" + ex.Message, ErrorBrush);
                SetTorchMessage("安装异常：" + ex.Message, false);
            }
            finally
            {
                torchInstalling = false;
                TorchInstallBtn.Content = "安装所选版本";
                TorchRefreshBtn.IsEnabled = true;
                UpdateTorchPreview();
            }
        }

        // ================= 关闭确认 =================
        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 同 svcRunning：判据不含端口，否则别人占着端口时会平白多问一次"是否停止服务"
            bool running = IsOurForgeRunning();
            if (running)
            {
                var rs = MessageBox.Show(
                    "后台服务仍在运行。是否同时停止服务并退出？\n\n是 = 停止服务并退出\n否 = 服务继续运行\n取消 = 返回",
                    "关闭确认",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (rs == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                else if (rs == MessageBoxResult.Yes)
                {
                    StopForge("窗口关闭");
                }
            }
            monitorCts?.Cancel();
        }
    }
}
