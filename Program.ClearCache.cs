using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static async Task ClearCache()
        {
            if (!Directory.Exists(CachePath))
                return;

            await Task.WhenAll(Directory.EnumerateFiles(CachePath).Select(path => Task.Run(() =>
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            })));
            WriteLine("Cache cleared", ConsoleColor.Green);
        }
    }
}