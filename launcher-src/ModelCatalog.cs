using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 「模型管理」页的数据来源 —— 把 <c>models</c> 目录里<b>到底有什么</b>摊开给人看。
    ///
    /// <para><b>为什么要有这一页</b>：整合包是「薄包」，模型要用户自己往里放。放完之后
    /// 启动器只肯在控制台打一行「模型目录：…」，用户想知道"我那个 lora 到底在不在、
    /// 是不是放错目录了"就只能自己去资源管理器翻 —— 而五个相似的中文名目录
    /// （Lora / VAE / embeddings / text_encoder / Stable-diffusion）很容易放错，
    /// 放错的后果是「文件明明在，WebUI 里就是没有」，排查很绕。</para>
    ///
    /// <para><b>两份数据、两种角色，别混</b>：</para>
    /// <list type="bullet">
    ///   <item><see cref="Known"/> 是<b>注解表</b> —— 只负责「这个目录是干嘛的、对应哪个
    ///     追加参数」。它<b>不是</b>要显示的清单：磁盘上真有的目录才是要显示的（见
    ///     <see cref="Scan"/> 里那段「注解表没写到的目录照样列出来」）。</item>
    ///   <item>列表本身<b>派生</b>于磁盘：这样上游新增一个目录、或用户自己建一个，
    ///     界面会自己跟上，不需要回来改代码。</item>
    /// </list>
    ///
    /// <para>目录名全部在<b>上游源码里核过</b>（依据见每条 <c>See</c> 注释），
    /// 不靠记忆 —— 名字写错的后果是"点开一个空目录"，不报错、没日志。</para>
    /// <para>⚠ 声明成 <c>public</c> 是**为了能被 <c>_modeltest</c> 引用**：那些测试工程是
    /// 引启动器的编译产物（DLL），不是把源文件合进来编译 —— <c>internal</c> 在那里看不见。
    /// 同一条理由见 <see cref="ExtensionManager"/> / <see cref="CoreUpdater"/>。</para>
    /// </summary>
    public static class ModelCatalog
    {
        // ==================================================================
        //  注解表
        // ==================================================================

        /// <summary>
        /// 一个已知的模型目录。
        ///
        /// <para><b><see cref="Tag"/> 的语义不是这里定的</b>：它必须与主程序
        /// <c>GetDirList()</c> 里那几个 <c>case</c> 逐字对应（ckpt / lora / vae / textenc）。
        /// 这正是刻意设计 —— 追加目录的读取只维护那一处 switch，这张表只声明
        /// 「我要看哪几类」，不去重复实现"哪一类是哪个字段"。</para>
        /// </summary>
        public sealed class KnownFolder
        {
            /// <summary>models 下的子目录名（上游源码里核过，见 <see cref="Known"/> 各处注释）</summary>
            public string Folder { get; set; } = "";
            /// <summary>界面上的分类名</summary>
            public string Title { get; set; } = "";
            /// <summary>一句话用途 —— 说清"放什么进去、放错了会怎样"</summary>
            public string Desc { get; set; } = "";
            /// <summary>追加目录的类型（与 GetDirList 的 case 一致）；空 = 这类目录没有追加参数</summary>
            public string Tag { get; set; } = "";
        }

        /// <summary>
        /// 已知目录。顺序即界面顺序（稳定、可预期，不按"有没有文件"动态重排 ——
        /// 用户熟悉之后不用每次找）。
        ///
        /// <para>⚠ <b>这里只写"能在上游源码里指名道姓核到"的目录</b>，别凭印象加。
        /// 每条的依据如下（本机 Forge Neo 源码）：</para>
        /// <list type="bullet">
        ///   <item>Stable-diffusion —— <c>modules/sd_models.py:17</c> <c>model_dir</c></item>
        ///   <item>Lora / VAE / text_encoder / embeddings / ESRGAN —— <c>modules/launch_utils.py:453-458</c>
        ///     的 <c>ModelRef</c> 清单（上游自己维护的那份）+ <c>modules/cmd_args.py:84,89</c> 的默认值</item>
        ///   <item>ControlNet / ControlNetPreprocessor / diffusers —— <c>modules_forge/shared.py:15,28,39</c></item>
        ///   <item>adetailer / interrogators / TaggerOnnx —— 本机 <c>models</c> 下实际存在的
        ///     扩展模型目录（ADetailer、WD14 tagger 各自建的）</item>
        /// </list>
        ///
        /// <para>刻意<b>不</b>收录的：<c>unet</c>（ComfyUI 那边才这么叫，Forge Neo 的单文件
        /// Flux 模型放 <c>Stable-diffusion</c>；本机并没有这个目录）、
        /// <c>VAE-approx</c> / <c>VAE-taesd</c> / <c>Codeformer</c> / <c>GFPGAN</c>
        /// （内核自带资产或本机不存在，列出来只会变成一串"目录不存在"）。</para>
        /// </summary>
        public static readonly KnownFolder[] Known =
        {
            new KnownFolder { Folder = "Stable-diffusion", Title = "主模型",
                              Desc = "出图底模（SD1.5 / SDXL / Flux 等单文件都放这里）", Tag = "ckpt" },
            new KnownFolder { Folder = "Lora", Title = "LoRA",
                              Desc = "风格与角色的轻量微调；提示词里用 <lora:文件名:权重> 调用", Tag = "lora" },
            new KnownFolder { Folder = "VAE", Title = "VAE",
                              Desc = "色彩解码器，影响出图色调与清晰度", Tag = "vae" },
            new KnownFolder { Folder = "text_encoder", Title = "文本编码器",
                              Desc = "Flux / Qwen 这类双编码器模型的 CLIP / T5 权重", Tag = "textenc" },
            new KnownFolder { Folder = "embeddings", Title = "嵌入（Embedding）",
                              Desc = "Textual Inversion（.pt），提示词里直接写文件名调用" },
            new KnownFolder { Folder = "ControlNet", Title = "ControlNet",
                              Desc = "结构控制模型（canny / depth / openpose …）" },
            new KnownFolder { Folder = "ControlNetPreprocessor", Title = "ControlNet 预处理器",
                              Desc = "控制图预处理器权重（annotator）" },
            new KnownFolder { Folder = "ESRGAN", Title = "超分模型",
                              Desc = "Extras 与 Hires.fix 用到的放大模型" },
            new KnownFolder { Folder = "diffusers", Title = "Diffusers",
                              Desc = "目录形态的模型（model_index.json + 分片权重）" },
            new KnownFolder { Folder = "adetailer", Title = "ADetailer",
                              Desc = "人脸 / 手部修复扩展用的检测模型" },
            new KnownFolder { Folder = "interrogators", Title = "反推模型",
                              Desc = "CLIP / DeepDanbooru 反推用的权重" },
            new KnownFolder { Folder = "TaggerOnnx", Title = "WD14 Tagger（ONNX）",
                              Desc = "标签反推扩展的 ONNX 模型" }
        };

        /// <summary>
        /// 一张卡片最多列多少条 —— 几百个 lora 时全列出来，卡片会长到没法看
        /// （也白白拖慢布局）。超出部分由 <see cref="ModelFolder.MoreText"/> 明说还有多少。
        /// </summary>
        public const int MaxEntriesShown = 50;

        // ==================================================================
        //  数据模型
        // ==================================================================

        /// <summary>
        /// 卡片里的一行：一个文件或一个子目录。
        ///
        /// <para>⚠ 成员一律写成<b>属性</b> —— WPF 绑定只反射属性，
        /// 公共字段会被静默忽略（界面空白且不报错）。</para>
        /// </summary>
        public sealed class ModelEntry
        {
            /// <summary>文件 / 目录名（目录带尾部的 <c>\</c>，一眼能分开）</summary>
            public string Name { get; set; } = "";
            /// <summary>体积文案（目录显示「文件夹」而不是体积 —— 递归算体积可能很慢）</summary>
            public string SizeText { get; set; } = "";
            /// <summary>是不是子目录（界面据此淡化显示）</summary>
            public bool IsDir { get; set; }
            /// <summary>目录的"多少项"提示；文件为空</summary>
            public string NoteText { get; set; } = "";
        }

        /// <summary>一个模型目录在界面上的全部信息（= 一张卡片）。</summary>
        public sealed class ModelFolder
        {
            public string Title { get; set; } = "";
            public string Desc { get; set; } = "";
            /// <summary>绝对路径（界面显示与「打开」按钮都用它）</summary>
            public string Dir { get; set; } = "";
            /// <summary>磁盘上真的存在这个目录吗（false 时 <see cref="NoteText"/> 会说明）</summary>
            public bool Exists { get; set; }
            /// <summary>「3 个文件 · 1 个文件夹」；空目录为空串</summary>
            public string CountText { get; set; } = "";
            /// <summary>文件总体积（只算本层的文件，不含子目录）；没有文件时为空串</summary>
            public string SizeText { get; set; } = "";
            /// <summary>空状态 / 异常说明（三种情况都明说，不糊弄）</summary>
            public string NoteText { get; set; } = "";
            /// <summary>被截断时的说明；没截断为空串</summary>
            public string MoreText { get; set; } = "";
            /// <summary>本层条目（文件在前、子目录在后；最多 <see cref="MaxEntriesShown"/> 条）</summary>
            public List<ModelEntry> Entries { get; set; } = new List<ModelEntry>();
        }

        // ==================================================================
        //  扫描
        // ==================================================================

        /// <summary>
        /// 扫出要显示的目录清单。
        ///
        /// <para><paramref name="extraOf"/> 是「给我一个类型，还我那一类追加目录」的回调 ——
        /// 主程序直接传 <c>GetDirList</c>，于是"哪一类对应哪个字段"只有那一处定义。
        /// 传 <c>null</c> = 不看追加目录（测试常用）。</para>
        ///
        /// <para><b>三条来源</b>：① 注解表里的已知目录（不管存不存在都列，不存在时明说，
        /// 因为"这一格该放什么"本身就是信息）；② <c>models</c> 下注解表没写到的子目录
        /// （派生 —— 上游新增目录或用户自己建的都能看见）；③ 用户配的追加目录。</para>
        ///
        /// <para>全程只读、只枚举一层，不发网络请求。唯一可能慢的是把网络盘配成追加目录 ——
        /// 那种情况下一层 <c>GetFiles</c> 也可能卡住，所以整段都包在 try 里，
        /// 扫不动就返回已经拿到的部分，不把界面带崩。</para>
        /// </summary>
        public static List<ModelFolder> Scan(string modelsDir, Func<string, IReadOnlyList<string>>? extraOf = null)
        {
            var list = new List<ModelFolder>();
            try
            {
                foreach (var k in Known)
                {
                    string dir = string.IsNullOrWhiteSpace(modelsDir)
                        ? k.Folder
                        : Path.Combine(modelsDir, k.Folder);
                    list.Add(ReadFolder(k.Title, k.Desc, dir));
                }

                // 磁盘上有、注解表没写的目录 —— 照样列出来。
                // ⚠ 这一段是"派生"的关键：只认注解表的话，用户自己建的目录就永远看不见，
                //   而那恰恰是最需要被看见的一类（手工放的模型最容易放错地方）。
                if (!string.IsNullOrWhiteSpace(modelsDir) && Directory.Exists(modelsDir))
                {
                    var known = new HashSet<string>(
                        Known.Select(k => k.Folder), StringComparer.OrdinalIgnoreCase);

                    foreach (var d in Directory.GetDirectories(modelsDir)
                                                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(d);
                        if (name.StartsWith(".")) continue;              // .cache 之类
                        if (known.Contains(name)) continue;
                        list.Add(ReadFolder(name, "注解表里没有的目录（上游新增，或你自己建的）", d));
                    }
                }

                // 追加目录（--ckpt-dirs / --lora-dirs / --vae-dirs / --text-encoder-dirs）
                if (extraOf != null)
                {
                    foreach (var k in Known)
                    {
                        if (string.IsNullOrEmpty(k.Tag)) continue;

                        IReadOnlyList<string> dirs;
                        try { dirs = extraOf(k.Tag) ?? Array.Empty<string>(); }
                        catch { continue; }

                        foreach (var d in dirs)
                        {
                            if (string.IsNullOrWhiteSpace(d)) continue;
                            string full = d.Trim();

                            // 与已列出的路径重复就不再出一张卡（用户可能把默认目录又加了一遍）
                            if (list.Any(m => SamePath(m.Dir, full))) continue;

                            // 「（追加）」写在标题里、参数名并进描述 —— 都比加一个角标省事，
                            // 也省掉一个"只有一半卡片有值"的字段（空字符串在 WPF 里照样占行高）。
                            list.Add(ReadFolder(k.Title + "（追加）",
                                                k.Desc + "　来自 --" + FlagOf(k.Tag), full));
                        }
                    }
                }
            }
            catch { /* 扫不动就返回已有的，不把界面带崩 */ }
            return list;
        }

        /// <summary>
        /// 读一个模型目录。三种"没有内容"的情况<b>各自明说</b>，不共用一句含糊话：
        /// 目录不存在 / 目录是空的 / 读取失败 —— 三者的下一步动作完全不同
        /// （建出来 / 去下模型 / 查权限）。
        /// </summary>
        private static ModelFolder ReadFolder(string title, string desc, string dir)
        {
            var m = new ModelFolder { Title = title, Desc = desc, Dir = dir };

            try
            {
                if (!Directory.Exists(dir))
                {
                    m.Exists = false;
                    m.NoteText = "目录不存在 —— 点「打开」会先创建空目录再打开";
                    return m;
                }

                m.Exists = true;

                long total = 0;
                int nFile = 0, nDir = 0;

                foreach (var f in Directory.GetFiles(dir)
                                           .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { /* 单个文件读不到就记 0，不打断整段 */ }
                    total += size;
                    nFile++;
                    m.Entries.Add(new ModelEntry
                    {
                        Name = Path.GetFileName(f),
                        SizeText = FormatSize(size),
                        IsDir = false
                    });
                }

                foreach (var d in Directory.GetDirectories(dir)
                                           .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(d);
                    nDir++;

                    // 子目录只数"有多少项"，**不递归算体积** ——
                    // 递归一棵 diffusers 目录会翻几千个文件，界面会卡一下。
                    int sub = 0;
                    try { sub = Directory.GetFileSystemEntries(d).Length; } catch { }

                    m.Entries.Add(new ModelEntry
                    {
                        Name = name + "\\",
                        SizeText = "文件夹",
                        IsDir = true,
                        NoteText = sub + " 项"
                    });
                }

                m.CountText = (nFile, nDir) switch
                {
                    (0, 0) => "",
                    (0, _) => $"{nDir} 个文件夹",
                    (_, 0) => $"{nFile} 个文件",
                    _ => $"{nFile} 个文件 · {nDir} 个文件夹"
                };
                m.SizeText = nFile > 0 ? FormatSize(total) : "";

                if (nFile == 0 && nDir == 0)
                    m.NoteText = "目录是空的 —— 还没往这里放模型";

                // 截断：文件在前、目录在后，所以先被砍掉的总是子目录（更次要的那批）
                if (m.Entries.Count > MaxEntriesShown)
                {
                    int real = m.Entries.Count;
                    m.Entries = m.Entries.Take(MaxEntriesShown).ToList();
                    m.MoreText = $"仅显示前 {MaxEntriesShown} 项（此目录共 {real} 项）";
                }
            }
            catch (Exception ex)
            {
                m.Exists = false;
                m.NoteText = "读取失败：" + ex.Message;
            }

            return m;
        }

        // ==================================================================
        //  体积格式化 / 过滤
        // ==================================================================

        /// <summary>
        /// 把字节数格式化成界面上的体积文案。
        ///
        /// <para><b>一律用 InvariantCulture</b>：中文 / 德语系统的小数点是逗号，
        /// 用本地文化会拼出 <c>6,5 GB</c> —— 与磁盘上、与别人的截图都对不上。
        /// （同一个坑在「预留显存」那处踩过，见 <c>AdvancedOptions.TryReserveVramArg</c>。）</para>
        /// </summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";

            const double kb = 1024.0, mb = kb * 1024, gb = mb * 1024;

            if (bytes < kb) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < mb) return (bytes / kb).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (bytes < gb) return (bytes / mb).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (bytes / gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>
        /// 按关键字过滤（**纯函数**，不改动传进来的列表）。
        ///
        /// <para>两种命中方式，粒度不同：分类自己命中（名称 / 用途 / 路径）→ 整张卡片留着；
        /// 只有文件名命中 → 只保留命中的那几个文件。这样"某个 lora 在不在"
        /// 搜出来的就是那一行，而不是一张列着几十个文件的卡片。</para>
        /// </summary>
        public static List<ModelFolder> Filter(List<ModelFolder> all, string keyword)
        {
            var result = new List<ModelFolder>();
            if (all == null) return result;

            if (string.IsNullOrWhiteSpace(keyword))
            {
                result.AddRange(all);
                return result;
            }

            string k = keyword.Trim();
            foreach (var m in all)
            {
                if (Hit(m.Title, k) || Hit(m.Desc, k) || Hit(m.Dir, k))
                {
                    result.Add(m);
                    continue;
                }

                var hits = m.Entries.Where(e => Hit(e.Name, k)).ToList();
                if (hits.Count == 0) continue;

                var copy = Clone(m);
                copy.Entries = hits;
                // 过滤后的条数是"命中数"，不是"显示的前 50 项"，原来那句截断说明必须撤掉
                copy.MoreText = "";
                result.Add(copy);
            }
            return result;

            static bool Hit(string s, string k) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>浅拷贝一张卡片（过滤时要换掉 <see cref="ModelFolder.Entries"/>，不能改原件）</summary>
        private static ModelFolder Clone(ModelFolder m) => new ModelFolder
        {
            Title = m.Title,
            Desc = m.Desc,
            Dir = m.Dir,
            Exists = m.Exists,
            CountText = m.CountText,
            SizeText = m.SizeText,
            NoteText = m.NoteText,
            MoreText = m.MoreText,
            Entries = new List<ModelEntry>(m.Entries)
        };

        /// <summary>Tag → 追加参数名（只用于界面上的来源角标；参数名在上游 cmd_args.py 里核过）</summary>
        private static string FlagOf(string tag) => tag switch
        {
            "ckpt" => "ckpt-dirs",
            "lora" => "lora-dirs",
            "vae" => "vae-dirs",
            "textenc" => "text-encoder-dirs",
            _ => tag
        };

        /// <summary>两个路径是不是同一个地方（大小写不敏感；非法路径就当不同）</summary>
        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                     Path.GetFullPath(b).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
