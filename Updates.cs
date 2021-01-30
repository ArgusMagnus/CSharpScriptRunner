using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System;

namespace CSharpScriptRunner
{
    sealed class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string Version { get; set; }

        [JsonPropertyName("html_url")]
        public string Url { get; set; }
    }

    static class Updates
    {
        const string RequestUri = "https://api.github.com/repos/ArgusMagnus/CSharpScriptRunner/releases/latest";
        public const string PowershellCommand =
            @"$dir=md ""$Env:Temp\{$(New-Guid)}""; $bkp=$ProgressPreference; $ProgressPreference='SilentlyContinue'; Write-Host 'Downloading...'; Invoke-WebRequest (Invoke-RestMethod -Uri '" + RequestUri +
            @"' | select -Expand assets | select-string -InputObject {$_.browser_download_url} -Pattern '-win\.zip$' | Select -Expand Line -First 1) -OutFile ""$dir\CSX.zip""; Write-Host 'Expanding archive...'; Expand-Archive -Path ""$dir\CSX.zip"" -DestinationPath ""$dir""; & ""$dir\win\x64\CSharpScriptRunner.exe"" 'install'; Remove-Item $dir -Recurse; $ProgressPreference=$bkp; Write-Host 'Done'";
        
        public static async Task<ReleaseInfo> CheckForNewRelease()
        {
            

            if (!BuildInfo.ReleaseTag.StartsWith('v') || !Version.TryParse(BuildInfo.ReleaseTag.Substring(1), out var version))
                return default;

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.DefaultRequestHeaders.Add("User-Agent", "CSharpScriptRunner");
                HttpResponseMessage response;
                try { response = await httpClient.GetAsync(RequestUri); }
                catch { return default; }
                if (!response.IsSuccessStatusCode)
                    return default;

                var release = JsonSerializer.Deserialize<ReleaseInfo>(await response.Content.ReadAsByteArrayAsync());
                if (string.IsNullOrEmpty(release.Version) || string.IsNullOrEmpty(release.Url) || !release.Version.StartsWith('v') || !Version.TryParse(release.Version.Substring(1), out var releaseVersion))
                    return default;

                if (version < releaseVersion)
                    return release;
            }
            return default;
        }
    }
}