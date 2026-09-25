using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using winsat.helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat.pages
{
    /// <summary>
    /// 导入向导页：把 wst 包解压到「安装目录\Scores」。
    /// </summary>
    public sealed partial class ImportChoose : Page
    {
        public ImportChoose()
        {
            InitializeComponent();

            ToolTipService.SetToolTip(BrowseButton, Loader.GetString("BrowseFileTip"));
        }

        /// <summary>选择一个 wst 包，填进输入框。</summary>
        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var window = App.MainWindow!;
            var picker = new FileOpenPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".wst");

            var result = await picker.PickSingleFileAsync();

            if (result != null)
            {
                ImportPathBox.Text = result.Path;
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            HideResult();

            string packagePath = ImportPathBox.Text.Trim();

            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
            {
                ShowError(Loader.GetString("ImportFileNotExist"));
                return;
            }

            ImportButton.IsEnabled = false;
            ImportWorkingBar.Visibility = Visibility.Visible;
            ImportWorkingRing.IsActive = true;

            try
            {
                ImportResult result = await Task.Run(() => ImportPackage(packagePath));

                switch (result.Status)
                {
                    case ImportStatus.FileNotExist:
                        ShowError(Loader.GetString("ImportFileNotExist"));
                        return;
                    case ImportStatus.Corrupted:
                        // 不是 zip / 中心目录、条目数据损坏
                        ShowError(Loader.GetString("ImportFileCorrupted"));
                        return;
                    case ImportStatus.PermissionDenied:
                        ShowError(Loader.GetString("ImportPermissionDenied"));
                        return;
                }

                if (result.Added.Count == 0 && result.Skipped.Count == 0)
                {
                    ShowError(Loader.GetString("ImportNoFile"));
                    return;
                }

                if (result.Skipped.Count > 0)
                {
                    // 与已有跑分完全相同（哈希一致）的文件不重复添加
                    string reason = Loader.GetString("ImportDuplicateReason");

                    ImportWarningBar.Message = Loader.GetString("ImportDuplicateSkipped") + "\n" +
                        string.Join("\n", result.Skipped.Select(s => string.Format(reason, s.FileName, s.SameAs)));
                    ImportWarningBar.IsOpen = true;
                }

                // 成功只提示“导入成功”，不带路径，也没有打开查看功能
                ImportSuccessBar.IsOpen = true;
                ShowEnd();
            }
            catch (Exception ex)
            {
                // 其它（非预期）异常：直接把详细信息给用户
                ShowError(ex.Message);
            }
            finally
            {
                ImportButton.IsEnabled = true;
                ImportWorkingBar.Visibility = Visibility.Collapsed;
                ImportWorkingRing.IsActive = false;
            }
        }

        /// <summary>显示错误 InfoBar 并进入结束区（简单报错用友好文案，其它用详细信息）。</summary>
        private void ShowError(string message)
        {
            ImportErrorBar.Message = message;
            ImportErrorBar.IsOpen = true;
            ShowEnd();
        }

        /// <summary>隐藏主内容，只显示结果（与导出流程一致）。</summary>
        private void ShowEnd()
        {
            SelectImport.Visibility = Visibility.Collapsed;
            ImportEnd.Visibility = Visibility.Visible;
        }

        private void HideResult()
        {
            ImportSuccessBar.IsOpen = false;
            ImportWarningBar.IsOpen = false;
            ImportErrorBar.IsOpen = false;
        }

        /// <summary>导入结果状态（可预期的失败都在后台线程内转成状态返回）。</summary>
        private enum ImportStatus
        {
            Ok,
            FileNotExist,
            Corrupted,
            PermissionDenied,
        }

        private sealed class ImportResult
        {
            public ImportStatus Status { get; set; } = ImportStatus.Ok;
            public List<string> Added { get; } = new();          // 实际写入的文件名
            public List<SkippedEntry> Skipped { get; } = new();  // 因内容完全相同而跳过的项
        }

        /// <summary>被跳过的项：文件名 + 与之内容相同的已有文件名。</summary>
        private sealed class SkippedEntry
        {
            public SkippedEntry(string fileName, string sameAs)
            {
                FileName = fileName;
                SameAs = sameAs;
            }

            public string FileName { get; }
            public string SameAs { get; }
        }

        /// <summary>
        /// 把 wst 包解压到 Scores 目录。可预期的失败（文件不在、权限不足、包损坏）
        /// 在这里就地捕获成状态返回——异常若抛出到工作线程外面，调试器会在抛出点中断，看起来像崩溃。
        /// </summary>
        private static ImportResult ImportPackage(string packagePath)
        {
            try
            {
                return ExtractPackage(packagePath);
            }
            catch (InvalidDataException)
            {
                // 不是 zip（End of Central Directory 找不到）/ 条目数据损坏
                return new ImportResult { Status = ImportStatus.Corrupted };
            }
            catch (UnauthorizedAccessException)
            {
                return new ImportResult { Status = ImportStatus.PermissionDenied };
            }
            catch (DirectoryNotFoundException)
            {
                return new ImportResult { Status = ImportStatus.PermissionDenied };
            }
            catch (FileNotFoundException)
            {
                return new ImportResult { Status = ImportStatus.FileNotExist };
            }
        }

        /// <summary>
        /// 真正做解压的流程：
        /// - 重名时补 " (数字)"，并优先填补缺失的序号（已有 (1) (3) (5) 时先补 (2)，再补 (4)）
        /// - 与 Scores 目录或系统 DataStore 里已有文件哈希相同的条目不重复添加（记进 Skipped）
        /// </summary>
        private static ImportResult ExtractPackage(string packagePath)
        {
            Directory.CreateDirectory(FileHelper.scoresDir);

            // 已有文件的哈希表：哈希 -> 文件名（Scores 里的优先，其次系统 DataStore）
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string existing in Directory.GetFiles(FileHelper.scoresDir))
            {
                hashes[HashOfFile(existing)] = Path.GetFileName(existing);
            }

            AddSystemHashes(hashes);

            var result = new ImportResult();

            using ZipArchive archive = ZipFile.OpenRead(packagePath);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // 目录项（以 / 结尾）没有内容，跳过
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                string hash = HashOfEntry(entry);

                if (hashes.TryGetValue(hash, out string sameAs))
                {
                    result.Skipped.Add(new SkippedEntry(Path.GetFileName(entry.FullName), sameAs));
                    continue;
                }

                string targetPath = GetUniquePath(FileHelper.scoresDir, Path.GetFileName(entry.FullName));

                entry.ExtractToFile(targetPath, true);

                result.Added.Add(Path.GetFileName(targetPath));
                hashes[hash] = Path.GetFileName(targetPath);
            }

            return result;
        }

        /// <summary>
        /// 把系统 DataStore 里已有跑分的哈希也加进表里（读不到就只按 Scores 去重，不影响导入）。
        /// </summary>
        private static void AddSystemHashes(Dictionary<string, string> hashes)
        {
            try
            {
                if (!Directory.Exists(FileHelper.winSatFilePath))
                {
                    return;
                }

                foreach (string systemFile in Directory.GetFiles(FileHelper.winSatFilePath).Where(FileHelper.IsAnalyzableFile))
                {
                    hashes.TryAdd(HashOfFile(systemFile), Path.GetFileName(systemFile));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // 系统目录读不到/文件被占用：忽略，只按 Scores 里的文件去重
            }
        }

        /// <summary>
        /// 目标文件路径：重名时在扩展名前补 " (数字)"，从 1 开始取第一个没被占用的序号，
        /// 因此缺号会被优先补上（(1) (3) (5) 存在时先补 (2)，再补 (4)）。
        /// </summary>
        private static string GetUniquePath(string directory, string fileName)
        {
            string targetPath = Path.Combine(directory, fileName);
            if (!File.Exists(targetPath))
            {
                return targetPath;
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);

            for (int index = 1; ; index++)
            {
                targetPath = Path.Combine(directory, $"{baseName} ({index}){extension}");
                if (!File.Exists(targetPath))
                {
                    return targetPath;
                }
            }
        }

        private static string HashOfFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using var sha = SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static string HashOfEntry(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            using var sha = SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }
}
