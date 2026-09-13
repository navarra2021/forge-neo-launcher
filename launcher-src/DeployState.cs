using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// <c>deploy.json</c> —— 部署产物的清单，放在包根目录。
    ///
    /// 它回答三个问题：
    /// <list type="number">
    ///   <item>这个包是在哪台机器、什么时候装好的（出问题时能对得上）</item>
    ///   <item>装的哪条 CUDA 支线、哪个 torch、用的哪个镜像档位（换机器时能照抄）</item>
    ///   <item>Python 是随包带的还是现场下载的（排查环境来源）</item>
    /// </list>
    ///
    /// <para><b>⚠ 这个文件绝对不能进发布包</b>：它记录了部署机器的绝对路径与时间戳，
    /// 随包发出去会让别人拿到一份「别人的部署记录」，
    /// 导致部署向导误判为「已部署」而跳过必要步骤。
    /// 打包脚本已把它列入排除清单（见 <c>pack.ps1</c>）。</para>
    /// </summary>
    internal sealed class DeployState
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

        /// <summary>部署时的包根目录（绝对路径）</summary>
        [JsonPropertyName("rootDir")] public string RootDir { get; set; } = "";

        /// <summary>部署完成时间（本地时间，ISO 8601）</summary>
        [JsonPropertyName("deployedAt")] public string DeployedAt { get; set; } = "";

        /// <summary>CUDA 支线，如 cu130</summary>
        [JsonPropertyName("cudaBranch")] public string CudaBranch { get; set; } = "";

        /// <summary>torch 版本，如 2.13.0</summary>
        [JsonPropertyName("torchVersion")] public string TorchVersion { get; set; } = "";

        /// <summary>venv 里解释器的版本，如 3.13.14</summary>
        [JsonPropertyName("pythonVersion")] public string PythonVersion { get; set; } = "";

        /// <summary>下载源档位：cn / official / custom</summary>
        [JsonPropertyName("mirrorProfile")] public string MirrorProfile { get; set; } = "";

        /// <summary>Python 来源：bundled（随包携带）/ downloaded（现场下载）</summary>
        [JsonPropertyName("runtimeSource")] public string RuntimeSource { get; set; } = "bundled";

        /// <summary>写入这份状态的启动器版本，便于回溯</summary>
        [JsonPropertyName("launcherVersion")] public string LauncherVersion { get; set; } = "";

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>读取；文件不存在或内容损坏都返回 null（调用方按「未部署」处理）</summary>
        public static DeployState? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<DeployState>(json);
            }
            catch
            {
                // 状态文件坏掉不该让启动器起不来 —— 当作没有，重新走一遍部署即可
                return null;
            }
        }

        /// <summary>写入；失败返回 false（不抛异常，调用方提示即可）</summary>
        public bool Save(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按当前实际环境采集一份状态</summary>
        public static DeployState Capture(string cudaBranch, string torchVersion,
                                          string mirrorProfile, string pythonVersion)
        {
            return new DeployState
            {
                Schema = 1,
                RootDir = AppPaths.Root,
                DeployedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                CudaBranch = cudaBranch ?? "",
                TorchVersion = torchVersion ?? "",
                PythonVersion = pythonVersion ?? "",
                MirrorProfile = mirrorProfile ?? "",
                RuntimeSource = AppPaths.FindBundledPython() != null ? "bundled" : "downloaded",
                LauncherVersion = AppInfo.Version
            };
        }

        /// <summary>
        /// 是否还需要走部署流程。
        ///
        /// 判据是「走一遍 <see cref="AppPaths.Inspect"/> 的必需项 + venv 是否存在」。
        /// v0.12 之前只看 `launch.py` 和 `venv` 两项，这有个漏洞：
        /// 随包 Python 或 uv 缺失时会被判成「已部署」，点一键启动才发现找不到解释器，
        /// 报出来的错（找不到 python.exe）跟真实原因（包不完整）对不上。
        /// </summary>
        public static bool NeedsDeploy() => Inspect() != DeployStatus.Ready;

        /// <summary>
        /// 检测包的就位状态 —— 界面据此决定「显示体检清单 / 亮一键部署按钮 / 还是提示重解压」。
        ///
        /// <para>三态而非布尔，因为「缺 venv」和「缺随包 Python」对使用者的含义完全不同：
        /// 前者点一下就能修，后者只能重新解压。</para>
        ///
        /// <para><b>判断顺序是有讲究的</b>：venv 一旦建好，随包运行时（runtime\python、
        /// runtime\uv）就不再是必需品 —— 它们的唯一用途就是建 venv。所以「有 venv、没 runtime」
        /// 属于正常形态（开发目录就是这样），必须先判 venv 再判运行时。</para>
        /// </summary>
        public static DeployStatus Inspect()
        {
            // 内核是跑起来的前提，缺了它什么都免谈
            if (!AppPaths.CoreOk) return DeployStatus.Broken;

            // venv 已建好 → 环境可用
            if (AppPaths.HasVenv) return DeployStatus.Ready;

            // venv 缺失：能不能建得起来，取决于随包运行时是否齐备
            return AppPaths.PrerequisitesOk ? DeployStatus.NeedsVenv : DeployStatus.Broken;
        }

        /// <summary>一行摘要，写日志用</summary>
        public string Summary()
        {
            if (string.IsNullOrEmpty(DeployedAt)) return "（无部署记录）";
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(CudaBranch)) parts.Add(CudaBranch);
            if (!string.IsNullOrEmpty(TorchVersion)) parts.Add("torch " + TorchVersion);
            if (!string.IsNullOrEmpty(PythonVersion)) parts.Add("Python " + PythonVersion);
            if (!string.IsNullOrEmpty(MirrorProfile)) parts.Add("源=" + MirrorProfile);
            return $"部署于 {DeployedAt} · {string.Join(" · ", parts)}";
        }
    }

    /// <summary>包的就位状态 —— 决定启动时是否自动切到「一键部署」页</summary>
    internal enum DeployStatus
    {
        /// <summary>全部就位，直接可用</summary>
        Ready,

        /// <summary>包是完整的（源码 / Python / uv 都在），只差 venv 没建 —— 一键部署能修</summary>
        NeedsVenv,

        /// <summary>包里缺必需文件（解压不完整 / 被杀软吞了）—— 部署也救不了，只能重新解压</summary>
        Broken
    }
}
