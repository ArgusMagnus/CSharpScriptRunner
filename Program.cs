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

namespace CSharpScriptRunner
{
    public sealed class ScriptGlobals
    {
        public IReadOnlyDictionary<string, string> Args { get; }

        internal ScriptGlobals(IDictionary<string, string> args) => Args = new ReadOnlyDictionary<string, string>(args);
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args == null || args.Length == 0)
                Install();
            else
                RunScript(args);
        }

        static void Install()
        {
            var oldFilename = Process.GetCurrentProcess().MainModule.FileName;
            var oldDir = Path.GetDirectoryName(oldFilename);
            var newDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(CSharpScriptRunner));
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, true);
            Directory.CreateDirectory(newDir);
            var newFilename = Path.Combine(newDir, Path.GetFileName(oldFilename));

            foreach (var file in Directory.EnumerateFiles(oldDir, "*", new EnumerationOptions { RecurseSubdirectories = true }))
            {
                var dst = Path.Combine(newDir, file.Substring(oldDir.Length + 1));
                var dir = Path.GetDirectoryName(dst);
                Directory.CreateDirectory(dir);
                Console.WriteLine($"Copying {dst} ...");
                File.Copy(file, dst, true);
            }

            var filetype = ".csx";

            using (var regKeyExt = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{filetype}", true))
            {
                filetype = filetype.Substring(1) + "_auto_file";
                regKeyExt.SetValue(string.Empty, filetype);
            }
            using (var regKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{filetype}\shell\execute", true))
            using (var regKeyCommand = regKey.CreateSubKey("command", true))
            {
                regKey.SetValue(string.Empty, "C# Skript ausführen");
                regKeyCommand.SetValue(string.Empty, $"\"{newFilename}\" \"%1\"");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Installation was successful.");
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

        static IDictionary<string, string> ParseArguments(IEnumerable<string> args)
        {
            var arguments = new Dictionary<string, string>();
            arguments["ThisScriptPath"] = args.First();
            string key = null;
            foreach (var value in args.Skip(1))
            {
                if (value.StartsWith("-"))
                {
                    if (key != null)
                        arguments[key] = true.ToString();
                    key = value.Substring(1);
                }
                else
                {
                    if (key == null)
                        throw new ArgumentNullException(value, "The parameter is missing its name.");
                    arguments[key] = value;
                }
            }
            if (key != null)
                arguments[key] = true.ToString();
            return arguments;
        }

        static bool TryBuild(string scriptFile, string assemblyFile, string hashFile, string configFile, byte[] scriptHash, out Config config)
        {
            config = null;
            var lines = File.ReadAllLines(scriptFile);
            Console.WriteLine($"Compiling script {scriptFile}...");
            var compilation = CSharpScript.Create(string.Join(Environment.NewLine, lines), ScriptOptions.Default.WithEmitDebugInformation(true), typeof(ScriptGlobals)).GetCompilation();
            var result = compilation.Emit(assemblyFile);
            PrintCompilationDiagnostics(result, lines);
            if (!result.Success)
                return false;

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

        static void RunScript(string[] args)
        {
            var filename = args[0];
            var cacheFileBase = GetCacheFilenameBase(filename);
            var assemblyFile = cacheFileBase + ".dll";
            var hashFile = cacheFileBase + ".hash";
            var configFile = cacheFileBase + ".json";
            var scriptHash = GetFileHash(filename);

            if (!TryGetCache(assemblyFile, hashFile, configFile, scriptHash, out var config))
            {
                if (!TryBuild(filename, assemblyFile, hashFile, configFile, scriptHash, out config))
                    return;
            }

            var assembly = Assembly.LoadFile(assemblyFile);
            var type = assembly.GetType(config.Type);
            if (type == null)
                return;
            var entryPoint = type.GetMethod(config.Method, BindingFlags.Static | BindingFlags.Public);
            if (entryPoint == null)
                return;

            Environment.CurrentDirectory = Path.GetDirectoryName(filename);
            var task = (Task<object>)entryPoint.Invoke(null, new object[] { new object[] { new ScriptGlobals(ParseArguments(args)), null } });
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
    }
}
