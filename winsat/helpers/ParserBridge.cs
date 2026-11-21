using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

// 桥接类型（PythonBridge / Score / RuntimeCheck / WstPackager）就在本命名空间：
// 它们是 ConsoleApp1 那三个文件的独立副本，由仓库根目录的 sync_shared_sources.ps1 生成。
namespace winsat.helpers
{
    /// <summary>
    /// 解析器的路径约定：运行组件统一收在 <c>&lt;安装目录&gt;\parser\</c> 下。
    ///
    /// <code>
    /// parser\
    ///  ├─ parser.dll          Cython 桥（DllImport 目标）
    ///  ├─ xml_parser.pyd      解析核心（机器码，不含源码）
    ///  ├─ templates\          export_html 用的 HTML 模板
    ///  ├─ Symbols\            符号文件 en-US.json / zh-Hans-CN.json
    ///  └─ runtime\            CPython embeddable + lxml/bs4
    ///     ├─ python314.dll / python314.zip / python314._pth
    ///     └─ Lib\site-packages\
    /// </code>
    ///
    /// 由 <c>stage_parser.ps1</c>（仓库根目录）组装；打包进 MSIX 靠 csproj 里的
    /// <c>Content Include="parser\**"</c>。
    /// </summary>
    internal static class ParserPaths
    {
        private static readonly string installDir = AppContext.BaseDirectory;
        private static readonly string parserDir = Path.Combine(installDir, "parser");

        /// <summary>parser.dll / xml_parser.pyd 所在目录（boot 后会被插进 sys.path）。</summary>
        public static string ParserDir => parserDir;

        /// <summary>python314.dll / 标准库 / site-packages 所在目录。</summary>
        public static string RuntimeDir => Path.Combine(parserDir, "runtime");

        /// <summary>HTML 模板目录（xml_parser 按「模块旁边的 templates」找）。</summary>
        public static string TemplatesDir => Path.Combine(parserDir, "templates");

        /// <summary>
        /// 符号文件目录。新版在 parser\Symbols；旧版放在安装根目录，
        /// 这里做一次兜底，免得升级时整个「友好视图」直接不可用。
        /// </summary>
        public static string SymbolsDir
        {
            get
            {
                string nested = Path.Combine(parserDir, "Symbols");
                return Directory.Exists(nested) ? nested : Path.Combine(installDir, "Symbols");
            }
        }

        /// <summary>某个语言对应的符号文件完整路径。</summary>
        public static string SymbolFile(string language) => Path.Combine(SymbolsDir, language + ".json");
    }

    /// <summary>解析器不可用（缺文件、位数不匹配、boot 失败）时抛出，消息可以直接给用户看。</summary>
    internal sealed class ParserException : Exception
    {
        public ParserException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// winsat 对解析器的唯一入口：进程内调用 parser.dll，不再起 python 子进程。
    ///
    /// 和原来 <c>Commands.Execute("python", "xmlParser.py ...")</c> 的区别：
    ///   * 客户机不需要装 Python（运行时跟着 MSIX 包走）；
    ///   * 没有进程启动开销，也不用从 stdout 捞文本；
    ///   * 协议不变 —— 返回的还是一行一条的文本（&lt;line&gt; / &lt;subline&gt; /
    ///     &lt;error&gt; / &lt;warning&gt; / 键,值），界面代码不用动。
    ///
    /// 线程：Python 侧是单线程串行的，所以这里统一用 <see cref="Task.Run(Action)"/>
    /// 把它挪到线程池，别在 UI 线程上等（890 行的转储大约几十毫秒）。
    /// </summary>
    internal static class ParserBridge
    {
        private static readonly object sync = new();

        private static PythonBridge? bridge;
        private static string? failure;

        /// <summary>解释器是否已经起来。</summary>
        public static bool IsReady
        {
            get { lock (sync) { return bridge is not null; } }
        }

        /// <summary>上一次启动失败的原因（成功后清空）。</summary>
        public static string? LastFailure
        {
            get { lock (sync) { return failure; } }
        }

        /// <summary>
        /// 启动解释器（幂等）。失败时把「缺什么 / 该放哪」的清单写进 <paramref name="message"/>，
        /// 不抛异常 —— 界面要能把它显示成一条 InfoBar，而不是闪退。
        /// </summary>
        public static bool TryStart(out string message)
        {
            lock (sync)
            {
                if (bridge is not null)
                {
                    message = string.Empty;
                    return true;
                }

                try
                {
                    // 组件收在 parser\ 下：桥和核心在 parser\，CPython 在 parser\runtime\。
                    PythonBridge.ParserDirectory = ParserPaths.ParserDir;
                    PythonBridge.RuntimeDirectory = ParserPaths.RuntimeDir;
                    PythonBridge.Verbose = false;   // 没有控制台，别打缓冲区协商日志

                    bridge = PythonBridge.Boot();
                    failure = null;
                    message = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    bridge = null;
                    failure = ex.Message;
                    message = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>解析一份跑分 XML，返回协议行文本。</summary>
        public static Task<string> ParseXmlAsync(string source, string symbolFile) =>
            Task.Run(() => GetBridge().ParseXml(source, symbolFile));

        /// <summary>
        /// 导出 HTML。<paramref name="symbolsDir"/> 是符号**目录**，score 为 null 时不导出分数表。
        /// 返回空字符串 = 成功，否则是 &lt;warning&gt; / &lt;error&gt; 行。
        /// </summary>
        public static Task<string> ExportHtmlAsync(string source, string symbolsDir, string outputPath,
                                                   Score? score = null) =>
            Task.Run(() => GetBridge().ExportHtml(source, symbolsDir, outputPath, score));

        /// <summary>
        /// 打包 .wst（原来的 <c>--wst</c>）。这一步和 Python 无关：C# 直接用
        /// System.IO.Compression 往 zip 里追加，所以不需要解释器，也就永远不依赖运行时。
        /// </summary>
        public static string ExportWst(string outputPath, IEnumerable<string> files) =>
            WstPackager.Export(outputPath, files);

        /// <summary>
        /// 现场诊断文本：路径、桥版本、sys.prefix / lxml 来源。
        /// 客户机报「分析不了」时让他把这段贴回来。
        /// </summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("安装目录：" + AppContext.BaseDirectory);
            sb.AppendLine("解析器  ：" + ParserPaths.ParserDir);
            sb.AppendLine("运行时  ：" + ParserPaths.RuntimeDir);
            sb.AppendLine("符号目录：" + ParserPaths.SymbolsDir);
            sb.AppendLine("模板目录：" + ParserPaths.TemplatesDir);
            sb.AppendLine();

            if (!TryStart(out string message))
            {
                sb.AppendLine("启动失败：");
                sb.AppendLine(message);
                return sb.ToString();
            }

            try
            {
                sb.AppendLine("桥版本：" + PythonBridge.Version);
                sb.AppendLine();
                sb.AppendLine(GetBridge().RuntimeInfo());
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取运行环境失败：" + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>退出时释放解释器（不调也不会崩，只是 Py_Finalize 不跑）。</summary>
        public static void Shutdown()
        {
            lock (sync)
            {
                bridge?.Dispose();
                bridge = null;
            }
        }

        private static PythonBridge GetBridge()
        {
            if (TryStart(out string message))
            {
                return bridge!;
            }

            throw new ParserException(message);
        }
    }
}
