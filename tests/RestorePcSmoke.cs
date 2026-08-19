using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZZZTouchLauncher
{
    internal static class RestorePcSmoke
    {
        private static readonly byte[] Magic = new byte[]
        {
            85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181,
            46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116
        };

        private static int RunLauncher(string launcherPath, string arguments, out string output)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(launcherPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(startInfo))
            {
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: RestorePcSmoke.exe <ZZZTouchLauncher.exe>");
                return 1;
            }

            string launcherPath = Path.GetFullPath(args[0]);
            if (!File.Exists(launcherPath))
            {
                Console.WriteLine("Launcher not found: " + launcherPath);
                return 1;
            }

            string baseDirectory = Path.GetDirectoryName(launcherPath);
            string gamePath = Path.Combine(baseDirectory, "restore-pc-fixture");
            string dataPath = Path.Combine(
                gamePath,
                "ZenlessZoneZero_Data",
                "Persistent",
                "LocalStorage",
                "GENERAL_DATA.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath));

            const string original = "{\"LocalUILayoutPlatform\":1,\"Sentinel\":\"keep\"}";
            Sleepy.WriteString(dataPath, original, Magic);

            string escapedGamePath = gamePath.Replace("\\", "\\\\");
            File.WriteAllText(
                Path.Combine(baseDirectory, "config.json"),
                "{\n  \"gamePath\": \"" + escapedGamePath + "\"\n}\n",
                new UTF8Encoding(false));

            string output;
            int firstExitCode = RunLauncher(launcherPath, "--restore-pc", out output);
            if (firstExitCode != 0)
            {
                Console.WriteLine("First restore failed with exit code " + firstExitCode);
                Console.WriteLine(output);
                return 1;
            }

            string restored = Sleepy.ReadString(dataPath, Magic);
            if (!Regex.IsMatch(restored, "LocalUILayoutPlatform\"\\s*:\\s*2") ||
                !restored.Contains("\"Sentinel\":\"keep\""))
            {
                Console.WriteLine("Restored content is invalid: " + restored);
                return 1;
            }

            int secondExitCode = RunLauncher(launcherPath, "--restore-pc", out output);
            if (secondExitCode != 0)
            {
                Console.WriteLine("Second restore failed with exit code " + secondExitCode);
                Console.WriteLine(output);
                return 1;
            }

            string restoredAgain = Sleepy.ReadString(dataPath, Magic);
            if (restoredAgain != restored)
            {
                Console.WriteLine("Restore is not idempotent.");
                return 1;
            }

            int invalidArgumentExitCode = RunLauncher(launcherPath, "--invalid", out output);
            if (invalidArgumentExitCode != 2)
            {
                Console.WriteLine("Invalid argument exit code was " + invalidArgumentExitCode + ", expected 2.");
                Console.WriteLine(output);
                return 1;
            }

            if (Sleepy.ReadString(dataPath, Magic) != restored)
            {
                Console.WriteLine("Invalid argument changed the configuration.");
                return 1;
            }

            Console.WriteLine("RestorePcSmoke=ok");
            return 0;
        }
    }
}
