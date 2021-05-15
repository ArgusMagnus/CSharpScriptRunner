﻿using System;
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
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Completion;

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
            @"' | select -Expand assets | select-string -InputObject {$_.browser_download_url} -Pattern '-win\.zip$' | Select -Expand Line -First 1) -OutFile ""$dir\CSX.zip""; Write-Host 'Expanding archive...'; Expand-Archive -Path ""$dir\CSX.zip"" -DestinationPath ""$dir""; & ""$dir\x64\CSharpScriptRunner.exe"" 'install'; Remove-Item $dir -Recurse; $ProgressPreference=$bkp; Write-Host 'Done'";


        static class Verbs
        {
            public const string Version = "--version";
            public const string Install = "install";
            public const string Update = "update";
            public const string New = "new";
            public const string ClearCache = "clear-cache";
            public const string InitVSCode = "init-vscode";
            public const string ListRunning = "list-running";
            public const string Repl = "repl";
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
            // return CurrentThreadSynchronizationContext.Run(async () => (int)(await MainAsync(args)));
            return (int)MainAsync(args).Result;
        }

        static async Task<ErrorCodes> MainAsync(string[] args)
        {
            using var syncCtxScope = new SynchronizationContextScope();
            await syncCtxScope.Install(new CurrentThreadSynchronizationContext());
            using var thisProcess = Process.GetCurrentProcess();
            var dir = Path.Combine(Path.GetDirectoryName(thisProcess.MainModule.FileName), "running");
            Directory.CreateDirectory(dir);

            using var file = new FileStream(Path.Combine(dir, thisProcess.Id.ToString()), FileMode.Create, FileAccess.Write, FileShare.Read, 512, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            using (var writer = new StreamWriter(file, null, -1, true))
                await writer.WriteAsync(Environment.CommandLine);

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
                    default: return await RunScript(args);
                    case Verbs.Version: break;
                    case Verbs.Install: await Install(args.Length > 1 ? string.Equals(args[1], "inplace", StringComparison.OrdinalIgnoreCase) : false); break;
                    case Verbs.Update: await Update(); break;
                    case Verbs.New: CreateNew(args.Length > 1 ? args[1] : null); break;
                    case Verbs.ClearCache: await ClearCache(); break;
                    case Verbs.InitVSCode: InitVSCode(); break;
                    case Verbs.ListRunning: await ListRunning(); break;
                    case Verbs.Repl: await DoRepl(); break;
                }
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                    ex = aggregateException.InnerException;
                WriteLineError(ex.ToString(), ConsoleColor.Red);
                return ErrorCodes.GenericError;
            }
            return ErrorCodes.OK;
        }

        static void PrintHelp()
        {
            var exe = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
            Console.WriteLine($"Alias: {CmdAlias}, {exe}");
            Console.WriteLine($"{CmdAlias} [-r<RT>] ScriptFilePath [args]");
            Console.WriteLine($"    Executes the specified script.");
            Console.WriteLine($"    <RT>: Runtime, e.g. x86/x64");
            Console.WriteLine($"{CmdAlias} {Verbs.Install}");
            Console.WriteLine($"    Installs {exe} for the current user.");
            Console.WriteLine($"{CmdAlias} {Verbs.Update}");
            Console.WriteLine($"    Updates {exe} for the current user.");
            Console.WriteLine($"{CmdAlias} {Verbs.New} [template]");
            Console.WriteLine($"    If a template name is provided, a new script file [template].csx is created.");
            Console.WriteLine($"    If the template name is omitted, the available templates are listed.");
            Console.WriteLine($"{CmdAlias} {Verbs.InitVSCode}");
            Console.WriteLine($"    Initializes VS Code debugging support (creates .vscode directory)");
            Console.WriteLine($"{CmdAlias} {Verbs.ClearCache}");
            Console.WriteLine($"    Cleares the cache of previously compiled scripts.");
            Console.WriteLine($"{CmdAlias} {Verbs.ListRunning}");
            Console.WriteLine($"    Lists the currently running script engine instances.");
            Console.WriteLine($"Returned error codes:");
            foreach (var code in Enum.GetValues<ErrorCodes>().Where(x => x != ErrorCodes.Reserved))
                Console.WriteLine($"    - {code,3:D}    {code}");
            Console.WriteLine($"    - Value returned by script.");
            Console.WriteLine($"      Values {ErrorCodes.OK + 1:D} - {ErrorCodes.Reserved:D} are reserved by the engine.");
            Console.WriteLine($"      If a script returns a value in the reserved range, {ErrorCodes.ScriptReturnRangeConflict} will be returned instead.");
        }

        static void WriteLine(string line, ConsoleColor color = (ConsoleColor)(-1))
        {
            if ((int)color > -1)
                Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ResetColor();
        }

        static void WriteLineError(string line, ConsoleColor color = (ConsoleColor)(-1))
        {
            if ((int)color > -1)
                Console.ForegroundColor = color;
            Console.Error.WriteLine(line);
            Console.ResetColor();
        }

        // static async Task DoRepl()
        // {
        //     try { Console.CursorLeft = Console.CursorLeft; }
        //     catch
        //     {
        //         WriteLineError("REPL not supported in current terminal", ConsoleColor.Red);
        //         return;
        //     }

        //     var script = CSharpScript.Create(string.Empty);

        //     // https://www.strathweb.com/2018/12/using-roslyn-c-completion-service-programmatically/
        //     var host = MefHostServices.DefaultHost;
        //     var ws = new AdhocWorkspace(host);
        //     var projectId = ProjectId.CreateNewId();
        //     var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, "Script", "Script", LanguageNames.CSharp, isSubmission: true)
        //         .WithCompilationOptions(script.GetCompilation().Options)
        //         .WithMetadataReferences(script.GetCompilation().References);

        //     var project = ws.AddProject(projectInfo);
        //     var docInfo = DocumentInfo.Create(DocumentId.CreateNewId(projectId), "Script", sourceCodeKind: SourceCodeKind.Script);
        //     var doc = ws.AddDocument(docInfo);

        //     var source = new StringBuilder();
        //     var line = new StringBuilder();

        //     while (true)
        //     {
        //         Console.Write("> ");
        //         source.Clear();
        //         source.Append(script.Code);
        //         line.Clear();
        //         while (true)
        //         {
        //             var keyInfo = Console.ReadKey(true);
        //             line.Append(keyInfo.KeyChar);
        //             source.Append(keyInfo.KeyChar);
        //             Console.Write(keyInfo.KeyChar);
        //             if (keyInfo.Key == ConsoleKey.Enter)
        //                 break;
        //             doc = doc.WithText(SourceText.From(source.ToString()));
        //             var service = CompletionService.GetService(doc);
        //             var items = await service.GetCompletionsAsync(doc, source.Length);
        //             if (items == null)
        //                 continue;
        //             var left = Console.CursorLeft;
        //             var top = Console.CursorTop;
        //             foreach (var item in items.Items)
        //             {
        //                 if (Console.CursorTop == 0)
        //                     break;
        //                 Console.CursorTop--;
        //                 Console.Write(item.DisplayText);
        //                 Console.CursorLeft = left;
        //             }
        //             Console.CursorTop = top;
        //         }

        //         if (string.Equals(line.ToString().TrimEnd(), "#reset", StringComparison.OrdinalIgnoreCase))
        //         {
        //             script = CSharpScript.Create(string.Empty);
        //             continue;
        //         }


        //         var newScript = script.ContinueWith(line.ToString());
        //         var succeeded = true;
        //         var color = Console.ForegroundColor;
        //         foreach (var diag in newScript.Compile())
        //         {
        //             switch (diag.Severity)
        //             {
        //                 case DiagnosticSeverity.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
        //                 case DiagnosticSeverity.Error: succeeded = false; Console.ForegroundColor = ConsoleColor.Red; break;
        //                 default: Console.ForegroundColor = color; break;
        //             }

        //             var loc = diag.Location.GetLineSpan();
        //             Console.WriteLine($"{diag.Severity} ({loc.StartLinePosition.Line}, {loc.StartLinePosition.Character}): {diag.GetMessage()}");
        //         }
        //         Console.ForegroundColor = color;

        //         if (!succeeded)
        //             continue;

        //         var result = await newScript.RunAsync();
        //         if (result.ReturnValue is string output)
        //             output = SymbolDisplay.FormatLiteral(output, true);
        //         else
        //             output = result.ReturnValue?.ToString();
        //         if (!string.IsNullOrEmpty(output))
        //             WriteLine(output);
        //         script = newScript;
        //     }
        // }
    }
}
