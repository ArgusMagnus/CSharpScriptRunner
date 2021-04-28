using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static async Task Install(bool installInPlace)
        {
            var thisPath = Process.GetCurrentProcess().MainModule.FileName;
            var newPath = thisPath;
            var filename = Path.GetFileName(thisPath);
            var valid = false;

            if (installInPlace)
            {
                var runtimesDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisPath)));
                foreach (var dir in Directory.EnumerateDirectories(runtimesDir))
                {
                    if (!File.Exists(Path.Combine(dir, "bin", filename)))
                        continue;

                    valid = true;

                    File.WriteAllText(Path.Combine(dir, Path.ChangeExtension(filename, ".cmd")), $@"@echo off & ""%~dp0bin\{filename}"" %*");
                    File.WriteAllText(Path.Combine(dir, $"{CmdAlias}.cmd"), $@"@echo off & ""%~dp0bin\{filename}"" %*");
                }
            }
            else
            {
                var oldDir = Path.GetDirectoryName(thisPath);
                var runtimeDir = Path.GetFileName(oldDir);
                oldDir = Path.GetDirectoryName(oldDir);
                var newDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(CSharpScriptRunner), BuildInfo.ReleaseTag);
                newPath = Path.Combine(newDir, runtimeDir, "bin", filename);
                if (Directory.Exists(newDir))
                {
                    WriteLine($"Removing previous installation...");
                    try
                    {
                        if (File.Exists(newPath))
                            File.Delete(newPath);
                        Directory.Delete(newDir, true);
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"Operation failed with {ex.GetType().Name}: {ex.Message}", ConsoleColor.Red);
                        Console.WriteLine();
                        var count = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length - 1;
                        WriteLine($"Please close all running instances ({count}) and try again:", ConsoleColor.Red);
                        return;
                    }
                }
                Directory.CreateDirectory(newDir);

                foreach (var dir in Directory.EnumerateDirectories(oldDir))
                {
                    if (!File.Exists(Path.Combine(dir, filename)))
                        continue;

                    valid = true;

                    var dstDir = Path.Combine(newDir, Path.GetFileName(dir));
                    await Task.WhenAll(Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true }).Select(file => Task.Run(() =>
                    {
                        var dst = Path.Combine(dstDir, "bin", file.Substring(dir.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        Console.WriteLine($"Copying {dst} ...");
                        File.Copy(file, dst, true);
                    })));

                    File.WriteAllText(Path.Combine(dstDir, Path.ChangeExtension(filename, ".cmd")), $@"@echo off & ""%~dp0bin\{filename}"" %*");
                    File.WriteAllText(Path.Combine(dstDir, $"{CmdAlias}.cmd"), $@"@echo off & ""%~dp0bin\{filename}"" %*");
                }
            }

            if (!valid)
            {
                WriteLine("Installation failed", ConsoleColor.Red);
                return;
            }

            var envPath = (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)
                ?.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.Contains(nameof(CSharpScriptRunner))) ?? Array.Empty<string>())
                .Append(Path.GetDirectoryName(Path.GetDirectoryName(newPath)));
            Environment.SetEnvironmentVariable("Path", string.Join(';', envPath), EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(nameof(CSharpScriptRunner) + "RuntimesDir", Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(newPath))), EnvironmentVariableTarget.User);

            var filetype = ".csx";

            // https://docs.microsoft.com/en-us/windows/win32/shell/context-menu-handlers
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

            using (var regKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\*\shell\CSharpScriptRunner", true))
            using (var regKeyCommand = regKey.CreateSubKey("command", true))
            {
                regKey.SetValue(string.Empty, "C# Skript ausführen");
                regKey.SetValue("AppliesTo", "System.FileExtension:\"csx\"", RegistryValueKind.String);
                regKey.SetValue("Position", "Top", RegistryValueKind.String);
                regKeyCommand.SetValue(string.Empty, $"\"{newPath}\" \"%1\"");
            }

            WriteLine("Installation was successful.", ConsoleColor.Green);
            await ClearCache();
        }
    }
}