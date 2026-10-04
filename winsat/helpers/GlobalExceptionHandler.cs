// GlobalExceptionHandler.cs
// 全局未处理异常监听：捕获异常后弹出 ContentDialog 显示异常详情（含堆栈），
// 对话框关闭后自动退出程序。文案硬编码英文，保证在语言未初始化/资源加载失败时依然能显示。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace winsat.helpers
{
    internal static class GlobalExceptionHandler
    {
        private const string DialogTitle = "Application Error";

        private static readonly ManualResetEventSlim DialogClosed = new(false);
        private static int _reported; // 0 = 尚未处理，1 = 已在处理中

        /// <summary>
        /// 注册全局异常监听。必须在 App 构造函数中调用（越早越好，窗口创建前即可生效）。
        /// </summary>
        public static void Register()
        {
            // UI 线程（XAML）异常：标记 Handled 阻止默认崩溃，改为展示对话框。
            Application.Current.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                Report(e.Exception, waitForDialog: false);
            };

            // 未观察到的 Task 异常（通常在线程池线程抛出）。
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
                Report(e.Exception, waitForDialog: false);
            };

            // 其它线程的未处理异常：运行时会在此处理器返回后终止进程，
            // 因此阻塞等待用户看完/关闭对话框，再让进程退出。
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Exception exception = e.ExceptionObject as Exception
                    ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
                Report(exception, waitForDialog: true);
            };
        }

        private static void Report(Exception exception, bool waitForDialog)
        {
            // 只处理第一次异常，避免连环异常弹出多个对话框。
            if (Interlocked.Exchange(ref _reported, 1) == 1)
            {
                if (waitForDialog)
                {
                    Terminate();
                }

                return;
            }

            string detail = Describe(exception);
            MainWindow? window = App.MainWindow;

            // 窗口已创建时走对话框；异常发生在窗口创建前（或不在 UI 线程）则调度过去。
            bool queued = window is not null
                && window.DispatcherQueue.TryEnqueue(() => { _ = ShowDialogAsync(window!, detail); });

            if (!queued)
            {
                // 兜底：UI 线程不可用时用系统消息框，至少让用户看到错误。
                FallbackMessageBox(detail);
                Terminate();
                return;
            }

            if (waitForDialog)
            {
                DialogClosed.Wait(TimeSpan.FromMinutes(5));
            }
        }

        private static async Task ShowDialogAsync(Window window, string detail)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = window.Content?.XamlRoot,
                    // 对话框不继承窗口内容主题，需显式跟随软件主题
                    RequestedTheme = AppTheme.CurrentElementTheme,
                    Title = DialogTitle,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 380,
                        Content = new TextBlock
                        {
                            Text = detail,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            FontFamily = new FontFamily("Consolas"),
                        },
                    },
                    // Copy 只复制、不关闭；关闭按钮才结束对话框。
                    PrimaryButtonText = "Copy",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Close,
                };

                dialog.PrimaryButtonClick += (_, args) =>
                {
                    args.Cancel = true;
                    CopyText(detail);
                };

                await dialog.ShowAsync();
            }
            catch
            {
                // 例如已有 ContentDialog 打开：退化为系统消息框。
                FallbackMessageBox(detail);
            }
            finally
            {
                DialogClosed.Set();
                Terminate();
            }
        }

        /// <summary>
        /// 拼装异常详情：类型、消息与堆栈，并逐层展开 InnerException。
        /// </summary>
        private static string Describe(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                exception = aggregate.Flatten();
            }

            var builder = new StringBuilder();

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("--- Inner exception ---");
                }

                builder.AppendLine($"{current.GetType().FullName}: {current.Message}");
                builder.AppendLine(current.StackTrace ?? "(no stack trace available)");
            }

            return builder.ToString().TrimEnd();
        }

        private static void CopyText(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch
            {
                // 剪贴板被其它进程占用等情况，忽略。
            }
        }

        /// <summary>
        /// 关闭窗口并退出进程。优雅退出失败时强制结束，保证异常后程序一定退出。
        /// </summary>
        private static void Terminate()
        {
            try
            {
                Application.Current.Exit();
            }
            catch
            {
                // 忽略：UI 线程已损坏时下面的强制退出兜底。
            }

            Environment.Exit(1);
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private static void FallbackMessageBox(string detail)
        {
            const uint MB_OK = 0x00000000;
            const uint MB_ICONERROR = 0x00000010;
            const uint MB_SETFOREGROUND = 0x00010000;

            try
            {
                MessageBox(IntPtr.Zero, detail, DialogTitle, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
            }
            catch
            {
                // 连消息框都失败就静默退出。
            }
        }
    }
}
