using System;
using System.Diagnostics;
using System.IO;

namespace LengJiaoConnect
{
    public static class AdbHelper
    {
        private static readonly string AdbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "adb.exe");

        public static string ExecuteCommand(string arguments)
        {
            if (!File.Exists(AdbPath))
            {
                return "Error: 未找到 adb.exe，请检查 Tools 目录。";
            }

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using (Process process = Process.Start(processInfo))
            {
                if (process == null) return "Error: 无法启动 ADB 进程。";

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                process.WaitForExit();

                return string.IsNullOrWhiteSpace(error) ? output : error;
            }
        }
    }
}