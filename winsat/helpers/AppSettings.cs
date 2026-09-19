using System;
using System.Collections.Generic;
using Windows.Storage;

namespace winsat.helpers
{
    /// <summary>
    /// 应用设置的读写封装。底层优先使用 <see cref="ApplicationData.LocalSettings"/>，
    /// 当应用以非打包（unpackaged）方式运行时自动回退到进程内存储。
    /// </summary>
    internal static class AppSettings
    {
        // 主题
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";
        public const string DefaultTheme = "Default";

        // 导航栏位置
        public const string LeftNavigation = "Left";
        public const string TopNavigation = "Top";

        // 显示语言
        public const string SystemLanguage = "Default";
        public const string ChineseLanguage = "zh-CN";
        public const string EnglishLanguage = "en-US";

        // 背景材质
        public const string Mica = "Mica";
        public const string MicaAlt = "MicaAlt";
        public const string Arcylic = "Arcylic";
        public const string ArcylicThin = "ArcylicThin";
        public const string None = "None";

        // 音效
        public const bool EnableSound = false;
        public const bool EnableSpatialSound = false;

        private const string ThemeKey = "AppTheme";
        private const string NavigationLocationKey = "NavigationLocation";
        private const string LanguageKey = "AppLanguage";
        private const string SoundKey = "Sound";
        private const string SpatialSoundKey = "SpatialSound";
        private const string BackgroundKey = "Background";

        private static readonly Dictionary<string, string> FallbackValues = new();
        private static readonly Dictionary<string, bool> FallbackBoolValues = new();

        /// <summary>软件主题：Light / Dark / Default。</summary>
        public static string Theme
        {
            get => GetString(ThemeKey, DefaultTheme);
            set => SetString(ThemeKey, value);
        }

        /// <summary>导航栏位置：Left / Top。</summary>
        public static string NavigationLocation
        {
            get => GetString(NavigationLocationKey, LeftNavigation);
            set => SetString(NavigationLocationKey, value);
        }

        /// <summary>软件显示语言：Default（跟随系统）/ zh-CN / en-US。</summary>
        public static string Language
        {
            get => GetString(LanguageKey, SystemLanguage);
            set => SetString(LanguageKey, value);
        }

        /// <summary>软件背景材质：Mica / MicaAlt / Arcylic / ArcylicThin / None（不使用透明效果）。</summary>
        public static string Background
        {
            get => GetString(BackgroundKey, Mica);
            set => SetString(BackgroundKey, value);
        }


        /// <summary>音效。</summary>
        public static bool Sound
        {
            get => GetBool(SoundKey, EnableSound);
            set => SetBool(SoundKey, value);
        }

        /// <summary>3D 音效。</summary>
        public static bool SpatialSound
        {
            get => GetBool(SpatialSoundKey, EnableSpatialSound);
            set => SetBool(SpatialSoundKey, value);
        }

        private static bool GetBool(string key, bool defaultValue)
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value)
                    && value is bool flag)
                {
                    return flag;
                }
            }
            catch (Exception)
            {
                // 非打包运行时 ApplicationData 不可用，改用回退存储。
            }

            return FallbackBoolValues.TryGetValue(key, out bool fallback)
                ? fallback
                : defaultValue;
        }

        private static void SetBool(string key, bool value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
                // 忽略写入失败，改用回退存储。
            }

            FallbackBoolValues[key] = value;
        }

        private static string GetString(string key, string defaultValue)
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value)
                    && value is string text
                    && !string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // 非打包运行时 ApplicationData 不可用，改用回退存储。
            }

            return FallbackValues.TryGetValue(key, out string? fallback) ? fallback : defaultValue;
        }

        private static void SetString(string key, string value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
                // 忽略写入失败，改用回退存储。
            }

            FallbackValues[key] = value;
        }
    }
}
