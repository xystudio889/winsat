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
    }
}

