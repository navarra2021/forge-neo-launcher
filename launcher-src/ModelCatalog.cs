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

        /// <summary>
        /// 元数据 / 附属文件类扩展名 —— <b>只用于"判断一个文件该不该被隐藏"，绝不用于"判断谁是模型"</b>。
        ///
        /// <para>⚠ 这个方向不能反（v0.36）。v0.34 立的规矩是<b>不许按后缀白名单过滤</b>：
        /// 白名单会<b>静默漏掉</b>新格式（`.gguf` 之类），用户看到"文件明明在、这里却没有"。
        /// 所以这里做的是<b>黑名单</b> —— 只滤"一眼能认出是附属文件"的，<b>黑名单之外的一切照样显示</b>：
        /// 哪天上游多出个 `.xyz` 模型格式，它不在黑名单里 ⇒ 照常列出来。
        /// 一句话：<b>错误的方向只能是"多显示一个附属文件"，不能是"少显示一个模型"。</b></para>
        ///
        /// <para>⚠ 而且<b>进了这张表也还不算数</b>：还得过 <see cref="IsSidecar"/> 的规则②
        /// ——"旁边必须真有个同名模型"。所以这张表宁可放宽（多列几个后缀，
        /// 代价只是"多滤掉一个恰好同名的文件"），窄了才是问题。</para>
        ///
        /// <para><b>v0.36 补过一轮</b>：起因是拿真实 <c>models\</c> 目录审了一遍（§3.6.1 的教训
        /// ——小目录永远测不出真磁盘上的形态），发现三类"旁边明明站着同名模型、却还在显示"的：
        /// <c>novaExanimeAM_v10.metadata.json</c>（中间多了一层 <c>.metadata</c>）、
        /// <c>Concept_waruochi_v2_merged.sha256</c>（校验和）、
        /// <c>Concept_waruochi_v2_merged.jpeg</c>（Civitai 下载的预览图，
        /// 老版本给的是 <c>.jpeg</c> 而不是 <c>.preview.png</c>）。
        /// 所以补进 <c>.csv</c> / <c>.toml</c> / <c>.md</c> 与四个图片后缀；
        /// 同时把规则②的匹配从"同名前缀"放宽到"逐段前缀"（看清那三条判据的注释）。</para>
        /// </summary>
        private static readonly string[] MetaExt =
            { ".json", ".yaml", ".yml", ".txt", ".csv", ".toml", ".md",
              ".png", ".jpg", ".jpeg", ".webp" };

        /// <summary>
        /// 模型常见的文件后缀 —— <b>仅用于 "<c>foo.json</c> 旁边是不是真有个叫 <c>foo</c> 的模型"</b>
        /// 这一个判断（见 <see cref="IsSidecar"/> 的规则②）。
        ///
        /// <para>它<b>不是</b>白名单：模型自己从不被这个数组筛掉，它只影响"某个元数据文件要不要藏"。
        /// 所以某个新格式不在表里，后果是<b>多显示一个 json</b>（看得见），而不是少显示一个模型。</para>
        /// </summary>
        private static readonly string[] ModelExt =
            { ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".sft", ".gguf", ".onnx", ".pkl", ".patch" };

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
            /// <summary>
            /// 被隐藏的附属文件说明（v0.36）；没有隐藏时为空串。
            ///
            /// <para>⚠ 它<b>不</b>让 <see cref="CountText"/> / <see cref="SizeText"/> 跟着变小 ——
            /// 那两个是<b>磁盘事实</b>（"这个目录里到底有什么"），隐藏是<b>显示层</b>的行为。
            /// 两个数字同时在场、再加这一行说明，账才对得上：卡片头写「7 个文件」、
            /// 下面列 3 行、小字说"已隐藏 4 个附属文件" —— 换句话说，<b>不许静默</b>。</para>
            /// </summary>
            public string HiddenText { get; set; } = "";
            /// <summary>
            /// 本层条目（文件在前、子目录在后）。
            ///
            /// <para>⚠ 语义随"在哪一层"而变：<c>ReadFolder</c> 产出的<b>数据层</b>是<b>全量</b>
            /// （不裁剪、不过滤）；交给界面的那份是 <c>Filter</c> → <c>Visible</c> → <c>Clip</c>
            /// 之后的结果（剔掉附属文件、最多 <see cref="MaxEntriesShown"/> 条）。
            /// 别拿前者当后者用 —— 这正是 v0.34 搜索假否定与 v0.36 列表全是元数据的同一个根。</para>
            /// </summary>
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

                // ⚠ 这里**既不过滤也不裁剪**：Entries 保留磁盘上的全量，
                //   "剔掉附属文件"与"最多显示 50 条"两件事都发生在显示层
                //   （Filter → Visible → Clip）。理由有两条，都踩过：
                //   ① v0.34 在这里就砍到前 50 项 —— 于是**搜索也只能在那 50 项里搜**：
                //      磁盘上 814 个 LoRA，敲自己模型的名字却命中 0 张卡，模型其实好好躺着；
                //   ② v0.36 若在这里就剔掉 .civitai.info 之类，CountText / SizeText 也会
                //      跟着变小 —— 那两个数该是**磁盘事实**，隐藏是显示层的事，两笔账分开记。
                //   「数据层给全量、显示层负责裁剪」与进度条那条「比例是模型、像素是投影」同源。
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
        /// 按关键字过滤，并产出**可直接绑定到界面的那一份**（**纯函数**，不改动传进来的列表）。
        ///
        /// <para>两种命中方式，粒度不同：分类自己命中（名称 / 用途 / 路径）→ 整张卡片留着；
        /// 只有文件名命中 → 只保留命中的那几个文件。这样"某个 lora 在不在"
        /// 搜出来的就是那一行，而不是一张列着几十个文件的卡片。</para>
        ///
        /// <para><b>匹配一律基于全量条目，裁剪只在这里做</b>：若在数据层就把卡片砍到前 50 项，
        /// 搜索也只剩那 50 项可搜 —— 用户敲自己模型的名字会得到 0 张卡，
        /// 而模型其实好好躺在磁盘上。这正是这一页最该避免的假否定。</para>
        ///
        /// <para>⚠ <b>先剔附属文件、再匹配关键词</b>（v0.36）：这一页要回答的是"我的模型在不在"，
        /// 而 <c>.civitai.info</c> / <c>.preview.png</c> 这类伴生文件在磁盘上永远和模型同名 ——
        /// 搜「nova」时若不过滤，三行里有四行是元数据。所以过滤是<b>整页统一的显示规则</b>，
        /// 带不带关键词都生效。</para>
        /// <para>⚠ 这么做<b>不会</b>造成"搜不到模型"：被隐藏的只有附属文件，
        /// <b>模型文件自己从不被过滤</b>（见 <see cref="MetaExt"/> 那段注释）。
        /// v0.35 那条"搜不到比没列出来更危险"照样守得住。</para>
        /// </summary>
        public static List<ModelFolder> Filter(List<ModelFolder> all, string keyword)
        {
            var result = new List<ModelFolder>();
            if (all == null) return result;

            string k = (keyword ?? "").Trim();

            foreach (var m in all)
            {
                // 显示层过滤：全量仍留在 m.Entries 里（CountText / SizeText 照旧是磁盘事实），
                // 这里只是产出"该画出来的那一份"，并顺带算好隐藏说明。
                var vis = Visible(m, out string hiddenText);

                if (k.Length == 0)
                {
                    result.Add(Clip(m, vis, false, hiddenText));
                    continue;
                }

                if (Hit(m.Title, k) || Hit(m.Desc, k) || Hit(m.Dir, k))
                {
                    result.Add(Clip(m, vis, false, hiddenText));
                    continue;
                }

                var hits = vis.Where(e => Hit(e.Name, k)).ToList();
                if (hits.Count == 0) continue;

                result.Add(Clip(m, hits, true, hiddenText));
            }
            return result;

            static bool Hit(string s, string k) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 把一张卡的全量条目过成"该画出来的那一份"，并给出被隐藏的说明。
        ///
        /// <para><b>纯函数</b>：<paramref name="m"/> 一个字段都不改，返回的是新列表。</para>
        /// </summary>
        private static List<ModelEntry> Visible(ModelFolder m, out string hiddenText)
        {
            hiddenText = "";

            // 先收一份"这个目录里有哪些文件"的名字表 —— 规则②要拿它问"旁边有同名模型吗"
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in m.Entries)
                if (!e.IsDir) names.Add(e.Name);

            var vis = new List<ModelEntry>();
            var kinds = new List<string>();
            int hidden = 0;

            foreach (var e in m.Entries)
            {
                if (!e.IsDir && IsSidecar(e.Name, names, out string kind))
                {
                    hidden++;
                    if (!kinds.Contains(kind)) kinds.Add(kind);   // 同类只报一次，别把说明撑成一行字
                    continue;
                }
                vis.Add(e);
            }

            if (hidden > 0)
                hiddenText = $"已隐藏 {hidden} 个附属文件（{string.Join(" / ", kinds)}）—— 它们不是模型，仍在磁盘上";

            return vis;
        }

        /// <summary>
        /// 这个文件是不是"模型的附属文件"（该从列表里去掉）？
        ///
        /// <para><b>规则①：名字里带明确标记</b> —— 这类<b>绝不可能是模型</b>，可以放心滤。
        /// <c>foo.safetensors.civitai.info</c>、<c>foo.preview.png</c>、
        /// <c>desktop.ini</c> / <c>Thumbs.db</c>、<c>foo.safetensors.sha256</c>，
        /// 以及上游自带的那批占位说明（<c>Put LoRA here.txt</c>）。</para>
        ///
        /// <para><b>规则②：元数据扩展名 + 旁边真有同名模型</b> —— 裸的 <c>foo.json</c> 单独看
        /// 分不清它是 Civitai 伴生还是某份模型的<b>必需组件</b>（<c>diffusers</c> 的
        /// <c>model_index.json</c>、ONNX 模型的 <c>config.json</c> 就是后者，滤掉会让用户
        /// 以为缺文件）。所以只有"旁边站着 <c>foo.safetensors</c> 之类"时才判为附属；
        /// <b>拿不准则保留</b> —— 与 <c>PortGuard</c> 那条 fail-safe 同源：
        /// 假阴性（多显示一个 json）无害，假阳性（藏掉一个必需文件）会让人白折腾。</para>
        ///
        /// <para>规则②的"旁边站着谁"有<b>三种形态</b>，缺一种就会漏（v0.36 实盘踩到第三种）：
        /// <list type="number">
        /// <item><c>foo.json</c> ← <c>foo.safetensors</c>：把文件名当模型名</item>
        /// <item><c>foo.safetensors.json</c> ← <c>foo.safetensors</c>：stem 自己就是模型文件</item>
        /// <item><c>foo.metadata.json</c> / <c>foo.safetensors.metadata.json</c>
        ///       ← <c>foo.safetensors</c>：stem 里<b>多了一段</b>，
        ///       这时要按 <c>.</c> 逐段取前缀再问 —— 光比"整段前缀"会把这一类全漏掉
        ///       （真实目录里 <c>novaExanimeAM_v10.metadata.json</c> 就是这么漏的）。</item>
        /// </list></para>
        /// </summary>
        private static bool IsSidecar(string fileName, HashSet<string> names, out string kind)
        {
            kind = "";

            // ---- 规则①：明确标记 ----
            if (fileName.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase))
            { kind = "Civitai 元数据"; return true; }

            if (fileName.IndexOf(".preview.", StringComparison.OrdinalIgnoreCase) >= 0)
            { kind = "预览图"; return true; }

            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            { kind = "系统文件"; return true; }

            // 校验和文件 —— 语义太明确：它绝无可能是模型、也不可能是模型的必需组件
            // （跟 .json 那种要防 diffusers 必需件的情况不同），所以不用等规则②。
            if (fileName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            { kind = "校验和"; return true; }

            // 上游 Forge 自带的占位说明：Put LoRA here.txt / Place Textual Inversion embeddings here.txt
            // 这批名字是**上游硬编码**的模板文件，不是用户资料，所以按"明确标记"处理。
            // 判据刻意收紧到"Put/Place 开头 + ' here.txt' 结尾"，免得误伤用户自己写的 x here.txt。
            if (fileName.EndsWith(" here.txt", StringComparison.OrdinalIgnoreCase) &&
                (fileName.StartsWith("Put ", StringComparison.OrdinalIgnoreCase) ||
                 fileName.StartsWith("Place ", StringComparison.OrdinalIgnoreCase)))
            { kind = "占位说明"; return true; }

            // ---- 规则②：元数据扩展名 + 旁边真有同名模型 ----
            string ext = Path.GetExtension(fileName);
            if (!MetaExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;

            string stem = fileName.Substring(0, fileName.Length - ext.Length);

            // (2) foo.safetensors.json —— stem 自己就是旁边那个模型文件
            if (names.Contains(stem)) { kind = "同名附属文件"; return true; }

            // (1)(3) 把 stem 在 '.' 处逐段取前缀，逐段去问"这个前缀 + 某个模型后缀"在不在
            //   foo.json                 → 前缀 foo                  → foo.safetensors ✓
            //   foo.metadata.json        → 前缀 foo                  → ✓（第一段就命中）
            //   foo.safetensors.metadata.json → 前缀 foo → foo.safetensors ✓
            //   这样"中间多一段"的形态不用单独写规则 —— 逐段扫描天然覆盖。
            int p = 0;
            while (true)
            {
                int dot = stem.IndexOf('.', p);
                string prefix = dot < 0 ? stem : stem.Substring(0, dot);
                if (prefix.Length > 0)
                {
                    foreach (var me in ModelExt)
                        if (names.Contains(prefix + me)) { kind = "同名附属文件"; return true; }
                }
                if (dot < 0) break;
                p = dot + 1;
            }

            return false;
        }

        /// <summary>
        /// 把一张卡裁成"界面要显示的那一份"（最多 <see cref="MaxEntriesShown"/> 条）。
        /// 文件在前、目录在后，所以先被砍掉的总是子目录 —— 更次要的那批。
        ///
        /// <para><b>只改副本</b>：<see cref="Clone"/> 已经浅拷贝过 <c>Entries</c>，
        /// 这里的取子集都生成新列表，原件一行不动（<c>_modeltest</c> 有断言盯着）。</para>
        /// </summary>
        private static ModelFolder Clip(ModelFolder m, List<ModelEntry> rows, bool hitMode, string hiddenText)
        {
            var copy = Clone(m);
            copy.HiddenText = hiddenText;

            if (rows.Count > MaxEntriesShown)
            {
                copy.Entries = rows.Take(MaxEntriesShown).ToList();
                copy.MoreText = hitMode
                    ? $"仅显示前 {MaxEntriesShown} 项（共 {rows.Count} 项命中）"
                    : $"仅显示前 {MaxEntriesShown} 项（此目录共 {rows.Count} 项）";
            }
            else
            {
                copy.Entries = new List<ModelEntry>(rows);
            }

            // 命中模式下条数已经不是"这个目录总共多少"，计数跟卡片对不上会看着像丢了东西
            if (hitMode)
                copy.CountText = $"{rows.Count} 项命中（此目录共 {m.Entries.Count} 项）";

            return copy;
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
            HiddenText = m.HiddenText,
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
