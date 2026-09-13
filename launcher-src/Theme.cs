using System.Windows;
using System.Windows.Media;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 主题管理：把亮色/暗色两套界面资源应用到窗口。
    /// 核心思路是替换窗口 Resources 里带 key 的 Brush，
    /// XAML 里用 {DynamicResource xxx} 引用的控件会即时刷新。
    ///
    /// 注意：日志区（Console）的颜色是硬编码的黑色，不参与主题切换，
    /// 因为无论亮暗主题，日志区都保持黑色终端观感。
    /// </summary>
    public static class Theme
    {
        public static void ApplyLight(FrameworkElement root)
        {
            SetBrush(root, "WindowBg", MakeGradient("#F7F8FA", "#FFFFFF", "#EEF1F5"));
            SetBrush(root, "WindowEdge", MakeBrush("#C9D2DE"));
            SetBrush(root, "SidebarBg", MakeBrush("#EEF1F5"));
            SetBrush(root, "SidebarBorder", MakeBrush("#E1E6EC"));
            SetBrush(root, "SubtitleText", MakeBrush("#8A93A0"));
            SetBrush(root, "MenuIdleFore", MakeBrush("#5B6472"));
            SetBrush(root, "MenuBtnHover", MakeBrush("#E7EBF0"));
            SetBrush(root, "TopBarBg", MakeBrush("#FFFFFF"));
            SetBrush(root, "TopBarBorder", MakeBrush("#E1E6EC"));
            SetBrush(root, "TopBtnBg", MakeBrush("#FFFFFF"));
            SetBrush(root, "TopBtnText", MakeBrush("#3A4452"));
            SetBrush(root, "TopBtnBorder", MakeBrush("#DFE4EA"));
            SetBrush(root, "TopBtnHover", MakeBrush("#E9EEF4"));
            SetBrush(root, "StateText", MakeBrush("#2E343E"));
            SetBrush(root, "StateDetail", MakeBrush("#7A8492"));
            SetBrush(root, "StateDotRing", MakeBrush("#FFFFFF"));
            SetBrush(root, "ProgressBack", MakeBrush("#EAEEF3"));
            SetBrush(root, "ProgressText", MakeBrush("#3A4452"));
            SetBrush(root, "ProgressDetail", MakeBrush("#7A8492"));
            SetBrush(root, "CardBg", MakeBrush("#FFFFFF"));
            SetBrush(root, "CardBorder", MakeBrush("#E1E6EC"));
            SetBrush(root, "ListHeaderBg", MakeBrush("#F5F7FA"));
            SetBrush(root, "StatusDotIdle", MakeBrush("#9E9E9E"));
            SetBrush(root, "StatusTextIdle", MakeBrush("#6B7280"));

            // 自绘标题栏
            SetBrush(root, "TitleBarBg", MakeBrush("#FFFFFF"));
            SetBrush(root, "TitleBarBorder", MakeBrush("#E1E6EC"));
            SetBrush(root, "TitleBarText", MakeBrush("#2E343E"));
            SetBrush(root, "CaptionBtnHover", MakeBrush("#E9EEF4"));
            SetBrush(root, "CaptionBtnPress", MakeBrush("#DCE3EB"));
            SetBrush(root, "CaptionCloseHover", MakeBrush("#E24B4A"));

            // 高级选项：开关
            SetBrush(root, "SwitchOffBg", MakeBrush("#DDE3EA"));
            SetBrush(root, "SwitchKnob", MakeBrush("#FFFFFF"));
        }

        public static void ApplyDark(FrameworkElement root)
        {
            SetBrush(root, "WindowBg", MakeGradient("#1B1F26", "#1E232C", "#181C22"));
            SetBrush(root, "WindowEdge", MakeBrush("#3A4350"));
            SetBrush(root, "SidebarBg", MakeBrush("#1E232B"));
            SetBrush(root, "SidebarBorder", MakeBrush("#2A3038"));
            SetBrush(root, "SubtitleText", MakeBrush("#7E8894"));
            SetBrush(root, "MenuIdleFore", MakeBrush("#9AA4B2"));
            SetBrush(root, "MenuBtnHover", MakeBrush("#262B33"));
            SetBrush(root, "TopBarBg", MakeBrush("#1C2027"));
            SetBrush(root, "TopBarBorder", MakeBrush("#2A3038"));
            SetBrush(root, "TopBtnBg", MakeBrush("#23272F"));
            SetBrush(root, "TopBtnText", MakeBrush("#C7CFDA"));
            SetBrush(root, "TopBtnBorder", MakeBrush("#2A3038"));
            SetBrush(root, "TopBtnHover", MakeBrush("#2E343E"));
            SetBrush(root, "StateText", MakeBrush("#C7CFDA"));
            SetBrush(root, "StateDetail", MakeBrush("#6B7683"));
            SetBrush(root, "StateDotRing", MakeBrush("#14161A"));
            SetBrush(root, "ProgressBack", MakeBrush("#24292F"));
            SetBrush(root, "ProgressText", MakeBrush("#DDE3EA"));
            SetBrush(root, "ProgressDetail", MakeBrush("#7E8894"));
            SetBrush(root, "CardBg", MakeBrush("#1E232B"));
            SetBrush(root, "CardBorder", MakeBrush("#2A3038"));
            SetBrush(root, "ListHeaderBg", MakeBrush("#242A33"));
            SetBrush(root, "StatusDotIdle", MakeBrush("#757575"));
            SetBrush(root, "StatusTextIdle", MakeBrush("#9E9E9E"));

            // 自绘标题栏（深色：标题栏用比侧栏略亮的层次，跟内容区区分开）
            SetBrush(root, "TitleBarBg", MakeBrush("#232932"));
            SetBrush(root, "TitleBarBorder", MakeBrush("#333A44"));
            SetBrush(root, "TitleBarText", MakeBrush("#DDE3EA"));
            SetBrush(root, "CaptionBtnHover", MakeBrush("#323945"));
            SetBrush(root, "CaptionBtnPress", MakeBrush("#3D4550"));
            SetBrush(root, "CaptionCloseHover", MakeBrush("#E24B4A"));

            // 高级选项：开关
            SetBrush(root, "SwitchOffBg", MakeBrush("#39414D"));
            SetBrush(root, "SwitchKnob", MakeBrush("#E4E9F0"));
        }

        // 辅助：设置 root.Resources 下的某个 Brush
        private static void SetBrush(FrameworkElement root, string key, Brush brush)
        {
            root.Resources[key] = brush;
        }

        // 辅助：纯色
        private static SolidColorBrush MakeBrush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // 辅助：三停渐变
        private static LinearGradientBrush MakeGradient(string c1, string c2, string c3)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0.0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c2), 0.5));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c3), 1.0));
            return g;
        }
    }
}
