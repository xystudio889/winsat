using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace winsat.helpers
{
    internal class ScoreHelper
    {
        private async static Task<List<string>> RunPwsh(string command)
        {
            var p = await Commands.Execute("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"");

            return p.Split(
    new[] { "\r\n", "\n", "\r" },
    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
).ToList();
        }

        public async static Task<List<String>> GetScore()
        {
            List<string> scores = await RunPwsh("Get-CimInstance Win32_WinSAT");
            List<string> score_list = [];

            foreach (string result in scores)
            {
                try
                {
                    var score = result.Split(':')[1].Trim();

                    if (score == "0") // 未跑分
                    {
                        score = Loader.GetString("NoScored");
                    }
                    score_list.Add(score);
                }
                catch { continue; }
            }

            if (score_list.Count < 8)
            {
                return Enumerable.Repeat("0", 8).ToList();
            }
                return score_list;
        }
    }
}
