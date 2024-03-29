using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static void CreateNew(string template)
        {
            var templates = typeof(Program).Assembly.GetManifestResourceNames()
                .Where(x => string.Equals(Path.GetExtension(x), ".csx", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => Path.GetExtension(Path.GetFileNameWithoutExtension(x)).Substring(1), StringComparer.OrdinalIgnoreCase);

            if (template == null)
            {
                Console.Write("Available templates: ");
                Console.WriteLine(string.Join(", ", templates.Keys));
                Console.Write("Enter the name of the template you wish to create: ");
                template = Console.ReadLine().Trim();
            }

            if (!templates.TryGetValue(template, out var resName))
            {
                WriteLineError($"The template '{template}' does not exists.", ConsoleColor.Red);
                return;
            }

            var filename = template + ".csx";
            if (File.Exists(filename))
            {
                WriteLineError($"The file '{filename} already exists.", ConsoleColor.Red);
                return;
            }

            using (var res = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream(resName)))
            using (var file = new StreamWriter(File.OpenWrite(filename), Encoding.UTF8))
            {
                var text = res.ReadToEnd();
                text = text
                    .Replace("{PowershellCommand}", PowershellUpdateCommand)
                    .Replace("{PowershellCommandEscaped}", PowershellUpdateCommand.Replace("\\", "\\\\").Replace("\"", "\\\""));
                file.Write(text);
            }

            WriteLine($"The file '{filename}' was created.", ConsoleColor.Green);
        }
    }
}