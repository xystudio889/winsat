using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace winsat.helpers
{
    /// <summary>
    /// 把 <see cref="ReportData"/> 排成 A4 的打印页：先按块生成视觉元素并量出高度，
    /// 再按整页高度切成分页。表格、可视化、源码都按纸张宽度换行。
    /// </summary>
    internal static class PrintReportLayout
    {
        /// <summary>A4 在 96 DPI 下的尺寸。</summary>
        public const double PageWidth = 793.7;

        public const double PageHeight = 1122.5;

        private const double PageMargin = 56;

        /// <summary>表格第一列的宽度（界面上是 128，打印时收窄到 112）。</summary>
        private const double TableFirstColumnWidth = 112;

        /// <summary>可视化“键”列宽度：界面上是 350，打印时左移到 180，值列因此有更多空间。</summary>
        private const double VisualizationKeyWidth = 180;

        private static readonly SolidColorBrush PageBackground = new SolidColorBrush(Colors.White);

        private static readonly SolidColorBrush SeparatorBrush = new(Windows.UI.Color.FromArgb(255, 138, 138, 138));

        private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 90, 90, 90));

        private static readonly SolidColorBrush HighlightBrush = new(Windows.UI.Color.FromArgb(255, 236, 236, 236));

        /// <summary>
        /// 生成全部打印页。<paramref name="host"/> 是用来量高度的面板（可以是界面上隐藏的 Canvas），
        /// 量完会把临时元素清掉。
        /// </summary>
        public static IReadOnlyList<UIElement> CreatePages(ReportData data, Panel host)
        {
            double contentWidth = PageWidth - PageMargin * 2;
            List<FrameworkElement> blocks = BuildBlocks(data, contentWidth);

            host.Children.Clear();

            var measurer = new StackPanel { Width = contentWidth };
            foreach (FrameworkElement block in blocks)
            {
                measurer.Children.Add(block);
            }

            host.Children.Add(measurer);
            measurer.Measure(new Size(contentWidth, double.PositiveInfinity));

            double availableHeight = PageHeight - PageMargin * 2;
            var pages = new List<UIElement>();

            var page = new StackPanel { Width = contentWidth };
            double usedHeight = 0;

            foreach (FrameworkElement block in blocks)
            {
                // 先在临时面板里量过，这里直接取高度；取完再挪到真正的页面上
                double blockHeight = block.DesiredSize.Height;

                if (usedHeight > 0 && usedHeight + blockHeight > availableHeight)
                {
                    pages.Add(CreatePage(page));
                    page = new StackPanel { Width = contentWidth };
                    usedHeight = 0;
                }

                measurer.Children.Remove(block);
                page.Children.Add(block);
                usedHeight += blockHeight;
            }

            if (page.Children.Count > 0 || pages.Count == 0)
            {
                pages.Add(CreatePage(page));
            }

            host.Children.Remove(measurer);
            return pages;
        }

        /// <summary>
        /// 给一页套上纸张尺寸和白底，打印与预览都按这个大小绘制。
        /// <para>
        /// <see cref="FrameworkElement.RequestedTheme"/> 必须设为浅色：WinUI 的打印管线渲染页面时
        /// 不会覆盖应用主题，深色主题下文字是白的、纸也是白的，预览就会看起来一片空白
        /// （WindowsAppSDK#6086）。
        /// </para>
        /// </summary>
        private static UIElement CreatePage(Panel content) => new Border
        {
            Width = PageWidth,
            Height = PageHeight,
            Padding = new Thickness(PageMargin),
            Background = PageBackground,
            RequestedTheme = ElementTheme.Light,
            Child = content,
        };

        private static List<FrameworkElement> BuildBlocks(ReportData data, double contentWidth)
        {
            var blocks = new List<FrameworkElement>();

            blocks.Add(CreateText(data.Title, 20, bold: true, contentWidth));
            blocks.Add(CreateText(data.Subtitle, 11, bold: false, contentWidth, MutedBrush));
            blocks.Add(new Border { Height = 10 });
            blocks.Add(CreateScoreTable(data, contentWidth));
            blocks.Add(new Border { Height = 10 });

            foreach (ReportItem item in data.Visualization)
            {
                blocks.Add(CreateVisualizationBlock(item, contentWidth));
            }

            AppendCodeBlocks(blocks, data, contentWidth);
            return blocks;
        }

        private static TextBlock CreateText(
            string text,
            double fontSize,
            bool bold,
            double width,
            Brush? foreground = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                Width = width,
            };

            if (bold)
            {
                block.FontWeight = FontWeights.SemiBold;
            }

            if (foreground is not null)
            {
                block.Foreground = foreground;
            }

            return block;
        }

        // ---- 分数表 ----

        private static FrameworkElement CreateScoreTable(ReportData data, double contentWidth)
        {
            double sideColumnWidth = contentWidth * 0.15;
            double secondColumnWidth = contentWidth - TableFirstColumnWidth - sideColumnWidth * 2;

            var grid = new Grid { Width = contentWidth };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableFirstColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(secondColumnWidth, 80)) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sideColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sideColumnWidth) });

            int rowCount = data.Scores.Count;

            for (int i = 0; i < rowCount + 3; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            // 表头
            AddCell(grid, Loader.GetString("Component.Text"), 0, 0);
            AddCell(grid, Loader.GetString("ScoredItems.Text"), 0, 1);
            AddCell(grid, Loader.GetString("SubScore.Text"), 0, 2, center: true);
            AddCell(grid, Loader.GetString("BaseScore.Text"), 0, 3, center: true);

            AddRowSeparator(grid, 1, 4);

            double lowest = FindLowestScore(data);

            for (int i = 0; i < rowCount; i++)
            {
                ScoreRow row = data.Scores[i];
                int gridRow = i + 2;

                AddCell(grid, row.Component, gridRow, 0, bold: true);
                AddCell(grid, row.Item, gridRow, 1);

                bool highlight = lowest > 0 && ParseScore(row.Score) == lowest;
                AddCell(grid, row.Score, gridRow, 2, center: true, highlight: highlight);

                if (i == 0)
                {
                    AddTotalCell(grid, data.TotalScore, gridRow, rowCount);
                }
            }

            AddRowSeparator(grid, rowCount + 2, 4);
            return grid;
        }

        private static void AddCell(
            Grid grid,
            string text,
            int row,
            int column,
            bool center = false,
            bool bold = false,
            bool highlight = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 4),
                HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };

            if (bold)
            {
                block.FontWeight = FontWeights.SemiBold;
            }

            FrameworkElement cell = block;

            if (highlight)
            {
                cell = new Border { Background = HighlightBrush, Child = block };
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        /// <summary>“基本分数”合并成一格：大号总分 + 小字说明。</summary>
        private static void AddTotalCell(Grid grid, string totalScore, int row, int rowSpan)
        {
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4),
            };

            content.Children.Add(new TextBlock
            {
                Text = totalScore,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            content.Children.Add(new TextBlock
            {
                Text = Loader.GetString("subScoreTip.Text"),
                FontSize = 9,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });

            Grid.SetRow(content, row);
            Grid.SetRowSpan(content, rowSpan);
            Grid.SetColumn(content, 3);
            grid.Children.Add(content);
        }

        private static void AddRowSeparator(Grid grid, int row, int columnSpan)
        {
            var line = new Border
            {
                Height = 1,
                Background = SeparatorBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, columnSpan);
            grid.Children.Add(line);
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

        private static FrameworkElement CreateVisualizationBlock(ReportItem item, double contentWidth)
        {
            switch (item.Kind)
            {
                case ReportItemKind.LargeLine:
                    return new Border
                    {
                        Width = contentWidth,
                        BorderThickness = new Thickness(0, 0, 0, 2),
                        BorderBrush = SeparatorBrush,
                        Margin = new Thickness(0, 10, 0, 2),
                        Child = new TextBlock
                        {
                            Text = item.Text,
                            FontSize = 13.5,
                            FontWeight = FontWeights.Bold,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 2),
                        },
                    };

                case ReportItemKind.SubLine:
                    return new TextBlock
                    {
                        Text = item.Text,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Width = contentWidth,
                        Margin = new Thickness(6, 6, 0, 2),
                    };

                case ReportItemKind.Spacing:
                    return new Border { Height = 6 };

                default:
                    return CreatePairBlock(item, contentWidth);
            }
        }

        private static FrameworkElement CreatePairBlock(ReportItem item, double contentWidth)
        {
            var grid = new Grid { Width = contentWidth, Margin = new Thickness(0, 2, 0, 2) };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VisualizationKeyWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(contentWidth - VisualizationKeyWidth - 12, 80)) });

            var key = new TextBlock
            {
                Text = item.Text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var value = new TextBlock
            {
                Text = item.Value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(key, 0);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
            return grid;
        }

        // ---- 源码 ----

        /// <summary>
        /// 源码按行切块，每块放若干行：既保证能在任意一行处换页，
        /// 又不会为几千行各建一个 TextBlock。
        /// </summary>
        private static void AppendCodeBlocks(List<FrameworkElement> blocks, ReportData data, double contentWidth)
        {
            if (data.CodeLines.Count == 0)
            {
                return;
            }

            blocks.Add(new Border { Height = 16 });

            const int linesPerBlock = 8;

            for (int start = 0; start < data.CodeLines.Count; start += linesPerBlock)
            {
                int count = Math.Min(linesPerBlock, data.CodeLines.Count - start);

                var text = new System.Text.StringBuilder();

                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(data.CodeLines[start + i]);
                }

                blocks.Add(new TextBlock
                {
                    Text = text.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    LineHeight = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Width = contentWidth,
                });
            }
        }
    }
}
