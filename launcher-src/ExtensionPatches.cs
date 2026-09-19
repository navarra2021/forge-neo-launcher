using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 扩展补丁 —— 把「已经修好、但会被下一次更新抹掉」的修正重新贴回去。
    ///
    /// <para><b>为什么需要它</b>：Forge 更新扩展走的是 <c>modules/extensions.py:219</c>
    /// 的 <c>fetch_and_reset_hard()</c> ——
    /// <c>repo.git.fetch(all=True)</c> + <c>repo.git.reset(commit, hard=True)</c>，
    /// <b>无条件覆盖工作区</b>。于是任何「手工改扩展文件」的修正，寿命都只到使用者
    /// 下一次点【更新】为止（本地 commit、<c>--skip-worktree</c> 全都挡不住）。</para>
    ///
    /// <para><b>实测后果</b>（wd14-tagger，2026-09-19）：opencv 上限被抹回来后，
    /// 启动时该扩展的 <c>install.py</c> 以非 0 退出 ——
    /// <c>Cannot install opencv-python4.12 ... (constraint) opencv-python==5.0.0.93</c>
    /// —— 同一份 requirements 里其它待装的包也一并不装，而且会先白下几十 MB 才失败。</para>
    ///
    /// <para><b>做法</b>：把修正写成<b>幂等</b>的规则，每次拉起 Forge 之前重新应用一遍。
    /// 文件本来就是对的 → <b>一个字节都不动</b>（这是常态路径，零副作用）；
    /// 被更新抹掉了 → 当场补回来，并在控制台说明原因。</para>
    ///
    /// <para><b>安全边界</b>：<list type="bullet">
    ///   <item>只碰 <see cref="All"/> 里明确列出的文件，不做任何「猜」的推断；</item>
    ///   <item>扩展没装（文件不存在）→ 静默跳过；</item>
    ///   <item>任何异常都降级成一条日志 —— <b>补丁失败绝不能把启动带崩</b>；</item>
    ///   <item>只做「删整行」，不改写保留的行，换行风格（LF / CRLF）原样保留。</item>
    /// </list></para>
    /// </summary>
    public static class ExtensionPatches
    {
        /// <summary>一条补丁：针对某个文件的幂等修正规则。</summary>
        private sealed class Patch
        {
            /// <summary>相对 Forge 根的路径。</summary>
            public string RelativePath = "";

            /// <summary>匹配到的整行会被删掉（正则，逐行匹配）。</summary>
            public string RemoveLinePattern = "";

            /// <summary>为什么要打这个补丁（会写进控制台日志，方便日后考古）。</summary>
            public string Why = "";
        }

        /// <summary>
        /// 全部补丁。以后遇到「上游写法会顶掉本体 / 与本体冲突」的扩展，往这里加一条即可。
        /// </summary>
        private static readonly Patch[] All =
        {
            new Patch
            {
                RelativePath = @"extensions\stable-diffusion-webui-wd14-tagger\requirements.txt",
                RemoveLinePattern = @"opencv_(?:contrib_)?python[ \t]*<",
                Why = "opencv 的「<4.12」上限是给 gradio 3.x 的 A1111 写的，而 Forge Neo 钉的是 "
                    + "opencv-python==5.0.0.93 —— 留着它，pip 会与本体钉版硬冲突并「整体」退出"
                    + "（Cannot install opencv-python4.12 ... (constraint) opencv-python==5.0.0.93），"
                    + "同一份清单里其它待装的包也一并不装。wd14-tagger 用到的 cv2 由本体提供，无需单独声明。",
            },
        };

        /// <summary>
        /// 把所有补丁重新应用一遍。已经就位的补丁不产生任何文件改动。
        /// </summary>
        /// <param name="root">Forge 根目录（<see cref="AppPaths.Root"/>）。</param>
        /// <param name="log">日志回调：(消息, 是否只是警告)。</param>
        /// <returns>真正发生改动的补丁条数（0 = 全部已就位）。</returns>
        public static int ApplyAll(string root, Action<string, bool> log)
        {
            int changed = 0;

            foreach (Patch p in All)
            {
                try
                {
                    string full = Path.Combine(root, p.RelativePath);
                    if (!File.Exists(full))
                        continue;                       // 这个扩展根本没装 —— 静默跳过

                    string before = File.ReadAllText(full, Encoding.UTF8);
                    string after = StripLines(before, p.RemoveLinePattern);
                    if (string.Equals(after, before, StringComparison.Ordinal))
                        continue;                       // 本来就是对的：不动文件

                    File.WriteAllText(full, after, new UTF8Encoding(false));
                    changed++;

                    log($"已重新应用扩展补丁：{p.RelativePath}", true);
                    log($"  原因：{p.Why}", false);
                    log("  Forge 更新扩展用的是 git reset --hard（无条件覆盖工作区），抹掉这类修正属正常现象。", false);
                }
                catch (Exception ex)
                {
                    // 补丁是「尽力而为」的加固：失败只记一笔，绝不能因此挡住启动。
                    log($"扩展补丁未能应用（不影响启动）：{p.RelativePath} — {ex.Message}", true);
                }
            }

            return changed;
        }

        /// <summary>
        /// 删掉所有匹配 <paramref name="linePattern"/> 的整行（连行尾一起删）。
        ///
        /// <para><c>(?m)^[ \t]*</c> 要求匹配从「行首（允许前置缩进）」开始 ——
        /// 这就天然把 <c>#</c> 开头的注释行排除在外，补丁自己写的说明不会被误删。
        /// <c>$</c> 兜住「文件最后一行没有换行符」的情况；其余行的换行风格原样保留。</para>
        /// </summary>
        private static string StripLines(string text, string linePattern)
        {
            return Regex.Replace(
                text,
                @"(?m)^[ \t]*" + linePattern + @"[^\r\n]*(?:\r?\n|$)",
                "");
        }
    }
}
