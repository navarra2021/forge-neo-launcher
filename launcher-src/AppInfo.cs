using System;
using System.Reflection;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 启动器自身的版本信息（与 .csproj 的 &lt;Version&gt; 保持同步）。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>
        /// 启动器版本号。0.x 阶段采用「线性递进」编号：
        /// 0.1 → 0.9 → 0.10 → 0.19 → 0.100（十进制进位，不是语义化的 major.minor.patch）。
        /// 故用两段式写法，以便自然支持 0.10、0.100 这类编号。
        /// </summary>
        public const string Version = "0.28";

        /// <summary>版本代号 / 里程碑说明</summary>
        public const string Codename = "未雨绸缪";

        /// <summary>带 v 前缀的简短版本（如 v0.9）</summary>
        public static string Short => "v" + Version;

        /// <summary>完整显示（如 v0.9 · 高级选项）</summary>
        public static string Full => $"v{Version} · {Codename}";

        /// <summary>从程序集读取的实际版本（编译后与 Version 一致；异常时回退常量）</summary>
        public static string AssemblyVersionText
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
                    {
                        // InformationalVersion 可能带 +commit 后缀，只取主版本
                        string v = info.InformationalVersion;
                        int plus = v.IndexOf('+');
                        return plus > 0 ? v.Substring(0, plus) : v;
                    }
                    var fv = asm.GetName().Version;
                    if (fv != null) return $"{fv.Major}.{fv.Minor}.{fv.Build}";
                }
                catch { }
                return Version;
            }
        }
    }
}
