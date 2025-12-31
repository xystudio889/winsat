using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat
{
    public sealed partial class MainWindow : Window
    {
        public static List<string> RunPwsh(string command)
        {
            var results = new List<string>();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe", // 或 "pwsh.exe"
                Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi)!)
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    results.Add(line);
                }

                string err = p.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(err))
                {
                    // 处理或记录错误
                    results.Add("[ERROR] " + err);
                }

                p.WaitForExit();
            }

            return results;
        }

        public MainWindow()
        {
            InitializeComponent(); // 初始化

            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(300, 300)); // 调整大小

            ExtendsContentIntoTitleBar = true; // 固定标题栏

            SetTitleBar(AppTitleBar); // 应用标题栏
        }

        public async void RunGetScore(object sender, RoutedEventArgs e)
        {
            List<string> scores = RunPwsh("Get-CimInstance Win32_WinSAT");
            List<string> score_list = [];
            string status = "";

            foreach (string result in scores)
            {
                try
                {
                    score_list.Add(result.Split(':')[1].Trim());
                }
                catch (Exception) {continue;}
            }

            switch(score_list[6])
            {
                case "0":
                    status = "未知";
                    break;
                case "1":
                    status = "成功";
                    break;
                case "2":
                    status = "硬件变动";
                    break;
                case "3":
                    status = "不可用";
                    break;
                case "4":
                    status = "无限";
                    break;
                default:
                    status = score_list[6];
                    break;
            }

            CPUScore.Text = "CPU分数：" + score_list[0];
            D3DScore.Text = "Direct3D分数：" + score_list[1];
            DiskScore.Text = "磁盘分数：" + score_list[2];
            GraphicsScore.Text = "图形分数：" + score_list[3];
            MemoryScore.Text = "内存分数：" + score_list[4];
            WinSATAssessmentState.Text = "评估状态：" + status;
            WinSPRLevel.Text = "总分：" + score_list[7];
        }

        public async void Debug_WindowSize(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new ContentDialog() // 弹窗
            {
                Title = "调试：窗口大小",
                Content = "当前窗口大小：" + this.AppWindow.Size.Width + "," + this.AppWindow.Size.Height,
                PrimaryButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
};