using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        sealed class Config
        {
            public string Type { get; set; }
            public string Method { get; set; }
        }

        static async Task<ErrorCodes> RunScript(string[] arguments)
        {
            const string NuGetReferenceRegex = @"^\s*#r\s+""\s*nuget:\s*(?<name>[\w\d.-]+)\s*\/\s*(?<version>[\w\d.-]+)\s*""\s*$";
            
            IEnumerable<string> args = arguments;
            var runtimeExt = string.Empty;
            while (args.FirstOrDefault()?.StartsWith('-') ?? false)
            {
                var arg = args.First();
                args = args.Skip(1);
                if (arg.StartsWith("-r", StringComparison.OrdinalIgnoreCase))
                    runtimeExt = arg.Substring(2);
                else
                {
                    WriteLine($"Argument {arg} is not recognized.", ConsoleColor.Red);
                    return ErrorCodes.UnrecognizedArgument;
                }
            }

            var filePath = args.FirstOrDefault();
            if (!File.Exists(filePath))
            {
                WriteLine($"Script file '{filePath}' does not exist.", ConsoleColor.Red);
                return ErrorCodes.ScriptFileDoesNotExist;
            }

            var scriptPath = Path.GetFullPath(filePath);
            if (string.IsNullOrEmpty(runtimeExt))
                runtimeExt = Path.GetExtension(Path.GetFileNameWithoutExtension(scriptPath)).TrimStart('.');
            if (!string.IsNullOrEmpty(runtimeExt))
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                var filename = Path.GetFileName(exePath);
                var dir = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
                var runtimeDir = Path.GetFileName(dir);
                dir = Path.GetDirectoryName(dir);
                if (runtimeExt != runtimeDir)
                {
                    exePath = Path.Combine(dir, runtimeExt, "bin", filename);
                    if (File.Exists(exePath))
                    {
                        using var process = Process.Start(new ProcessStartInfo(exePath, string.Join(" ", args.Select(x => $"\"{x.Replace("\"", "\\\"")}\""))) { UseShellExecute = false });
                        await process.WaitForExitAsync();
                        return (ErrorCodes)process.ExitCode;
                    }

                    WriteLine($"Warning: Runtime '{runtimeExt}' was not found. Proceeding anyway...", ConsoleColor.Yellow);
                }
            }

            var (buildReferences, assemblyLoader) = await LoadPackages(scriptPath);

            var cacheFileBase = GetCacheFilenameBase(scriptPath);
            var assemblyFile = cacheFileBase + ".dll";
            var hashFile = cacheFileBase + ".hash";
            var configFile = cacheFileBase + ".json";
            var scriptHash = GetFileHash(scriptPath);

            if (!TryGetCache(assemblyFile, hashFile, configFile, scriptHash, out var config))
            {
                // Check for new release only when compiling
                Process.Start(new ProcessStartInfo { FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = Verbs.Update, CreateNoWindow = true, UseShellExecute = false });

                if (!TryBuild(scriptPath, assemblyFile, hashFile, configFile, scriptHash, buildReferences, out config))
                    return ErrorCodes.ScriptCompilationFailed;
            }

            var assembly = Assembly.Load(File.ReadAllBytes(assemblyFile));
            var type = assembly.GetType(config.Type);
            if (type == null)
                throw new Exception($"Entry type '{config.Type}' not found");
            var entryPoint = type.GetMethod(config.Method, BindingFlags.Static | BindingFlags.Public);
            if (entryPoint == null)
                throw new Exception($"Entry point '{config.Type}.{config.Method}' not found");

            // Environment.CurrentDirectory = Path.GetDirectoryName(scriptPath);
            var task = (Task<object>)entryPoint.Invoke(null, new object[] { new object[] { new ScriptGlobals(args.Skip(1).ToArray()), assemblyLoader } });
            var errorCode = (ErrorCodes?)((await task) as int?) ?? ErrorCodes.OK;
            if ((errorCode & ErrorCodes.ErrorMask) != default)
                return ErrorCodes.ScriptReturnRangeConflict;
            return errorCode;

            static string GetCacheFilenameBase(string filename)
            {
                using (var hasher = SHA256.Create())
                {
                    var bytes = hasher.ComputeHash(Encoding.Unicode.GetBytes(filename.ToUpperInvariant()));
                    var result = new StringBuilder(bytes.Length * 2);
                    foreach (var b in bytes)
                        result.Append(b.ToString("X2"));
                    Directory.CreateDirectory(CachePath);
                    return Path.Combine(CachePath, result.ToString());
                }
            }

            static byte[] GetFileHash(string filename)
            {
                var bytes = File.ReadAllBytes(filename);
                using (var hasher = SHA256.Create())
                {
                    return hasher.ComputeHash(bytes);
                }
            }

            static bool TryGetCache(string assemblyFile, string hashFile, string configFile, byte[] scriptHash, out Config config)
            {
                config = null;
                if (!File.Exists(assemblyFile) || !File.Exists(hashFile) || !File.Exists(configFile))
                    return false;

                var cachedHash = File.ReadAllBytes(hashFile);
                if (!cachedHash.SequenceEqual(scriptHash))
                    return false;

                var cfgJson = File.ReadAllText(configFile, Encoding.UTF8);
                config = JsonSerializer.Deserialize<Config>(cfgJson);
                return true;
            }

            static bool TryBuild(string scriptFile, string assemblyFile, string hashFile, string configFile, byte[] scriptHash, IEnumerable<string> references, out Config config)
            {
                config = null;
                var lines = File.ReadAllLines(scriptFile);
                Console.WriteLine($"Compiling script '{scriptFile}'...");
                var options = ScriptOptions.Default
                    .WithEmitDebugInformation(true)
                    .WithFilePath(scriptFile)
                    .WithFileEncoding(Encoding.UTF8)
                    .AddReferences(references)
                    ;

                var script = string.Join(Environment.NewLine, lines);
                script = Regex.Replace(script, NuGetReferenceRegex, match => new string(match.Value.Select(x => char.IsWhiteSpace(x) ? x : ' ').ToArray()), RegexOptions.Multiline | RegexOptions.IgnoreCase);
                var compilation = CSharpScript.Create(script, options, typeof(ScriptGlobals)).GetCompilation();
                //compilation = compilation.WithOptions(compilation.Options.WithOutputKind(OutputKind.ConsoleApplication));
                using (var stream = File.OpenWrite(assemblyFile))
                {
                    var result = compilation.Emit(stream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
                    PrintCompilationDiagnostics(result, lines);
                    if (!result.Success)
                        return false;
                }

                File.WriteAllBytes(hashFile, scriptHash);

                var scriptEntryPoint = compilation.GetEntryPoint(default);
                config = new Config
                {
                    Type = scriptEntryPoint.ContainingType.MetadataName,
                    Method = scriptEntryPoint.Name
                };

                var json = JsonSerializer.Serialize(config);
                File.WriteAllText(configFile, json, Encoding.UTF8);
                return true;
            }

            static async Task<(IEnumerable<string> BuildReferences, InteractiveAssemblyLoader assemblyLoader1)> LoadPackages(string scriptPath)
            {
                var buildReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var runtimeReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var matches = Regex.Matches(File.ReadAllText(scriptPath), NuGetReferenceRegex, RegexOptions.Multiline | RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                    await NuGet.LoadPackage(match.Groups["name"].Value, match.Groups["version"].Value, buildReferences, runtimeReferences);

                var assemblyLoader = new InteractiveAssemblyLoader();
                foreach (var path in runtimeReferences)
                    assemblyLoader.RegisterDependency(Assembly.LoadFrom(path));
                return (buildReferences, assemblyLoader);
            }

            static void PrintCompilationDiagnostics(EmitResult result, string[] lines)
            {
                var color = Console.ForegroundColor;
                foreach (var diag in result.Diagnostics)
                {
                    switch (diag.Severity)
                    {
                        case DiagnosticSeverity.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
                        case DiagnosticSeverity.Error: Console.ForegroundColor = ConsoleColor.Red; break;
                        default: Console.ForegroundColor = color; break;
                    }

                    var loc = diag.Location.GetLineSpan();
                    Console.WriteLine($"{diag.Severity} ({loc.StartLinePosition.Line}, {loc.StartLinePosition.Character}): {diag.GetMessage()}");
                    if (lines != null)
                    {
                        var color2 = Console.ForegroundColor;
                        var codeStart = lines[loc.StartLinePosition.Line].Substring(0, loc.StartLinePosition.Character);
                        var codeEnd = lines[loc.EndLinePosition.Line].Substring(loc.EndLinePosition.Character);
                        var code = string.Join(Environment.NewLine, lines.Skip(loc.StartLinePosition.Line).Take(loc.EndLinePosition.Line - loc.StartLinePosition.Line + 1));
                        code = code.Substring(codeStart.Length, code.Length - codeStart.Length - codeEnd.Length);

                        Console.ForegroundColor = color;
                        Console.Write(codeStart);
                        Console.ForegroundColor = color2;
                        Console.Write(code);
                        Console.ForegroundColor = color;
                        Console.Write(codeEnd);
                        Console.WriteLine();
                    }
                }
                Console.ForegroundColor = color;
            }
        }
    }
}