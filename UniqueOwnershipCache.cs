using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LadderLover.Poeladder;
using Newtonsoft.Json;

namespace LadderLover;

public class UniqueOwnershipCache
{
    private readonly string _cacheFilePath;
    private Dictionary<string, UniqueFilterEntry> _entriesByName = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private DateTime? _lastFileWriteUtc;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(2);

    public UniqueOwnershipCache(string configDirectory)
    {
        _cacheFilePath = Path.Combine(configDirectory, "ownership_cache.json");
    }

    public DateTime LastRefreshUtc => _lastRefreshUtc;
    public int Count => _entriesByName.Count;

    public bool IsReady => _entriesByName.Count > 0;

    public bool NeedsRefresh => !IsReady || (DateTime.UtcNow - _lastRefreshUtc) >= RefreshInterval;

    public void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var payload = JsonConvert.DeserializeObject<CachePayload>(json);
            if (payload == null)
            {
                return;
            }

            _entriesByName = (payload.Entries ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .GroupBy(e => NormalizeName(e.Name))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _lastRefreshUtc = payload.RefreshedUtc;
            _lastFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
        }
        catch
        {
        }
    }

    public bool TryGetOwnership(string uniqueName, out bool owned)
    {
        owned = false;
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            return false;
        }

        if (_entriesByName.TryGetValue(NormalizeName(uniqueName), out var entry))
        {
            owned = entry.Owned;
            return true;
        }

        return false;
    }

    public async Task RefreshFromApi(string username, string ladderIdentifier)
    {
        var entries = await PoeladderApi.FetchUniqueFilters(username, ladderIdentifier).ConfigureAwait(false);
        ApplyEntries(entries);
        SaveToDisk(entries);
    }

    private void ApplyEntries(List<UniqueFilterEntry> entries)
    {
        _entriesByName = (entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .GroupBy(e => NormalizeName(e.Name))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _lastRefreshUtc = DateTime.UtcNow;
    }

    private void SaveToDisk(List<UniqueFilterEntry> entries)
    {
        try
        {
            var payload = new CachePayload
            {
                RefreshedUtc = _lastRefreshUtc,
                Entries = entries
            };
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            File.WriteAllText(_cacheFilePath, json);
            _lastFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
        }
        catch
        {
        }
    }

    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name.Replace('\x2019', '\'').Trim();
    }

    private class CachePayload
    {
        [JsonProperty("refreshedUtc")] public DateTime RefreshedUtc { get; set; }
        [JsonProperty("entries")] public List<UniqueFilterEntry> Entries { get; set; }
    }
}