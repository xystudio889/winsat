using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.ViewManagement;
using Windows.UI;
using Microsoft.UI.Dispatching;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class CodePage : Page
    {

        // ---- 主题检测与更新 ----
        private void StartThemeListener()
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        public static bool IsSystemDarkMode()
        {
            const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string RegistryValueName = "AppsUseLightTheme";

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
            {
                object registryValueObject = key?.GetValue(RegistryValueName);
                if (registryValueObject == null)
                    return false; // 默认返回浅色模式

                int registryValue = (int)registryValueObject;
                // 值为 0 表示深色模式，1 表示浅色模式
                return registryValue == 0;
            }
        }
        private void OnColorValuesChanged(UISettings sender, object args)
        {
            _dispatcherQueue.TryEnqueue(() => {
                UpdateTheme();
                HighlightXml(xmlContent);
            });
        }

        private void UpdateTheme()
        {
            var theme = WindowThemeIsDark();
            if (theme)
            {
                _defaultTextBrush = new SolidColorBrush(Color.FromArgb(255, 0xEF, 0xEF, 0xEF));
            }
            else // Light
            {
                _defaultTextBrush = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x00, 0x01)); // #100001
            }
        }

        private bool WindowThemeIsDark()
        {
            // 创建一个 UISettings 实例
            var settings = new UISettings();

            // 获取背景颜色值
            var backgroundColor = settings.GetColorValue(UIColorType.Background);

            Debug.WriteLine($"当前颜色：{backgroundColor.ToString()}");

            // 判断：如果背景色接近黑色，则为深色模式；接近白色则为浅色模式[reference:2]
            // bool isDarkMode = backgroundColor.ToString() == "#FF000000";
            return backgroundColor.ToString() != "#FFFFFFFF";
        }

        // ---- 高亮逻辑 ----


        public void HighlightXml(string xml)
        {
            xmlContent = xml;
            HighlightBlock.Blocks.Clear();
            if (string.IsNullOrEmpty(xml)) return;

            var paragraph = new Paragraph();
            HighlightBlock.Blocks.Add(paragraph);

            int index = 0;
            while (index < xml.Length)
            {
                char c = xml[index];
                if (c == '<')
                {
                    if (xml.Length - index >= 4 && xml.Substring(index, 4) == "<!--")
                    {
                        int start = index;
                        int end = xml.IndexOf("-->", index);
                        if (end == -1) end = xml.Length;
                        else end += 3;
                        string comment = xml.Substring(start, end - start);
                        AddRun(paragraph, comment, GreenBrush);
                        index = end;
                    }
                    else if (xml.Length - index >= 2 && xml.Substring(index, 2) == "<?")
                    {
                        int start = index;
                        int end = xml.IndexOf("?>", index);
                        if (end == -1) end = xml.Length;
                        else end += 2;
                        string pi = xml.Substring(start, end - start);
                        AddRun(paragraph, pi, DarkGrayBrush);
                        index = end;
                    }
                    else if (xml.Length - index >= 9 && xml.Substring(index, 9) == "<![CDATA[")
                    {
                        int start = index;
                        int end = xml.IndexOf("]]>", index);
                        if (end == -1) end = xml.Length;
                        else end += 3;
                        string cdata = xml.Substring(start, end - start);
                        AddRun(paragraph, cdata, null); // 使用默认文本颜色
                        index = end;
                    }
                    else
                    {
                        int start = index;
                        int end = FindTagEnd(xml, index);
                        if (end == -1) end = xml.Length;
                        string tag = xml.Substring(start, end - start);
                        ParseTag(tag, paragraph);
                        index = end;
                    }
                }
                else
                {
                    int start = index;
                    int next = xml.IndexOf('<', index);
                    if (next == -1) next = xml.Length;
                    string text = xml.Substring(start, next - start);
                    AddRun(paragraph, text, null);
                    index = next;
                }
            }
        }

        private int FindTagEnd(string xml, int start)
        {
            int i = start + 1;
            bool inQuote = false;
            char quoteChar = '\0';
            while (i < xml.Length)
            {
                char c = xml[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                }
                else if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                }
                else if (!inQuote && c == '>')
                {
                    return i + 1;
                }
                i++;
            }
            return -1;
        }

        private void ParseTag(string tag, Paragraph paragraph)
        {
            var tokens = TokenizeTag(tag);
            foreach (var token in tokens)
            {
                SolidColorBrush brush = null;
                switch (token.Type)
                {
                    case TokenType.Delimiter:
                        brush = DarkGrayBrush;
                        break;
                    case TokenType.TagName:
                        brush = OrangeBrush;
                        break;
                    case TokenType.AttributeName:
                        brush = LightBlueBrush;
                        break;
                    case TokenType.AttributeValue:
                        brush = DeepBlueBrush;
                        break;
                    case TokenType.Whitespace:
                        brush = null; // 使用默认文本颜色
                        break;
                }
                AddRun(paragraph, token.Text, brush);
            }
        }

        private enum TokenType
        {
            Delimiter,
            TagName,
            AttributeName,
            AttributeValue,
            Whitespace
        }

        private class Token
        {
            public string Text { get; set; }
            public TokenType Type { get; set; }
        }

        private List<Token> TokenizeTag(string tag)
        {
            var tokens = new List<Token>();
            int i = 0;
            const int STATE_START = 0;
            const int STATE_TAG_NAME = 1;
            const int STATE_AFTER_NAME = 2;
            const int STATE_ATTRIBUTE_NAME = 3;
            const int STATE_AFTER_ATTR_NAME = 4;
            const int STATE_AFTER_EQUAL = 5;
            const int STATE_ATTRIBUTE_VALUE = 6;
            int state = STATE_START;
            int tokenStart = 0;
            char quoteChar = '\0';

            while (i < tag.Length)
            {
                char c = tag[i];
                switch (state)
                {
                    case STATE_START:
                        if (c == '<')
                        {
                            tokens.Add(new Token { Text = "<", Type = TokenType.Delimiter });
                            i++;
                            state = STATE_TAG_NAME;
                        }
                        else i++;
                        break;

                    case STATE_TAG_NAME:
                        if (char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.' || c == '-')
                        {
                            tokenStart = i;
                            while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] == '_' || tag[i] == ':' || tag[i] == '.' || tag[i] == '-'))
                                i++;
                            string name = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = name, Type = TokenType.TagName });
                            state = STATE_AFTER_NAME;
                        }
                        else if (c == '/')
                        {
                            tokens.Add(new Token { Text = "/", Type = TokenType.Delimiter });
                            i++;
                            if (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] == '_' || tag[i] == ':' || tag[i] == '.' || tag[i] == '-'))
                                state = STATE_TAG_NAME;
                            else
                                state = STATE_AFTER_NAME;
                        }
                        else state = STATE_AFTER_NAME;
                        break;

                    case STATE_AFTER_NAME:
                        if (char.IsWhiteSpace(c))
                        {
                            tokenStart = i;
                            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
                            string ws = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = ws, Type = TokenType.Whitespace });
                            state = STATE_ATTRIBUTE_NAME;
                        }
                        else if (c == '/')
                        {
                            tokens.Add(new Token { Text = "/", Type = TokenType.Delimiter });
                            i++;
                            state = STATE_AFTER_NAME;
                        }
                        else if (c == '>')
                        {
                            tokens.Add(new Token { Text = ">", Type = TokenType.Delimiter });
                            i++;
                            return tokens;
                        }
                        else if (c == '=')
                        {
                            tokens.Add(new Token { Text = "=", Type = TokenType.Delimiter });
                            i++;
                            state = STATE_AFTER_EQUAL;
                        }
                        else state = STATE_ATTRIBUTE_NAME;
                        break;
                    case STATE_ATTRIBUTE_NAME:
                        if (char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.' || c == '-')
                        {
                            tokenStart = i;
                            while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] == '_' || tag[i] == ':' || tag[i] == '.' || tag[i] == '-'))
                                i++;
                            string attrName = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = attrName, Type = TokenType.AttributeName });
                            state = STATE_AFTER_ATTR_NAME;
                        }
                        else if (c == '/' || c == '>')
                        {
                            // 回到 STATE_AFTER_NAME 让结束符号被正确处理
                            state = STATE_AFTER_NAME;
                            // 不 i++，让下一循环处理该字符
                        }
                        else
                        {
                            i++; // 跳过其他无效字符
                        }
                        break;

                    case STATE_AFTER_ATTR_NAME:
                        if (char.IsWhiteSpace(c))
                        {
                            tokenStart = i;
                            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
                            string ws = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = ws, Type = TokenType.Whitespace });
                            state = STATE_AFTER_ATTR_NAME;
                        }
                        else if (c == '=')
                        {
                            tokens.Add(new Token { Text = "=", Type = TokenType.Delimiter });
                            i++;
                            state = STATE_AFTER_EQUAL;
                        }
                        else state = STATE_AFTER_NAME;
                        break;

                    case STATE_AFTER_EQUAL:
                        if (char.IsWhiteSpace(c))
                        {
                            tokenStart = i;
                            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
                            string ws = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = ws, Type = TokenType.Whitespace });
                            state = STATE_ATTRIBUTE_VALUE;
                        }
                        else if (c == '"' || c == '\'')
                        {
                            quoteChar = c;
                            tokenStart = i;
                            i++;
                            while (i < tag.Length && tag[i] != quoteChar) i++;
                            if (i < tag.Length) i++;
                            string value = tag.Substring(tokenStart, i - tokenStart);
                            tokens.Add(new Token { Text = value, Type = TokenType.AttributeValue });
                            state = STATE_AFTER_NAME;
                        }
                        else i++;
                        break;

                    default: i++; break;
                }
            }
            return tokens;
        }

        // 添加 Run，若 brush 为 null 则使用当前主题对应的默认颜色
        private void AddRun(Paragraph paragraph, string text, SolidColorBrush brush)
        {
            if (string.IsNullOrEmpty(text)) return;
            var run = new Run { Text = text };
            run.Foreground = brush ?? _defaultTextBrush;
            paragraph.Inlines.Add(run);
        }
    }
}
