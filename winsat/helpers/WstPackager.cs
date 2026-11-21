// ============================================================================
// 本文件是 ConsoleApp1\WstPackager.cs 的副本，由仓库根目录的 sync_shared_sources.ps1 生成。
// winsat 是能单独拷走的独立程序，所以不复用 ConsoleApp1 的工程，只复用这份代码。
// 改逻辑请改 ConsoleApp1\WstPackager.cs，然后重跑 .\sync_shared_sources.ps1 —— 别直接改这里。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace winsat.helpers;

/// <summary>
/// export_wst 的 C# 实现（原来写在 Python 里，现在内化到这一层）。
///
/// 行为与原 Python 版保持一致：
///   * 往 outputPath 指向的 .wst（zip）包里追加；包里已有的同名条目不再重复添加
///     （同名条目会让读取端不知道用哪一个）；
///   * 单个文件：包内路径就是它的文件名；
///     文件夹：递归添加，包内路径为「文件夹名/相对路径」（统一用 / 分隔）；
///   * 源文件/文件夹不存在，或添加途中被删除 → 记一条 &lt;warning&gt;，继续处理其余项；
///   * 其它错误（权限不足、不是合法 zip 等）→ 记一条 &lt;error&gt;。
///
/// 返回值：空字符串 = 一切正常；否则是若干行 &lt;warning&gt; / &lt;error&gt;，
/// 多条用 \n 连接，和 Python 侧 export_html 的返回约定完全一致。
///
/// 文案：暂不读取符号文件的 $locale，统一用英文
/// （对应 xml_parser.DEFAULT_TEXTS 里的 warning_file_missing / error_wst_failed）。
/// </summary>
public static class WstPackager
{
    private const string MissingFileFormat = "file '{0}' does not exist, skipped";
    private const string FailedFormat = "failed to write wst package '{0}': {1}";

    /// <summary>把 paths 里的文件/文件夹追加进 outputPath 指向的 wst 包。</summary>
    public static string Export(string outputPath, IEnumerable<string> paths)
    {
        var problems = new List<string>();

        try
        {
            using ZipArchive archive = OpenArchive(outputPath, out IReadOnlyCollection<string> existing);

            // 包里已有的同名项不再重复添加
            // （注意：Create 模式下 archive.Entries 是不可访问的，所以由 OpenArchive 给出）
            var added = new HashSet<string>(existing, StringComparer.Ordinal);

            foreach (string path in paths)
            {
                if (!Path.Exists(path))
                {
                    problems.Add(Warning(string.Format(MissingFileFormat, path)));
                    continue;
                }

                if (Directory.Exists(path))
                {
                    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        Add(archive, file, ArcName(file, path), added, problems);
                    }
                }
                else
                {
                    Add(archive, path, ArcName(path, null), added, problems);
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            problems.Add(Error(string.Format(FailedFormat, outputPath, ex.Message)));
        }

        return string.Join('\n', problems);
    }

    // ------------------------------------------------------------------
    // 私有辅助
    // ------------------------------------------------------------------

    /// <summary>包内路径：单个文件用文件名；文件夹里的文件用「文件夹名/相对路径」。</summary>
    private static string ArcName(string path, string? root)
    {
        if (root is null)
        {
            return Path.GetFileName(path);
        }

        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) + "/" + relative;
    }

    private static void Add(ZipArchive archive, string source, string arcName,
                            HashSet<string> added, List<string> problems)
    {
        if (!added.Add(arcName))
        {
            return; // 已存在的同名项（含包里原有的）跳过
        }

        try
        {
            ZipFileExtensions.CreateEntryFromFile(archive, source, arcName, CompressionLevel.Optimal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 添加途中被删除（或路径不可达）：只发警告，不中断其余项
            added.Remove(arcName);
            problems.Add(Warning(string.Format(MissingFileFormat, source)));
        }
    }

    /// <summary>
    /// 打开（或新建）wst 包，并把包里原有的条目名带出来：
    ///   * 文件不存在 / 是 0 字节（保存对话框会先创建出空文件）→ Create；
    ///   * 已有内容 → 先按 Update 打开，不是合法 zip 时退化成覆盖重建。
    /// Create 模式下 archive.Entries 会抛 InvalidOperationException，
    /// 所以原有条目名必须在这里取好。
    /// </summary>
    private static ZipArchive OpenArchive(string outputPath, out IReadOnlyCollection<string> existing)
    {
        existing = Array.Empty<string>();

        string full = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(full) && new FileInfo(full).Length > 0)
        {
            try
            {
                var stream = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    var archive = new ZipArchive(stream, ZipArchiveMode.Update);
                    existing = archive.Entries.Select(entry => entry.FullName).ToArray();
                    return archive;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (InvalidDataException)
            {
                // 不是合法的 zip：当空包重写
            }
        }

        return new ZipArchive(
            new FileStream(full, FileMode.Create, FileAccess.ReadWrite, FileShare.None),
            ZipArchiveMode.Create);
    }

    private static bool IsExpected(Exception ex) => ex
        is IOException or UnauthorizedAccessException or InvalidDataException
        or NotSupportedException or ArgumentException;

    private static string Warning(string text) => "<warning>" + Escape(text);

    private static string Error(string text) => "<error>" + Escape(text);

    /// <summary>换行转义成字面量 \n，保证「一条输出 = 一行」。</summary>
    private static string Escape(string text) => text.Replace("\r\n", "\\n").Replace("\n", "\\n");
}
