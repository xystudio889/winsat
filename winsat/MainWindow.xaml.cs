using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.ApplicationModel.Resources;
using Windows.UI.ApplicationSettings;
using winsat.helpers;
using winsat.pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
{
    public sealed partial class MainWindow : Window
    {
        // ---- 尺寸断点（单位 DIP，即 100% / 96 DPI 下的像素；其它 DPI 由系统按比例换算）----

        /// <summary>窗口最小宽度：再小就拖不动了。</summary>
        private const double MinWindowWidth = 765;

        /// <summary>窗口最小高度：再小就拖不动了。</summary>
        private const double MinWindowHeight = 400;

        /// <summary>低于此宽度：侧边栏完全隐藏（点标题栏按钮才以覆盖层打开）。</summary>
        private const double CompactPaneWidth = 800;

        /// <summary>低于此宽度：侧边栏只显示图标；达到此宽度：侧边栏常驻、展开时不覆盖内容。</summary>
        private const double ExpandedPaneWidth = 1100;

        /// <summary>当前导航栏位置（<see cref="AppSettings.LeftNavigation"/> / <see cref="AppSettings.TopNavigation"/>）。</summary>
        private string _navigationLocation = AppSettings.LeftNavigation;

        /// <summary>
        /// 宽屏（≥ <see cref="ExpandedPaneWidth"/>）下侧边栏是否展开。
        /// 首次布局时按当前宽度取值（宽屏默认展开，与 NavigationView 的默认一致），
        /// 之后只由用户<b>在宽屏下</b>手动开关改变；缩回窄屏再放大回宽屏时按它恢复。
        /// 图标 / 极简模式里开关的只是临时覆盖层，不影响它。
        /// </summary>
        private bool _paneOpenInWideMode;

        /// <summary>是否已经按首次布局的宽度确定过宽屏展开偏好。</summary>
        private bool _panePreferenceInitialized;

        /// <summary>是否已监听 XamlRoot.Changed（避免 Loaded 多次触发时重复订阅）。</summary>
        private bool _xamlRootHooked;

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
            ElementTheme elementTheme = AppTheme.ToElementTheme(theme);

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = elementTheme;
            }

            // 标题栏按钮由系统绘制，不继承窗口内容的 RequestedTheme：
            // 必须显式指定标题栏主题，否则反色模式下按钮配色会跟随系统。
            try
            {
                AppWindow.TitleBar.PreferredTheme = AppTheme.ToTitleBarTheme(theme);
            }
            catch (Exception)
            {
                // 系统不支持 PreferredTheme 时忽略，标题栏按钮保持系统默认配色。
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
        /// 左侧导航时，侧边栏的具体形态由窗口宽度决定（见 <see cref="ApplyAdaptivePaneLayout"/>）。
        /// </summary>
        public void SetNavigationLocation(string location)
        {
            _navigationLocation = location;
            titleBar.IsPaneToggleButtonVisible = (location == AppSettings.LeftNavigation);

            if (location == AppSettings.TopNavigation)
            {
                NavigationViewControl.PaneDisplayMode = NavigationViewPaneDisplayMode.Top;
                return;
            }

            // 左侧导航：按当前宽度重新决定形态（窗口尚未布局时交由 RootGrid_SizeChanged 处理）
            ApplyAdaptivePaneLayout(RootGrid.ActualWidth);
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyWindowMinSize();

            // 窗口被拖到不同缩放比例的显示器时，最小尺寸要按新的比例重算
            if (!_xamlRootHooked && RootGrid.XamlRoot is { } xamlRoot)
            {
                _xamlRootHooked = true;
                xamlRoot.Changed += (_, _) => ApplyWindowMinSize();
            }
        }

        /// <summary>
        /// 限制窗口最小尺寸（750 × 400，单位 DIP，即 100% / 96 DPI 下的像素）。
        /// OverlappedPresenter 的最小尺寸按物理像素计算，这里用缩放比例换算，
        /// 保证在高 DPI 下窗口逻辑尺寸也不会小于 750 × 400。
        /// </summary>
        private void ApplyWindowMinSize()
        {
            if (RootGrid.XamlRoot is null)
                return;

            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                return;

            double scale = RootGrid.XamlRoot.RasterizationScale;
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinWindowWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinWindowHeight * scale);
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyAdaptivePaneLayout(e.NewSize.Width);
        }

        /// <summary>
        /// 左侧导航时按窗口宽度切换侧边栏形态（阈值单位 DIP）：
        ///   &lt; 800 ：完全隐藏，只有点标题栏的展开按钮才以<b>覆盖层</b>打开；
        ///   &lt; 1100：只显示图标，展开同样是<b>覆盖层</b>；
        ///   ≥ 1100：展开时占用宽度、<b>不</b>覆盖内容（内联）；收起时保留图标条（与 800~1100 连续）。
        ///
        /// 宽屏下展开还是收起，按用户在宽屏下的手动开关记忆（首次布局时按当前宽度取“展开”），
        /// 缩回窄屏再放大回宽屏时恢复。
        /// 宽屏「收起」之所以用 LeftCompact 而不是 Left + 关闭：NavigationView 只要切到 Left（Expanded）
        /// 就会自动 OpenPane()，切过去再关会闪一下“从展开缩回”的动画。
        /// 顶部导航不做自适应（顶部不会窄到显示不出来）。
        /// </summary>
        private void ApplyAdaptivePaneLayout(double width)
        {
            if (_navigationLocation != AppSettings.LeftNavigation)
                return;   // 顶部导航不参与

            if (width <= 0)
                return;   // 还没布局：等 RootGrid_SizeChanged

            if (!_panePreferenceInitialized)
            {
                // 首次布局：按当前宽度定下初始偏好（宽屏展开、否则收起），
                // 与 NavigationView 在同样宽度下的默认状态一致，避免刚启动就把侧边栏收掉
                _paneOpenInWideMode = width >= ExpandedPaneWidth;
                _panePreferenceInitialized = true;
            }

            NavigationViewPaneDisplayMode target =
                width < CompactPaneWidth ? NavigationViewPaneDisplayMode.LeftMinimal :
                width >= ExpandedPaneWidth && _paneOpenInWideMode ? NavigationViewPaneDisplayMode.Left :
                NavigationViewPaneDisplayMode.LeftCompact;

            if (target == NavigationViewControl.PaneDisplayMode)
                return;

            NavigationViewControl.PaneDisplayMode = target;

            // 图标 / 极简模式下的展开只是临时覆盖层，换挡时一律收起；
            // 宽屏则恢复用户上次在宽屏下的选择。
            NavigationViewControl.IsPaneOpen = target == NavigationViewPaneDisplayMode.Left;
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
            if (RootGrid.ActualWidth >= ExpandedPaneWidth)
            {
                // 宽屏：展开 = 内联（占用宽度、不覆盖内容）；收起 = 回到图标条。
                // 这次选择会被记住，缩回窄屏再放大回宽屏时恢复。
                _paneOpenInWideMode = NavigationViewControl.PaneDisplayMode != NavigationViewPaneDisplayMode.Left;
                ApplyAdaptivePaneLayout(RootGrid.ActualWidth);
                return;
            }

            // 图标 / 极简模式：打开/关闭的只是临时覆盖层，不影响宽屏下的默认状态
            NavigationViewControl.IsPaneOpen = !NavigationViewControl.IsPaneOpen;
        }
    }
}