// ErrorDialog.cs
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using System;
using System.Windows.Input;

namespace winsat.widgets
{
    public static class ErrorDialog
    {
        public static async void Show(XamlRoot root, string message, string stackTrace = "")
        {
            // 组合完整错误信息
            string fullMessage = message + Environment.NewLine + Environment.NewLine + stackTrace;

            // 创建一个带错误颜色的 TextBlock 作为内容
            var contentTextBlock = new TextBlock
            {
                Text = fullMessage,
                TextWrapping = TextWrapping.Wrap,
                // 关键：使用主题资源中的关键色（红色）
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            };

            // 构建 ContentDialog
            ContentDialog err = new ContentDialog()
            {
                XamlRoot = root,
                Title = "发生了一个错误",
                Content = contentTextBlock,   // 将 TextBlock 赋给 Content
                CloseButtonText = "确定",
                PrimaryButtonText = "复制并关闭",
                PrimaryButtonCommand = new CopyCommand(fullMessage)
            };

            await err.ShowAsync();
        }
    }

    // 复制命令实现（保持不变）
    public class CopyCommand : ICommand
    {
        private string _copyText;

        public CopyCommand(string input)
        {
            _copyText = input;
        }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            DataPackage package = new DataPackage();
            package.SetText(_copyText);
            Clipboard.SetContent(package);
        }

        public event EventHandler CanExecuteChanged;
    }
}