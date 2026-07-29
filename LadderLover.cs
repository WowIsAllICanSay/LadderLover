using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace LadderLover;

[SupportedOSPlatform("windows")]
public class LadderLover : BaseSettingsPlugin<LadderLoverSettings>
{
    private UniqueOwnershipCache _ownershipCache;
    private bool _cacheRefreshInProgress;
    private DateTime _lastCacheCheckUtc = DateTime.MinValue;

    private readonly Dictionary<uint, GroundItemOwnership> _resolvedItems = new();
    private readonly HashSet<uint> _alertedItems = new();
    private Dictionary<string, List<string>> _uniqueArtMapping;
    private bool _uniqueArtMappingLoaded;

    private DateTime _lastDebugSummaryUtc = DateTime.MinValue;
    private int _debugUniqueCount;
    private int _debugUnidentifiedCount;
    private int _debugNameFoundCount;
    private int _debugNameMissCount;

    private string _debugLogPath;
    private string _soundFilePath;
    private const long MaxDebugLogBytes = 512 * 1024;
    private readonly object _debugLogLock = new();

    private bool Debug => Settings.DebugSettings.EnableDebugLogging;

    private void DebugLog(string message)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (_debugLogLock)
        {
            try
            {
                if (File.Exists(_debugLogPath))
                {
                    var fi = new FileInfo(_debugLogPath);
                    if (fi.Length > MaxDebugLogBytes)
                    {
                        File.WriteAllText(_debugLogPath, "");
                    }
                }
                File.AppendAllText(_debugLogPath, line);
            }
            catch
            {
            }
        }
        LogMessage(message, 5);
    }

    private void DebugLogError(string message)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [ERROR] {message}{Environment.NewLine}";
        lock (_debugLogLock)
        {
            try
            {
                if (File.Exists(_debugLogPath))
                {
                    var fi = new FileInfo(_debugLogPath);
                    if (fi.Length > MaxDebugLogBytes)
                    {
                        File.WriteAllText(_debugLogPath, "");
                    }
                }
                File.AppendAllText(_debugLogPath, line);
            }
            catch
            {
            }
        }
        LogError(message);
    }

    private const string AlertWavName = "alert.wav";

    public override bool Initialise()
    {
        Name = "LadderLover";
        _ownershipCache = new UniqueOwnershipCache(ConfigDirectory);
        _ownershipCache.LoadFromDisk();
        _debugLogPath = Path.Combine(ConfigDirectory, "debug.log");

        _soundFilePath = Path.Combine(ConfigDirectory, AlertWavName);

        Settings.SaveUsernameHandler = OnSaveUsername;
        Settings.LeagueSelectedHandler = OnLeagueSelected;
        Settings.SoundSettings.TestSound.OnPressed += TestSound;

        UpdateCacheStatusDisplay();
        if (Debug) DebugLog($"LadderLover init. Cache ready={_ownershipCache.IsReady}, count={_ownershipCache.Count}, configDir={ConfigDirectory}");

        if (!string.IsNullOrWhiteSpace(Settings.Username))
        {
            if (Debug) DebugLog($"Init: re-fetching curio leagues for saved username \"{Settings.Username}\"");
            OnSaveUsername(Settings.Username);
        }

        return base.Initialise();
    }

    public override void AreaChange(AreaInstance area)
    {
        _resolvedItems.Clear();
        _alertedItems.Clear();
        _debugUniqueCount = 0;
        _debugUnidentifiedCount = 0;
        _debugNameFoundCount = 0;
        _debugNameMissCount = 0;
        _uniqueArtMapping = null;
        _uniqueArtMappingLoaded = false;
        _gameFilesAttempted = false;
    }

    public override void Render()
    {
        if (!Settings.Enable)
        {
            return;
        }

        MaybeRefreshCache();

        DrawGroundItemLabels();
    }

    private void OnSaveUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            Settings.FetchStatus = "Error: username is empty.";
            Settings.AvailableLeagues.Clear();
            Settings.SelectedLeagueIdentifier = "";
            return;
        }

        Settings.IsFetching = true;
        Settings.FetchStatus = "";
        Settings.AvailableLeagues.Clear();

        var user = username.Trim();
        if (Debug) DebugLog($"Fetching curio leagues for {user}");
        Task.Run(async () =>
        {
            try
            {
                var leagues = await Poeladder.PoeladderApi.FetchCurioLeagues(user);
                Settings.AvailableLeagues = leagues;
                Settings.IsFetching = false;
                Settings.FetchStatus = leagues.Count > 0
                    ? $"Loaded {leagues.Count} league(s) for {user}."
                    : $"No curio leagues found for {user}.";
                if (Debug) DebugLog($"Curio fetch done: {leagues.Count} leagues");
            }
            catch (Exception ex)
            {
                Settings.IsFetching = false;
                Settings.FetchStatus = $"Error fetching leagues: {ex.Message}";
                if (Debug) DebugLogError($"Curio fetch failed: {ex}");
            }
        });
    }

    private void OnLeagueSelected(string ladderIdentifier)
    {
        if (string.IsNullOrWhiteSpace(ladderIdentifier))
        {
            return;
        }

        _resolvedItems.Clear();
        if (Debug) DebugLog($"League selected: {ladderIdentifier} - forcing cache refresh");
        TryRefreshCache(force: true);
    }

    private void UpdateCacheStatusDisplay()
    {
        Settings.CachedUniqueCount = _ownershipCache.Count;
        if (_ownershipCache.IsReady)
        {
            var age = DateTime.UtcNow - _ownershipCache.LastRefreshUtc;
            Settings.CacheStatus = $"last refreshed {(int)age.TotalHours}h {age.Minutes}m ago";
        }
        else
        {
            Settings.CacheStatus = "not loaded yet";
        }
    }

    private void MaybeRefreshCache()
    {
        if ((DateTime.UtcNow - _lastCacheCheckUtc).TotalSeconds < 30)
        {
            return;
        }

        _lastCacheCheckUtc = DateTime.UtcNow;
        if (_ownershipCache.NeedsRefresh)
        {
            if (Debug) DebugLog("Ownership cache needs refresh, triggering fetch");
            TryRefreshCache(force: false);
        }
        UpdateCacheStatusDisplay();
    }

    private void TryRefreshCache(bool force)
    {
        if (_cacheRefreshInProgress)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Settings.Username) || string.IsNullOrWhiteSpace(Settings.SelectedLeagueIdentifier))
        {
            return;
        }

        if (!force && !_ownershipCache.NeedsRefresh)
        {
            return;
        }

        _cacheRefreshInProgress = true;
        var user = Settings.Username.Trim();
        var league = Settings.SelectedLeagueIdentifier;

        if (Debug) DebugLog($"Refreshing ownership cache from API (user={user}, league={league})");
        Task.Run(async () =>
        {
            try
            {
                await _ownershipCache.RefreshFromApi(user, league);
                Settings.CacheStatus = $"refreshed {DateTime.UtcNow:h:mm:ss tt} UTC";
                if (Debug) DebugLog($"Cache refreshed: {_ownershipCache.Count} uniques");
            }
            catch (Exception ex)
            {
                Settings.CacheStatus = $"Error refreshing cache: {ex.Message}";
                if (Debug) DebugLogError($"Cache refresh failed: {ex}");
            }
            finally
            {
                _cacheRefreshInProgress = false;
                UpdateCacheStatusDisplay();
            }
        });
    }

    public override void EntityRemoved(Entity entity)
    {
        if (entity != null)
        {
            _resolvedItems.Remove(entity.Id);
        }
    }

    private static string NormalizeArtPath(string artPath)
    {
        return string.IsNullOrWhiteSpace(artPath) ? null : artPath.Replace('\\', '/').Trim();
    }

    private const string EmbeddedArtMappingName = "uniqueArtMapping.default.json";

    private bool _gameFilesAttempted;

    private void EnsureUniqueArtMapping()
    {
        if (_uniqueArtMappingLoaded && _uniqueArtMapping != null && _uniqueArtMapping.Count > 0)
        {
            return;
        }

        try
        {
            if (!_gameFilesAttempted)
            {
                _gameFilesAttempted = true;
                var gameMapping = TryBuildGameFileArtMapping();

                if (gameMapping != null && gameMapping.Count > 0)
                {
                    _uniqueArtMapping = gameMapping;
                    _uniqueArtMappingLoaded = true;
                    if (Debug) DebugLog($"Art mapping loaded from game files: {_uniqueArtMapping.Count} art paths");
                    return;
                }
            }

            var embeddedMapping = LoadEmbeddedArtMapping();
            if (embeddedMapping != null && embeddedMapping.Count > 0)
            {
                _uniqueArtMapping = embeddedMapping;
                _uniqueArtMappingLoaded = true;
                if (Debug) DebugLog($"Art mapping loaded from embedded fallback: {_uniqueArtMapping.Count} art paths");
                return;
            }

            if (Debug) DebugLog("Art mapping: no entries from game files or embedded fallback");
        }
        catch (Exception ex)
        {
            if (Debug) DebugLogError($"Failed to build unique art mapping: {ex}");
        }

        _uniqueArtMapping = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, List<string>> TryBuildGameFileArtMapping()
    {
        try
        {
            var files = GameController.Files;
            var uidCount = files.UniqueItemDescriptions.EntriesList.Count;
            var iviCount = files.ItemVisualIdentities.EntriesList.Count;

            if (uidCount == 0 || iviCount == 0)
            {
                try
                {
                    files.GetType().GetMethod("LoadFiles", Type.EmptyTypes)?.Invoke(files, null);
                    uidCount = files.UniqueItemDescriptions.EntriesList.Count;
                    iviCount = files.ItemVisualIdentities.EntriesList.Count;
                }
                catch (Exception ex)
                {
                    if (Debug) DebugLogError($"LoadFiles reflection call failed: {ex.Message}");
                }
            }

            if (uidCount == 0 || iviCount == 0)
            {
                if (Debug) DebugLog($"Art mapping: game files empty ({uidCount} UID, {iviCount} IVI)");
                return null;
            }

            return files.ItemVisualIdentities.EntriesList
                .Where(x => x.ArtPath != null)
                .GroupJoin(files.UniqueItemDescriptions.EntriesList.Where(x => x.ItemVisualIdentity != null),
                    x => x,
                    x => x.ItemVisualIdentity, (ivi, descriptions) => (ivi.ArtPath, descriptions: descriptions.ToList()))
                .GroupBy(x => x.ArtPath, x => x.descriptions)
                .Select(x => (x.Key, Names: x
                    .SelectMany(items => items)
                    .Select(item => item.UniqueName?.Text?.Replace('\x2019', '\''))
                    .Where(name => name != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
                .Where(x => x.Names.Any())
                .ToDictionary(x => NormalizeArtPath(x.Key), x => x.Names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            if (Debug) DebugLogError($"Game file art mapping failed: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, List<string>> LoadEmbeddedArtMapping()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedArtMappingName);
            if (stream == null)
            {
                if (Debug) DebugLogError($"Embedded resource {EmbeddedArtMappingName} not found");
                return null;
            }

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var raw = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(content);
            if (raw == null || raw.Count == 0)
            {
                return null;
            }

            var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in raw)
            {
                var normalizedKey = NormalizeArtPath(key);
                if (normalizedKey == null)
                {
                    continue;
                }

                var names = (value ?? [])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Replace('\x2019', '\'').Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (names.Count > 0)
                {
                    normalized[normalizedKey] = names;
                }
            }

            return normalized.Count > 0 ? normalized : null;
        }
        catch (Exception ex)
        {
            if (Debug) DebugLogError($"Failed to load embedded art mapping: {ex.Message}");
            return null;
        }
    }

    public override Job Tick()
    {
        if (!Settings.Enable)
        {
            return null;
        }

        if (!_ownershipCache.IsReady)
        {
            if (Debug && (DateTime.UtcNow - _lastDebugSummaryUtc).TotalSeconds > 5)
            {
                _lastDebugSummaryUtc = DateTime.UtcNow;
                DebugLog("Tick: ownership cache not ready, skipping ground scan");
            }
            return null;
        }

        var labelsOnGround = GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
        if (labelsOnGround == null || labelsOnGround.Count == 0)
        {
            return null;
        }

        if (Debug)
        {
            _debugUniqueCount = 0;
            _debugUnidentifiedCount = 0;
            _debugNameFoundCount = 0;
            _debugNameMissCount = 0;
        }

        foreach (var labelOnGround in labelsOnGround)
        {
            var itemEntity = labelOnGround.ItemOnGround;
            if (itemEntity == null || !itemEntity.IsValid)
            {
                continue;
            }

            if (_resolvedItems.ContainsKey(itemEntity.Id))
            {
                continue;
            }

            if (!itemEntity.TryGetComponent<WorldItem>(out var worldItem))
            {
                continue;
            }

            var groundItem = worldItem.ItemEntity;
            if (groundItem == null || !groundItem.IsValid)
            {
                continue;
            }

            if (!groundItem.TryGetComponent<Mods>(out var mods))
            {
                continue;
            }

            if (mods.ItemRarity != ItemRarity.Unique)
            {
                continue;
            }

            if (Debug) _debugUniqueCount++;

            var uniqueName = mods.UniqueName?.Replace('\x2019', '\'');
            if (string.IsNullOrWhiteSpace(uniqueName))
            {
                EnsureUniqueArtMapping();
                var artPath = NormalizeArtPath(groundItem.GetComponent<RenderItem>()?.ResourcePath);
                var candidates = artPath != null && _uniqueArtMapping.TryGetValue(artPath, out var names)
                    ? names
                    : null;

                if (candidates is { Count: > 0 })
                {
                    candidates = candidates.Where(c => !c.StartsWith("Replica ", StringComparison.Ordinal)).ToList();
                }

                if (candidates is { Count: > 0 })
                {
                    var resolvedViaArt = false;
                    foreach (var candidate in candidates)
                    {
                        if (_ownershipCache.TryGetOwnership(candidate, out _))
                        {
                            _resolvedItems[itemEntity.Id] = new GroundItemOwnership(itemEntity.Id, candidate, false, labelOnGround, OwnershipState.Resolved);
                            TryAlertNotOwned(itemEntity.Id, candidate);
                            if (Debug)
                            {
                                _debugUnidentifiedCount++;
                                _debugNameFoundCount++;
                                if (_debugUnidentifiedCount <= 3)
                                {
                                    DebugLog($"Unidentified unique resolved via art to \"{candidate}\" (in cache = not owned, art={artPath})");
                                }
                            }
                            resolvedViaArt = true;
                            break;
                        }
                    }

                    if (!resolvedViaArt)
                    {
                        _resolvedItems[itemEntity.Id] = new GroundItemOwnership(itemEntity.Id, candidates[0], true, labelOnGround, OwnershipState.Resolved);
                        if (Debug)
                        {
                            _debugUnidentifiedCount++;
                            _debugNameMissCount++;
                            if (_debugUnidentifiedCount <= 3)
                            {
                                DebugLog($"Unidentified unique resolved via art to \"{candidates[0]}\" (NOT in cache = owned, art={artPath}, {candidates.Count} candidates)");
                            }
                        }
                    }
                }
                else
                {
                    _resolvedItems[itemEntity.Id] = new GroundItemOwnership(itemEntity.Id, null, false, labelOnGround, OwnershipState.Unidentified);
                    if (Debug)
                    {
                        _debugUnidentifiedCount++;
                        if (_debugUnidentifiedCount <= 3)
                        {
                            DebugLog($"Unidentified unique with no art mapping (id={itemEntity.Id}, art={artPath}) - cannot resolve");
                        }
                    }
                }

                continue;
            }

            if (_ownershipCache.TryGetOwnership(uniqueName, out _))
            {
                _resolvedItems[itemEntity.Id] = new GroundItemOwnership(itemEntity.Id, uniqueName, false, labelOnGround, OwnershipState.Resolved);
                TryAlertNotOwned(itemEntity.Id, uniqueName);
                if (Debug)
                {
                    _debugNameFoundCount++;
                    if (_debugNameFoundCount <= 3)
                    {
                        DebugLog($"Unique in cache (not owned): \"{uniqueName}\"");
                    }
                }
            }
            else
            {
                _resolvedItems[itemEntity.Id] = new GroundItemOwnership(itemEntity.Id, uniqueName, true, labelOnGround, OwnershipState.Resolved);
                if (Debug)
                {
                    _debugNameMissCount++;
                    if (_debugNameMissCount <= 3)
                    {
                        DebugLog($"Unique NOT in cache (assumed owned): \"{uniqueName}\" (id={itemEntity.Id})");
                    }
                }
            }
        }

        var staleKeys = _resolvedItems.Values
            .Where(x => x.Label?.Label == null || !x.Label.Label.IsValid || x.Label.ItemOnGround == null || !x.Label.ItemOnGround.IsValid)
            .Select(x => x.EntityId)
            .ToList();
        foreach (var key in staleKeys)
        {
            _resolvedItems.Remove(key);
        }

        if (Debug && (DateTime.UtcNow - _lastDebugSummaryUtc).TotalSeconds > 2)
        {
            _lastDebugSummaryUtc = DateTime.UtcNow;
            var ownedCount = _resolvedItems.Values.Count(x => x.State == OwnershipState.Resolved && x.Owned);
            var notOwnedCount = _resolvedItems.Values.Count(x => x.State == OwnershipState.Resolved && !x.Owned);
            var unidentifiedCount = _resolvedItems.Values.Count(x => x.State == OwnershipState.Unidentified);
            DebugLog(
                $"Tick scan: {_debugUniqueCount} uniques on ground, " +
                $"{ownedCount} owned (not in cache), " +
                $"{notOwnedCount} not-owned (in cache), " +
                $"{unidentifiedCount} unidentified, " +
                $"cache has {_ownershipCache.Count} entries");
        }

        return null;
    }

    private void TryAlertNotOwned(uint entityId, string uniqueName)
    {
        if (!Settings.SoundSettings.EnableAlertSound)
        {
            return;
        }

        if (!_alertedItems.Add(entityId))
        {
            return;
        }

        PlayAlertSound();
        if (Debug) DebugLog($"Alert sound played for not-owned unique: \"{uniqueName}\" (id={entityId})");
    }

    private void TestSound()
    {
        PlayAlertSound();
        if (Debug) DebugLog("Test sound played");
    }

    private void PlayAlertSound()
    {
        try
        {
            if (File.Exists(_soundFilePath))
            {
                GameController.SoundController.PlaySound(_soundFilePath, Settings.SoundSettings.Volume.Value);
            }
            else if (Debug) DebugLogError($"Sound file not found at {_soundFilePath}. Place a wav file named {AlertWavName} in your config directory.");
        }
        catch (Exception ex)
        {
            if (Debug) DebugLogError($"Failed to play alert sound: {ex.Message}");
        }
    }

    private void DrawGroundItemLabels()
    {
        if (_resolvedItems.Count == 0)
        {
            return;
        }

        if (!Settings.LabelSettings.ShowNotOwnedLabel && !Settings.LabelSettings.ShowOwnedLabel)
        {
            return;
        }

        var ingameUi = GameController.IngameState.IngameUi;
        var leftPanelRect = ingameUi.OpenLeftPanel.Address != 0
            ? ingameUi.OpenLeftPanel.GetClientRectCache
            : RectangleF.Empty;
        var rightPanelRect = ingameUi.OpenRightPanel.Address != 0
            ? ingameUi.OpenRightPanel.GetClientRectCache
            : RectangleF.Empty;

        ImGui.Begin("ladderlover_labels",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
        var drawList = ImGui.GetBackgroundDrawList();

        var drawnCount = 0;
        foreach (var entry in _resolvedItems.Values)
        {
            if (entry.State != OwnershipState.Resolved)
            {
                continue;
            }

            var label = entry.Label;
            if (label?.Label == null || !label.Label.IsValid || !label.Label.IsVisible)
            {
                continue;
            }

            var box = label.Label.GetClientRectCache;
            if (box.Intersects(leftPanelRect) || box.Intersects(rightPanelRect))
            {
                continue;
            }

            bool shouldDraw;
            string text;
            Color textColor;
            Color backgroundColor;

            if (entry.Owned)
            {
                shouldDraw = Settings.LabelSettings.ShowOwnedLabel;
                text = Settings.LabelSettings.OwnedText.Value;
                textColor = Settings.LabelSettings.OwnedTextColor;
                backgroundColor = Settings.LabelSettings.OwnedBackgroundColor;
            }
            else
            {
                shouldDraw = Settings.LabelSettings.ShowNotOwnedLabel;
                text = Settings.LabelSettings.NotOwnedText.Value;
                textColor = Settings.LabelSettings.NotOwnedTextColor;
                backgroundColor = Settings.LabelSettings.NotOwnedBackgroundColor;
            }

            if (!shouldDraw || string.IsNullOrEmpty(text))
            {
                continue;
            }

            float GetRatio(string labelText)
            {
                var textSize = Graphics.MeasureText(labelText);
                return Math.Min(box.Width * Settings.LabelSettings.LabelTextScale.Value / textSize.X, (box.Height - 2) / textSize.Y);
            }

            var scale = GetRatio(text);
            ImGui.SetWindowFontScale(scale);
            var newTextSize = ImGui.CalcTextSize(text);
            var textPosition = box.Center.ToVector2Num() - newTextSize / 2;
            var rectPosition = new Vector2(textPosition.X, box.Top + 1);
            drawList.AddRectFilled(rectPosition, rectPosition + new Vector2(newTextSize.X, box.Height - 2), backgroundColor.ToImgui());
            drawList.AddText(textPosition, textColor.ToImgui(), text);
            ImGui.SetWindowFontScale(1);
            drawnCount++;
        }

        ImGui.End();

        if (Debug && drawnCount == 0 && _resolvedItems.Values.Any(x => x.State == OwnershipState.Resolved))
        {
            if ((DateTime.UtcNow - _lastDebugSummaryUtc).TotalSeconds > 5)
            {
                _lastDebugSummaryUtc = DateTime.UtcNow;
                DebugLog("Draw: resolved items exist but 0 were drawn (check ShowOwned/ShowNotOwned toggles, label text, or panel intersections)");
            }
        }
    }

    private enum OwnershipState
    {
        Resolved,
        Unidentified,
    }

    private record GroundItemOwnership(uint EntityId, string UniqueName, bool Owned, LabelOnGround Label, OwnershipState State);
}