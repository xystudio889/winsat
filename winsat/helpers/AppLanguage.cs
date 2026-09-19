using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace winsat.helpers
{
    /// <summary>
    /// 应用显示语言的读写与应用。语言必须在任何资源加载之前生效，
    /// 因此 <see cref="ApplySavedLanguage"/> 要在 <c>App</c> 构造函数中先于 InitializeComponent 调用。
    /// </summary>
    internal static class AppLanguage
    {
        /// <summary>简体中文对应的符号文件语言标记。</summary>
        public const string ChineseSymbolLanguage = "zh-Hans-CN";

        /// <summary>英文对应的符号文件语言标记。</summary>
        public const string EnglishSymbolLanguage = "en-US";

        /// <summary>把已保存的语言设置应用到应用（<see cref="AppSettings.SystemLanguage"/> 表示跟随系统）。</summary>
        public static void ApplySavedLanguage()
        {
            Apply(AppSettings.Language);
        }

        /// <summary>应用指定语言，传入 <see cref="AppSettings.SystemLanguage"/> 时恢复为跟随系统。</summary>
        public static void Apply(string language)
        {
            string overrideValue = language == AppSettings.SystemLanguage ? string.Empty : language;

            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = overrideValue;
            }
            catch (Exception)
            {
                // 少数运行环境不允许覆盖语言，此时界面会退回系统语言。
            }

            // 非打包应用需要 WinAppSDK 提供的实现，用反射调用可避免在旧 SDK 上编译失败。
            ApplyUnpackagedOverride(overrideValue);
        }

        /// <summary>
        /// 当前界面语言对应的符号文件语言（<see cref="ChineseSymbolLanguage"/> 或 <see cref="EnglishSymbolLanguage"/>）。
        /// </summary>
        public static string SymbolLanguage
        {
            get
            {
                string preferred = AppSettings.Language == AppSettings.SystemLanguage
                    ? GetSystemLanguage()
                    : AppSettings.Language;

                string mapped = preferred.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? ChineseSymbolLanguage
                    : EnglishSymbolLanguage;

                if (File.Exists(GetSymbolPath(mapped)))
                {
                    return mapped;
                }

                // 缺少对应符号文件时退回另一种，避免友好视图直接不可用。
                string fallback = mapped == ChineseSymbolLanguage ? EnglishSymbolLanguage : ChineseSymbolLanguage;
                return File.Exists(GetSymbolPath(fallback)) ? fallback : mapped;
            }
        }

        /// <summary>重启应用，让新的语言设置生效。</summary>
        public static async Task RestartAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath)
                    {
                        UseShellExecute = true,
                    });

                    Application.Current.Exit();
                }
            }
            catch (Exception)
            {
                // 兜底重启同样失败时保持当前窗口，用户可稍后手动重启。
            }
        }

        /// <summary>读取系统首选语言（如 zh-Hans-CN）。</summary>
        private static string GetSystemLanguage()
        {
            try
            {
                var languages = GlobalizationPreferences.Languages;
                if (languages.Count > 0 && !string.IsNullOrEmpty(languages[0]))
                {
                    return languages[0];
                }
            }
            catch (Exception)
            {
                // 读不到系统语言列表时退回当前 UI 区域。
            }

            return CultureInfo.CurrentUICulture.Name;
        }

        /// <summary>通过反射调用 WinAppSDK 为非打包应用提供的语言覆盖入口。</summary>
        private static void ApplyUnpackagedOverride(string overrideValue)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type = assembly.GetType("Microsoft.Windows.Globalization.ApplicationLanguages");
                PropertyInfo? property = type?.GetProperty(
                    "PrimaryLanguageOverride",
                    BindingFlags.Public | BindingFlags.Static);

                if (property?.CanWrite == true && property.PropertyType == typeof(string))
                {
                    property.SetValue(null, overrideValue);
                    return;
                }
            }
        }

        private static string GetSymbolPath(string language) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Symbols", $"{language}.json");
    }
}
