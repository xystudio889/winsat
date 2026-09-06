using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
            SetWindowProperties();
        }

        private void SetWindowProperties()
        {
            NavigationViewControl.IsPaneOpen = false;
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(titleBar);
            this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            rootFrame.Navigate(typeof(HomePage));

#if DEBUG
            this.Title = "Windows 跑分工具 测试版";
            titleBar.Subtitle = "测试版";
            DebugNavigation.Visibility = Visibility.Visible;
#else
            this.Title = "Windows 跑分工具";
            DebugNavigation.Visibility = Visibility.Collapsed;
#endif
        }

        private void OnNavigationViewSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                if (rootFrame.CurrentSourcePageType != typeof(SettingsPage))
                {
                    rootFrame.Navigate(typeof(SettingsPage));
                }
            }
            else if (args.SelectedItem is NavigationViewItem item)
            {
                string tag = item.Tag as string;
                switch (tag)
                {
                    case "Home":
                        rootFrame.Navigate(typeof(HomePage));
                        break;
                    case "Advanced":
                        rootFrame.Navigate(typeof(AdvancedPage));
                        break;
                    case "Debug":
                        rootFrame.Navigate(typeof(DebugPage));
                        break;
                }
            }
        }

        private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        {
            NavigationViewControl.IsPaneOpen = !NavigationViewControl.IsPaneOpen;
        }

        public async void Debug_WindowSize(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new ContentDialog() // 弹窗
            {
                Title = "调试：窗口大小",
                Content = "当前窗口大小：" + this.AppWindow.Size.Width + "," + this.AppWindow.Size.Height,
                PrimaryButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}