using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.CodeAnalysis.CSharp;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CSharpScriptRunner
{
    public sealed class ScriptGlobals
    {
        public string[] Args { get; }

        internal ScriptGlobals(string[] args) => Args = args;
    }

    static partial class Program
    {
        const string CmdAlias = "csx";
        static readonly string CachePath = Path.Combine(Path.GetTempPath(), nameof(CSharpScriptRunner));

        const string UpdateRequestUri = "https://api.github.com/repos/ArgusMagnus/CSharpScriptRunner/releases/latest";
        const string PowershellUpdateCommand =
            @"$dir=md ""$Env:Temp\{$(New-Guid)}""; $bkp=$ProgressPreference; $ProgressPreference='SilentlyContinue'; Write-Host 'Downloading...'; Invoke-WebRequest (Invoke-RestMethod -Uri '" + UpdateRequestUri +
            @"' | select -Expand assets | select-string -InputObject {$_.browser_download_url} -Pattern '-win\.zip$' | Select -Expand Line -First 1) -OutFile ""$dir\CSX.zip""; Write-Host 'Expanding archive...'; Expand-Archive -Path ""$dir\CSX.zip"" -DestinationPath ""$dir""; & ""$dir\win\x64\CSharpScriptRunner.exe"" 'install'; Remove-Item $dir -Recurse; $ProgressPreference=$bkp; Write-Host 'Done'";


        static class Verbs
        {
            public const string Version = "--version";
            public const string Install = "install";
            public const string Update = "update";
            public const string New = "new";
            public const string ClearCache = "clear-cache";
            public const string InitVSCode = "init-vscode";
            public const string ListRunning = "list-running";
        }

        enum ErrorCodes
        {
            OK = 0,
            GenericError,
            ScriptReturnRangeConflict,
            UnrecognizedArgument,
            ScriptFileDoesNotExist,
            ScriptCompilationFailed,
            Reserved = 0xFF
        }

        [STAThread]
        // Must run on STA thread (for WinForms/WPF support)
        static int Main(string[] args)
        {
            return CurrentThreadSynchronizationContext.Run(() => MainAsync(args));
        }

        static async Task<ErrorCodes> MainAsync(string[] args)
        {
            using var thisProcess = Process.GetCurrentProcess();
            var dir = Path.Combine(Path.GetDirectoryName(thisProcess.MainModule.FileName), "running");
            Directory.CreateDirectory(dir);

            using var file = new FileStream(Path.Combine(dir, thisProcess.Id.ToString()), FileMode.Create, FileAccess.Write, FileShare.Read, 512, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            using (var writer = new StreamWriter(file, null, -1, true))
                await writer.WriteLineAsync(Environment.CommandLine);

            Console.WriteLine($"{nameof(CSharpScriptRunner)}, {BuildInfo.ReleaseTag}");

            if (args == null || args.Length == 0)
            {
                PrintHelp();
                return ErrorCodes.OK;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case Verbs.Version: break;
                    case Verbs.Install: await Install(args.Length > 1 ? string.Equals(args[1], "inplace", StringComparison.OrdinalIgnoreCase) : false); break;
                    case Verbs.Update: await Update(); break;
                    case Verbs.New: CreateNew(args.Length > 1 ? args[1] : null); break;
                    case Verbs.ClearCache: await ClearCache(); break;
                    case Verbs.InitVSCode: InitVSCode(); break;
                    case Verbs.ListRunning: await ListRunning(); break;
                    default: return await RunScript(args);
                }
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                    ex = aggregateException.InnerException;
                WriteLine(ex.ToString(), ConsoleColor.Red);
                return ErrorCodes.GenericError;
            }
            return ErrorCodes.OK;
        }

        static void PrintHelp()
        {
            var exe = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
            Console.WriteLine($"Alias: {CmdAlias}");
            Console.WriteLine($"{exe} [-r<RT>] ScriptFilePath [args]");
            Console.WriteLine($"        Executes the specified script.");
            Console.WriteLine($"        <RT>: Runtime, e.g. x86/x64");
            Console.WriteLine($"{exe} {Verbs.Install}");
            Console.WriteLine($"        Installs {exe} for the current user.");
            Console.WriteLine($"{exe} {Verbs.Update}");
            Console.WriteLine($"        Updates {exe} for the current user.");
            Console.WriteLine($"{exe} {Verbs.New} [template]");
            Console.WriteLine($"        If a template name is provided, a new script file [template].csx is created.");
            Console.WriteLine($"        If the template name is omitted, the available templates are listed.");
            Console.WriteLine($"{exe} {Verbs.InitVSCode}");
            Console.WriteLine($"        Initializes VS Code debugging support (creates .vscode directory)");
            Console.WriteLine($"{exe} {Verbs.ClearCache}");
            Console.WriteLine($"        Cleares the cache of previously compiled scripts.");
            Console.WriteLine($"{exe} {Verbs.ListRunning}");
            Console.WriteLine($"        Lists the currently running script engine instances.");
            Console.WriteLine($"Returned error codes:");
            foreach (var code in Enum.GetValues<ErrorCodes>().Where(x => x != ErrorCodes.Reserved))
                Console.WriteLine($"        - {code,3:D}    {code}");
            Console.WriteLine($"        - Value returned by script.");
            Console.WriteLine($"          Values {ErrorCodes.OK + 1:D} - {ErrorCodes.Reserved:D} are reserved by the engine.");
            Console.WriteLine($"          If a script returns a value in the reserved range, {ErrorCodes.ScriptReturnRangeConflict} will be returned instead.");
        }

        static void WriteLine(string line, ConsoleColor color = (ConsoleColor)(-1))
        {
            if ((int)color > -1)
                Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ResetColor();
        }

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

                    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    var args = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (args.StartsWith('"'))
                        args = args.Substring(args.IndexOf('"', 1) + 1).TrimStart();
                    else
                        args = args.Split(' ', 2).Last();
                    WriteLine($"{processId,10}    {rt,-3}    {args}");
                }
            }
        }
    }
}
