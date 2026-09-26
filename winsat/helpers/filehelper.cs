using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace winsat.helpers
{

    internal class FileHelper
    {
        public static string winSatFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Performance", "WinSAT", "DataStore");
        public static string installDir = AppDomain.CurrentDomain.BaseDirectory; // 软件安装路径

        /// <summary>
        /// 导入跑分的存放候选（按优先级）：
        /// 1) 安装目录\Scores —— 便携/非打包安装，数据跟着程序走
        /// 2) 应用本地数据目录\Scores —— MSIX（打包）安装，安装目录只读，这里是唯一可写处
        /// 3) %LOCALAPPDATA%\winsat\Scores —— 非打包但安装目录不可写时的兜底
        /// </summary>
        private static readonly List<string> scoresDirs = BuildScoresDirs();

        private static string? writableScoresDir;

        /// <summary>写入导入跑分用的 Scores 目录：候选里第一个真正可写的。</summary>
        public static string scoresDir
        {
            get
            {
                writableScoresDir ??= scoresDirs.FirstOrDefault(IsWritableDirectory) ?? scoresDirs[^1];
                return writableScoresDir;
            }
        }

        private static List<string> BuildScoresDirs()
        {
            List<string> dirs = new() { Path.Combine(installDir, "Scores") };

            try
            {
                // 打包安装时可用：%LOCALAPPDATA%\Packages\<包族名>\LocalState\Scores
                dirs.Add(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Scores"));
            }
            catch (Exception)
            {
                // 非打包应用没有 ApplicationData，跳过这个候选
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                dirs.Add(Path.Combine(localAppData, "winsat", "Scores"));
            }

            return dirs
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 目录是否可写：先尝试创建，再真正写一个临时文件探一下
        /// （目录已存在但只读时 CreateDirectory 不会报错）。
        /// </summary>
        private static bool IsWritableDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);

                string probe = Path.Combine(directory, $".winsat-probe-{Guid.NewGuid():N}.tmp");
                using (File.Create(probe)) { }
                File.Delete(probe);

                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException
                                       || ex is NotSupportedException || ex is ArgumentException)
            {
                return false;
            }
        }

        public static List<String> GetFileTimeList(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("无法提取目录");

            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"目录不存在: {directory}");

            return Directory.GetFiles(directory)
                .Select(f => new FileInfo(f))
                .OrderBy(fi => fi.LastWriteTime)
                .Select(fi => fi.FullName)
                .ToList();
        }

        public static List<String> FlitFile(List<String> files)
        {
            return files
                .Where(s => s.IndexOf("Formal.Assessment", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        /// <summary>
        /// 路径是否位于指定目录内。
        /// </summary>
        private static bool IsInDirectory(string path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
                return false;

            string fullPath = Path.GetFullPath(path);
            string root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否为 Scores 目录（导入进来的跑分）里的文件（含所有候选位置）。
        /// </summary>
        public static bool IsInScoresDir(string path) => scoresDirs.Any(dir => IsInDirectory(path, dir));

        /// <summary>
        /// 能否被分析/展示的文件名：DataStore 下只有名字含 "Formal.Assessment" 的才是一份完整跑分；
        /// Scores 下的文件都是导入进来的跑分。
        /// </summary>
        public static bool IsAnalyzableFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            if (IsInScoresDir(path))
                return true;

            return Path.GetFileName(path).Contains("Formal.Assessment", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 可分析文件的完整路径列表（按修改时间升序）：
        /// DataStore 里的跑分 + Scores 目录下导入的跑分。
        /// 目录不存在或无权限时返回已收集到的部分，不抛异常。
        /// </summary>
        public static List<string> GetAnalyzableFiles()
        {
            List<string> files = new();

            try
            {
                if (Directory.Exists(winSatFilePath))
                {
                    files.AddRange(Directory.GetFiles(winSatFilePath).Where(IsAnalyzableFile));
                }

                // 所有候选 Scores 目录都扫，位置变更后旧数据也不会“消失”
                foreach (string dir in scoresDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        files.AddRange(Directory.GetFiles(dir).Where(IsAnalyzableFile));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 忽略读不到的目录，返回已收集到的文件
            }

            return files.OrderBy(f => File.GetLastWriteTime(f)).ToList();
        }
    }
}

