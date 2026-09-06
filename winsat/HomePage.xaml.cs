using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.ViewManagement;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>

    public sealed partial class HomePage : Page
    {
        public ObservableCollection<FileInfo> Files { get; } = new();
        private bool _isDialogShowing = false; // 互斥标志
        private ScrollViewer _scrollViewer = null;
        private bool _isRunning = false;

        // 固定高亮颜色（按新配色）
        private static readonly SolidColorBrush DarkGrayBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x5C, 0x5C, 0x5C));
        private static readonly SolidColorBrush OrangeBrush =
            new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x88, 0x0A));
        private static readonly SolidColorBrush LightBlueBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x0E, 0xBA, 0xFF));
        private static readonly SolidColorBrush DeepBlueBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x0A, 0xED, 0xCF));
        private static readonly SolidColorBrush GreenBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x05, 0xAB, 0x18));

        // 动态默认文本颜色（随主题变化）
        private SolidColorBrush _defaultTextBrush;

        // 用于监听系统主题变化的 UISettings
        private UISettings _uiSettings;

        private readonly DispatcherQueue _dispatcherQueue;

        public string xmlContent = String.Empty;

        public HomePage()
        {
            InitializeComponent();

            FileComboBox.ItemsSource = Files;

            LoadFilesAsync();
        }

        private async Task LoadFilesAsync()
        {
            try
            {
                // 获取 Windows 目录路径（例如 C:\Windows）
                string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                // 组合 Minidump 文件夹路径
                string minidumpDir = Path.Combine(windowsDir, "Performance", "WinSAT", "DataStore");

                // 检查目录是否存在
                if (!Directory.Exists(minidumpDir))
                {
                    await new ContentDialog
                    {
                        Title = "目录不存在",
                        Content = $"未找到分析文件夹：{minidumpDir}",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                    return;
                }

                // 异步获取该目录下的所有文件（仅直接文件，不含子目录）
                var files = await Task.Run(() =>
                {
                    var directoryInfo = new DirectoryInfo(minidumpDir);
                    return directoryInfo.GetFiles();
                });

                Files.Clear(); // 清空
                foreach (var file in files)
                {
                    Files.Add(file);
                }

                FileComboBox.SelectedIndex = 0;
            }
            catch (UnauthorizedAccessException)
            {
                await new ContentDialog
                {
                    Title = "权限不足",
                    Content = "无法读取文件夹，请以管理员身份运行应用程序。",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "加载失败",
                    Content = $"发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileComboBox.SelectedItem is FileInfo selectedFile)
            {
                LoadingFileErrorBar.IsOpen = false;
                // SelectedFilePathText.Text = selectedFile.FullName;

                if (!System.IO.File.Exists(selectedFile.FullName))
                {
                    LoadingFileErrorBar.IsOpen = true;
                    return;
                }
                SelectorBar_SelectionChanged(SelectorBar, null);
            }
            else
            {
                LoadingFileErrorBar.IsOpen = true;
            }
        }

        private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            SelectorBarItem selectedItem = sender.SelectedItem;
            int currentSelectedIndex = sender.Items.IndexOf(selectedItem);
            System.Type pageType;

            switch (currentSelectedIndex)
            {
                case 0:
                    pageType = typeof(FriendlyPage);
                    break;
                case 1:
                    HighlightXml("<code />");
                    //pageType = typeof(CodePage);
                    break;
                default:
                    return;
            }

            // ContentFrame.Navigate(pageType);
        }

        public static List<string> RunPwsh(string command)
        {
            var results = new List<string>();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe", // 或 "pwsh.exe"
                Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi)!)
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    results.Add(line);
                }

                string err = p.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(err))
                {
                    // 处理或记录错误
                    results.Add("[ERROR] " + err);
                }

                p.WaitForExit();
            }

            return results;
        }

        public async void RefreshFileButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingFileErrorBar.IsOpen = false;
            await LoadFilesAsync();
            RefreshedFile.Visibility = Visibility.Visible; // 显示刷新成功
        }

        public async void GetScore(object sender, RoutedEventArgs e)
        {
            // 防止并发
            if (_isDialogShowing) return;
            _isDialogShowing = true;

            try
            {
                List<string> scores = RunPwsh("Get-CimInstance Win32_WinSAT");
                List<string> score_list = [];
                string status = "";

                foreach (string result in scores)
                {
                    try { score_list.Add(result.Split(':')[1].Trim()); }
                    catch { continue; }
                }

                // 确保 score_list 有足够元素
                if (score_list.Count < 8)
                {
                    // 处理数据不足情况
                    return;
                }

                switch (score_list[6])
                {
                    case "0": status = "未知"; break;
                    case "1": status = "成功"; break;
                    case "2":
                        status = "硬件变动";
                        await ShowConfirmDialog("你的硬件变动了", "测试时与现在的硬件不一致，是否开始跑分后查看结果？");
                        break;
                    case "3":
                        status = "未跑分";
                        await ShowConfirmDialog("当前你未跑分", "是否开始跑分后查看结果？");
                        break;
                    case "4": status = "无效"; break;
                    case "5": status = "用户自定义"; break;
                    default: status = score_list[6]; break;
                }

                CPUScore.Text = score_list[0];
                D3DScore.Text = score_list[1];
                DiskScore.Text = score_list[2];
                GraphicsScore.Text = score_list[3];
                MemoryScore.Text = score_list[4];
                WinSATAssessmentState.Text = status;
                WinSPRLevel.Text = score_list[7];
            }
            finally
            {
                _isDialogShowing = false;
            }
        }

        // 辅助方法，显示确认对话框并处理用户选择
        private async Task ShowConfirmDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = "确定",
                SecondaryButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // 用户选择“确定”，执行跑分（调用 RunScore 方法）
                RunScore(null, null);
            }
            // 选择“取消”则什么都不做
        }

        private async Task ExecuteCommand(string command)
        {
            var process = new Process();
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = "/c " + command;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
        }

        public async void RunScore(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _isRunning = true;
            RunScoreButton.IsEnabled = false;

            // 执行命令
            await ExecuteCommand("winsat formal -restart clean");

            RunScoreButton.IsEnabled = true;
            _isRunning = false;

            GetScore(null, null);
            RefreshFileButton_Click(null, null);
        }

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
                _dispatcherQueue.TryEnqueue(() =>
                {
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
}
