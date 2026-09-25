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

                var files = await Task.Run(() =>
                {
                    var directoryInfo = new DirectoryInfo(winSatFilePath);
                    return directoryInfo.GetFiles();
                });

                Files.Clear();
                foreach (var file in files)
                {
                    if (file.Name.Contains("Formal.Assessment"))
                    {
                        Files.Add(file);
                    }
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

        private void HTMLExportShow(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Collapsed;
            HTMLExport.Visibility = Visibility.Visible;

            ControlBar.Visibility = Visibility.Visible;
        }

        private void WSTExportShow(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Collapsed;
            WSTExport.Visibility = Visibility.Visible;

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
                savePicker.FileTypeChoices.Add("HTML 文件", new List<string>{".html", ".htm" });

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

                    bool showError = false;
                    string errMsg = String.Empty;

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

                    foreach (string line in cmdlines.Split("\n"))
                    {
                        if (!string.IsNullOrEmpty(line) && line.StartsWith("<error>", StringComparison.Ordinal))
                        {
                            showError = true;
                            errMsg += "\n" + line.Replace("<error>", "").Replace("\\n", "\n");
                        }
                    }

                    ExportErrorBar.IsOpen = showError;
                    ExportErrorBar.Message = errMsg.Trim();

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
                savePicker.FileTypeChoices.Add("Windows Experience Index 导出 文件", new List<string> { ".wst"});
                Debug.WriteLine(result.Path);
            }
        }
    }
}
