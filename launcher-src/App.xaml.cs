using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ForgeNeoLauncher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ApplyCrashGuards();
            base.OnStartup(e);
        }

        // =====================================================================
        //  未处理异常的兜底
        //
        //  为什么必须有：WPF 应用里任何一个**未处理异常**都会直接终止进程 ——
        //  表现形式就是"窗口凭空消失"（闪退），日志区什么都没留下，
        //  使用者只会说"我点了一下它就没了"。v0.16 就踩过一次：
        //  后台线程写控件抛 InvalidOperationException，整个启动器瞬间消失。
        //
        //  这层兜底做三件事：① 写一份崩溃日志（带堆栈，方便定位）；
        //  ② UI 线程的异常吞掉并让程序继续活着；③ 第一次出问题时告诉使用者
        //  日志在哪 —— 之后再出只记日志不弹窗，免得定时器里连抛出刷屏。
        // =====================================================================

        /// <summary>崩溃日志路径（包根目录；取不到就退到临时目录）</summary>
        private static string CrashLogPath()
        {
            try { return Path.Combine(AppPaths.Root, "launcher-crash.log"); }
            catch
            {
                try { return Path.Combine(AppContext.BaseDirectory, "launcher-crash.log"); }
                catch { return Path.Combine(Path.GetTempPath(), "launcher-crash.log"); }
            }
        }

        private static int crashCount;
        private static bool dialogShown;

        private static void WriteCrashLog(string where, Exception? ex)
        {
            try
            {
                string path = CrashLogPath();

                // 别让日志无限增长：超过 1 MB 就重开一份
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(path);
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + where + " ====");
                sb.AppendLine(ex == null ? "(没有异常对象)" : ex.ToString());
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void ApplyCrashGuards()
        {
            // ① UI 线程：吞掉，让窗口继续活着
            DispatcherUnhandledException += (s, ev) =>
            {
                ev.Handled = true;
                WriteCrashLog("UI 线程", ev.Exception);
                TellUserOnce(ev.Exception);
            };

            // ② 后台线程：CLR 会直接结束进程，只能尽量把原因记下来
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                WriteCrashLog("后台线程", ev.ExceptionObject as Exception);
            };

            // ③ 没人 await 的 Task：默认会被静默丢弃，记一笔
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                WriteCrashLog("未观察的任务", ev.Exception);
                ev.SetObserved();
            };
        }

        private static void TellUserOnce(Exception? ex)
        {
            crashCount++;
            if (dialogShown || crashCount > 1) return;   // 只弹一次，重复出错只记日志
            dialogShown = true;

            try
            {
                MessageBox.Show(
                    "启动器遇到了一个内部错误，已记录并继续运行。\n\n" +
                    "如果界面显示不正常，请关掉启动器重新打开一次。\n\n" +
                    "崩溃日志：\n" + CrashLogPath() + "\n\n" +
                    "错误摘要：\n" + Short(ex),
                    "Forge Neo 启动器", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }

        private static string Short(Exception? ex)
        {
            if (ex == null) return "(未知)";
            string s = ex.GetType().Name + ": " + ex.Message;
            return s.Length <= 300 ? s : s.Substring(0, 300) + " …";
        }
    }
}
