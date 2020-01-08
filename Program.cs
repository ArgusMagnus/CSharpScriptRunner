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

namespace CSharpScriptRunner
{
    public sealed class ScriptGlobals
    {
        public string[] Args { get; }

        internal ScriptGlobals(string[] args) => Args = args;
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args == null || args.Length == 0)
                return;

            try
            {
                if (args[0] == "install")
                    Install();
                else if (args[0] == "new")
                    CreateNew(args.Length > 1 ? args[1] : null);
                else
                    RunScript(args);
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                    ex = aggregateException.InnerException;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex);
                Console.ResetColor();
                System.Threading.Thread.Sleep(5000);
            }
        }

        static void Install()
        {
            var oldPath = Process.GetCurrentProcess().MainModule.FileName;
            var filename = Path.GetFileName(oldPath);
            var oldDir = Path.GetDirectoryName(oldPath);
            var runtimeDir = Path.GetFileName(oldDir);
            oldDir = Path.GetDirectoryName(oldDir);
            var newDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(CSharpScriptRunner));
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, true);
            Directory.CreateDirectory(newDir);
            var newPath = Path.Combine(newDir, runtimeDir, filename);

            foreach (var dir in Directory.EnumerateDirectories(oldDir))
            {
                if (!File.Exists(Path.Combine(dir, filename)))
                    continue;

                Task.WhenAll(Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true }).Select(file => Task.Run(() =>
                {
                    var dst = Path.Combine(newDir, file.Substring(oldDir.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    Console.WriteLine($"Copying {dst} ...");
                    File.Copy(file, dst, true);
                }))).Wait();
            }

            var envPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.Contains(nameof(CSharpScriptRunner)))
                .Append(Path.GetDirectoryName(newPath));
            Environment.SetEnvironmentVariable("Path", string.Join(';', envPath), EnvironmentVariableTarget.User);

            var filetype = ".csx";

            using (var regKeyExt = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{filetype}", true))
            {
                var tmp = regKeyExt.GetValue(string.Empty) as string;
                if (!string.IsNullOrEmpty(tmp))
                    filetype = tmp;
                else
                {
                    filetype = filetype.Substring(1) + "_auto_file";
                    regKeyExt.SetValue(string.Empty, filetype);
                }
            }

            using (var regKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{filetype}\shell\execute", true))
            using (var regKeyCommand = regKey.CreateSubKey("command", true))
            {
                regKey.SetValue(string.Empty, "C# Skript ausführen");
                regKeyCommand.SetValue(string.Empty, $"\"{newPath}\" \"%1\"");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Installation was successful.");
            Console.ResetColor();
            System.Threading.Thread.Sleep(5000);
        }

        static string GetCacheFilenameBase(string filename)
        {
            using (var hasher = SHA256.Create())
            {
                var bytes = hasher.ComputeHash(Encoding.Unicode.GetBytes(filename.ToUpperInvariant()));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                    result.Append(b.ToString("X2"));
                var dir = Path.Combine(Path.GetTempPath(), nameof(CSharpScriptRunner));
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, result.ToString());
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

        sealed class Config
        {
            public string Type { get; set; }
            public string Method { get; set; }
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

        const string NuGetReferenceRegex = @"^\s*#r\s+""\s*nuget:\s*(?<name>[\w\d.-]+)\s*\/\s*(?<version>[\w\d.-]+)\s*""\s*$";

        static bool TryBuild(string scriptFile, string assemblyFile, string hashFile, string configFile, byte[] scriptHash, IEnumerable<string> references, out Config config)
        {
            config = null;
            var lines = File.ReadAllLines(scriptFile);
            Console.WriteLine($"Compiling script {scriptFile}...");
            var options = ScriptOptions.Default
                .WithEmitDebugInformation(true)
                .WithFilePath(scriptFile)
                .WithFileEncoding(Encoding.UTF8)
                .AddReferences(references)
                ;

            var script = string.Join(Environment.NewLine, lines);
            script = Regex.Replace(script, NuGetReferenceRegex, Environment.NewLine, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var compilation = CSharpScript.Create(script, options, typeof(ScriptGlobals)).GetCompilation();
            using (var stream = File.OpenWrite(assemblyFile))
            {
                var result = compilation.Emit(stream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
                PrintCompilationDiagnostics(result, lines);
                if (!result.Success)
                {
                    System.Threading.Thread.Sleep(5000);
                    return false;
                }
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

        static IEnumerable<string> LoadPackages(string scriptPath, out InteractiveAssemblyLoader assemblyLoader)
        {
            var buildReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runtimeReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(File.ReadAllText(scriptPath), NuGetReferenceRegex, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match match in matches)
                NuGet.LoadPackage(match.Groups["name"].Value, match.Groups["version"].Value, buildReferences, runtimeReferences).Wait();

            assemblyLoader = new InteractiveAssemblyLoader();
            foreach (var path in runtimeReferences)
                assemblyLoader.RegisterDependency(Assembly.LoadFrom(path));
            return buildReferences;
        }

        static void RunScript(string[] args)
        {
            var scriptPath = Path.GetFullPath(args[0]);
            var runtimeExt = Path.GetExtension(Path.GetFileNameWithoutExtension(scriptPath));
            if (!string.IsNullOrEmpty(runtimeExt))
            {
                runtimeExt = runtimeExt.Substring(1);
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                var filename = Path.GetFileName(exePath);
                var dir = Path.GetDirectoryName(exePath);
                var runtimeDir = Path.GetFileName(dir);
                dir = Path.GetDirectoryName(dir);
                if (runtimeExt != runtimeDir)
                {
                    exePath = Path.Combine(dir, runtimeExt, filename);
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo(exePath, string.Join(" ", args.Select(x => $"\"{x.Replace("\"", "\\\"")}\""))) { UseShellExecute = false });
                        return;
                    }

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Runtime '{runtimeExt}' was not found. Proceeding anyway...");
                    Console.ResetColor();
                }
            }

            var buildReferences = LoadPackages(scriptPath, out var assemblyLoader);

            var cacheFileBase = GetCacheFilenameBase(scriptPath);
            var assemblyFile = cacheFileBase + ".dll";
            var hashFile = cacheFileBase + ".hash";
            var configFile = cacheFileBase + ".json";
            var scriptHash = GetFileHash(scriptPath);

            if (!TryGetCache(assemblyFile, hashFile, configFile, scriptHash, out var config))
            {
                if (!TryBuild(scriptPath, assemblyFile, hashFile, configFile, scriptHash, buildReferences, out config))
                    return;
            }

            var assembly = Assembly.LoadFile(assemblyFile);
            var type = assembly.GetType(config.Type);
            if (type == null)
                return;
            var entryPoint = type.GetMethod(config.Method, BindingFlags.Static | BindingFlags.Public);
            if (entryPoint == null)
                return;

            Environment.CurrentDirectory = Path.GetDirectoryName(scriptPath);
            var task = (Task<object>)entryPoint.Invoke(null, new object[] { new object[] { new ScriptGlobals(args.Skip(1).ToArray()), assemblyLoader } });
            task.Wait();
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

        static void CreateNew(string template)
        {
            var templates = typeof(Program).Assembly.GetManifestResourceNames()
                .ToDictionary(x => Path.GetExtension(Path.GetFileNameWithoutExtension(x)).Substring(1), StringComparer.OrdinalIgnoreCase);

            if (template == null)
            {
                Console.Write("Available templates: ");
                Console.WriteLine(string.Join(", ", templates.Keys));
                Console.Write("Enter the name of the template you wish to create: ");
                template = Console.ReadLine();
            }

            if (!templates.TryGetValue(template, out var resName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"The template '{template}' does not exists.");
                Console.ResetColor();
                return;
            }

            var filename = template + ".csx";
            if (File.Exists(filename))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"The file '{filename} already exists.");
                Console.ResetColor();
                return;
            }

            using (var res = typeof(Program).Assembly.GetManifestResourceStream(resName))
            using (var file = File.OpenWrite(filename))
            {
                res.CopyTo(file);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"The file '{filename}' was created.");
            Console.ResetColor();
        }
    }
}
