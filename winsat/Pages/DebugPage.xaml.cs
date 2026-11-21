using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using winsat.helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat.pages
{
    public sealed partial class DebugPage : Page
    {
        public DebugPage()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// 打印解析器组件的现场信息：路径、桥版本、sys.prefix、lxml 来源。
        /// 打包安装时 sys.prefix 必须落在包内（...\parser\runtime），
        /// 落在 D:\Python314 之类就说明客户机上装了别的 Python 且把我们的运行时抢掉了。
        /// </summary>
        private async void ParserDiagnoseButton_Click(object sender, RoutedEventArgs e)
        {
            ParserDiagnoseButton.IsEnabled = false;
            ParserDiagnoseText.Text = "检查中…";

            try
            {
                // 启动解释器 + import lxml 有点耗时，别在 UI 线程上等
                ParserDiagnoseText.Text = await Task.Run(ParserBridge.Describe);
            }
            catch (Exception ex)
            {
                ParserDiagnoseText.Text = ex.ToString();
            }
            finally
            {
                ParserDiagnoseButton.IsEnabled = true;
            }
        }
    }
}
