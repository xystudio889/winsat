using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace winsat.helpers
{
    /// <summary>
    /// 软件主题（<see cref="AppSettings.Theme"/>）与各个 UI 框架主题之间的映射。
    /// <para>
    /// 用于统一处理“反色模式”（系统主题与软件主题不一致）：
    /// 标题栏按钮、ContentDialog 等不继承窗口内容 <c>RequestedTheme</c> 的部分，
    /// 必须显式指定主题，否则会跟随系统主题。
    /// </para>
    /// </summary>
    internal static class AppTheme
    {
        /// <summary>软件主题设置 → XAML 元素主题。</summary>
        public static ElementTheme ToElementTheme(string theme) => theme switch
        {
            AppSettings.LightTheme => ElementTheme.Light,
            AppSettings.DarkTheme => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        /// <summary>
        /// 软件主题设置 → 标题栏主题。
        /// <para>
        /// 必须显式指定 Light/Dark：默认的 <see cref="TitleBarTheme.UseDefaultAppMode"/> 跟随的是
        /// 系统的“应用模式”，反色模式下会导致最大化/最小化/关闭按钮的配色跟随系统。
        /// </para>
        /// </summary>
        public static TitleBarTheme ToTitleBarTheme(string theme) => theme switch
        {
            AppSettings.LightTheme => TitleBarTheme.Light,
            AppSettings.DarkTheme => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        /// <summary>
        /// 当前软件主题对应的元素主题。
        /// 供 <c>ContentDialog</c> 等不继承窗口内容主题的弹层使用。
        /// </summary>
        public static ElementTheme CurrentElementTheme => ToElementTheme(AppSettings.Theme);
    }
}
