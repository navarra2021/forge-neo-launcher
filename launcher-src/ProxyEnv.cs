using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 给子进程注入「本机地址不走系统代理」的规则（<c>NO_PROXY</c> / <c>no_proxy</c>）。
    ///
    /// <para><b>为什么必须有这条</b>：装了 Clash / Steam++ / v2ray 这类<b>全局代理</b>时，
    /// WebUI 启动阶段会用 <c>urllib.request.getproxies()</c> 判断「localhost 是否可达」，
    /// 一旦 <c>localhost</c> 命中代理，就被判成不可达，打出</para>
    ///
    /// <code>When localhost is not accessible, a shareable link must be created.</code>
    ///
    /// <para>接着要么去建公网分享链接、要么直接起不来。<b>报错文字里一个「代理」字都没有</b>，
    /// 而服务其实好好的就在本机 —— 这类问题最难查。本机装了系统代理的用户必中招，
    /// 所以这里是<b>无条件注入</b>，不做开关。</para>
    ///
    /// <para><b>合并而不是覆盖</b>：用户可能已经在系统里配了 NO_PROXY（公司内网域名之类），
    /// 直接覆盖等于把别人的设置吃掉。这里只做「追加缺失项」，已有内容原样保留、顺序不动。</para>
    ///
    /// <para><b>大小写两个都写</b>：Python 的 <c>urllib.request.getproxies_environment()</c>
    /// 会把变量名统一转小写再取，所以 Windows 上只写 <c>NO_PROXY</c> 本也够用；
    /// 但环境变量在 Linux / macOS 上区分大小写，两个都写才能保证换平台也不失效。
    /// （Windows 上 <c>ProcessStartInfo.Environment</c> 不区分大小写，写两次不会产生重复项。）</para>
    ///
    /// <para>⚠ 本类<b>不碰系统环境</b>，只改传进来的 <see cref="ProcessStartInfo"/> ——
    /// 整合包「零污染」的承诺靠这条守住，和 <c>ApplyChildEnv</c> 里的其余注入同源。</para>
    /// </summary>
    internal static class ProxyEnv
    {
        /// <summary>
        /// 必须绕过代理的本机地址。
        /// <c>0.0.0.0</c> 也在内：开了 <c>--listen</c> 时 gradio 会以它作为绑定名做可达性判断。
        /// </summary>
        public static readonly string[] LoopbackHosts = { "localhost", "127.0.0.1", "::1", "0.0.0.0" };

        /// <summary>
        /// 把必须绕过的本机地址并进已有的 NO_PROXY 值。
        /// 已有项原样保留、顺序不动，只追加缺的那些，重复项去掉。
        /// 已有值里含 <c>*</c>（表示全部不走代理）时原样返回 —— 我们这几项已被它包含。
        /// </summary>
        public static string MergeNoProxy(string? existing)
        {
            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string s)
            {
                s = (s ?? "").Trim();
                if (s.Length == 0) return;
                if (seen.Add(s)) items.Add(s);
            }

            // 分隔符两种都认：no_proxy 的惯例是逗号，但有人用分号
            foreach (var part in (existing ?? "").Split(new[] { ',', ';' })) Add(part);

            if (items.Any(x => x == "*")) return string.Join(",", items);

            foreach (var h in LoopbackHosts) Add(h);
            return string.Join(",", items);
        }

        /// <summary>把 NO_PROXY / no_proxy 写进子进程环境</summary>
        public static void Apply(ProcessStartInfo psi)
        {
            var existing = Environment.GetEnvironmentVariable("NO_PROXY")
                           ?? Environment.GetEnvironmentVariable("no_proxy");
            var merged = MergeNoProxy(existing);

            psi.Environment["NO_PROXY"] = merged;
            psi.Environment["no_proxy"] = merged;
        }
    }
}
