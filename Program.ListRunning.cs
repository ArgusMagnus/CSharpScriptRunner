using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static async Task ListRunning()
        {
            WriteLine("Process ID    RT     Arguments");
            WriteLine("----------    ---    ---------");
            using var thisProcess = Process.GetCurrentProcess();
            var runtimesDir = Path.GetDirectoryName(thisProcess.MainModule.FileName); // bin
            runtimesDir = Path.GetDirectoryName(runtimesDir); // x64
            runtimesDir = Path.GetDirectoryName(runtimesDir); // vX.Y.Z
            foreach (var runtimeDir in Directory.EnumerateDirectories(runtimesDir))
            {
                var dir = Path.Combine(runtimeDir, "bin", "running");
                if (!Directory.Exists(dir))
                    continue;

                var rt = Path.GetFileName(runtimeDir);

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var processId = Path.GetFileNameWithoutExtension(file);
                    if (thisProcess.Id == int.Parse(processId))
                        continue;
                        
                    try
                    {
                        File.Delete(file);
                        continue;
                    }
                    catch { }

                    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    var args = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (args.StartsWith('"'))
                        args = args.Substring(args.IndexOf('"', 1) + 1);
                    else
                        args = args.Split(' ', 2).Last();
                    WriteLine($"{processId,10}    {rt,-3}    {args.Trim()}");
                }
            }
        }
    }
}