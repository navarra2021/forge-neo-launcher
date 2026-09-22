using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 把目录送进 Windows 回收站 —— 「卸载扩展」用它。
    ///
    /// <para><b>为什么不直接 <c>Directory.Delete(path, true)</c></b>：卸载是不可逆操作，
    /// 而用户点「卸载」时心里想的往往是"先关掉试试"。删错了（选错行、改名后重装过、
    /// 想留一份对照）在硬删下没有任何补救；走回收站则只需右键还原。
    /// 代价不过是多一次 shell 调用。</para>
    ///
    /// <para>⚠ <b>回收站不可用时，这里返回失败，绝不降级为硬删</b>。
    /// <c>SHFileOperation</c> 在「目录在网络盘 / 超过 MAX_PATH / 回收站被组策略禁用」时
    /// 会失败 —— 那时把错误如实报给用户，让他自己决定（比如手动去资源管理器里删），
    /// 比程序替他决定"那就算了直接删掉吧"安全得多。</para>
    ///
    /// <para>⚠⚠ <b>但"失败"要看目录在不在，不能只看返回值</b>（v0.36 修正）。
    /// 本机实测（2026-09-22）：<c>SHFileOperationW</c> 返回 <b>2</b>（ERROR_FILE_NOT_FOUND）
    /// 而目录<b>确实已经进了回收站</b> —— <c>C:\$Recycle.Bin\&lt;SID&gt;\</c> 里躺着它的
    /// <c>$R…</c> 条目。某些 shell 扩展 / 回收站状态下这个老接口会回一个错的码。
    /// 原先"<c>rc != 0</c> 就当没删"的写法会让界面告诉用户"卸载失败（目录未改动）"，
    /// 可插件其实已经不在磁盘上了 —— 列表里还留着它，用户再点一次卸载会得到"目录不存在"。
    /// <b>接口的返回值只是提示，磁盘才是事实</b>（与端口归属那条 fail-safe 同源）。</para>
    /// </summary>
    public static class RecycleBin
    {
        // ---- Win32 ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;

        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;   // ← 送回收站的关键
        private const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        // ---- 问回收站"你现在装着多少件" ----
        // 用途：让「已移入回收站」这句话是**确认过的**，不是"我们请求了 ALLOWUNDO，所以
        // 应该是进回收站了吧"。删除类操作不该靠猜。
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        /// <summary>该盘回收站里当前有多少件；查不到返回 -1（别当成 0）</summary>
        private static long BinCount(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;

                var info = new SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                int hr = SHQueryRecycleBinW(root, ref info);
                return hr == 0 ? info.i64NumItems : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 把 <paramref name="path"/> 指向的目录送进回收站。
        /// </summary>
        /// <returns>true = 目录已经不在磁盘上了；false = 没删（<paramref name="error"/> 写明原因）。</returns>
        public static bool TryDelete(string path, out string error)
            => TryDelete(path, out error, out _);

        /// <summary>
        /// 同上，并额外回答"确认它进了回收站吗"。
        /// </summary>
        /// <param name="confirmedInBin">
        /// true = 回收站的件数确实涨了（东西在里面）；
        /// false = 没确认成（回收站查询不可用，或东西根本没进回收站）——
        /// <b>这时调用方该把话说得含糊一点</b>，别替回收站打包票。
        /// </param>
        public static bool TryDelete(string path, out string error, out bool confirmedInBin)
        {
            error = "";
            confirmedInBin = false;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }

                string full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) { error = "目录不存在"; return false; }

                // SHFileOperation 是老接口，不支持 \\?\ 长路径前缀；超过 MAX_PATH 就会失败。
                // 这里提前判出来，给一句人能看懂的话 —— 而不是让它回一个数字错误码。
                if (full.Length >= 260)
                {
                    error = $"路径过长（{full.Length} 字符），Windows 回收站接口不支持 260 字符以上的路径";
                    return false;
                }

                long binBefore = BinCount(full);

                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    // ⚠ pFrom 必须以**双** null 结尾：一个由 marshaler 补，一个自己补。
                    //   少一个的话它会把后面的东西当成第二个路径，行为不可预料。
                    pFrom = full + "\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
                };

                int rc = SHFileOperationW(ref op);

                // ★ 判据取**文件系统**，而不是 rc —— 顺序不能颠倒。
                //   rc 只是提示；"目录还在不在"才是这件事有没有发生的事实。
                bool gone = !Directory.Exists(full);

                if (!gone)
                {
                    if (rc != 0) error = $"回收站接口返回错误码 {rc}";
                    else if (op.fAnyOperationsAborted) error = "操作被中止";
                    else error = "调用返回成功，但目录仍然存在（可能被占用）";
                    return false;
                }

                // 目录不在了：操作确实发生了。再问一次回收站的件数，确认它落在里面 ——
                // 注意"没涨"不等于"没进回收站"（查询本身可能不可用），所以两个值分开返回。
                long binAfter = BinCount(full);
                confirmedInBin = binBefore >= 0 && binAfter > binBefore;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
