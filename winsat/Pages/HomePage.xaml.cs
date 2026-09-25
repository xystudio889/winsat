using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Windows.UI;
using Windows.UI.ViewManagement;
using winsat.widgets;
using winsat.helpers;

using IoPath = System.IO.Path;

namespace winsat.pages
{
    public sealed partial class HomePage : Page
    {
        public ObservableCollection<FileInfo> Files { get; } = new();
        private bool _isDialogShowing = false;
        private bool _isRunning = false;

        private SolidColorBrush TextBrush;
        private SolidColorBrush SymbolBrush;
        private SolidColorBrush TagBrush;
        private SolidColorBrush KeyBrush;
        private SolidColorBrush ValueBrush;
        private SolidColorBrush CommentBrush;

        private UISettings _uiSettings;
        private readonly DispatcherQueue _dispatcherQueue;

        public string xmlContent = String.Empty;

        public string deletedFiles = "";

        public List<string> fileList;
        public string latestFile;
        public int lastIndex = -2;
        string topUserLanguage = AppLanguage.SymbolLanguage;

        public HomePage()
        {
            InitializeComponent();

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            FileComboBox.ItemsSource = Files;
            HighlightBlockBackground.Visibility = Visibility.Collapsed;

            // 运行加载函数
            getFileList();
            UpdateTheme();
            StartThemeListener();
            // 用户强制指定主题（或主题被解析）时同步刷新高亮配色
            ActualThemeChanged += OnActualThemeChanged;
            LoadFilesAsync();
            GetScore(null, null);
        }

        public async void DeleteFile(object sender, RoutedEventArgs e)
        {
            if (FileComboBox.SelectedItem is FileInfo selectedFile)
            {
                deletedFiles = String.Empty;
                foreach (string line in FiltFile(selectedFile.FullName))
                {
                    deletedFiles += ("^\"" + line + "^\" ");
                }
                await deleteFile(deletedFiles);
                RemoveButton.Flyout.Hide();
                RefreshFileButton_Click(null, null);

            }
        }

        public async Task deleteFile(string Files)
        {
            string batchFilePath = IoPath.Combine(FileHelper.installDir, "Commands", "File_delete.bat");

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"\"{batchFilePath}\" {Files}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
            };

            try
            {
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    // 用 Task.Run 避免阻塞 UI 线程；WaitForExit 在 runas 场景下仍然有效
                    await Task.Run(() => process.WaitForExit());
                }
            }
            catch (Exception)
            {
                ErrorDialog.Show(this.Content.XamlRoot, Loader.GetString("DeleteCanceled"));
                throw; // 让调用者知道失败了，可以不刷新
            }
        }

        public static string FormatDateTime(List<string> stringList)
        {
            if (stringList == null || stringList.Count == 0)
                throw new ArgumentException("列表不能为空");

            // 1. 获取最后一项
            string lastItem = stringList.Last();

            // 2. 按空格分割，取第一部分（日期时间字符串）
            string dateTimePart = lastItem.Split(' ')[0] + " " + lastItem.Split(' ')[1];

            // 3. 解析为 DateTime（格式：yyyy-MM-dd HH.mm.ss.fff）
            string format = "yyyy-MM-dd HH.mm.ss.fff";
            if (!DateTime.TryParseExact(dateTimePart, format, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out DateTime parsedDate))
            {
                throw new FormatException($"无法解析日期时间：{dateTimePart}");
            }

            // 4. 按系统当前区域设置格式化，使用 "G" 标准格式（含秒）
            string formatted = parsedDate.ToString("G", CultureInfo.CurrentCulture);
            return formatted;
        }

        /// <summary>
        /// 获取指定文件所在目录的文件列表（按修改时间升序），
        /// 并以该文件为最底端，向上查找文件名包含 "Formal.Assessment" 的文件，
        /// 返回介于两者之间的所有文件（不包括标记文件，包括输入文件本身）。
        /// 若找不到标记文件，则返回从最旧文件到输入文件的所有文件。
        /// </summary>
        /// <param name="filePath">目标文件路径（必须存在于目录中）</param>
        /// <param name="marker">标记字符串，默认 "Formal.Assessment"（忽略大小写）</param>
        /// <returns>符合条件的文件完整路径列表</returns>

        public static List<string> FiltFile(string filePath, string marker = "Formal.Assessment")
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", nameof(filePath));

            string? directory = IoPath.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("无法提取目录", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"文件不存在: {filePath}");

            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"目录不存在: {directory}");

            // 1. 获取目录中所有文件，按修改时间升序排序（最旧 → 最新）
            var sortedFiles = FileHelper.GetFileTimeList(directory);

            // 2. 找到输入文件在排序列表中的索引
            int inputIndex = sortedFiles.IndexOf(filePath);
            if (inputIndex == -1)
                throw new InvalidOperationException($"文件 {filePath} 未在目录的文件列表中（可能已被删除）");

            // 3. 从输入文件的前一个位置开始向上查找标记文件
            int markerIndex = -1;
            for (int i = inputIndex - 1; i >= 0; i--)
            {
                string fileName = IoPath.GetFileName(sortedFiles[i]);
                if (fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    markerIndex = i;
                    break;
                }
            }

            // 4. 构造返回范围
            int startIndex;
            if (markerIndex != -1)
            {
                // 找到标记文件，从标记文件的下一个开始，到输入文件结束（含输入文件）
                startIndex = markerIndex + 1;
            }
            else
            {
                // 未找到标记文件，从列表开头到输入文件（含输入文件）
                startIndex = 0;
            }

            // 5. 提取子列表（包括输入文件）
            int count = inputIndex - startIndex + 1;
            if (count <= 0)
                return new List<string>(); // 理论上不会发生，但若标记文件紧邻输入文件则返回空

            return sortedFiles.GetRange(startIndex, count);
        }

        public void getFileList()
        {
            fileList = FileHelper.GetFileTimeList(FileHelper.winSatFilePath).Select(x => IoPath.GetFileName(x)).ToList();

            if (fileList.Count != 0)
            {
                latestFile = fileList.Last();
            } else
            {
                latestFile = string.Empty;
                lastIndex = -2;
            }
        }

        private async Task LoadFilesAsync()
        {
            try
            {
                if (!Directory.Exists(FileHelper.winSatFilePath))
                {
                    await new ContentDialog
                    {
                        Title = Loader.GetString("DirectoryNotFoundTitle"),
                        Content = string.Format(Loader.GetString("DirectoryNotFoundContent"), FileHelper.winSatFilePath),
                        CloseButtonText = Loader.GetString("OK"),
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                    return;
                }

                var files = await Task.Run(() =>
                {
                    var directoryInfo = new DirectoryInfo(FileHelper.winSatFilePath);
                    return directoryInfo.GetFiles();
                });

                Files.Clear();
                foreach (var file in files)
                {
                    if(file.Name.Contains("Formal.Assessment")) {
                        Files.Add(file);
                    }
                }

                if (FileComboBox.Items.Count == 0) // 无项
                {
                    RemoveButton.Visibility = Visibility.Collapsed;
                    LatestWarning.Visibility = Visibility.Collapsed;
                    SelectorBar.Visibility = Visibility.Collapsed;
                    HighlightBlockBackground.Visibility = Visibility.Collapsed;
                    FriendlyBackground.Visibility = Visibility.Collapsed;
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    LoadingRing.IsActive = false;
                } else
                {
                    RemoveButton.Visibility = Visibility.Visible;
                    LatestWarning.Visibility = Visibility.Visible;
                    SelectorBar.Visibility = Visibility.Visible;
                    HighlightBlockBackground.Visibility = Visibility.Collapsed;
                    FriendlyBackground.Visibility = Visibility.Collapsed;
                }

                getFileList();

                if (lastIndex == -2)
                {
                    FileComboBox.SelectedIndex = FileComboBox.Items.Count - 1;
                } else
                {
                    FileComboBox.SelectedIndex = lastIndex;
                }
                lastIndex = FileComboBox.SelectedIndex;
            }
            catch (UnauthorizedAccessException)
            {
                await new ContentDialog
                {
                    Title = Loader.GetString("PermissionDeniedTitle"),
                    Content = Loader.GetString("PermissionDeniedContent"),
                    CloseButtonText = Loader.GetString("OK"),
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
            catch (ArgumentException)
            {
                // 参数错误：通常是 FileComboBox 选择了不存在的索引
                FileComboBox.SelectedIndex = FileComboBox.Items.Count - 1; // 重置
                lastIndex = FileComboBox.SelectedIndex;
            }
        }

        private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileComboBox.SelectedItem is FileInfo selectedFile)
            {
                RemoveButton.IsEnabled = !(IoPath.GetFileName(selectedFile.FullName) == latestFile);
                LatestWarning.Visibility = (IoPath.GetFileName(selectedFile.FullName) == latestFile) ? Visibility.Visible: Visibility.Collapsed;

                LoadingPanel.Visibility = Visibility.Visible; // 显示加载动画
                LoadingRing.IsActive = true;
                SelectorBar.Visibility = Visibility.Visible;
                LoadingFileErrorBar.IsOpen = false;
                LoadingSignalErrorBar.IsOpen = false;
                LoadingFileErrorBar.Message = "";
                LoadingSignalErrorBar.Message = "";
                HighlightBlockBackground.Visibility = Visibility.Collapsed;
                FriendlyBackground.Visibility = Visibility.Collapsed;

                ValuePanel.Children.Clear();
                HighlightXml(""); // 清空之前的内容

                if (!System.IO.File.Exists(selectedFile.FullName))
                {
                    LoadingFileErrorBar.Message = Loader.GetString("FileNotExist");
                    LoadingFileErrorBar.IsOpen = true;
                    return;
                }
                SelectorBarItem selectedItem = SelectorBar.SelectedItem;
                int currentSelectedIndex = SelectorBar.Items.IndexOf(selectedItem);

                switch (currentSelectedIndex)
                {
                    case 0:
                        bool isBarError = false;
                        bool notLoad = false;

                        var symbolPath = IoPath.Combine(FileHelper.installDir, "Symbols", $"{topUserLanguage}.json");
#if DEBUG
                        var parserPath = IoPath.Combine(FileHelper.installDir, "Commands", "xmlparser.py");
                        var command = "python";
                        var args = $"\"{parserPath}\" \"{selectedFile.FullName}\" \"{symbolPath}\" ";
#else
                        var parserPath = IoPath.Combine(FileHelper.installDir, "Commands", "xmlparser.exe");
                        var command = parserPath;
                        var args = $"\"{selectedFile.FullName}\" \"{symbolPath}\" ";
#endif

                        if ((!File.Exists(parserPath)) || (!File.Exists(symbolPath)))
                        {
                            isBarError = true;
                            LoadingSignalErrorBar.Message += Loader.GetString("ParserMissing") + "\n";
                            notLoad = true;
                        }

                        string lines = (await Commands.Execute(command, args));

                        if (lines.StartsWith("Error"))
                        {
                            isBarError = true;
                            LoadingSignalErrorBar.Message += $"{lines}\n";
                            notLoad = true;
                        }

                        foreach (string line in lines.Split("\n"))
                        {
                            if (!string.IsNullOrEmpty(line) && line.StartsWith("<error>", StringComparison.Ordinal))
                            {
                                isBarError = true;
                                LoadingSignalErrorBar.Message += $"\n{line.Replace("<error>", "").Replace("\\n", "\n")}";
                            }
                            else if (!string.IsNullOrEmpty(line) && line.StartsWith("<line>", StringComparison.Ordinal))
                            {
                                var panel = new Grid()
                                {
                                    Margin = new Thickness(0, 0, 0, 4)
                                };
                                var text = new TextBlock()
                                {
                                    Text = line.Replace("<line>", "").Trim(),
                                    FontSize = 16,
                                    FontWeight = FontWeights.Bold
                                }; // 字符
                                var Blueline = new Line() {
                                    X1 = 160,
                                    X2 = 1900,
                                    Y1 = 12,
                                    Y2 = 12,
                                    StrokeThickness = 4,
                                    Stroke = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentTextFillColorTertiaryBrush"]
                                }; // 线

                                panel.Children.Add(text);
                                panel.Children.Add(Blueline);
                                ValuePanel.Children.Add(panel);
                            }
                            else if (!string.IsNullOrEmpty(line) && line.StartsWith("<subline>", StringComparison.Ordinal))
                            {
                                var panel = new Grid()
                                {
                                    Margin = new Thickness(0, 4, 0, 4)
                                };
                                var text = new TextBlock()
                                {
                                    Text = line.Replace("<subline>", "").Trim(),
                                    FontSize = 13,
                                    FontWeight = FontWeights.Bold
                                }; // 字符

                                panel.Children.Add(text);
                                ValuePanel.Children.Add(panel);
                            }
                            else if (!string.IsNullOrEmpty(line) && line.StartsWith("<spacing>", StringComparison.Ordinal)) // 空格
                            {
                                var panel = new Grid()
                                {
                                    Margin = new Thickness(0, 0, 0, 2)
                                };
                                ValuePanel.Children.Add(panel);
                            }
                            else
                            {
                                String[] splited = line.Trim().Split(",", 2);
                                if (splited.Length == 2)
                                {
                                    var panel = new Grid(); // 面板

                                    var fridenlyText = new TextBlock()
                                    {
                                        Text = splited[0],
                                    }; // 键
                                    var friendlyValue = new TextBlock()
                                    {
                                        Text = splited[1],
                                        Margin = new Thickness(350, 0, 0, 0)
                                    }; // 值

                                    // 添加
                                    panel.Children.Add(fridenlyText);
                                    panel.Children.Add(friendlyValue);
                                    ValuePanel.Children.Add(panel);
                                }
                            }
                        }
                        if (isBarError)
                        {
                            LoadingSignalErrorBar.Message = LoadingSignalErrorBar.Message.Trim();
                            LoadingSignalErrorBar.IsOpen = true;
                        } // 加载可视化试图

                        if (!notLoad)
                        {
                            FriendlyBackground.Visibility = Visibility.Visible;
                        }
                        LoadingPanel.Visibility = Visibility.Collapsed;
                        LoadingRing.IsActive = false;
                        break;
                    case 1:
                        string fileContent = System.IO.File.ReadAllText(selectedFile.FullName);
                        HighlightXml(fileContent);
                        LoadingPanel.Visibility = Visibility.Collapsed;
                        LoadingRing.IsActive = false;

                        HighlightBlockBackground.Visibility = Visibility.Visible;
                        break;
                    default:
                        return;
                }
            }
            else
            {
                return;
            }
        }

        public void selecterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            SelectionChanged(null, null);
        }

        public async void RefreshFileButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingFileErrorBar.IsOpen = false;
            await LoadFilesAsync();
            RefreshedFile.Visibility = Visibility.Visible;
        }

        public async void GetScore(object sender, RoutedEventArgs e)
        {
            if (_isDialogShowing) return;
            _isDialogShowing = true;

            try
            {
                List<string> score_list = await ScoreHelper.GetScore();

                // 设置显示
                LatestTip.Visibility = Visibility.Collapsed;
                RectangleCanvas.Visibility = Visibility.Visible;
                MinScoreText.Visibility = Visibility.Visible;
                LatestUpdateTime.Visibility = Visibility.Collapsed;
                RectangleBackground.Background = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentTextFillColorTertiaryBrush"];
                // score_list[6] = "2";

                switch (score_list[6])
                {
                    case "1":
                        LatestTip.Visibility = Visibility.Visible;
                        LatestUpdateTime.Visibility = Visibility.Visible;
                        LatestUpdateTime.Text = string.Format(Loader.GetString("LastUpdateTime"), FormatDateTime(FileHelper.GetFileTimeList(FileHelper.winSatFilePath).Select(x => IoPath.GetFileName(x)).ToList()));
                        break;
                    case "2":
                        WinSatTip.Title = Loader.GetString("HardwareChangedTitle");
                        WinSatTip.Message = Loader.GetString("HardwareChangedText");
                        WinsatTipButton.Content = Loader.GetString("Refresh");
                        WinSatTip.IsOpen = true;
                        RectangleBackground.Background = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentTextFillColorDisabledBrush"];
                        break;
                    case "3":
                        WinSatTip.Title = "";
                        WinSatTip.Message = Loader.GetString("NotGetTip");
                        WinsatTipButton.Content = Loader.GetString("GetScore");
                        WinSatTip.IsOpen = true;
                        RectangleBackground.Background = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentTextFillColorDisabledBrush"];
                        RectangleCanvas.Visibility = Visibility.Collapsed;
                        MinScoreText.Visibility = Visibility.Collapsed;
                        break;
                    default: break;
                }

                CPUScore.Text = score_list[0];
                D3DScore.Text = score_list[1];
                DiskScore.Text = score_list[2];
                GraphicsScore.Text = score_list[3];
                MemoryScore.Text = score_list[4];
                WinSPRLevel.Text = score_list[7];
            }
            finally
            {
                _isDialogShowing = false;
            }
        }

        public async void RunScore(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _isRunning = true;
            WinSatTip.IsOpen = false;

            await Commands.Execute("cmd", "/c winsat formal -restart clean");

            _isRunning = false;

            GetScore(null, null);
            RefreshFileButton_Click(null, null);
        }

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

            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
            {
                object? registryValueObject = key?.GetValue(RegistryValueName);
                if (registryValueObject == null)
                    return false;

                int registryValue = (int)registryValueObject;
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

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateTheme();
            HighlightXml(xmlContent);
        }

        private void UpdateTheme()
        {
            var theme = WindowThemeIsDark();
            if (theme)
            {
                SymbolBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
                TagBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x88, 0x0A));
                KeyBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x0E, 0xBA, 0xFF));
                ValueBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x0A, 0xED, 0xCF));
                CommentBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x09, 0xCD, 0x05));
                TextBrush = new SolidColorBrush(Color.FromArgb(255, 0xEF, 0xEF, 0xEF));
            }
            else
            {
                SymbolBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x5C, 0x5C, 0x5C));
                TagBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0xE8, 0x6B, 0x17));
                KeyBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x01, 0x79, 0xAD));
                ValueBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x0D, 0xAB, 0x8B));
                CommentBrush =
                    new SolidColorBrush(Color.FromArgb(255, 0x05, 0xAB, 0x18));
                TextBrush = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x00, 0x01));
            }
        }

        private bool WindowThemeIsDark()
        {
            // 使用元素实际生效的主题，这样用户强制浅色/深色时高亮配色也能保持一致
            return ActualTheme == ElementTheme.Dark;
        }

        // ---- XML 格式化（新增） ----
        private string FormatXml(string xml)
        {
            if (string.IsNullOrEmpty(xml))
                return String.Empty;

            try
            {
                var doc = new XmlDocument();
                doc.PreserveWhitespace = false;
                doc.LoadXml(xml);

                using var sw = new StringWriter();
                using var xtw = new XmlTextWriter(sw)
                {
                    Formatting = Formatting.Indented,
                    Indentation = 2
                };
                doc.WriteTo(xtw);
                xtw.Flush();
                return sw.ToString();
            }
            catch
            {
                // 格式化失败（例如 XML 无效），返回 null，后续使用原始内容
                return String.Empty;
            }
        }

        // ---- 高亮逻辑（已集成格式化） ----
        public void HighlightXml(string xml)
        {
            if (new object?[] { TextBrush, SymbolBrush, TagBrush, KeyBrush, ValueBrush, CommentBrush }.Any(x => x is null))
            {
                // 存在 null
                throw new ArgumentNullException("出现一个 Null 的Brush");
            }

            xmlContent = xml; // 保留原始内容

            // 自动格式化（缩进2空格），若失败则保留原始
            string formatted = FormatXml(xml);
            if (!string.IsNullOrEmpty(formatted))
                xml = formatted;

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
                        AddRun(paragraph, comment, CommentBrush);
                        index = end;
                    }
                    else if (xml.Length - index >= 2 && xml.Substring(index, 2) == "<?")
                    {
                        int start = index;
                        int end = xml.IndexOf("?>", index);
                        if (end == -1) end = xml.Length;
                        else end += 2;
                        string pi = xml.Substring(start, end - start);
                        AddRun(paragraph, pi, SymbolBrush);
                        index = end;
                    }
                    else if (xml.Length - index >= 9 && xml.Substring(index, 9) == "<![CDATA[")
                    {
                        int start = index;
                        int end = xml.IndexOf("]]>", index);
                        if (end == -1) end = xml.Length;
                        else end += 3;
                        string cdata = xml.Substring(start, end - start);
                        AddRun(paragraph, cdata, null);
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
                SolidColorBrush brush;
                switch (token.Type)
                {
                    case TokenType.Delimiter:
                        brush = SymbolBrush;
                        break;
                    case TokenType.TagName:
                        brush = TagBrush;
                        break;
                    case TokenType.AttributeName:
                        brush = KeyBrush;
                        break;
                    case TokenType.AttributeValue:
                        brush = ValueBrush;
                        break;
                    default:
                        brush = TextBrush;
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
                            state = STATE_AFTER_NAME;
                        }
                        else
                        {
                            i++;
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

        private void AddRun(Paragraph paragraph, string text, SolidColorBrush brush)
        {
            if (string.IsNullOrEmpty(text)) return;
            var run = new Run { Text = text };
            run.Foreground = brush ?? TextBrush;
            paragraph.Inlines.Add(run);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}