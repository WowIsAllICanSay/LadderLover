using System;
using System.Linq;
using System.Runtime.Versioning;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using LadderLover.Poeladder;
using Newtonsoft.Json;
using SharpDX;

namespace LadderLover;

[SupportedOSPlatform("windows")]
public class LadderLoverSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public string Username { get; set; } = "";
    public string SelectedLeagueIdentifier { get; set; } = "";

    [JsonIgnore] public System.Collections.Generic.List<CurioLeague> AvailableLeagues { get; set; } = new();
    [JsonIgnore] public string FetchStatus { get; set; } = "";
    [JsonIgnore] public bool IsFetching { get; set; }
    [JsonIgnore] public Action<string> SaveUsernameHandler { get; set; }
    [JsonIgnore] public Action<string> LeagueSelectedHandler { get; set; }
    [JsonIgnore] public string CacheStatus { get; set; } = "";
    [JsonIgnore] public int CachedUniqueCount { get; set; }

    [Menu("Label Display")]
    public LabelSettings LabelSettings { get; set; } = new LabelSettings();

    [Menu("Always Need List")]
    public AlwaysNeedSettings AlwaysNeedSettings { get; set; } = new AlwaysNeedSettings();

    [Menu("Sound")]
    public SoundSettings SoundSettings { get; set; } = new SoundSettings();

    [Menu("Debug")]
    public DebugSettings DebugSettings { get; set; } = new DebugSettings();

    [JsonIgnore]
    [Menu("Poeladder Account", "You must be signed up at poeladder.com to use this plugin.")]
    public CustomNode AccountPanel { get; }

    public LadderLoverSettings()
    {
        string usernameBuffer = "";
        bool bufferInitialized = false;

        AccountPanel = new CustomNode
        {
            DrawDelegate = () =>
            {
                if (!bufferInitialized)
                {
                    usernameBuffer = Username ?? "";
                    bufferInitialized = true;
                }

                ImGui.TextWrapped("You must be signed up at poeladder.com to use this plugin.");
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.Text("Username:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200);
                ImGui.InputTextWithHint("##poeladder_username", "User-1234 (username from pathofexile.com with the # replaced by a -)",
                    ref usernameBuffer, 100);

                ImGui.SameLine();
                if (ImGui.Button("Save"))
                {
                    Username = usernameBuffer;
                    SaveUsernameHandler?.Invoke(usernameBuffer);
                }

                if (AvailableLeagues.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("League:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250);

                    var displayNames = AvailableLeagues
                        .Select(l => l.PoeLadderLeagueName ?? "Unknown")
                        .ToArray();
                    var currentIndex = AvailableLeagues.FindIndex(
                        l => PoeladderApi.ExtractLadderIdentifier(l) == SelectedLeagueIdentifier);
                    if (currentIndex < 0)
                        currentIndex = 0;

                    if (ImGui.Combo("##poeladder_league", ref currentIndex, displayNames, displayNames.Length))
                    {
                        SelectedLeagueIdentifier = PoeladderApi.ExtractLadderIdentifier(AvailableLeagues[currentIndex]) ?? "";
                        LeagueSelectedHandler?.Invoke(SelectedLeagueIdentifier);
                    }
                }

                ImGui.Spacing();
                if (IsFetching)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 1f, 0f, 1f), "Fetching leagues...");
                }
                else if (!string.IsNullOrEmpty(FetchStatus))
                {
                    var color = FetchStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        ? new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f)
                        : new System.Numerics.Vector4(0.3f, 1f, 0.3f, 1f);
                    ImGui.TextColored(color, FetchStatus);
                }

                if (CachedUniqueCount > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.85f, 1f, 1f),
                        $"Ownership cache: {CachedUniqueCount} uniques loaded");
                    if (!string.IsNullOrEmpty(CacheStatus))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"({CacheStatus})");
                    }
                }
                else if (!string.IsNullOrEmpty(CacheStatus))
                {
                    ImGui.Spacing();
                    var color = CacheStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        ? new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f)
                        : new System.Numerics.Vector4(1f, 1f, 0f, 1f);
                    ImGui.TextColored(color, CacheStatus);
                }
            }
        };
    }
}

[Submenu(CollapsedByDefault = false)]
[SupportedOSPlatform("windows")]
public class LabelSettings
{
    [Menu(null, "Show a label over uniques you DON'T own. Owned uniques are hidden by default. " +
                "Ownership is inferred from the poeladder unowned list: items in the list = not owned, " +
                "items NOT in the list = owned. The list refreshes every 2 hours, so a freshly-collected " +
                "unique may still show as not-owned until the next refresh.")]
    public ToggleNode ShowNotOwnedLabel { get; set; } = new ToggleNode(true);

    [Menu(null, "Also show a label over uniques you DO own (for testing).")]
    public ToggleNode ShowOwnedLabel { get; set; } = new ToggleNode(false);

    [Menu(null, "Text scale for ground labels (1 = default, 2 = double size)")]
    public RangeNode<float> LabelTextScale { get; set; } = new RangeNode<float>(2f, 0.5f, 10f);

    [Menu("Not-owned label text color")]
    public ColorNode NotOwnedTextColor { get; set; } = new ColorNode(Color.White);

    [Menu("Not-owned label background color")]
    public ColorNode NotOwnedBackgroundColor { get; set; } = new ColorNode(new Color(175, 96, 37, 220));

    [Menu("Owned label text color")]
    public ColorNode OwnedTextColor { get; set; } = new ColorNode(new Color(175, 96, 37));

    [Menu("Owned label background color")]
    public ColorNode OwnedBackgroundColor { get; set; } = new ColorNode(Color.White);

    [Menu(null, "Text shown on uniques you do not own")]
    public TextNode NotOwnedText { get; set; } = new TextNode("NEED");

    [Menu(null, "Text shown on uniques you do own")]
    public TextNode OwnedText { get; set; } = new TextNode("OWNED");
}

[Submenu(CollapsedByDefault = true)]
[SupportedOSPlatform("windows")]
public class AlwaysNeedSettings
{
    public string AlwaysNeedText { get; set; } = "";

    [JsonIgnore]
    public System.Collections.Generic.HashSet<string> AlwaysNeedSet { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public CustomNode AlwaysNeedInput { get; }

    private string _buffer = "";
    private bool _bufferInitialized = false;

    public AlwaysNeedSettings()
    {
        AlwaysNeedInput = new CustomNode
        {
            DrawDelegate = () =>
            {
                if (!_bufferInitialized)
                {
                    _buffer = AlwaysNeedText ?? "";
                    _bufferInitialized = true;
                }

                ImGui.TextWrapped("Enter unique names, one per line. These will always show as NEED, bypassing the poeladder ownership check.");
                ImGui.Spacing();

                const int bufferSize = 8192;
                if (_buffer.Length < bufferSize)
                {
                    var padded = _buffer + new string('\0', bufferSize - _buffer.Length);
                    _buffer = padded;
                }

                ImGui.InputTextMultiline("##always_need_input", ref _buffer, (uint)_buffer.Length, new System.Numerics.Vector2(-1, 200));

                var cleanBuffer = _buffer.TrimEnd('\0');
                var changed = cleanBuffer != (AlwaysNeedText ?? "");
                if (changed)
                {
                    AlwaysNeedText = cleanBuffer;
                    RebuildSet(cleanBuffer);
                }

                ImGui.Spacing();
                ImGui.TextDisabled($"({AlwaysNeedSet.Count} names)");
            }
        };
    }

    public void RebuildSet(string text)
    {
        AlwaysNeedSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var line in text.Split('\n'))
        {
            var name = line.Trim().Replace('\x2019', '\'');
            if (!string.IsNullOrWhiteSpace(name))
            {
                AlwaysNeedSet.Add(name);
            }
        }
    }

    public bool Contains(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName) || AlwaysNeedSet == null)
        {
            return false;
        }

        return AlwaysNeedSet.Contains(uniqueName.Replace('\x2019', '\'').Trim());
    }
}

[Submenu(CollapsedByDefault = true)]
[SupportedOSPlatform("windows")]
public class DebugSettings
{
    [Menu(null, "Enable debug logging to ExileAPI console. Off by default to avoid large log files.")]
    public ToggleNode EnableDebugLogging { get; set; } = new ToggleNode(false);
}

[Submenu(CollapsedByDefault = false)]
[SupportedOSPlatform("windows")]
public class SoundSettings
{
    [Menu(null, "Play a sound when an unowned unique drops. Only plays once per item. " +
                "Requires alert.wav in the plugin config folder.")]
    public ToggleNode EnableAlertSound { get; set; } = new ToggleNode(false);

    [Menu(null, "Volume (0 to 1)")]
    public RangeNode<float> Volume { get; set; } = new RangeNode<float>(0.5f, 0f, 1f);

    [JsonIgnore]
    [Menu(null, "Test the alert sound")]
    public ButtonNode TestSound { get; set; } = new ButtonNode();
}