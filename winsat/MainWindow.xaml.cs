using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using winsat.helpers;
using Windows.ApplicationModel.Resources;

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

            // 恢复上次保存的外观设置
            ApplyTheme(AppSettings.Theme);
            ApplyBackground(AppSettings.Background);
            SetNavigationLocation(AppSettings.NavigationLocation);
        }

        /// <summary>
        /// 应用软件主题。传入 "Light"、"Dark" 或 "Default"（跟随系统）。
        /// </summary>
        public void ApplyTheme(string theme)
        {
            ElementTheme elementTheme = theme switch
            {
                AppSettings.LightTheme => ElementTheme.Light,
                AppSettings.DarkTheme => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = elementTheme;
            }
        }

        /// <summary>
        /// 应用背景材质。传入 "Mica"、"MicaAlt"、"Arcylic"、"ArcylicThin" 或 "None"（不使用透明效果）。
        /// 系统不支持时由 <see cref="AppBackground.Create"/> 自动回退。
        /// </summary>
        public void ApplyBackground(string background)
        {
            SystemBackdrop? backdrop = AppBackground.Create(background);

            // 不使用透明效果时，用不透明的主题色铺满窗口，避免露出透明/黑色底。
            OpaqueBackground.Visibility = backdrop is null ? Visibility.Visible : Visibility.Collapsed;

            SystemBackdrop = backdrop;
        }

        /// <summary>
        /// 应用导航栏位置。传入 "Left"（左侧）或 "Top"（上方）。
        /// </summary>
        public void SetNavigationLocation(string location)
        {
            titleBar.IsPaneToggleButtonVisible = (location == AppSettings.LeftNavigation);
            NavigationViewControl.PaneDisplayMode =
                location == AppSettings.TopNavigation
                    ? NavigationViewPaneDisplayMode.Top
                    : NavigationViewPaneDisplayMode.Left;
        }

        private void SetWindowProperties()
        {
            NavigationViewControl.IsPaneOpen = false;
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(titleBar);
            this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            rootFrame.Navigate(typeof(HomePage));
#if DEBUG
            this.Title += Loader.GetString("Dev");
            titleBar.Subtitle = Loader.GetString("Dev");
            DebugNavigation.Visibility = Visibility.Visible;
#else
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
                string? tag = item.Tag as string;
                switch (tag)
                {
                    case "Home":
                        rootFrame.Navigate(typeof(HomePage));
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
    }
}