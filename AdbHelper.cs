using System;
using System.Diagnostics;
using System.IO;

namespace LengJiaoConnect
{
    public static class AdbHelper
    {
        private static readonly string AdbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "adb.exe");

        public static string GetHelperApkPath()
        {
            // 1. Try project structure development path (up 3 levels)
            string apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "HelperApk", "LengJiaoHelper.apk");
            if (File.Exists(apkPath)) return apkPath;

            // 2. Try subfolder HelperApk
            apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HelperApk", "LengJiaoHelper.apk");
            if (File.Exists(apkPath)) return apkPath;

            // 3. Try root directory
            apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LengJiaoHelper.apk");
            if (File.Exists(apkPath)) return apkPath;

            return apkPath;
        }

        public static string GetAndroidManifestPath()
        {
            // 1. Try project structure development path (up 3 levels)
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "HelperApk", "AndroidManifest.xml");
            if (File.Exists(path)) return path;

            // 2. Try subfolder HelperApk
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HelperApk", "AndroidManifest.xml");
            if (File.Exists(path)) return path;

            // 3. Try root directory
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AndroidManifest.xml");
            if (File.Exists(path)) return path;

            return path;
        }

        public static bool IsPackageInstalled(string serial, string packageName)
        {
            if (string.IsNullOrEmpty(serial)) return false;
            string pathRes = ExecuteCommand($"-s {serial} shell pm path {packageName}");
            return pathRes.Contains("package:");
        }

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

                if (!string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
                return error;
            }
        }
    }
}