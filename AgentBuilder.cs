using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Compression;

namespace LengJiaoConnect
{
    public static class AgentBuilder
    {
        public static string CompileAndPushAgent(string serial)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string rootDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            string agentSrcDir = Path.Combine(rootDir, "Agent");
            if (!Directory.Exists(agentSrcDir)) agentSrcDir = Path.Combine(baseDir, "Agent");
            
            string mainJava = Path.Combine(agentSrcDir, "Main.java");
            if (!File.Exists(mainJava)) return "Error: 找不到 Agent 源码 Main.java。请确认路径: " + mainJava;

            string sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
            if (!Directory.Exists(sdkRoot)) return "Error: 未找到 Android SDK 目录。";
            
            // Find android.jar
            string platformsDir = Path.Combine(sdkRoot, "platforms");
            string androidJar = Directory.GetDirectories(platformsDir)
                                         .Select(d => Path.Combine(d, "android.jar"))
                                         .LastOrDefault(f => File.Exists(f));
            
            // Find d8.bat
            string buildToolsDir = Path.Combine(sdkRoot, "build-tools");
            string d8Path = Directory.GetFiles(buildToolsDir, "d8.bat", SearchOption.AllDirectories).LastOrDefault();

            if (androidJar == null || d8Path == null) return "Error: 找不到 Android SDK 编译工具 (android.jar 或 d8.bat)";

            // Compile Java
            string outDir = Path.Combine(agentSrcDir, "AgentBuild");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            
            string javacArgs = $"-source 8 -target 8 -cp \"{androidJar}\" -d \"{outDir}\" \"{mainJava}\"";
            var javacProc = Process.Start(new ProcessStartInfo("javac", javacArgs) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true });
            javacProc.WaitForExit();
            if (javacProc.ExitCode != 0) return "Java 编译失败:\n" + javacProc.StandardError.ReadToEnd();

            // Create DEX
            string classFile = Path.Combine(outDir, "com", "lengjiao", "Main.class");
            var d8Proc = Process.Start(new ProcessStartInfo(d8Path, $"--output \"{outDir}\" \"{classFile}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true });
            d8Proc.WaitForExit();
            if (d8Proc.ExitCode != 0) return "D8 打包失败:\n" + d8Proc.StandardError.ReadToEnd();

            // Create JAR (zip classes.dex)
            string jarPath = Path.Combine(outDir, "agent.jar");
            if (File.Exists(jarPath)) File.Delete(jarPath);
            using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(outDir, "classes.dex"), "classes.dex");
            }

            // Push to device
            string pushRes = AdbHelper.ExecuteCommand($"-s {serial} push \"{jarPath}\" /data/local/tmp/agent.jar");
            
            if (pushRes.Contains("error")) return "推送到手机失败:\n" + pushRes;
            
            return "OK";
        }
    }
}
