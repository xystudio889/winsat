using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using winsat.helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
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
            RefreshedFile.Visibility = Visibility.Visible;
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
        }

        private void WSTExportShow(object sender, RoutedEventArgs e)
        {
            SelectExport.Visibility = Visibility.Collapsed;
            WSTExport.Visibility = Visibility.Visible;
        }
    }
}
