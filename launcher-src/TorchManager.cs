using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ForgeNeoLauncher
{
    /// <summary>CUDA 支线（一个 wheel 频道，如 cu130）</summary>
    public class TorchBranch
    {
        public string Id { get; set; } = "";          // cu130
        public string CudaVersion { get; set; } = "";  // 13.0
        public string Title { get; set; } = "";        // CUDA 13.0 (cu130)
        public string Description { get; set; } = "";
        public bool IsCurrent { get; set; }
        public List<TorchBuild> Builds { get; set; } = new List<TorchBuild>();

        /// <summary>
        /// 下拉框收起时显示的一行文字。
        /// 标记「当前」字样，让用户不用展开也知道自己正用哪条支线。
        /// </summary>
        public string SelectText => IsCurrent ? $"{Title} — 当前" : Title;
    }

    /// <summary>某个支线下的一个具体版本组合（三件套已对齐）</summary>
    public class TorchBuild
    {
        public string BranchId { get; set; } = "";     // cu130
        public string Torch { get; set; } = "";        // 2.13.0
        public string TorchVision { get; set; } = "";  // 0.28.0
        /// <summary>可为空——torchaudio 在 2.12 之后官方已停止发布</summary>
        public string TorchAudio { get; set; } = "";
        public string Note { get; set; } = "";
        public bool IsCurrent { get; set; }

        /// <summary>显示标题，如 "PyTorch 2.13.0+cu130"</summary>
        public string Title => $"PyTorch {Torch}+{BranchId}";

        /// <summary>下拉框收起时显示的一行文字，如 "PyTorch 2.13.0+cu130 — 当前"</summary>
        public string SelectText => IsCurrent ? $"{Title} — 当前" : Title;

        /// <summary>副标题：列出配套组件，像 ComfyUI Desktop 那样一眼看全</summary>
        public string Subtitle
        {
            get
            {
                var parts = new List<string> { $"torchvision {TorchVision}+{BranchId}" };
                if (!string.IsNullOrEmpty(TorchAudio))
                    parts.Add($"torchaudio {TorchAudio}+{BranchId}");
                parts.Add(Note);
                return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
            }
        }

        /// <summary>该组合要安装的包规格（安装命令用）</summary>
        public List<string> Specs()
        {
            var list = new List<string>
            {
                $"torch=={Torch}+{BranchId}",
                $"torchvision=={TorchVision}+{BranchId}"
            };
            if (!string.IsNullOrEmpty(TorchAudio))
                list.Add($"torchaudio=={TorchAudio}+{BranchId}");
            return list;
        }
    }

    /// <summary>当前 venv 里实际的 PyTorch 环境</summary>
    public class TorchEnvInfo
    {
        public string PythonVersion { get; set; } = "";
        public string Torch { get; set; } = "";
        public string TorchVision { get; set; } = "";
        public string TorchAudio { get; set; } = "";
        public string CompiledCuda { get; set; } = "";
        public bool CudaAvailable { get; set; }
        public string GpuName { get; set; } = "";
        public string ComputeCap { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>形如 "2.13.0+cu130" → "cu130"；识别不出返回空</summary>
        public string BranchId
        {
            get
            {
                int i = Torch.IndexOf('+');
                return i >= 0 ? Torch.Substring(i + 1) : "";
            }
        }

        /// <summary>形如 "2.13.0+cu130" → "2.13.0"</summary>
        public string TorchBase
        {
            get
            {
                int i = Torch.IndexOf('+');
                return i >= 0 ? Torch.Substring(0, i) : Torch;
            }
        }

        public bool HasTorch => !string.IsNullOrEmpty(Torch);
    }

    /// <summary>
    /// PyTorch 环境管理器：检测现状 / 按支线+版本安装。
    ///
    /// 关于版本清单为什么是内置的：
    /// wheel 的可用性是既成事实（哪个版本有 cp313-win_amd64 的轮子不会变），
    /// 而且必须先知道「torch 2.13 配 torchvision 0.28」这种对应关系才能给出可安装的组合。
    /// 这份清单是逐个访问 download.pytorch.org 的索引实测出来的，不是凭记忆写的。
    ///
    /// 🔑 两条实测得出的规律（照抄官方发布节奏）：
    ///   1. torchvision 版本 = 0.(torch 次版本号 + 15)，如 torch 2.13 → torchvision 0.28
    ///   2. torchaudio 在 2.12 之后已停止发布 —— 故 2.12+ 的组合只装 torch + torchvision。
    ///      硬装 torchaudio==2.13.0 会直接 404。
    /// </summary>
    public static class TorchManager
    {
        /// <summary>PyTorch 官方 wheel 索引（模板）。实际用哪个源见 <see cref="DownloadSource"/>。</summary>
        public const string IndexUrlTemplate = "https://download.pytorch.org/whl/{0}";

        // ==================== 版本清单 ====================
        // 每个支线取「最新稳定 + 几个常用回退版本」。全部经 pip --dry-run 实测可解析。

        public static List<TorchBranch> GetBranches()
        {
            return new List<TorchBranch>
            {
                new TorchBranch
                {
                    Id = "cu130", CudaVersion = "13.0", Title = "CUDA 13.0 (cu130)",
                    Description = "CUDA 13.0 — 当前稳定 CUDA 支线，适用于 RTX 20 系列及更新显卡",
                    Builds = new List<TorchBuild>
                    {
                        new TorchBuild { Torch = "2.14.0", TorchVision = "0.29.0", Note = "该支线最新" },
                        new TorchBuild { Torch = "2.13.0", TorchVision = "0.28.0" },
                        new TorchBuild { Torch = "2.12.1", TorchVision = "0.27.1", Note = "官方自此版本起不再发布 torchaudio" },
                        new TorchBuild { Torch = "2.11.0", TorchVision = "0.26.0", TorchAudio = "2.11.0", Note = "最后一个含 torchaudio 的组合" },
                        new TorchBuild { Torch = "2.10.0", TorchVision = "0.25.0", TorchAudio = "2.10.0" },
                    }
                },
                new TorchBranch
                {
                    Id = "cu128", CudaVersion = "12.8", Title = "CUDA 12.8 (cu128)",
                    Description = "CUDA 12.8 — 适用于 RTX 20 系列及更新显卡，无需升级到 CUDA 13 驱动",
                    Builds = new List<TorchBuild>
                    {
                        new TorchBuild { Torch = "2.11.0", TorchVision = "0.26.0", TorchAudio = "2.11.0", Note = "该支线最新" },
                        new TorchBuild { Torch = "2.10.0", TorchVision = "0.25.0", TorchAudio = "2.10.0" },
                        new TorchBuild { Torch = "2.9.1", TorchVision = "0.24.1", TorchAudio = "2.9.1" },
                        new TorchBuild { Torch = "2.8.0", TorchVision = "0.23.0", TorchAudio = "2.8.0" },
                        new TorchBuild { Torch = "2.7.1", TorchVision = "0.22.1", TorchAudio = "2.7.1" },
                    }
                },
                new TorchBranch
                {
                    Id = "cu126", CudaVersion = "12.6", Title = "CUDA 12.6 (cu126)",
                    Description = "CUDA 12.6 — 传统支线；保留较旧 GPU（GTX 900/10 系列及更新）的内核",
                    Builds = new List<TorchBuild>
                    {
                        new TorchBuild { Torch = "2.11.0", TorchVision = "0.26.0", TorchAudio = "2.11.0", Note = "该支线最新" },
                        new TorchBuild { Torch = "2.9.1", TorchVision = "0.24.1", TorchAudio = "2.9.1" },
                        new TorchBuild { Torch = "2.8.0", TorchVision = "0.23.0", TorchAudio = "2.8.0" },
                        new TorchBuild { Torch = "2.7.1", TorchVision = "0.22.1", TorchAudio = "2.7.1" },
                        new TorchBuild { Torch = "2.6.0", TorchVision = "0.21.0", TorchAudio = "2.6.0", Note = "最保守" },
                    }
                },
            };
        }

        /// <summary>
        /// 把当前环境标记到清单上（哪个支线、哪个版本正在用）。
        /// env 允许为 null —— 环境探测失败时只清掉所有「当前」标记，不影响清单本身可用。
        /// </summary>
        public static void MarkCurrent(TorchEnvInfo? env, List<TorchBranch> branches)
        {
            foreach (var b in branches)
            {
                b.IsCurrent = false;
                foreach (var bd in b.Builds)
                {
                    bd.IsCurrent = false;
                    bd.BranchId = b.Id;
                }
            }
            if (env == null || !env.HasTorch) return;

            var cur = branches.FirstOrDefault(x => x.Id == env.BranchId);
            if (cur == null) return;
            cur.IsCurrent = true;

            var cb = cur.Builds.FirstOrDefault(x => x.Torch == env.TorchBase);
            if (cb != null) cb.IsCurrent = true;
        }

        /// <summary>取当前选中（或当前环境）所对应的构建</summary>
        public static TorchBuild? FindBuild(string branchId, string torchVersion)
        {
            var br = GetBranches().FirstOrDefault(x => x.Id == branchId);
            return br?.Builds.FirstOrDefault(x => x.Torch == torchVersion);
        }

        // ==================== 环境检测 ====================

        /// <summary>内联的探测脚本。base64 传递，避开引号转义地狱。</summary>
        private const string ProbeScript = @"
import json, sys
r = {}
r['python'] = '%d.%d.%d' % sys.version_info[:3]
try:
    import torch
    r['torch'] = torch.__version__
    r['cuda'] = torch.version.cuda or ''
    ok = torch.cuda.is_available()
    r['cuda_ok'] = bool(ok)
    if ok:
        r['gpu'] = torch.cuda.get_device_name(0)
        r['cap'] = '%d.%d' % torch.cuda.get_device_capability(0)
except Exception as e:
    r['torch'] = ''
    r['err'] = str(e)
for mod, key in (('torchvision', 'torchvision'), ('torchaudio', 'torchaudio')):
    try:
        m = __import__(mod)
        r[key] = getattr(m, '__version__', '')
    except Exception:
        r[key] = ''
print(json.dumps(r))
";

        /// <summary>
        /// 读取 venv 里的真实 PyTorch 环境。
        /// 用 python 自己去 import 才是最可信的——读 site-packages 目录名会在
        /// 多版本共存/半安装状态下给出错误答案。
        /// </summary>
        public static async Task<TorchEnvInfo> DetectAsync(string pythonPath, int timeoutMs = 60000)
        {
            var info = new TorchEnvInfo();
            if (!File.Exists(pythonPath))
            {
                info.Error = "找不到 Python：" + pythonPath;
                return info;
            }

            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ProbeScript));
            // -c "import base64;exec(base64.b64decode('...').decode('utf-8'))"
            string code = $"import base64;exec(base64.b64decode('{b64}').decode('utf-8'))";

            try
            {
                var psi = new ProcessStartInfo(pythonPath, "-c \"" + code + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.Environment["PYTHONIOENCODING"] = "utf-8";

                using var p = Process.Start(psi);
                if (p == null) { info.Error = "无法启动 Python"; return info; }

                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!await Task.Run(() => p.WaitForExit(timeoutMs)))
                {
                    try { p.Kill(true); } catch { }
                    info.Error = "检测超时（Python 启动或 import torch 过慢）";
                    return info;
                }

                string stdout = await so;
                string stderr = await se;

                int i = stdout.IndexOf('{');
                int j = stdout.LastIndexOf('}');
                if (i < 0 || j <= i)
                {
                    info.Error = string.IsNullOrWhiteSpace(stderr)
                        ? "Python 未返回有效结果"
                        : stderr.Trim().Split('\n').Last().Trim();
                    return info;
                }

                using var doc = JsonDocument.Parse(stdout.Substring(i, j - i + 1));
                var r = doc.RootElement;
                info.PythonVersion = Str(r, "python");
                info.Torch = Str(r, "torch");
                info.CompiledCuda = Str(r, "cuda");
                info.CudaAvailable = r.TryGetProperty("cuda_ok", out var ck) && ck.ValueKind == JsonValueKind.True;
                info.GpuName = Str(r, "gpu");
                info.ComputeCap = Str(r, "cap");
                info.TorchVision = Str(r, "torchvision");
                info.TorchAudio = Str(r, "torchaudio");
                if (!info.HasTorch) info.Error = Str(r, "err");
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        // ==================== 安装 ====================

        /// <summary>
        /// 组装安装命令（不含 pip 本身），供预览与执行共用。
        /// <paramref name="source"/> 是下载源档位 —— 与「高级选项 → 依赖下载源」是同一个开关，
        /// 地址从 <see cref="DownloadSource"/> 取。预览与执行都调这里，故不可能出现
        /// 「预览写官方源、实际走镜像」这种不一致。
        /// </summary>
        public static List<string> BuildInstallArgs(TorchBuild build, bool forceReinstall, string? source)
        {
            var args = new List<string>
            {
                "-m", "pip", "install",
                "--index-url", DownloadSource.TorchIndex(source, build.BranchId),
                // 三件套必须一起升/降，否则 torchvision 的 C 扩展会和 torch ABI 对不上，
                // 报 "operator torchvision::nms does not exist"
                "--upgrade"
            };
            if (forceReinstall) args.Add("--force-reinstall");
            args.AddRange(build.Specs());
            return args;
        }

        public static string BuildInstallCommandLine(TorchBuild build, bool forceReinstall, string? source)
            => ArgLine.Build(BuildInstallArgs(build, forceReinstall, source));

        /// <summary>
        /// 执行安装，边跑边把输出喂给回调。
        /// ⚠️ 只传 -m pip install，不做任何“顺手升级别的包”的操作——venv 是用户的，不是我们的。
        /// </summary>
        public static async Task<(bool ok, string msg)> InstallAsync(
            string pythonPath, TorchBuild build, bool forceReinstall, string? source,
            Action<string> onLine, int timeoutMs = 3600000)
        {
            if (!File.Exists(pythonPath)) return (false, "找不到 Python：" + pythonPath);

            var args = BuildInstallArgs(build, forceReinstall, source);
            try
            {
                var psi = new ProcessStartInfo(pythonPath, ArgLine.Build(args))
                {
                    WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(pythonPath)) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONUTF8"] = "1";
                // pip 输出量大时不让它卡在缓冲
                psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";

                using var p = Process.Start(psi);
                if (p == null) return (false, "无法启动 pip");

                p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = await Task.Run(() => p.WaitForExit(timeoutMs));
                if (!exited)
                {
                    try { p.Kill(true); } catch { }
                    return (false, "安装超时，已中断");
                }

                // 等异步输出读完，否则最后几行日志会丢
                await Task.Delay(300);

                return p.ExitCode == 0
                    ? (true, $"{build.Title} 安装完成")
                    : (false, $"pip 返回退出码 {p.ExitCode}，请查看日志了解详情");
            }
            catch (Exception ex)
            {
                return (false, "安装失败：" + ex.Message);
            }
        }

        /// <summary>把环境信息格式化成几行可读文本</summary>
        public static string Describe(TorchEnvInfo e)
        {
            if (e == null) return "（未检测）";
            if (!e.HasTorch)
                return e.Error.Length > 0
                    ? $"未检测到 PyTorch（{e.Error}）"
                    : "未检测到 PyTorch";

            var sb = new StringBuilder();
            sb.Append($"torch {e.Torch}");
            if (!string.IsNullOrEmpty(e.TorchVision)) sb.Append($" · torchvision {e.TorchVision}");
            if (!string.IsNullOrEmpty(e.TorchAudio)) sb.Append($" · torchaudio {e.TorchAudio}");
            if (!string.IsNullOrEmpty(e.CompiledCuda)) sb.Append($" · CUDA {e.CompiledCuda}");
            if (!string.IsNullOrEmpty(e.GpuName)) sb.Append($" · {e.GpuName}");
            if (e.CudaAvailable) sb.Append($" · 算力 sm_{e.ComputeCap.Replace(".", "")}");
            return sb.ToString();
        }
    }
}
