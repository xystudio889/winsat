using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Printing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Printing;
using WinRT.Interop;

namespace winsat.helpers
{
    /// <summary>
    /// 用系统打印（WinUI 的 <see cref="PrintDocument"/> + 打印预览窗口）把已经排好的页面交给打印机，
    /// 分页在 <see cref="PrintReportLayout"/> 里完成，这里只负责把页面交给打印管线。
    /// </summary>
    internal sealed class PrintHelper
    {
        private readonly PrintDocument _document = new();
        private readonly IReadOnlyList<UIElement> _pages;
        private readonly string _jobName;
        private readonly Action<Exception>? _onError;

        /// <summary>
        /// 在 UI 线程上取好的文档源。<see cref="PrintDocument.DocumentSource"/> 是 UI 线程上的 XAML 对象，
        /// 而打印任务的回调运行在打印线程上，到那时再读取会抛 RPC_E_WRONG_THREAD（0x8001010E）。
        /// </summary>
        private IPrintDocumentSource? _documentSource;

        /// <summary>构造时所在的（UI）线程的队列：XAML 打印对象只能在它上面调用。</summary>
        private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        public PrintHelper(IReadOnlyList<UIElement> pages, string jobName, Action<Exception>? onError = null)
        {
            _pages = pages;
            _jobName = jobName;
            _onError = onError;
        }

        /// <summary>
        /// 打开系统的打印界面（预览、选打印机、打印都由系统负责）。
        /// 打印任务结束或用户取消后返回。
        /// </summary>
        public async Task<bool> ShowPrintUIAsync(Window window)
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(window);
            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetWindowHandle returned an empty handle.");
            }

            PrintManager printManager = PrintManagerInterop.GetForWindow(windowHandle);

            // 必须在 UI 线程上先取好文档源，打印线程只负责把它交给打印任务
            _documentSource = _document.DocumentSource;

            // 事件必须在打印界面关闭前一直挂着，否则打印管线拿不到文档源
            printManager.PrintTaskRequested += OnPrintTaskRequested;
            _document.Paginate += OnPaginate;
            _document.GetPreviewPage += OnGetPreviewPage;
            _document.AddPages += OnAddPages;

            try
            {
                return await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
            }
            finally
            {
                printManager.PrintTaskRequested -= OnPrintTaskRequested;
                _document.Paginate -= OnPaginate;
                _document.GetPreviewPage -= OnGetPreviewPage;
                _document.AddPages -= OnAddPages;
            }
        }

        private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            try
            {
                IPrintDocumentSource? source = _documentSource;

                if (source is null)
                {
                    return;
                }

                args.Request.CreatePrintTask(_jobName, sourceRequested => sourceRequested.SetSource(source));
            }
            catch (Exception ex)
            {
                // 这里抛异常会静默失败：打印界面就一直停在“正在连接打印机”，
                // 所以把异常送回 UI 线程让用户看到原因。
                ReportError(ex);
            }
        }

        private void OnPaginate(object sender, PaginateEventArgs e)
        {
            RunOnUiThread(() => _document.SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final));
        }

        private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            int index = e.PageNumber - 1;

            if (index < 0 || index >= _pages.Count)
            {
                return;
            }

            RunOnUiThread(() => _document.SetPreviewPage(e.PageNumber, _pages[index]));
        }

        /// <summary>
        /// 真正开始打印时，打印管线通过 AddPages 取页面；不处理这个事件的话预览正常但出不了纸。
        /// </summary>
        private void OnAddPages(object sender, AddPagesEventArgs e)
        {
            RunOnUiThread(() =>
            {
                foreach (UIElement page in _pages)
                {
                    _document.AddPage(page);
                }

                _document.AddPagesComplete();
            });
        }

        /// <summary>
        /// XAML 打印对象绑定在创建它的 UI 线程上，从别的线程调用会抛 RPC_E_WRONG_THREAD（0x8001010E）。
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                action();
                return;
            }

            _dispatcherQueue.TryEnqueue(() => action());
        }

        private void ReportError(Exception exception)
        {
            if (_onError is null)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _onError(exception);
                }
                catch
                {
                    // 提示失败就算了，不能因为报错再崩一次
                }
            });
        }
    }
}
