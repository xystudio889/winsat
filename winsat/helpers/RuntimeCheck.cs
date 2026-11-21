// ============================================================================
// 本文件是 ConsoleApp1\RuntimeCheck.cs 的副本，由仓库根目录的 sync_shared_sources.ps1 生成。
// winsat 是能单独拷走的独立程序，所以不复用 ConsoleApp1 的工程，只复用这份代码。
// 改逻辑请改 ConsoleApp1\RuntimeCheck.cs，然后重跑 .\sync_shared_sources.ps1 —— 别直接改这里。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace winsat.helpers;

/// <summary>
/// 部署自检：程序目录里到底缺什么。
///
/// 为什么需要它 —— 客户机器上没装 Python 时，.NET 抛的是
/// <c>DllNotFoundException: 无法加载 DLL "parser.dll"</c>；
/// 这句话既没说缺 python314.dll，也没说该往哪放，压根没法照着修。
/// 这里在 P/Invoke 之前先把清单列出来。
///
/// 分两档：
///   * 必需 —— 我们自己产出的文件，缺一个就启动不了；
///   * 非必需 —— CPython 本体 / 标准库 / lxml：开发机可以靠 PATH 和本机安装，
///     但发布给客户必须打进程序目录（用 bundle_runtime.ps1）。
/// </summary>
internal static class RuntimeCheck
{
    /// <summary>一条检查项。</summary>
    public sealed record Item(string Path, string Why, bool Present, bool Required);

    /// <summary>
    /// 程序目录里所有相关项的现状。
    ///
    /// 运行时（python3XX.dll / 标准库 / lxml）默认跟 <paramref name="directory"/> 同层；
    /// 收在子目录里的布局（如 <c>&lt;程序目录&gt;\parser\runtime</c>）会自动往下找一层，
    /// 也可以显式传 <paramref name="runtimeDirectory"/>。
    /// </summary>
    public static IReadOnlyList<Item> Inspect(string directory, string? runtimeDirectory = null)
    {
        string runtime = ResolveRuntimeDirectory(directory, runtimeDirectory);
        string[] pythonDlls = FindPythonDlls(runtime);
        bool hasStdlib = pythonDlls.Any(dll => File.Exists(Path.Combine(runtime, StdlibZipName(dll))))
                         || File.Exists(Path.Combine(runtime, "Lib", "os.py"));
        bool hasLxml = Directory.Exists(Path.Combine(runtime, "Lib", "site-packages", "lxml"));
        string? core = FindCore(directory);

        return
        [
            Check(directory, "parser.dll", "Cython 编译出的原生桥（python\\build_bridge.py 产出）", required: true),
            new Item(
                Path.Combine(directory, core ?? "xml_parser.pyd"),
                (core ?? string.Empty) switch
                {
                    "xml_parser.pyd" => "Python 解析核心（已编译成原生模块，不含源码）",
                    "xml_parser.py" => "Python 解析核心（源码形态：开发期正常，发布前应编成 .pyd）",
                    _ => "Python 解析核心（python\\build_bridge.py 的产物）",
                },
                core is not null,
                Required: true),
            Check(directory, Path.Combine("templates", "index.html"), "HTML 模板目录", required: true),

            new Item(
                Path.Combine(runtime, pythonDlls.Length > 0 ? Path.GetFileName(pythonDlls[0]) : "python314.dll"),
                "CPython 运行时本体（客户机没装 Python 时必须随程序发布）",
                pythonDlls.Length > 0,
                Required: false),

            new Item(
                Path.Combine(runtime, "python314.zip"),
                "Python 标准库（embeddable 包是 .zip，完整安装是 Lib\\）",
                hasStdlib,
                Required: false),

            new Item(
                Path.Combine(runtime, "Lib", "site-packages", "lxml"),
                "lxml / beautifulsoup4（也可来自机器上已装的 Python）",
                hasLxml,
                Required: false),
        ];
    }

    /// <summary>必需项里缺了哪些（相对路径）。</summary>
    public static IReadOnlyList<string> MissingFiles(string directory, string? runtimeDirectory = null) =>
        Inspect(directory, runtimeDirectory)
            .Where(item => item.Required && !item.Present)
            .Select(item => Path.GetRelativePath(directory, item.Path))
            .ToList();

    /// <summary>人能照着修的诊断文本。</summary>
    public static string Describe(string directory, string? runtimeDirectory = null)
    {
        IReadOnlyList<Item> items = Inspect(directory, runtimeDirectory);
        var sb = new StringBuilder();

        sb.AppendLine($"  程序目录：{directory}");
        sb.AppendLine("  文件清单（✘ = 缺了就无法启动）：");

        foreach (Item item in items)
        {
            string mark = item.Present ? "√" : item.Required ? "✘" : "·";
            string name = Path.GetRelativePath(directory, item.Path);
            sb.AppendLine($"    {mark} {name,-30} {item.Why}");
        }

        if (items.Any(item => item.Required && !item.Present))
        {
            sb.AppendLine();
            sb.AppendLine("  修法：源码目录执行 .\\build.ps1 —— 会重编 parser.dll / xml_parser.pyd，");
            sb.AppendLine("        并把 parser.dll / xml_parser.pyd / templates\\ 复制到输出目录。");
        }

        bool hasRuntime = FindPythonDlls(ResolveRuntimeDirectory(directory, runtimeDirectory)).Length > 0;
        if (!hasRuntime)
        {
            sb.AppendLine();
            sb.AppendLine("  没有找到 CPython 运行时（python3XX.dll）：");
            sb.AppendLine("    · 开发机上只要 PATH 里有 Python 安装目录（含 python314.dll）就能跑；");
            sb.AppendLine("    · 客户机没装 Python 时，必须把运行时和程序放在一起 ——");
            sb.AppendLine("      用本仓库的部署脚本产出（bundle_runtime.ps1 / stage_parser.ps1），");
            sb.AppendLine("      然后把整个输出目录一起分发，别只发 exe。");
        }

        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------

    private static Item Check(string directory, string relativePath, string why, bool required)
    {
        string full = Path.Combine(directory, relativePath);
        return new Item(full, why, File.Exists(full), required);
    }

    /// <summary>
    /// 运行时到底在哪：显式指定 &gt; 同层 &gt; 往下一层的 runtime\。
    /// 两种布局都支持（平铺的发布目录 / 收进子目录的 WinUI 应用）。
    /// </summary>
    private static string ResolveRuntimeDirectory(string directory, string? runtimeDirectory)
    {
        if (!string.IsNullOrEmpty(runtimeDirectory))
        {
            return runtimeDirectory;
        }

        if (FindPythonDlls(directory).Length > 0)
        {
            return directory;
        }

        string nested = Path.Combine(directory, "runtime");
        return FindPythonDlls(nested).Length > 0 ? nested : directory;
    }

    /// <summary>
    /// 解析核心的两种形态：编译产物优先（发布形态，源码不外露），
    /// 源码其次（开发形态，用 build_bridge.py --core=copy 生成）。
    /// </summary>
    private static string? FindCore(string directory)
    {
        if (File.Exists(Path.Combine(directory, "xml_parser.pyd")))
        {
            return "xml_parser.pyd";
        }

        return File.Exists(Path.Combine(directory, "xml_parser.py")) ? "xml_parser.py" : null;
    }

    /// <summary>
    /// 目录里的 python3XX.dll：排除 python3.dll（ABI3 转发层）
    /// 和 python314t.dll（free-threaded 版，与我们的 pyd 不兼容）。
    /// internal：NativeBridge 也要用它来预加载运行时。
    /// </summary>
    internal static string[] FindPythonDlls(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "python3*.dll", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    string version = name["python".Length..];   // "314" / "3" / "314t"
                    return version.Length >= 3 && version.All(char.IsAsciiDigit);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string StdlibZipName(string pythonDll) =>
        Path.GetFileNameWithoutExtension(pythonDll) + ".zip";
}
