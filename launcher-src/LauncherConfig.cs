using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 启动器配置层：读写 exe 同目录的 launcher.cfg。
    ///
    /// 格式为「每行一条 key=value」，# 开头为注释，多值用 | 分隔（Windows 路径里不会出现 |）。
    /// 由启动器自动维护，也允许手工编辑——手工加的行不会被丢弃，除非同名键被程序改写。
    ///
    /// 之所以做成通用的键值容器而不是每个功能各写一对 Load/Save：
    /// 主题、高级选项等都要往同一个文件里写，各写各的会互相覆盖。
    /// </summary>
    internal static class LauncherConfig
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前使用的配置文件路径（Load 时确定）</summary>
        public static string FilePath { get; private set; } = "";

        public static void Load(string path)
        {
            FilePath = path;
            Map.Clear();
            try
            {
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            catch { /* 配置读不出来就全用默认值，不该因此打不开启动器 */ }
        }

        public static string Get(string key, string fallback = "")
            => Map.TryGetValue(key, out var v) ? v : fallback;

        public static bool GetBool(string key, bool fallback = false)
            => Map.TryGetValue(key, out var v) ? v.Trim() == "1" : fallback;

        public static int GetInt(string key, int fallback)
            => Map.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out var n) ? n : fallback;

        /// <summary>读取以 | 分隔的多值（自动去空、去重）</summary>
        public static List<string> GetList(string key)
        {
            var list = new List<string>();
            foreach (var part in Get(key).Split('|'))
            {
                var t = part.Trim();
                if (t.Length > 0 && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
            }
            return list;
        }

        public static void Set(string key, string value) => Map[key] = value ?? "";
        public static void SetBool(string key, bool value) => Map[key] = value ? "1" : "0";
        public static void SetInt(string key, int value) => Map[key] = value.ToString();
        public static void SetList(string key, IEnumerable<string> values)
            => Map[key] = string.Join("|", values);

        public static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath)) return;

                var lines = new List<string>
                {
                    "# Forge Neo 启动器配置 —— 由启动器自动维护，也可手工编辑（改完重启启动器生效）"
                };

                // DarkTheme 放第一条，方便人工查看
                if (Map.ContainsKey("DarkTheme")) lines.Add("DarkTheme=" + Map["DarkTheme"]);

                foreach (var k in Map.Keys
                                     .Where(k => !k.Equals("DarkTheme", StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(k + "=" + Map[k]);
                }

                File.WriteAllLines(FilePath, lines);
            }
            catch { /* 写不进去就算了，不能因为保存失败把启动器搞崩 */ }
        }
    }
}
