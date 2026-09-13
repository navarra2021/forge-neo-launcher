using System.Collections.Generic;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 依赖下载源档位 —— 决定 pip / uv 去哪儿拉包。
    ///
    /// <para><b>为什么需要它</b>：薄包首装要从网上拉约 4~5 GB（torch 占大头），
    /// 全部来自国外站点在国内可能慢到不可用。这里把「用哪个源」收成一个开关，
    /// 由启动器翻译成环境变量交给子进程，<b>不改动 Forge 本身的任何文件</b>。</para>
    ///
    /// <para><b>三个环境变量的分工</b>（都只注入子进程，不写系统环境）：</para>
    /// <list type="bullet">
    ///   <item><c>INDEX_URL</c> —— Forge 的 <c>modules/launch_utils.py</c> 会读它，
    ///         拼进 <c>pip install ... --index-url &lt;v&gt;</c>，管的是普通 PyPI 包</item>
    ///   <item><c>UV_DEFAULT_INDEX</c> —— uv 自己的默认索引，管的是 uv 直接发起的操作
    ///         （<c>uv venv --seed</c>、<c>uv pip install</c> 中没写 --index-url 的那些）</item>
    ///   <item><c>TORCH_INDEX_URL</c> —— Forge 的 <c>prepare_environment()</c> 读它，
    ///         拼成 torch 的 <c>--extra-index-url</c>，管的是 torch / torchvision 的 wheel</item>
    /// </list>
    ///
    /// <para><b>镜像选型是实测出来的，不是抄教程</b>（2026-09-13 本机实测）：</para>
    /// <list type="bullet">
    ///   <item>阿里云 PyPI <c>mirrors.aliyun.com/pypi/simple/</c> —— 标准 PEP503，可用；
    ///         实测 <c>uv pip install packaging==26.2</c> 0.4 秒完成</item>
    ///   <item>上海交大 <c>mirror.sjtu.edu.cn/pytorch-wheels/&lt;支线&gt;/</c> —— PEP503 结构，
    ///         实测解析 torch 2.13.0+cu130 + torchvision 0.28.0+cu130 得 13 个包，
    ///         1.2 秒（比官方 4.1 秒还快），且 cu130 / cu128 / cu126 三条支线都在</item>
    ///   <item>⚠ 南京大学 <c>mirrors.nju.edu.cn/pytorch/whl/&lt;支线&gt;/</c> —— 结构看着对，
    ///         但实测拉依赖元数据时持续超时（126 秒后失败），<b>故不采用</b></item>
    ///   <item>⚠ 阿里云 <c>pytorch-wheels</c> 是**扁平目录**而非 PEP503 索引
    ///         （<c>/cu130/torch/</c> 返回 404），只能配 <c>--find-links</c>，
    ///         接不进 Forge 现有的 <c>--extra-index-url</c> 链路，同样不采用</item>
    /// </list>
    /// </summary>
    internal static class DownloadSource
    {
        /// <summary>国内加速（默认）—— 阿里云 PyPI + 上海交大 PyTorch</summary>
        public const string Cn = "cn";

        /// <summary>官方源 —— pypi.org + download.pytorch.org，与 Forge 原生行为一致</summary>
        public const string Official = "official";

        /// <summary>兜底：认不出的一律当国内加速（也是新装的默认值）</summary>
        public static string Normalize(string? v) => v == Official ? Official : Cn;

        public static bool IsOfficial(string? v) => Normalize(v) == Official;

        /// <summary>下拉框里显示的一行文字</summary>
        public static string Title(string? v) => IsOfficial(v) ? "官方源" : "国内加速";

        /// <summary>下拉框里显示的第二行说明</summary>
        public static string Detail(string? v) => IsOfficial(v)
            ? "pypi.org + download.pytorch.org —— 与 Forge 原生行为一致；镜像站故障时切到这里"
            : "阿里云 PyPI + 上海交大 PyTorch 镜像 —— 国内实测更快，默认选它";

        /// <summary>下拉框数据源（界面只是这份清单的投影）</summary>
        public static List<SourceOption> Options() => new List<SourceOption>
        {
            new SourceOption { Id = Cn,       Title = Title(Cn),        Detail = Detail(Cn) },
            new SourceOption { Id = Official, Title = Title(Official),  Detail = Detail(Official) }
        };

        /// <summary>普通 PyPI 包的索引地址</summary>
        public static string PyPiIndex(string? v) => IsOfficial(v)
            ? "https://pypi.org/simple/"
            : "https://mirrors.aliyun.com/pypi/simple/";

        /// <summary>PyTorch wheel 索引地址。<paramref name="branch"/> 形如 cu130。</summary>
        public static string TorchIndex(string? v, string branch)
        {
            if (string.IsNullOrWhiteSpace(branch)) branch = "cu130";
            return IsOfficial(v)
                ? $"https://download.pytorch.org/whl/{branch}"
                : $"https://mirror.sjtu.edu.cn/pytorch-wheels/{branch}";
        }
    }

    /// <summary>下载源下拉框的一项</summary>
    internal sealed class SourceOption
    {
        // ⚠ 必须是「属性」不能是「字段」！
        //   WPF 绑定（Binding / DisplayMemberPath / DataTrigger）只反射属性，
        //   字段会被静默忽略 —— 不报错、不留日志，展开的下拉菜单直接一片空白。
        //   v0.13 这三个就是字段，于是「收起态有字、展开后空白」：
        //   收起态绑的是 SelectText（属性，能过），展开项绑 Title/Detail（字段，全灭）。
        //   判据：凡是可能被 XAML 绑定的类，成员一律写 { get; set; }。
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";

        /// <summary>
        /// 下拉框<b>收起</b>时显示的文字。
        ///
        /// <para>⚠ 不能省：<c>ComfyCombo</c> 样式的收起态是自己画的
        /// （<c>ContentPresenter Content="{TemplateBinding SelectedItem}"</c> +
        /// 固定绑 <c>SelectText</c>），不会复用项模板。
        /// 少了这个属性，收起后就是一片空白 —— 这个坑在 PyTorch 那两个下拉框上踩过一次。</para>
        /// </summary>
        public string SelectText => Title;
    }
}
