using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        sealed class ReleaseInfo
        {
            [JsonPropertyName("tag_name")]
            public string Version { get; set; }

            [JsonPropertyName("html_url")]
            public string Url { get; set; }

            [JsonPropertyName("assets")]
            public List<Asset> Assets { get; set; }

            public sealed class Asset
            {
                [JsonPropertyName("browser_download_url")]
                public string DownloadUrl { get; set; }
            }
        }
        
        static async Task Update()
        {
            const string UpdateMutexName = "C371A9A2-6CBE-43DE-B834-AC8F73E47705";

            using var mutex = new Mutex(true, UpdateMutexName);
            if (!mutex.WaitOne(0, true))
            {
                WriteLine($"{Verbs.Update} command is already running", ConsoleColor.Red);
                return;
            }

            WriteLine("Checking for new version...");

            using var httpClient = new HttpClient();
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpClient.DefaultRequestHeaders.Add("User-Agent", nameof(CSharpScriptRunner));
            var response = await httpClient.GetAsync(UpdateRequestUri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var release = JsonSerializer.Deserialize<ReleaseInfo>(await response.Content.ReadAsByteArrayAsync());
            var dstDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(CSharpScriptRunner), release.Version);
            if (release.Version == BuildInfo.ReleaseTag)
            {
                foreach (var dir in Directory.EnumerateDirectories(Path.GetDirectoryName(dstDir)))
                {
                    if (string.Equals(dir, dstDir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { Directory.Delete(dir, true); }
                    catch { continue; }
                }
                WriteLine($"${nameof(CSharpScriptRunner)} is up-to-date ({BuildInfo.ReleaseTag})", ConsoleColor.Green);
                return;
            }

            var tmpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(CSharpScriptRunner), Guid.NewGuid().ToString());
            if (Directory.Exists(dstDir))
            {
                WriteLine($"Newest version ({release.Version}) is already isntalled", ConsoleColor.Green);
                return;
            }

            WriteLine($"New version ({release.Version}) available, downloading...");

            var platform = BuildInfo.RuntimeIdentifier.Split('-').First();
            var downloadUrl = release.Assets.Select(x => x.DownloadUrl).FirstOrDefault(x => x.EndsWith($"-{platform}.zip", StringComparison.OrdinalIgnoreCase));
            if (downloadUrl == null)
            {
                WriteLine("Operation failed", ConsoleColor.Red);
                return;
            }

            httpClient.DefaultRequestHeaders.Accept.Clear();
            response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            using (var ms = new MemoryStream((int)response.Content.Headers.ContentLength))
            {
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var buffer = new byte[(int)response.Content.Headers.ContentLength / 100 + 1];
                    var progress = string.Empty;
                    while (true)
                    {
                        var length = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (length < 1)
                            break;
                        await ms.WriteAsync(buffer, 0, length);
                        var newProgress = $"{(double)ms.Length / ms.Capacity,4:P0}";
                        if (newProgress != progress)
                        {
                            progress = newProgress;
                            try { Console.CursorLeft = 0; }
                            catch { }
                            Console.Write(progress);
                        }
                    }
                    Console.WriteLine();
                }

                ms.Seek(0, SeekOrigin.Begin);
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        Console.WriteLine($"Extracting {entry.FullName} ...");
                        var parts = entry.FullName.Split('/');
                        parts = parts.Take(1).Append("bin").Concat(parts.Skip(1)).Prepend(tmpDir).ToArray();
                        var dstPath = Path.Combine(parts);
                        Directory.CreateDirectory(Path.GetDirectoryName(dstPath));
                        using var src = entry.Open();
                        using var dst = new FileStream(dstPath, FileMode.Create);
                        await src.CopyToAsync(dst);
                    }
                }
            }

            Directory.Move(tmpDir, dstDir);

            var installed = false;
            var path = Path.Combine(dstDir, BuildInfo.RuntimeIdentifier.Split('-').Last(), "bin", Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName));
            if (File.Exists(path))
            {
                using var process = Process.Start(new ProcessStartInfo { FileName = path, Arguments = "install inplace", UseShellExecute = false });
                process.WaitForExit();
                installed = process.ExitCode == (int)ErrorCodes.OK;
            }

            if (!installed)
                Directory.Move(dstDir, tmpDir);
        }
    }
}