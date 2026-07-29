using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace LadderLover.Poeladder;

public static class PoeladderApi
{
    private const string BaseUrl = "https://poeladder.com/api/v1";

    public static async Task<List<CurioLeague>> FetchCurioLeagues(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(15);
        var url = $"{BaseUrl}/users/{Uri.EscapeDataString(username)}/curio";
        var body = await client.GetStringAsync(url).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<List<CurioLeague>>(body) ?? [];
    }

    public static string ExtractLadderIdentifier(CurioLeague league)
    {
        var refUrl = league.RefUrl;
        if (string.IsNullOrEmpty(refUrl))
        {
            return null;
        }

        var queryIndex = refUrl.IndexOf('?');
        if (queryIndex < 0 || queryIndex + 1 >= refUrl.Length)
        {
            return null;
        }

        var query = refUrl.Substring(queryIndex + 1);
        foreach (var pair in query.Split('&'))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var key = pair.Substring(0, eqIndex);
            if (key.Equals("ladderIdentifier", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair.Substring(eqIndex + 1));
            }
        }

        return null;
    }

    public static async Task<List<UniqueFilterEntry>> FetchUniqueFilters(string username, string ladderIdentifier)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(ladderIdentifier))
        {
            return [];
        }

        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        var url = $"{BaseUrl}/users/{Uri.EscapeDataString(username)}/leagues/{Uri.EscapeDataString(ladderIdentifier)}/filters";
        var body = await client.GetStringAsync(url).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<List<UniqueFilterEntry>>(body) ?? [];
    }
}

public class CurioLeague
{
    [JsonProperty("poeLadderLeagueName")] public string PoeLadderLeagueName { get; set; }
    [JsonProperty("gggLeagueName")] public string GggLeagueName { get; set; }
    [JsonProperty("baseLeagueName")] public string BaseLeagueName { get; set; }
    [JsonProperty("baseLeagueVersion")] public string BaseLeagueVersion { get; set; }
    [JsonProperty("baseLeagueStart")] public string BaseLeagueStart { get; set; }
    [JsonProperty("baseLeagueEnd")] public string BaseLeagueEnd { get; set; }
    [JsonProperty("$ref")] public string RefUrl { get; set; }
}

public class UniqueFilterEntry
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("grouping")] public string Grouping { get; set; }
    [JsonProperty("base")] public string Base { get; set; }
    [JsonProperty("category")] public string Category { get; set; }
    [JsonProperty("tier")] public int? Tier { get; set; }
    [JsonProperty("league")] public string League { get; set; }
    [JsonProperty("owned")] public bool Owned { get; set; }
    [JsonProperty("altOwned")] public bool AltOwned { get; set; }
    [JsonProperty("retired")] public bool Retired { get; set; }
}