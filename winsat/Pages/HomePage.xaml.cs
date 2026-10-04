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
using System.ComponentModel;
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

        /// <summary>分数方框当前是否使用强调色（否则使用禁用态画笔），主题变化时据此重新取色。</summary>
        private bool _scoreBoxUsesAccent = true;

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
                // 双保险：最新一份跑分不允许删除（正常情况下按钮已经被禁用，
                // 这里再挡一次，避免按钮状态和实际选中项不同步时删掉当前分数）
                if (IsLatestDataStoreScore(selectedFile))
                {
                    RemoveButton.IsEnabled = false;
                    LatestWarning.Visibility = Visibility.Visible;
                    RemoveButton.Flyout.Hide();
                    return;
                }

                deletedFiles = String.Empty;

                if (FileHelper.IsInScoresDir(selectedFile.FullName))
                {
                    // Scores 下导入的跑分：只删这一个文件（不动 DataStore 里同一份跑分的其它文件）
                    deletedFiles = "^\"" + selectedFile.FullName + "^\" ";
                }
                else
                {
                    foreach (string line in FiltFile(selectedFile.FullName))
                    {
                        deletedFiles += ("^\"" + line + "^\" ");
                    }
                }

                // 返回 false = 没删成（用户取消了 UAC 提权，或真的出错了）：此时不要刷新列表
                bool deleted = await deleteFile(deletedFiles);
                RemoveButton.Flyout.Hide();

                if (deleted)
                {
                    RefreshFileButton_Click(null, null);
                }
            }
        }

        /// <summary>
        /// 通过批处理删除文件。true = 已执行删除，false = 用户取消了 UAC 提权或发生错误。
        /// 本方法绝不能向外抛异常：它由 async void 的 DeleteFile 调用，
        /// 异常逃出去会走到 GlobalExceptionHandler，弹完错误框后直接退出程序
        /// （在删除时按 UAC 的"否"就是这个后果）。
        /// </summary>
        public async Task<bool> deleteFile(string Files)
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

                return true;
            }
            catch (Win32Exception ex) when (IsUserRefusedElevation(ex))
            {
                // 用户在 UAC 提权对话框上点了"否"：这是一次正常的选择，不是错误。
                // 静默返回：不弹错误框（UAC 本身已经把意思表达清楚了），也不刷新列表。
                return false;
            }
            catch (Exception ex)
            {
                // 其它失败：提示用户，但不 rethrow
                ErrorDialog.Show(this.Content.XamlRoot, ex.Message);
                return false;
            }
        }

        // 删除文件时需要 UAC 提权，用户点"否"时 Win32Exception 的错误码在不同
        // Windows/.NET 版本下不一样：ERROR_CANCELLED、ERROR_ACCESS_DENIED，
        // 或者错误码取到 0 导致 HResult 落回异常默认的 E_FAIL(0x80004005)。
        private const int ErrorCancelled = 1223; // ERROR_CANCELLED
        private const int ErrorAccessDenied = 5; // ERROR_ACCESS_DENIED
        private const int EFail = unchecked((int)0x80004005);

        private static bool IsUserRefusedElevation(Win32Exception ex)
            => ex.NativeErrorCode == ErrorCancelled
            || ex.NativeErrorCode == ErrorAccessDenied
            || ex.HResult == EFail;

        public static string FormatDateTime(List<string> stringList)
        {
            if (stringList == null || stringList.Count == 0)
                throw new ArgumentException("列表不能为空");

            // 1. 获取最后一项（列表按修改时间升序，最后一项即最新文件）。
            //    调用方可能传完整路径，也可能只传文件名，这里统一按文件名解析。
            string lastItem = stringList.Last();
            string fileName = IoPath.GetFileName(lastItem);

            // 2. 优先从文件名里解析时间，例如 "2025-08-24 16.32.19.423.winsat.etl"。
            DateTime time;
            if (TryParseDateTimeFromName(fileName, out DateTime fromName))
            {
                time = fromName;
            }
            else if (File.Exists(lastItem))
            {
                // 3. 文件名里没有可解析的时间（例如 winsat.log，或其它机型的命名格式），
                //    退回文件修改时间——不同机型 ETL 命名格式不一样，这里不能再抛异常。
                time = File.GetLastWriteTime(lastItem);
            }
            else
            {
                // 连文件都不存在（只给了文件名）：原样返回，避免崩溃。
                return fileName;
            }

            // 4. 按系统当前区域设置格式化，使用 "G" 标准格式（含秒）
            return time.ToString("G", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// 从文件名解析时间。ETL 文件名形如 "2025-08-24 16.32.19.423.winsat.etl"：
        /// 日期部分用系统区域格式（yyyy-MM-dd、M-d-yyyy 等都可能），时间部分固定为 "HH.mm.ss.fff"，
        /// 后面还可能跟着 ".winsat.etl" 等扩展名。解析不出来时返回 false，由调用方兜底。
        /// </summary>
        private static bool TryParseDateTimeFromName(string fileName, out DateTime result)
        {
            result = default;

            // 日期与时间用空格分隔，时间里的 '.' 同时也是扩展名分隔符，故按空格切成两段。
            string[] tokens = fileName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return false;

            if (!DateTime.TryParse(tokens[0], CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime date) &&
                !DateTime.TryParse(tokens[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return false;

            // 时间部分取前 4 段（时.分.秒.毫秒），忽略后面 ".winsat"、".etl" 等。
            string[] timeParts = tokens[1].Split('.');
            if (timeParts.Length < 4)
                return false;

            if (!int.TryParse(timeParts[0], out int hour) || hour > 23 ||
                !int.TryParse(timeParts[1], out int minute) || minute > 59 ||
                !int.TryParse(timeParts[2], out int second) || second > 59)
                return false;

            string fraction = timeParts[3];
            if (fraction.Length == 0 || !fraction.All(char.IsDigit))
                return false;

            int millisecond = int.Parse(fraction.PadRight(3, '0')[..3]);
            result = new DateTime(date.Year, date.Month, date.Day, hour, minute, second, millisecond);
            return true;
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
            // 判定"最新"时只统计 DataStore 里"可分析的跑分"（含 Formal.Assessment 的那几份）。
            // 不能直接用整个目录：一次 winsat 会同时写入 .etl 和各分量 XML，
            // 它们的 LastWriteTime 往往和 Formal 那份落在同一毫秒附近，
            // 排序后 Last() 很容易落到一个根本不在下拉框里的文件名上，
            // latestFile 便对不上任何选项，"最新一份不可删除"的保护就失效了。
            fileList = FileHelper.GetFileTimeList(FileHelper.winSatFilePath)
                .Where(FileHelper.IsAnalyzableFile)
                .Select(IoPath.GetFileName)
                .ToList();

            if (fileList.Count != 0)
            {
                latestFile = fileList.Last();
            } else
            {
                latestFile = string.Empty;
                lastIndex = -2;
            }
        }

        /// <summary>
        /// 是否为 DataStore 里最新的那份跑分：最新一份不允许删除
        /// （界面上的当前分数就来自它，删掉它还会连带删除该次跑分的其它文件）。
        /// Scores 目录下导入的跑分不算最新，永远可删。
        /// </summary>
        private bool IsLatestDataStoreScore(FileInfo file)
            => !FileHelper.IsInScoresDir(file.FullName)
            && string.Equals(file.Name, latestFile, StringComparison.OrdinalIgnoreCase);

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
                        XamlRoot = this.Content.XamlRoot,
                        // 对话框不继承窗口内容主题，需显式跟随软件主题
                        RequestedTheme = AppTheme.CurrentElementTheme
                    }.ShowAsync();
                    return;
                }

                var files = await Task.Run(() => FileHelper.GetAnalyzableFiles()
                    .Select(path => new FileInfo(path))
                    .ToList());

                Files.Clear();
                foreach (var file in files)
                {
                    Files.Add(file);
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
                    XamlRoot = this.Content.XamlRoot,
                    // 对话框不继承窗口内容主题，需显式跟随软件主题
                    RequestedTheme = AppTheme.CurrentElementTheme
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
                // "最新文件不可删除"只针对 DataStore 里最新的那份；Scores 下导入的跑分永远可删
                bool isProtectedLatest = IsLatestDataStoreScore(selectedFile);

                RemoveButton.IsEnabled = !isProtectedLatest;
                LatestWarning.Visibility = isProtectedLatest ? Visibility.Visible : Visibility.Collapsed;

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
                                // 线：必须"继续扩张"，不能用 Line + X2=1900 这种固定坐标——
                                // Line 的期望宽度是它几何图形的完整宽度（约 1740），
                                // 而横向 Auto 的 ScrollViewer 会把这个宽度报成"滚动容器的横向大小"，
                                // 于是整张卡片的 Content 被撑到 1000+，右侧内容全被裁掉。
                                // Rectangle 用 Stretch，期望宽度为 0，高度和位置与原来的 4px 线一致。
                                var Blueline = new Rectangle() {
                                    Height = 4,
                                    Margin = new Thickness(160, 10, 0, 0),
                                    HorizontalAlignment = HorizontalAlignment.Stretch,
                                    VerticalAlignment = VerticalAlignment.Top,
                                    Fill = (Microsoft.UI.Xaml.Media.SolidColorBrush)AccentBrushProbe.Background
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
                TotalScorePanel.Visibility = Visibility.Visible;
                LatestUpdateTime.Visibility = Visibility.Collapsed;
                SetScoreBoxAccent(true);
                // score_list[6] = "2";

                switch (score_list[6])
                {
                    case "1":
                        LatestTip.Visibility = Visibility.Visible;
                        LatestUpdateTime.Visibility = Visibility.Visible;
                        // 传完整路径：文件名解析不出来时还能退回文件修改时间
                        LatestUpdateTime.Text = string.Format(Loader.GetString("LastUpdateTime"), FormatDateTime(FileHelper.GetFileTimeList(FileHelper.winSatFilePath)));
                        break;
                    case "2":
                        WinSatTip.Title = Loader.GetString("HardwareChangedTitle");
                        WinSatTip.Message = Loader.GetString("HardwareChangedText");
                        WinsatTipButton.Content = Loader.GetString("Refresh");
                        WinSatTip.IsOpen = true;
                        SetScoreBoxAccent(false);
                        break;
                    case "3":
                        WinSatTip.Title = "";
                        WinSatTip.Message = Loader.GetString("NotGetTip");
                        WinsatTipButton.Content = Loader.GetString("GetScore");
                        WinSatTip.IsOpen = true;
                        SetScoreBoxAccent(false);
                        TotalScorePanel.Visibility = Visibility.Collapsed;
                        break;
                    default: break;
                }

                CPUScore.Text = score_list[0];
                D3DScore.Text = score_list[1];
                DiskScore.Text = score_list[2];
                GraphicsScore.Text = score_list[3];
                MemoryScore.Text = score_list[4];
                WinSPRLevel.Text = score_list[7];

                HighlightLowestScores();
            }
            finally
            {
                _isDialogShowing = false;
            }
        }

        /// <summary>
        /// 给子分数最低的那一行（并列则都算）加上背景：左边两角圆角、右边无圆角，
        /// 与第 4 列（基本分数）那整块无圆角背景连成一条。
        /// 取不到数值（未跑分 / 未评分文案）或最低分为 0 时，全部不显示。
        /// </summary>
        private void HighlightLowestScores()
        {
            var cells = new (Border Background, TextBlock Text)[]
            {
                (CPUScoreBackground, CPUScore),
                (MemoryScoreBackground, MemoryScore),
                (GraphicsScoreBackground, GraphicsScore),
                (D3DScoreBackground, D3DScore),
                (DiskScoreBackground, DiskScore),
            };

            var values = new double?[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                values[i] = double.TryParse(cells[i].Text.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) ? value : null;
            }

            // 有取不到的值，或最低分为 0（未跑分）：不显示任何背景
            if (values.Any(v => v is null) || values.Min(v => v!.Value) <= 0)
            {
                foreach (var cell in cells)
                    cell.Background.Visibility = Visibility.Collapsed;
                return;
            }

            double lowest = values.Min(v => v!.Value);
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i].Background.Visibility = values[i] == lowest
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
            RefreshScoreBoxBrush();
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

        /// <summary>
        /// 设置分数方框的颜色状态：强调色或禁用态。
        /// 画笔从页面内的主题探针读取，保证反色模式下跟随软件主题而非系统主题。
        /// </summary>
        private void SetScoreBoxAccent(bool useAccent)
        {
            _scoreBoxUsesAccent = useAccent;
            RefreshScoreBoxBrush();
        }

        /// <summary>按当前主题重新取色并应用到分数方框（主题切换时调用）。</summary>
        private void RefreshScoreBoxBrush()
        {
            RectangleBackground.Background = (SolidColorBrush)(_scoreBoxUsesAccent
                ? AccentBrushProbe.Background
                : AccentDisabledBrushProbe.Background);
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