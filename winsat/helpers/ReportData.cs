using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace winsat.helpers
{
    /// <summary>打印报告里“可视化”部分的一行类型（对应解析器的输出协议）。</summary>
    internal enum ReportItemKind
    {
        /// <summary>大标题行（&lt;line&gt;）。</summary>
        LargeLine,

        /// <summary>小标题行（&lt;subline&gt;）。</summary>
        SubLine,

        /// <summary>空隙（&lt;spacing&gt;）。</summary>
        Spacing,

        /// <summary>键值对行（“键,值”）。</summary>
        Pair,
    }

    /// <summary>可视化部分的一行。</summary>
    internal sealed class ReportItem
    {
        public ReportItemKind Kind { get; init; }

        public string Text { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }

    /// <summary>分数表的一行。</summary>
    internal sealed class ScoreRow
    {
        public ScoreRow(string component, string item, string score)
        {
            Component = component;
            Item = item;
            Score = score;
        }

        /// <summary>组件名（处理器、内存……）。</summary>
        public string Component { get; }

        /// <summary>评分的项目。</summary>
        public string Item { get; }

        /// <summary>子分数。</summary>
        public string Score { get; }
    }

    /// <summary>
    /// 一份打印用的报告数据：只保留文本，与界面无关。
    /// 文案在<b>创建时</b>按当时的界面语言解析，因此打印语言就是用户打印时使用的语言。
    /// </summary>
    internal sealed class ReportData
    {
        public string Title { get; init; } = string.Empty;

        public string Subtitle { get; init; } = string.Empty;

        public string TotalScore { get; init; } = string.Empty;

        public IReadOnlyList<ScoreRow> Scores { get; init; } = Array.Empty<ScoreRow>();

        public IReadOnlyList<ReportItem> Visualization { get; init; } = Array.Empty<ReportItem>();

        public IReadOnlyList<string> CodeLines { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 收集打印所需的全部内容：分数表、分析后的可视化、源码。
        /// </summary>
        public static async Task<ReportData> CreateAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(Loader.GetString("FileNotExist"), filePath);
            }

            List<string> scores = await ScoreHelper.GetScore();
            List<ReportItem> visualization = await LoadVisualizationAsync(filePath);

            string xml = await File.ReadAllTextAsync(filePath);
            List<string> codeLines = await Task.Run(() => SplitCodeLines(FormatXml(xml)));

            return new ReportData
            {
                Title = Loader.GetString("PrintReportTitle"),
                Subtitle = string.Format(
                    Loader.GetString("PrintReportSubtitle"),
                    Path.GetFileName(filePath),
                    DateTime.Now.ToString("G", CultureInfo.CurrentCulture)),
                TotalScore = scores[7],
                // 与主页表格保持一致：处理器、内存、图形、游戏图形、主硬盘
                Scores = new[]
                {
                    new ScoreRow(Loader.GetString("cs_Processor"), Loader.GetString("cs_CalcPerSecond"), scores[0]),
                    new ScoreRow(Loader.GetString("cs_MemoryRAM"), Loader.GetString("cs_MemoryOps"), scores[4]),
                    new ScoreRow(Loader.GetString("cs_Graphics"), Loader.GetString("cs_DesktopGraphics"), scores[3]),
                    new ScoreRow(Loader.GetString("cs_GamingGraphics"), Loader.GetString("cs_GamingGraphicsDesc"), scores[1]),
                    new ScoreRow(Loader.GetString("cs_PrimaryDisk"), Loader.GetString("cs_DiskTransferRate"), scores[2]),
                },
                Visualization = visualization,
                CodeLines = codeLines,
            };
        }

        /// <summary>
        /// 用当前语言的符号文件跑一次解析器，把 stdout 转成可视化行。
        /// 解析器报 &lt;error&gt; 时抛出，由调用方提示用户。
        /// </summary>
        private static async Task<List<ReportItem>> LoadVisualizationAsync(string filePath)
        {
            // 每次打印都重新取一次语言：打印语言跟随用户打印时的界面语言
            string symbolPath = Path.Combine(
                FileHelper.installDir, "Symbols", $"{AppLanguage.SymbolLanguage}.json");

            if (!File.Exists(symbolPath))
            {
                throw new FileNotFoundException(Loader.GetString("ParserMissing"), symbolPath);
            }

#if DEBUG
            string parserPath = Path.Combine(FileHelper.installDir, "Commands", "xmlParser.py");
            if (!File.Exists(parserPath))
            {
                throw new FileNotFoundException(Loader.GetString("ParserMissing"), parserPath);
            }

            string output = await Commands.Execute(
                "python.exe", $"\"{parserPath}\" \"{filePath}\" \"{symbolPath}\"");
#else
            string parserPath = Path.Combine(FileHelper.installDir, "Commands", "xmlParser.exe");
            if (!File.Exists(parserPath))
            {
                throw new FileNotFoundException(Loader.GetString("ParserMissing"), parserPath);
            }

            string output = await Commands.Execute(
                $"\"{parserPath}\"", $"\"{filePath}\" \"{symbolPath}\"");
#endif

            if (output.StartsWith("Error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(output);
            }

            var items = new List<ReportItem>();
            var errors = new List<string>();

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("<error>", StringComparison.Ordinal))
                {
                    errors.Add(line["<error>".Length..].Replace("\\n", "\n"));
                    continue;
                }

                if (line.StartsWith("<warning>", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("<line>", StringComparison.Ordinal))
                {
                    items.Add(new ReportItem
                    {
                        Kind = ReportItemKind.LargeLine,
                        Text = line["<line>".Length..].Trim(),
                    });
                    continue;
                }

                if (line.StartsWith("<subline>", StringComparison.Ordinal))
                {
                    items.Add(new ReportItem
                    {
                        Kind = ReportItemKind.SubLine,
                        Text = line["<subline>".Length..].Trim(),
                    });
                    continue;
                }

                if (line == "<spacing>")
                {
                    items.Add(new ReportItem { Kind = ReportItemKind.Spacing });
                    continue;
                }

                int comma = line.IndexOf(',');
                if (comma > 0)
                {
                    items.Add(new ReportItem
                    {
                        Kind = ReportItemKind.Pair,
                        Text = line[..comma].Trim(),
                        Value = line[(comma + 1)..].Trim(),
                    });
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }

            return items;
        }

        /// <summary>按行切开源码；统一成 \n，避免 \r 在 TextBlock 里显示成方块。</summary>
        private static List<string> SplitCodeLines(string xml) =>
            xml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        /// <summary>
        /// 把 XML 排成 2 空格缩进（与界面上的源码视图一致）；格式化失败时原样返回。
        /// </summary>
        private static string FormatXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return xml;
            }

            try
            {
                var document = new XmlDocument { PreserveWhitespace = false };
                document.LoadXml(xml);

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    NewLineChars = "\n",
                };

                using var stringWriter = new Utf8StringWriter();
                using (XmlWriter writer = XmlWriter.Create(stringWriter, settings))
                {
                    document.Save(writer);
                }

                return stringWriter.ToString();
            }
            catch (Exception)
            {
                // 格式化失败（XML 无效、含非法字符等）不影响打印：直接用原始内容
                return xml;
            }
        }

        /// <summary>让 XML 声明里写 encoding="utf-8"，而不是 StringWriter 默认的 utf-16。</summary>
        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
