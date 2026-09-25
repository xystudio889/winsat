using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace winsat.helpers
{
    internal class Commands
    {
        public static async Task<string> Execute(string command, string arguments = "")
        {
            var processInfo = new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string output = "";
            string error = "";

            using (var process = new Process { StartInfo = processInfo })
            {
                process.Start();

                // 异步读取输出流
                output = await process.StandardOutput.ReadToEndAsync();
                error = await process.StandardError.ReadToEndAsync();

                // 等待进程退出 (.NET 5+ 支持)
                await process.WaitForExitAsync(); // 或使用 process.WaitForExit()
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return $"Error: {error}";
            }

            return output;
        }
    }
}
