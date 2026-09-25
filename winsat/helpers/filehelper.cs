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
        public static string scoresDir = Path.Combine(installDir, "Scores"); // 导入进来的跑分

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
        /// 是否为 Scores 目录（导入进来的跑分）里的文件。
        /// </summary>
        public static bool IsInScoresDir(string path) => IsInDirectory(path, scoresDir);

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

                if (Directory.Exists(scoresDir))
                {
                    files.AddRange(Directory.GetFiles(scoresDir).Where(IsAnalyzableFile));
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

