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
        public static async Task<ReleaseInfo> CheckForNewRelease()
        {
            const string RequestUri = "https://api.github.com/repos/ArgusMagnus/CSharpScriptRunner/releases/latest";

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