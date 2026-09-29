using System;
using System.IO;
using System.Text;

internal static class ArgumentEchoGame
{
    private static int Main(string[] args)
    {
        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "received-arguments.txt");

        var lines = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            lines[i] = Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i]));
        }

        File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));
        return 0;
    }
}
