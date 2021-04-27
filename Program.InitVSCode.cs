using System;
using System.IO;
using System.Linq;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static void InitVSCode()
        {
            const string VSCodeDir = ".vscode";
            const string Filter = ".Templates.vscode.";

            var templates = typeof(Program).Assembly.GetManifestResourceNames()
                .Select(x => (i: x.IndexOf(Filter), s: x))
                .Where(x => x.i > -1)
                .Select(x => (name: x.s, File: Path.Combine(VSCodeDir, x.s.Substring(x.i + Filter.Length))));

            var dir = new DirectoryInfo(VSCodeDir);
            if (!dir.Exists)
            {
                dir.Create();
                //dir.Attributes |= FileAttributes.Hidden;
            }

            foreach (var template in templates)
            {
                if (File.Exists(template.File))
                {
                    WriteLine($"File '{template.File}' already exists.", ConsoleColor.Yellow);
                    continue;
                }

                using (var stream = typeof(Program).Assembly.GetManifestResourceStream(template.name))
                using (var file = File.OpenWrite(template.File))
                {
                    WriteLine($"Creating file '{template.File}'...", ConsoleColor.Green);
                    stream.CopyTo(file);
                }
            }
        }
    }
}