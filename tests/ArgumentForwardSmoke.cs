using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ZZZTouchLauncher
{
    internal static class ArgumentForwardSmoke
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: ArgumentForwardSmoke.exe <launcher> <game-directory>");
                return 1;
            }

            Process[] existingGames = Process.GetProcessesByName("ZenlessZoneZero");
            if (existingGames.Length > 0)
            {
                foreach (Process game in existingGames)
                {
                    game.Dispose();
                }
                Console.WriteLine("FAIL: a ZenlessZoneZero process is already running");
                return 1;
            }

            string[] steamArguments =
            [
                "--flag",
                "two words",
                "",
                "quote\"value",
                "C:\\path with space\\"
            ];
            string steamUri = Program.BuildSteamLaunchUri(steamArguments);
            const string steamPrefix = "steam://run/4162040//";
            if (!steamUri.StartsWith(steamPrefix, StringComparison.Ordinal) ||
                Uri.UnescapeDataString(steamUri.Substring(steamPrefix.Length)) !=
                    Program.BuildCommandLineArguments(steamArguments))
            {
                Console.WriteLine("FAIL: Steam launch URI does not preserve arguments");
                return 1;
            }

            string launcherPath = Path.GetFullPath(args[0]);
            string gameDirectory = Path.GetFullPath(args[1]);
            string gamePath = Path.Combine(gameDirectory, "ZenlessZoneZero.exe");
            string steamAppIdPath = Path.Combine(gameDirectory, "steam_appid.txt");
            if (!File.Exists(launcherPath) || !File.Exists(gamePath))
            {
                Console.WriteLine("FAIL: launcher or argument echo game is missing");
                return 1;
            }

            string escapedGamePath = gameDirectory.Replace("\\", "\\\\");
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(launcherPath), "config.json"),
                "{\n  \"gamePath\": \"" + escapedGamePath + "\"\n}\n",
                new UTF8Encoding(false));

            string receivedPath = Path.Combine(gameDirectory, "received-arguments.txt");
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }
            if (File.Exists(steamAppIdPath))
            {
                File.Delete(steamAppIdPath);
            }
            if (Program.HasSteamAppId(gameDirectory))
            {
                Console.WriteLine("FAIL: direct-launch fixture was detected as Steam");
                return 1;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                Arguments = "--flag \"two words\" \"\" \"quote\\\"value\" \"C:\\path with space\\\\\"",
                WorkingDirectory = Path.GetDirectoryName(launcherPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string output;
            int exitCode;
            using (Process launcher = Process.Start(startInfo))
            {
                output = launcher.StandardOutput.ReadToEnd() + launcher.StandardError.ReadToEnd();
                if (!launcher.WaitForExit(30000))
                {
                    launcher.Kill();
                    Console.WriteLine("FAIL: launcher timed out");
                    return 1;
                }
                exitCode = launcher.ExitCode;
            }

            if (exitCode != 0)
            {
                Console.WriteLine("FAIL: launcher exited with " + exitCode);
                Console.WriteLine(output);
                return 1;
            }

            for (int i = 0; i < 50 && !File.Exists(receivedPath); i++)
            {
                Thread.Sleep(100);
            }
            if (!File.Exists(receivedPath))
            {
                Console.WriteLine("FAIL: game did not record arguments");
                Console.WriteLine(output);
                return 1;
            }

            string[] expected = steamArguments;
            string[] actualLines = File.ReadAllLines(receivedPath, Encoding.UTF8);
            if (actualLines.Length != expected.Length)
            {
                Console.WriteLine(
                    "FAIL: expected " + expected.Length + " arguments, got " + actualLines.Length);
                return 1;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                string actual = Encoding.UTF8.GetString(Convert.FromBase64String(actualLines[i]));
                if (actual != expected[i])
                {
                    Console.WriteLine(
                        "FAIL: argument " + i + " was [" + actual + "], expected [" + expected[i] + "]");
                    return 1;
                }
            }

            File.WriteAllText(steamAppIdPath, "4162040\n", new UTF8Encoding(false));
            if (!Program.HasSteamAppId(gameDirectory))
            {
                Console.WriteLine("FAIL: steam_appid.txt was not detected");
                return 1;
            }
            File.Delete(steamAppIdPath);

            using (var closeScope = new Program.ConsoleCloseScope())
            {
                if (closeScope.HandleConsoleControl(2))
                {
                    Console.WriteLine("FAIL: console close handler suppressed normal termination");
                    return 1;
                }
                if (!closeScope.CloseRequested)
                {
                    Console.WriteLine("FAIL: console close was not recorded");
                    return 1;
                }
            }

            Console.WriteLine("ArgumentForwardSmoke=ok");
            return 0;
        }
    }
}
