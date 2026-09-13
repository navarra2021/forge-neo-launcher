using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 界面动效的小工具。
    ///
    /// <para><b>为什么单独抽出来</b>：这类"没有百分比的等待"动效很容易写成看着能跑、
    /// 实际不可控的代码。抽出来之后，它可以在一个独立的测试工程里
    /// 被真实 WPF 计时系统驱动一遍 —— 不需要开窗口，也能验证"动画到底动没动"。</para>
    ///
    /// <para><b>为什么是 public</b>（与 <c>Theme</c> 一样）：引用启动器编译产物的测试工程
    /// 只能看见 public 类型。若保持 internal，那边就只能用
    /// <c>&lt;Compile Include&gt;</c> 把源码再编一遍 —— 那样测的是另一份副本。</para>
    /// </summary>
    public static class UiFx
    {
        /// <summary>
        /// 一个永不停止的横向滑动动画：把某个 <see cref="TranslateTransform"/> 从
        /// <paramref name="from"/> 平移到 <paramref name="to"/>，循环往复。
        ///
        /// <para>⚠ 用 <c>BeginAnimation</c> 直接作用在 Transform 上，而不是走
        /// <c>Storyboard.Begin()</c>：无参 <c>Begin()</c> 启动的 Storyboard 是
        /// <b>不可控</b>的，之后调 <c>Stop()</c> 会抛 <c>InvalidOperationException</c>，
        /// 结果就是"想停停不掉"、每次部署都漏一个还在跑的动画。
        /// 直接 <c>BeginAnimation</c> 则传 <c>null</c> 即可干净地停掉。</para>
        /// </summary>
        public static DoubleAnimation Marquee(double from, double to, double seconds)
        {
            return new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromSeconds(seconds),
                RepeatBehavior = RepeatBehavior.Forever
            };
        }

        /// <summary>开始滑动</summary>
        public static void StartMarquee(TranslateTransform target, double from, double to, double seconds)
        {
            target.BeginAnimation(TranslateTransform.XProperty, Marquee(from, to, seconds));
        }

        /// <summary>停止并清掉动画，元素回到原始位置</summary>
        public static void StopMarquee(TranslateTransform? target)
        {
            target?.BeginAnimation(TranslateTransform.XProperty, null);
        }

        /// <summary>
        /// 进度条填充宽度：百分比 → 像素。
        ///
        /// <para>夹取边界（负值 / 超 100 / NaN）都在这里做，免得调用点各写一遍；
        /// <paramref name="trackWidth"/> 为 0 时给个兜底宽 —— 控件首帧还没量到实际宽度，
        /// 下一秒刷新就会自我修正，比画一条 0 宽度的条要好。</para>
        /// </summary>
        public static double FillWidth(double percent, double trackWidth)
        {
            if (double.IsNaN(percent)) percent = 0;
            double w = trackWidth > 0 ? trackWidth : FallbackTrackWidth;

            double r = percent / 100.0;
            if (r < 0) r = 0;
            if (r > 1) r = 1;
            return w * r;
        }

        /// <summary>还没量到宽度时的兜底轨道宽（约等于部署卡片的内容宽）</summary>
        public const double FallbackTrackWidth = 420;

        // ===================== 入场动效 =====================

        /// <summary>入场时内容先下移多少像素，再浮回原位</summary>
        public const double EntranceRise = 34;

        /// <summary>入场时长（秒）。0.42 是"看得到但绝不拖沓"的一档</summary>
        public const double EntranceSeconds = 0.42;

        /// <summary>
        /// 入场动画的"把手"：两段式（先摆姿势、后开演）需要它把载体带过中间这段时间。
        /// </summary>
        public sealed class EntranceHandle
        {
            internal UIElement? Content;
            internal TranslateTransform? Shift;

            /// <summary>是否已经放行（<see cref="FireEntrance"/> 过）</summary>
            public bool Fired { get; internal set; }

            /// <summary>是否已经取消（<see cref="CancelEntrance"/> 过）</summary>
            public bool Cancelled { get; internal set; }
        }

        /// <summary>
        /// 入场动画的参数与方向（单独暴露出来，测试才能**确定性地**读到 From/To/时长/缓动 ——
        /// 挂在控件上的动画没有公开 API 可取，同 <see cref="Marquee"/> 的做法）。
        ///
        /// <para><b>方向</b>：起点是「下移 <see cref="EntranceRise"/>」而不是「上移」。
        /// 下移意味着内容起始位置在窗口下缘之外多出来一点（被窗口自然裁掉），
        /// 上移则会让顶部先被切掉一块 —— 前者看起来才像"浮上来"。</para>
        /// </summary>
        public static DoubleAnimation EntranceShift()
        {
            return new DoubleAnimation
            {
                From = EntranceRise,
                To = 0,
                Duration = TimeSpan.FromSeconds(EntranceSeconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }

        /// <summary>入场用的淡入动画</summary>
        public static DoubleAnimation EntranceFade()
        {
            return new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromSeconds(EntranceSeconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }

        /// <summary>
        /// 第一段：**只摆姿势，不挂动画** —— 把内容放到起点（偏下 + 透明）。
        ///
        /// <para><b>为什么必须两段式</b>（v0.22 的教训）：把动画直接挂在 <c>Loaded</c> 里是
        /// <b>看不到的</b>。真机截帧实测：<c>Loaded</c> 只代表逻辑树就绪，那时窗口在屏幕上
        /// 一个像素都没画出来；而动画时钟不会等窗口 —— 等首帧真正上屏，整个 0.4 秒早走完了，
        /// 只剩收尾的 2~3px（肉眼等于"直接就进入界面了"）。</para>
        ///
        /// <para><b>为什么这里不挂"暂停的时钟"</b>：试过
        /// <c>CreateClock()</c> → <c>Controller.Begin()</c> → <c>Pause()</c>，
        /// 想让它停在 0 时刻。但 <c>Begin()</c> 只是把时钟排进下一帧（pending begin）：
        /// 真窗口里它会照常推进（实测 <c>Y</c> 已走到 20.99），离屏时又完全不驱动
        /// —— 行为不确定，不能当"起点态"的依据。
        /// <b>直接写属性值才是确定的</b>。</para>
        /// </summary>
        public static EntranceHandle ArmEntrance(UIElement content, TranslateTransform? shift)
        {
            var h = new EntranceHandle { Content = content, Shift = shift };
            if (shift != null) shift.Y = EntranceRise;   // 起点：偏下
            content.Opacity = 0;                         // 起点：透明
            return h;
        }

        /// <summary>
        /// 第二段：开演。
        ///
        /// <para>先把<b>基值</b>落到终点（Y=0 / Opacity=1），再挂上从起点出发的动画：
        /// 此刻屏幕上是第一段留下的起点态，与动画起点完全重合，所以不会"闪一下"。
        /// 动画 <c>FillBehavior = Stop</c>，播完正好把控制权交还给基值（= 终点），
        /// 既不跳变，也不留一个常驻动画在跑。</para>
        /// </summary>
        public static void FireEntrance(EntranceHandle? h)
        {
            if (h == null || h.Fired || h.Cancelled) return;
            h.Fired = true;

            if (h.Shift != null)
            {
                h.Shift.Y = 0;                                    // 基值 = 终点
                h.Shift.BeginAnimation(TranslateTransform.YProperty, EntranceShift());
            }
            if (h.Content != null)
            {
                h.Content.Opacity = 1;                            // 基值 = 终点
                h.Content.BeginAnimation(UIElement.OpacityProperty, EntranceFade());
            }
        }

        /// <summary>
        /// 还没开演就取消：直接恢复可见（相当于从没摆过姿势）。
        ///
        /// <para><b>兜底用</b>：万一 <c>ContentRendered</c> 没来（例如窗口以最小化状态启动），
        /// 停在起点的内容会一直"偏下且透明"—— 也就是界面一片空白。
        /// 宁可不动，也绝不能空白，所以到点没放行就调这里。</para>
        /// </summary>
        public static void CancelEntrance(EntranceHandle? h)
        {
            if (h == null || h.Fired || h.Cancelled) return;
            h.Cancelled = true;
            ResetEntrance(h.Content, h.Shift);
        }

        /// <summary>挂上并立刻开演（把两段合成一步；测试与"立即播"场景用）</summary>
        public static EntranceHandle PlayEntrance(UIElement content, TranslateTransform? shift)
        {
            var h = ArmEntrance(content, shift);
            FireEntrance(h);
            return h;
        }

        /// <summary>
        /// 摘掉动画并把值复位到终点（Y=0 / 不透明）。
        ///
        /// <para>复位这一步不能省：第一段是<b>直接写属性值</b>摆的姿势，
        /// 只 <c>BeginAnimation(prop, null)</c> 是清不掉那些值的 —— 内容会留在原地偏下。</para>
        /// </summary>
        public static void ClearEntrance(UIElement content, TranslateTransform? shift)
        {
            ResetEntrance(content, shift);
        }

        private static void ResetEntrance(UIElement? content, TranslateTransform? shift)
        {
            if (shift != null)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.Y = 0;
            }
            if (content != null)
            {
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
            }
        }
    }
}
