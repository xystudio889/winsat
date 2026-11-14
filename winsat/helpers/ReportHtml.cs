using System;
using System.Text;

namespace winsat.helpers
{
    /// <summary>
    /// 把 <see cref="ReportData"/> 生成为“精简版网页”：
    /// 排版按打印来（A4 宽度、标题小一点、表格、可视化、源码），
    /// 分页和打印交给浏览器（Ctrl+P）。
    /// </summary>
    internal static class ReportHtml
    {
        private const string Css = """
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                padding: 24px;
                background: #fff;
                color: #1a1a1a;
                font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif;
                font-size: 12px;
                line-height: 1.45;
            }
            .page { max-width: 794px; margin: 0 auto; }
            h1 { font-size: 20px; line-height: 1.3; font-weight: 600; margin: 0 0 4px; }
            .sub { color: #5a5a5a; font-size: 11px; margin: 0 0 12px; }
            .hint {
                color: #5a5a5a; font-size: 10.5px;
                border: 1px dashed #c8c8c8; border-radius: 4px;
                padding: 6px 8px; margin: 0 0 12px;
            }
            table { width: 100%; border-collapse: collapse; table-layout: fixed; margin: 0 0 14px; }
            col.c0 { width: 112px; }
            col.c2, col.c3 { width: 15%; }
            th {
                font-size: 10.5px; color: #5a5a5a; font-weight: 600;
                text-align: left; padding: 4px;
            }
            th.center { text-align: center; }
            thead th { border-top: 2px solid #8a8a8a; border-bottom: 1px solid #8a8a8a; }
            tbody td { padding: 4px; vertical-align: top; font-size: 12px; }
            tbody td.comp { font-weight: 600; }
            td.score { text-align: center; }
            td.score.lowest, td.total { background: #ececec; }
            td.total { text-align: center; vertical-align: middle; }
            td.total .num { font-size: 22px; font-weight: 700; line-height: 1.1; }
            td.total .tip { font-size: 9px; color: #5a5a5a; margin-top: 3px; }
            .line {
                font-weight: 700; font-size: 13.5px;
                margin: 10px 0 2px; padding-bottom: 2px;
                border-bottom: 2px solid #8a8a8a;
            }
            .subline { font-weight: 600; font-size: 12.5px; margin: 6px 0 2px 6px; }
            .spacer { height: 6px; }
            .pair { display: grid; grid-template-columns: 180px minmax(0, 1fr); gap: 12px; margin: 2px 0; }
            .key, .value { white-space: normal; overflow-wrap: anywhere; }
            pre.code {
                font-family: Consolas, "Cascadia Mono", "Courier New", monospace;
                font-size: 10.5px; line-height: 1.35;
                white-space: pre-wrap; overflow-wrap: anywhere;
                margin: 16px 0 0;
            }
            @media print {
                @page { size: A4; margin: 12mm; }
                body { padding: 0; font-size: 11px; }
                .page { max-width: none; }
                .hint { display: none; }
                .pair, .line, .subline, tr { break-inside: avoid; }
                pre.code { font-size: 9.5px; }
            }
            """;

        public static string Build(ReportData data)
        {
            string lang = AppLanguage.SymbolLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : "en-US";

            var builder = new StringBuilder();
            builder.Append("<!DOCTYPE html>\n<html lang=\"").Append(lang).Append("\">\n<head>\n");
            builder.Append("<meta charset=\"utf-8\">\n");
            builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            builder.Append("<title>").Append(Escape(data.Title)).Append("</title>\n");
            builder.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n<div class=\"page\">\n");

            builder.Append("<h1>").Append(Escape(data.Title)).Append("</h1>\n");
            builder.Append("<div class=\"sub\">").Append(Escape(data.Subtitle)).Append("</div>\n");
            builder.Append("<div class=\"hint\">")
                .Append(Escape(Loader.GetString("WebReportHint")))
                .Append("</div>\n");

            AppendTable(builder, data);
            AppendVisualization(builder, data);
            AppendCode(builder, data);

            builder.Append("</div>\n</body>\n</html>\n");
            return builder.ToString();
        }

        // ---- 分数表 ----

        private static void AppendTable(StringBuilder builder, ReportData data)
        {
            builder.Append("<table>\n<colgroup>")
                .Append("<col class=\"c0\"><col><col class=\"c2\"><col class=\"c3\">")
                .Append("</colgroup>\n<thead>\n<tr>");
            AppendHeader(builder, Loader.GetString("Component.Text"), false);
            AppendHeader(builder, Loader.GetString("ScoredItems.Text"), false);
            AppendHeader(builder, Loader.GetString("SubScore.Text"), true);
            AppendHeader(builder, Loader.GetString("BaseScore.Text"), true);
            builder.Append("</tr>\n</thead>\n<tbody>\n");

            double lowest = FindLowestScore(data);
            int rowCount = data.Scores.Count;

            for (int i = 0; i < rowCount; i++)
            {
                ScoreRow row = data.Scores[i];
                bool highlight = lowest > 0 && ParseScore(row.Score) == lowest;

                builder.Append("<tr>");
                builder.Append("<td class=\"comp\">").Append(Escape(row.Component)).Append("</td>");
                builder.Append("<td>").Append(Escape(row.Item)).Append("</td>");
                builder.Append("<td class=\"score")
                    .Append(highlight ? " lowest" : string.Empty)
                    .Append("\">").Append(Escape(row.Score)).Append("</td>");

                if (i == 0)
                {
                    builder.Append("<td class=\"total\" rowspan=\"").Append(rowCount).Append("\">");
                    builder.Append("<div class=\"num\">").Append(Escape(data.TotalScore)).Append("</div>");
                    builder.Append("<div class=\"tip\">")
                        .Append(Escape(Loader.GetString("subScoreTip.Text")))
                        .Append("</div>");
                    builder.Append("</td>");
                }

                builder.Append("</tr>\n");
            }

            builder.Append("</tbody>\n</table>\n");
        }

        private static void AppendHeader(StringBuilder builder, string text, bool center)
        {
            builder.Append("<th").Append(center ? " class=\"center\"" : string.Empty).Append('>')
                .Append(Escape(text)).Append("</th>");
        }

        private static double FindLowestScore(ReportData data)
        {
            if (data.Scores.Count == 0)
            {
                return -1;
            }

            double lowest = double.MaxValue;
            foreach (ScoreRow row in data.Scores)
            {
                double value = ParseScore(row.Score);
                if (value < 0)
                {
                    return -1;
                }

                lowest = Math.Min(lowest, value);
            }

            return lowest > 0 ? lowest : -1;
        }

        private static double ParseScore(string text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : -1;

        // ---- 可视化 ----

        private static void AppendVisualization(StringBuilder builder, ReportData data)
        {
            foreach (ReportItem item in data.Visualization)
            {
                switch (item.Kind)
                {
                    case ReportItemKind.LargeLine:
                        builder.Append("<div class=\"line\">").Append(Escape(item.Text)).Append("</div>\n");
                        break;

                    case ReportItemKind.SubLine:
                        builder.Append("<div class=\"subline\">").Append(Escape(item.Text)).Append("</div>\n");
                        break;

                    case ReportItemKind.Spacing:
                        builder.Append("<div class=\"spacer\"></div>\n");
                        break;

                    case ReportItemKind.Pair:
                        builder.Append("<div class=\"pair\">");
                        builder.Append("<div class=\"key\">").Append(Escape(item.Text)).Append("</div>");
                        builder.Append("<div class=\"value\">").Append(Escape(item.Value)).Append("</div>");
                        builder.Append("</div>\n");
                        break;
                }
            }
        }

        // ---- 源码 ----

        private static void AppendCode(StringBuilder builder, ReportData data)
        {
            if (data.CodeLines.Count == 0)
            {
                return;
            }

            builder.Append("<pre class=\"code\">");
            for (int i = 0; i < data.CodeLines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(Escape(data.CodeLines[i]));
            }

            builder.Append("</pre>\n");
        }

        private static string Escape(string text) =>
            text.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }
}
