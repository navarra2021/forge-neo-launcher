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

        /// <summary>
        /// 把 <paramref name="path"/> 指向的目录送进回收站。
        /// </summary>
        /// <returns>true = 已进回收站；false = 没删（<paramref name="error"/> 写明原因）。</returns>
        public static bool TryDelete(string path, out string error)
        {
            error = "";
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

                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    // ⚠ pFrom 必须以**双** null 结尾：一个由 marshaler 补，一个自己补。
                    //   少一个的话它会把后面的东西当成第二个路径，行为不可预料。
                    pFrom = full + "\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
                };

                int rc = SHFileOperationW(ref op);
                if (rc != 0) { error = $"回收站接口返回错误码 {rc}"; return false; }
                if (op.fAnyOperationsAborted) { error = "操作被中止"; return false; }

                // 回读确认：有些情况下接口报成功但目录还在（例如被占用）
                if (Directory.Exists(full)) { error = "调用返回成功，但目录仍然存在（可能被占用）"; return false; }

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
