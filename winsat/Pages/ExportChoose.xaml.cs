using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using winsat.helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat.pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ExportChoose : Page
    {
        private int lastIndex = -2; // 最后加载的索引
        public string winSatFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Performance", "WinSAT", "DataStore"); // 跑分路径
        public ObservableCollection<FileInfo> Files { get; } = new(); // 文件容器
        public List<string> fileList; // 文件列表
        public string latestFile; // 最新文件
        public string openFilePath; // 打开的文件路径

        public ExportChoose()
        {
            InitializeComponent();
            getFileList();

            FileComboBox.ItemsSource = Files; // 设置容器

            LoadFilesAsync();
        }

        public void getFileList()
        {
            fileList = FileHelper.GetFileTimeList(winSatFilePath).Select(x => Path.GetFileName(x)).ToList();

            if (fileList.Count != 0)
            {
                latestFile = fileList.Last();
            }
            else
            {
                latestFile = string.Empty;
                lastIndex = -2;
            }
        }

        public void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileComboBox.SelectedItem is FileInfo selectedFile)
            {
                LoadingFileErrorBar.IsOpen = false;
                BottomBar.Visibility = Visibility.Visible;

                if (!File.Exists(selectedFile.FullName))
                {
                    LoadingFileErrorBar.Title = Loader.GetString("LoadFileErrorTitleCS");
                    LoadingFileErrorBar.Message = Loader.GetString("FileNotExist");
                    LoadingFileErrorBar.IsOpen = true;
                    BottomBar.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        public async void RefreshFileButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingFileErrorBar.IsOpen = false;
            RefreshComplete.Visibility = Visibility.Visible; // 显示刷新完成按钮
            await LoadFilesAsync();
        }

        private async Task LoadFilesAsync()
        {
            try
            {
                if (!Directory.Exists(winSatFilePath))
                {
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

                getFileList();

                if (lastIndex == -2)
                {
                    FileComboBox.SelectedIndex = FileComboBox.Items.Count - 1;
                }
                else
                {
                    FileComboBox.SelectedIndex = lastIndex;
                }
                lastIndex = FileComboBox.SelectedIndex;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
                // 参数错误：通常是 FileComboBox 选择了不存在的索引
                FileComboBox.SelectedIndex = FileComboBox.Items.Count - 1; // 重置
                lastIndex = FileComboBox.SelectedIndex;
            }
        }

        private bool syncingWstOptions; // true 表示正在由代码同步勾选状态，需忽略随之触发的 Checked/Unchecked/Indeterminate
        private bool wstListReady; // 列表构建完成后才允许“全选”交互（XAML 解析期间及首次加载完成前子项容器可能还是 null）
        private bool? allOptionsState; // “全选”框最近一次由代码设置的状态，用于判断“点击前”的状态
        private int wstReloadVersion; // 刷新版本号，避免并发刷新时旧结果覆盖新结果

        /// <summary>
        /// 重新加载“包模式”的文件勾选列表（进入包模式与“刷新”按钮共用）
        /// </summary>
        private void ReloadFileList()
        {
            _ = ReloadFileListAsync();
        }

        private void RefreshWstFileButton_Click(object sender, RoutedEventArgs e)
        {
            WSTRefreshComplete.Visibility = Visibility.Visible; // 显示刷新完成提示
            ReloadFileList();
        }

        private async Task ReloadFileListAsync()
        {
            int version = ++wstReloadVersion;

            // 记住刷新前的勾选状态（按文件完整路径），刷新后仍存在的文件沿用旧状态，新文件默认勾选
            var checkedBefore = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var box in GetWstOptionBoxes())
            {
                if (box.Tag is string path)
                {
                    checkedBefore[path] = box.IsChecked == true;
                }
            }

            List<string> files;
            try
            {
                files = await Task.Run(() => FileHelper.GetAnalyzableFiles());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
            {
                Debug.WriteLine($"[WST] 读取跑分目录失败: {ex.Message}");
                files = new List<string>();
            }

            if (version != wstReloadVersion) return; // 已有更新的刷新，丢弃本次结果

            syncingWstOptions = true;
            ExportFileContent.Children.Clear();

            foreach (string path in files)
            {
                var option = new CheckBox
                {
                    Content = Path.GetFileName(path), // 只显示源文件名
                    Tag = path,
                    Margin = new Thickness(24, 0, 0, 0),
                    IsChecked = !checkedBefore.TryGetValue(path, out bool wasChecked) || wasChecked,
                };
                option.Checked += Option_Checked;
                option.Unchecked += Option_Unchecked;

                ExportFileContent.Children.Add(option);
            }

            OptionsAllCheckBox.IsEnabled = files.Count > 0; // 没有可选文件时禁用“全选”
            OptionsAllCheckBox.IsChecked = files.Count > 0; // 默认全选
            allOptionsState = files.Count > 0;
            syncingWstOptions = false;
            wstListReady = true;
        }

        /// <summary>
        /// 子项容器；XAML 解析期间可能尚未创建，此处返回空集合避免 NullReferenceException
        /// </summary>
        private IEnumerable<CheckBox> GetWstOptionBoxes() =>
            ExportFileContent is null ? Enumerable.Empty<CheckBox>() : ExportFileContent.Children.OfType<CheckBox>();

        /// <summary>
        /// 取出所有已勾选选项指向的文件路径
        /// </summary>
        private List<string> GetCheckedWstFiles() =>
            GetWstOptionBoxes()
                .Where(box => box.IsChecked == true && box.Tag is string)
                .Select(box => (string)box.Tag)
                .ToList();

        // 一键选择：全不 -> 全选，部分 -> 全不，全选 -> 全不。
        // 不依赖 CheckBox 自带三态循环的先后顺序（各框架/版本不一致，之前就是因此“按了没反应”），
        // 而是用 allOptionsState 里记录的“点击前”状态来决定目标状态。

        private void SelectAll_Checked(object sender, RoutedEventArgs e) => OnSelectAllToggled();

        private void SelectAll_Unchecked(object sender, RoutedEventArgs e) => OnSelectAllToggled();

        private void SelectAll_Indeterminate(object sender, RoutedEventArgs e) => OnSelectAllToggled();

        /// <summary>
        /// 点击三态“全选”框：只有点击前是“全不”才变成“全选”，其余（部分/全选）都变成“全不”
        /// </summary>
        private void OnSelectAllToggled()
        {
            if (!wstListReady || syncingWstOptions) return;

            SetAllWstOptions(allOptionsState == false);
        }

        private void Option_Checked(object sender, RoutedEventArgs e) => SyncAllCheckBoxState();

        private void Option_Unchecked(object sender, RoutedEventArgs e) => SyncAllCheckBoxState();

        /// <summary>
        /// 勾选/取消勾选全部子项，并同步“全选”框本身
        /// </summary>
        private void SetAllWstOptions(bool isChecked)
        {
            if (!wstListReady) return;

            syncingWstOptions = true;

            foreach (var box in GetWstOptionBoxes())
            {
                box.IsChecked = isChecked;
            }

            OptionsAllCheckBox.IsChecked = isChecked;
            allOptionsState = isChecked;

            syncingWstOptions = false;
        }

        /// <summary>
        /// 根据子项勾选情况同步三态“全选”框：全不 -> false，全选 -> true，部分 -> null
        /// </summary>
        private void SyncAllCheckBoxState()
        {
            if (!wstListReady || syncingWstOptions) return;

            var boxes = GetWstOptionBoxes().ToList();
            int checkedCount = boxes.Count(box => box.IsChecked == true);

            bool? state;
            if (boxes.Count == 0 || checkedCount == 0)
            {
                state = false;
            }
            else if (checkedCount == boxes.Count)
            {
                state = true;
            }
            else
            {
                state = null;
            }

            syncingWstOptions = true;
            OptionsAllCheckBox.IsChecked = state;
            allOptionsState = state;
            syncingWstOptions = false;
        }

        private void HTMLExportShow(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Collapsed;
            HTMLExport.Visibility = Visibility.Visible;
            ExportSuccessBar.IsOpen = false;
            ExportWarningBar.IsOpen = false;
            ExportErrorBar.IsOpen = false;
            WorkingBar.Visibility = Visibility.Collapsed;
            WorkingRing.IsActive = false;

            ControlBar.Visibility = Visibility.Visible;
        }

        private void WSTExportShow(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Collapsed;
            WSTExport.Visibility = Visibility.Visible;
            ExportSuccessBar.IsOpen = false;
            ExportWarningBar.IsOpen = false;
            ExportErrorBar.IsOpen = false;
            WorkingBar.Visibility = Visibility.Collapsed;
            WorkingRing.IsActive = false;
            WSTRefreshComplete.Visibility = Visibility.Collapsed;
            ReloadFileList();

            ControlBar.Visibility = Visibility.Visible;
        }

        private void OnPrevPage(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Visible;
            HTMLExport.Visibility = Visibility.Collapsed;
            WSTExport.Visibility = Visibility.Collapsed;

            ControlBar.Visibility = Visibility.Collapsed;
        }

        private async void ExportSuccessFileButton_Click(object sender, RoutedEventArgs e)
        {
            await Commands.Execute("explorer.exe", $"/select,\"{openFilePath}\"");
        }

        /// <summary>
        /// 从解析器输出里收集带指定协议前缀的行（&lt;error&gt; / &lt;warning&gt;），把转义的 \\n 还原成换行。
        /// </summary>
        private static bool CollectProtocolLines(string output, string prefix, out string message)
        {
            bool found = false;
            string text = String.Empty;

            foreach (string line in output.Split("\n"))
            {
                if (!string.IsNullOrEmpty(line) && line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    found = true;
                    text += "\n" + line.Substring(prefix.Length).Replace("\\n", "\n");
                }
            }

            message = text.Trim();
            return found;
        }

        private async void ExportFileButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. 创建 FileSavePicker，并传入当前窗口的 AppWindow.Id
            var window = App.MainWindow!;
            var savePicker = new FileSavePicker(window.AppWindow.Id)
            {
                // 2. 可选：设置初始位置、建议文件名、文件类型等
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)} WinSAT", // 设置格式
            };


            if (HTMLExport.Visibility == Visibility.Visible)
            {
                savePicker.FileTypeChoices.Add(Loader.GetString("HTMLExtDesc"), new List<string>{".html", ".htm" });

                // 3. 显示保存对话框，并等待用户操作
                var result = await savePicker.PickSaveFileAsync();

                if (result != null)
                {
                    var scores = await ScoreHelper.GetScore();

                    string checkState;

                    if (EasyViewCheckbox.IsChecked != null)
                    {
                        checkState = (bool)EasyViewCheckbox.IsChecked ? "--score" : "";
                    }
                    else
                    {
                        checkState = String.Empty;
                    }

                    ControlBar.Visibility = Visibility.Collapsed;
                    BottomBar.Visibility = Visibility.Visible;
                    WorkingBar.Visibility = Visibility.Visible;
                    WorkingRing.IsActive = true;
#if DEBUG
                    var cmdlines = await Commands.Execute(
                        "python.exe",
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "xmlParser.py")}\" " +
                        $"\"{FileComboBox.SelectedValue}\" " +
                        $"\"{Path.Combine(FileHelper.installDir, "Symbols")}\" " +
                        $"--html \"{result.Path}\" " +
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "templates")}\" " +
                        string.Join(" ", scores.Take(5)) +
                        $" {checkState}");
#else
                    var cmdlines = await Commands.Execute(
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "xmlParser.exe")}\" ",
                        $"\"{FileComboBox.SelectedValue}\" " +
                        $"\"{Path.Combine(FileHelper.installDir, "Symbols")}\" " +
                        $"--html \"{result.Path}\" " +
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "templates")}\" " +
                        string.Join(" ", scores.Take(5)) +
                        $" {checkState}");
#endif

                    bool showError = CollectProtocolLines(cmdlines, "<error>", out string errMsg);
                    bool showWarning = CollectProtocolLines(cmdlines, "<warning>", out string warnMsg);

                    ExportErrorBar.IsOpen = showError;
                    ExportErrorBar.Message = errMsg;

                    ExportWarningBar.IsOpen = showWarning;
                    ExportWarningBar.Message = warnMsg;

                    WorkingBar.Visibility = Visibility.Collapsed;
                    WorkingRing.IsActive = false;

                    // HTML 导出成功
                    HTMLExport.Visibility = Visibility.Collapsed;
                    ExportEnd.Visibility = Visibility.Visible;

                    if (!showError)
                    {
                        ExportSuccessBar.Message = Loader.GetString("ExportSuccessBarTitleCS") + result.Path;
                        ExportSuccessBar.IsOpen = true;

                        openFilePath = result.Path;
                    }
                }
            }
            else if (WSTExport.Visibility == Visibility.Visible)
            {
                ExportErrorBar.IsOpen = false;
                ExportWarningBar.IsOpen = false;
                ExportSuccessBar.IsOpen = false;

                List<string> checkedFiles = GetCheckedWstFiles();

                if (checkedFiles.Count == 0)
                {
                    ExportErrorBar.Message = Loader.GetString("NoFileSelected");
                    ExportErrorBar.IsOpen = true;
                    return;
                }

                savePicker.FileTypeChoices.Add(Loader.GetString("WSTExtDesc"), new List<string> { ".wst"});

                var result = await savePicker.PickSaveFileAsync();

                if (result != null)
                {
                    // wst 模式用不到 source，占位传空串；symbol 只用来读 $locale（警告/错误的文案来源）
                    string symbolPath = Path.Combine(FileHelper.installDir, "Symbols", $"{AppLanguage.SymbolLanguage}.json");
                    string fileArgs = string.Join(" ", checkedFiles.Select(file => $"\"{file}\""));

                    ControlBar.Visibility = Visibility.Collapsed;
                    WorkingBar.Visibility = Visibility.Visible;
                    WorkingRing.IsActive = true;

#if DEBUG
                    var cmdlines = await Commands.Execute(
                        "python.exe",
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "xmlParser.py")}\" " +
                        $"\"a\" " +
                        $"\"{symbolPath}\" " +
                        $"--wst \"{result.Path}\" " +
                        fileArgs);
#else
                    var cmdlines = await Commands.Execute(
                        $"\"{Path.Combine(FileHelper.installDir, "Commands", "xmlParser.exe")}\"",
                        $"\"a\" " +
                        $"\"{symbolPath}\" " +
                        $"--wst \"{result.Path}\" " +
                        fileArgs);
#endif

                    WorkingBar.Visibility = Visibility.Collapsed;
                    WorkingRing.IsActive = false;

                    bool showError = CollectProtocolLines(cmdlines, "<error>", out string errMsg);
                    bool showWarning = CollectProtocolLines(cmdlines, "<warning>", out string warnMsg);

                    ExportErrorBar.IsOpen = showError;
                    ExportErrorBar.Message = errMsg;

                    ExportWarningBar.IsOpen = showWarning;
                    ExportWarningBar.Message = warnMsg;

                    // 包导出成功（警告信息可以与成功提示并存）
                    WSTExport.Visibility = Visibility.Collapsed;
                    ExportEnd.Visibility = Visibility.Visible;

                    if (!showError)
                    {
                        ExportSuccessBar.Message = Loader.GetString("ExportSuccessBarTitleCS") + result.Path;
                        ExportSuccessBar.IsOpen = true;

                        openFilePath = result.Path;
                    }
                }
            }
        }
    }
}
