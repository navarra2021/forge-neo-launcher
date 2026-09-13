using System.Collections.Generic;
using System.IO;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 首页「文件夹」区那九张卡片的数据来源。
    ///
    /// <para>每个目录名都在<b>上游源码里核过</b>，不靠记忆（依据写在 <see cref="AppPaths"/> 的
    /// 派生路径注释里）。写错一个名字的后果很隐蔽：点开是个空目录，不报错、没日志 ——
    /// 所以上游换版本时这几个名字要重新核一遍，别照着上一版抄。</para>
    ///
    /// <para>排在前面的是「改造包」的目录，后面五个是产物目录，顺序即界面上的顺序（每行三张）。</para>
    /// </summary>
    internal static class HomeFolders
    {
        /// <summary>卡片项。⚠ 成员一律写成属性 —— WPF 绑定只反射属性，公共字段会被静默忽略（UI 一片空白且不报错）</summary>
        internal sealed class HomeFolderItem
        {
            /// <summary>稳定标识，供代码定位某一项（如把「模型目录」换成 A1111 那个目录）</summary>
            public string Key { get; set; } = "";
            /// <summary>卡片标题</summary>
            public string Title { get; set; } = "";
            /// <summary>副标题：相对包根的路径（比全路径好认）；全路径挂在 ToolTip 上</summary>
            public string Sub { get; set; } = "";
            /// <summary>点击后打开的目录（绝对路径）</summary>
            public string FullPath { get; set; } = "";
            /// <summary>Segoe MDL2 Assets 字形</summary>
            public string Glyph { get; set; } = "";
        }

        // Segoe MDL2 Assets。
        //
        // ⚠ 这几个编码是**渲染出来确认过**的，不是凭记忆写的 —— 字形编码写错不报错，
        //   只会画出个不相干的图形（甚至空白），只有肉眼看得见。
        //   实测踩到的坑：E8B7 通常被当成「Folder」，但在本机字体版本里画出来是个**折角页面**；
        //   真正的文件夹形态在 EC50（打开的文件夹夹着一张纸）/ EC51（文件夹里插着书）。
        //   复核办法：把某一段编码整段渲染成对照图，肉眼比对。
        private const string GlyphHome   = "\uE80F";   // Home（房子）
        private const string GlyphFolder = "\uEC51";   // 文件夹（里头有东西）
        private const string GlyphOpen   = "\uEC50";   // 打开的文件夹（有产出物掉出来）

        /// <summary>模型目录卡的标识（主程序要按它替换成 A1111 目录）</summary>
        public const string KeyModels = "models";

        public static List<HomeFolderItem> Build()
        {
            return new List<HomeFolderItem>
            {
                new HomeFolderItem { Key = "root",  Title = "根目录",     Sub = AppPaths.Root, Glyph = GlyphHome,
                                     FullPath = AppPaths.Root },
                new HomeFolderItem { Key = "ext",   Title = "扩展文件夹", Sub = "extensions",  Glyph = GlyphFolder,
                                     FullPath = AppPaths.ExtensionsDir },
                new HomeFolderItem { Key = KeyModels, Title = "模型目录", Sub = "models",      Glyph = GlyphFolder,
                                     FullPath = AppPaths.ModelsDir },
                new HomeFolderItem { Key = "tmp",   Title = "临时文件夹", Sub = "tmp",         Glyph = GlyphFolder,
                                     FullPath = AppPaths.TmpDir },

                new HomeFolderItem { Key = "extras", Title = "超分输出",  Sub = Rel(Path.Combine("output", "extras-images")),
                                     Glyph = GlyphOpen, FullPath = AppPaths.ExtrasImagesDir },
                new HomeFolderItem { Key = "t2i-g",  Title = "文生图（网格）", Sub = Rel(Path.Combine("output", "txt2img-grids")),
                                     Glyph = GlyphOpen, FullPath = AppPaths.Txt2ImgGridsDir },
                new HomeFolderItem { Key = "t2i-i",  Title = "文生图（单图）", Sub = Rel(Path.Combine("output", "txt2img-images")),
                                     Glyph = GlyphOpen, FullPath = AppPaths.Txt2ImgImagesDir },
                new HomeFolderItem { Key = "i2i-g",  Title = "图生图（网格）", Sub = Rel(Path.Combine("output", "img2img-grids")),
                                     Glyph = GlyphOpen, FullPath = AppPaths.Img2ImgGridsDir },
                new HomeFolderItem { Key = "i2i-i",  Title = "图生图（单图）", Sub = Rel(Path.Combine("output", "img2img-images")),
                                     Glyph = GlyphOpen, FullPath = AppPaths.Img2ImgImagesDir }
            };
        }

        /// <summary>副标题里显示的相对路径（Windows 上用反斜杠，跟资源管理器一致）</summary>
        private static string Rel(string relative) => relative.Replace('/', '\\');
    }
}
