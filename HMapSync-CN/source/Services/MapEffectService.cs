// Map effect (duty arena state) browser for virtual loads.
//
// Slot / SGB / state-name resolution is adapted from Hyperborea's MapEffectResolver
// (https://github.com/kawaii/Hyperborea, BSD-3-Clause, (c) kawaii & NightmareXIV).
// Applying a state goes through ClientStructs' ContentDirector.ApplyMapEffect, which needs the instance-content
// director that ZoneLoadService creates when map effects are enabled.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

public sealed class MapEffectSlotInfo
{
    public ushort Slot;
    public uint LayoutId;
    public bool DefaultOn;
    public string SgbPath = "";
    public (ushort State, string Name)[] States = [];
    public ushort[] AutoPlayStates = [];   // states whose timeline the SGB itself plays on load

    public string SgbName => string.IsNullOrEmpty(SgbPath)
        ? $"#{LayoutId:X8}"
        : Path.GetFileNameWithoutExtension(SgbPath);
}

/// <summary>A bg.lgb scene layer (e.g. FRU's n4gw_p4-5 phase geometry) with the InstanceKeys it owns.</summary>
public sealed class SceneLayer
{
    public uint Id;
    public string Name = "";
    public HashSet<uint> InstanceIds = new();
}

/// <summary>A named area inside a level (boss arena, event/ending room, phase arena...) read from its LGB layers.</summary>
public sealed class ZoneArea
{
    public string Layer = "";
    public string Label = "";
    public System.Numerics.Vector3 Position;
}

public sealed unsafe class MapEffectService
{
    private static readonly string[] LgbNames = ["bg", "planmap", "planevent", "planlive", "vfx", "sound", "planner"];

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private Dictionary<uint, ushort>? cfcToManagedSgRow;
    private readonly Dictionary<uint, List<MapEffectSlotInfo>> zoneCache = [];

    public MapEffectService(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    /// <summary>The bg of the cutscene stage swapped in over this territory, or null for an ordinary map.</summary>
    public Func<uint, string?>? StageBgOf;

    // A swap stage runs under its donor's territory id, so anything read from the level must follow the stage's bg.
    private string? SceneBg(uint territoryType) =>
        StageBgOf?.Invoke(territoryType) ?? data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryType)?.Bg.ToString();

    private string SceneCacheKey(uint territoryType) => territoryType + "|" + StageBgOf?.Invoke(territoryType);

    /// <summary>True when an instance-content director exists, i.e. map effects can be applied right now.</summary>
    public bool DirectorReady
    {
        get
        {
            var ef = EventFramework.Instance();
            return ef != null && ef->GetContentDirector() != null;
        }
    }

    /// <summary>Current state of a slot as the director sees it, or null when unavailable.</summary>
    public ushort? GetCurrentState(ushort slot)
    {
        var ef = EventFramework.Instance();
        if (ef == null) return null;
        var director = ef->GetContentDirector();
        if (director == null || director->MapEffects == null) return null;
        var list = director->MapEffects;
        if (slot >= list->ItemCount || slot >= 128) return null;
        return list->Items[slot].State;
    }

    // Initial slot states captured the first time the director is seen for a territory - "reset" writes them back
    // instead of reloading the map (re-creating a director right after tearing it down could crash the client).
    private uint snapshotTerritory;
    private readonly Dictionary<ushort, ushort> initialStates = [];

    /// <summary>Records the slots' current states as the reset baseline (once per territory load).</summary>
    public void EnsureInitialSnapshot(uint territoryType)
    {
        if (snapshotTerritory == territoryType && initialStates.Count > 0) return;
        if (!DirectorReady) return;
        initialStates.Clear();
        foreach (var s in GetZoneSlots(territoryType))
        {
            var st = GetCurrentState(s.Slot);
            if (st.HasValue) initialStates[s.Slot] = st.Value;
        }
        snapshotTerritory = territoryType;
    }

    /// <summary>Forgets the baseline (call when a new map loads so the next one is captured fresh).</summary>
    public void InvalidateSnapshot() { snapshotTerritory = 0; initialStates.Clear(); }

    public bool HasSnapshot(uint territoryType) => snapshotTerritory == territoryType && initialStates.Count > 0;

    /// <summary>Writes the captured initial states back to every slot. Returns how many slots were reset.</summary>
    public int ResetToInitial(uint territoryType)
    {
        if (!DirectorReady) return 0;
        // Prefer the duty's opening states (what the server would set); fall back to the captured snapshot.
        var target = GetOpeningStates(territoryType);
        if (target.Count == 0)
        {
            if (!HasSnapshot(territoryType)) return 0;
            target = initialStates;
        }
        int n = 0;
        foreach (var kv in target)
            if (Apply(kv.Key, kv.Value)) n++;   // always re-apply (stored state may match while the timeline never ran)
        return n;
    }

    public bool Apply(ushort slot, ushort state, ushort timeline = 0)
    {
        try
        {
            var ef = EventFramework.Instance();
            if (ef == null) return false;
            var director = ef->GetContentDirector();
            if (director == null) return false;
            director->ApplyMapEffect(slot, state, timeline != 0 ? timeline : state);
            log.Information($"[HMSync] [MAPEFFECT] slot={slot} state={state} timeline={(timeline != 0 ? timeline : state)}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [MAPEFFECT] apply failed: " + ex.Message);
            return false;
        }
    }

    public List<MapEffectSlotInfo> GetZoneSlots(uint territoryType)
    {
        if (StageBgOf?.Invoke(territoryType) != null) return [];   // a swap stage has none of the donor's gimmicks
        if (zoneCache.TryGetValue(territoryType, out var cached)) return cached;

        var result = new List<MapEffectSlotInfo>();
        zoneCache[territoryType] = result;
        try
        {
            var territory = data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryType);
            if (territory == null) return result;

            var cfcId = territory.Value.ContentFinderCondition.RowId;
            if (cfcId == 0) return result;

            EnsureCfcMap();
            if (!cfcToManagedSgRow!.TryGetValue(cfcId, out var sgRowId)) return result;

            var subrows = data.GetSubrowExcelSheet<ContentDirectorManagedSG>()?.GetRowOrDefault(sgRowId);
            if (subrows == null) return result;

            ushort slotIdx = 0;
            foreach (var sub in subrows.Value)
            {
                var slot = slotIdx++;
                if (sub.LayoutId == 0) continue;
                result.Add(new MapEffectSlotInfo { Slot = slot, LayoutId = (uint)sub.LayoutId, DefaultOn = sub.Unknown1 });
            }
            if (result.Count == 0) return result;

            ResolveSgbPaths(territory.Value.Bg.ToString(), result);
            foreach (var s in result)
                if (!string.IsNullOrEmpty(s.SgbPath)) (s.States, s.AutoPlayStates) = ReadStateMappings(s.SgbPath);
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [MAPEFFECT] slot resolve failed for territory " + territoryType + ": " + ex.Message);
        }
        return result;
    }

    // ── Festivals (adapted from Hyperborea Utils.GetZoneFestivals / ApplyFestivals) ───────────────────────────────
    private readonly Dictionary<string, List<(int Id, List<int> Phases)>> festivalCache = [];

    /// <summary>Festival ids (with their phase ids) whose layers exist in this level's bg/planevent LGBs.</summary>
    public List<(int Id, List<int> Phases)> GetZoneFestivals(uint territoryType)
    {
        var cacheKey = SceneCacheKey(territoryType);
        if (festivalCache.TryGetValue(cacheKey, out var cached)) return cached;
        var map = new SortedDictionary<int, SortedSet<int>>();
        try
        {
            var bg = SceneBg(territoryType);
            if (!string.IsNullOrEmpty(bg))
            {
                var slash = bg.LastIndexOf('/');
                var dir = slash >= 0 ? bg[..slash] : bg;
                foreach (var file in new[] { "bg", "planevent" })
                {
                    LgbFile? lgb;
                    try { lgb = data.GetFile<LgbFile>($"bg/{dir}/{file}.lgb"); }
                    catch { continue; }
                    if (lgb == null) continue;
                    foreach (var layer in lgb.Layers)
                    {
                        if (layer.FestivalID == 0) continue;
                        if (!map.TryGetValue(layer.FestivalID, out var phases)) map[layer.FestivalID] = phases = [];
                        phases.Add(layer.FestivalPhaseID);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [FESTIVAL] read failed for territory " + territoryType + ": " + ex.Message);
        }
        var result = new List<(int, List<int>)>();
        foreach (var kv in map) result.Add((kv.Key, new List<int>(kv.Value)));
        festivalCache[cacheKey] = result;
        return result;
    }

    /// <summary>The festivals the active layout currently shows (id, phase), zero entries omitted.</summary>
    public List<(ushort Id, ushort Phase)> GetActiveFestivals()
    {
        var result = new List<(ushort, ushort)>();
        var lw = FFXIVClientStructs.FFXIV.Client.LayoutEngine.LayoutWorld.Instance();
        if (lw == null || lw->ActiveLayout == null) return result;
        var layout = lw->ActiveLayout;
        for (int i = 0; i < 8; i++)
        {
            var f = layout->ActiveFestivals[i];
            if (f.Id != 0) result.Add((f.Id, f.Phase));
        }
        return result;
    }

    /// <summary>Replaces the active layout's festivals (max 8; an empty list clears them). Local only.</summary>
    public bool SetFestivals(IReadOnlyList<(ushort Id, ushort Phase)> festivals)
    {
        try
        {
            var lw = FFXIVClientStructs.FFXIV.Client.LayoutEngine.LayoutWorld.Instance();
            if (lw == null || lw->ActiveLayout == null) return false;
            var layout = lw->ActiveLayout;
            // SetActiveFestivals refuses a change while a festival transition is still running (status not 0/5).
            if (layout->FestivalStatus != 0 && layout->FestivalStatus != 5)
            {
                log.Warning("[HMSync] [FESTIVAL] layout busy (status " + layout->FestivalStatus + "), try again in a moment");
                return false;
            }
            var arr = stackalloc FFXIVClientStructs.FFXIV.Client.Game.GameMain.Festival[8];
            int n = Math.Min(8, festivals.Count);
            for (int i = 0; i < n; i++) arr[i] = new() { Id = festivals[i].Id, Phase = festivals[i].Phase };
            layout->SetActiveFestivals(arr);
            log.Information("[HMSync] [FESTIVAL] set " + n + " festival(s)");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [FESTIVAL] set failed: " + ex.Message);
            return false;
        }
    }

    // ── Scene layers ──────────────────────────────────────────────────────────────────────────────────────────────
    // Many duties switch phase scenery by showing/hiding whole bg layers (FRU: n4gw_p2-3, n4gw_p4-5, n4gw_p6-7 ...),
    // driven by the server. A client-side load shows them all at once; these let the user toggle each layer by hand.
    private readonly Dictionary<string, List<SceneLayer>> layerCache = [];

    public List<SceneLayer> GetSceneLayers(uint territoryType)
    {
        var cacheKey = SceneCacheKey(territoryType);
        if (layerCache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = new List<SceneLayer>();
        layerCache[cacheKey] = result;
        try
        {
            var bg = SceneBg(territoryType);
            if (string.IsNullOrEmpty(bg)) return result;
            var slash = bg.LastIndexOf('/');
            var dir = slash >= 0 ? bg[..slash] : bg;
            LgbFile? lgb;
            try { lgb = data.GetFile<LgbFile>($"bg/{dir}/bg.lgb"); }
            catch { return result; }
            if (lgb == null) return result;
            foreach (var layer in lgb.Layers)
            {
                if (layer.InstanceObjects.Length == 0) continue;
                var sl = new SceneLayer { Id = layer.LayerId, Name = layer.Name ?? ("#" + layer.LayerId) };
                foreach (var io in layer.InstanceObjects) sl.InstanceIds.Add(io.InstanceId);
                result.Add(sl);
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [LAYERS] read failed for territory " + territoryType + ": " + ex.Message);
        }
        return result;
    }

    // ── Built-in phase presets (scene-layer based) ───────────────────────────────────────────────────────────────
    // Hand-tuned per territory where the layer→phase mapping is known from the layer names; every other territory gets
    // auto presets from "_pN" phase tags in its bg layer names. Each preset hides a set of layers and shows the rest.
    // Full duty flows: what the server sends per phase, captured from retail replays by AnoMech
    // (https://github.com/anomek/AnoMech, FruConstants/FruZone/FruReplayData). Every slot rests at RestState at
    // zone-in; each phase then switches a few slots and the weather. Scenery (P1 stage, ending flower field ...) is
    // driven by the WEATHER, not by layers: "n4gw's combat background uses bg6_1; weather 1 enables the victory scene".
    // Weather: -1 = the territory's default weather, 0 = leave as is.
    // Weather: -1 = territory default, -2 = none (weather 0, HMS "None - Atmospheric"), 0 = leave, >0 = that id.
    // HideLayers: bg layer name prefixes to hide in this phase (scenery that isn't a map-effect slot).
    // NativeWeather: write the id straight into EnvManager (AnoMech's path) instead of HMS's SetWeatherOrGraft, which
    // crams a foreign sky for ids it doesn't consider native (FRU's victory weather 1 is in the envb but not the rate).
    public sealed record PhaseDef(string Name, int Weather, Dictionary<ushort, ushort> Slots, Dictionary<string, ushort>? BySgb = null,
        string[]? HideLayers = null, bool NativeWeather = false);

    // Flows keyed by SGB name (applied to every slot whose SGB matches) - for duties without captured slot indices.
    // M11 / M11S (x6rb): sgbg_x6rb_a1_gmc02 is the arena controller - f_def_on 4 = whole floor, f_div_on_a 2 = the
    // split cinematic, f_div_on 1 = split floor, f_hide_l/r_off 16/64 = a half gone, f_clear_iv_on 4096 = clear.
    private static readonly Dictionary<uint, PhaseDef[]> SgbFlowPresets = new()
    {
        [1324] = M11Flow(),
        [1325] = M11Flow(),
        // 亚历山大绝境战 (d2az, ManagedSG 15). States from the SGBs: p3_bos00 1 on/4 off; ac_alx00 1 off/4 on (inverted);
        // p4_sky0 / p4_boss0 1 on/4 off/64 fast/256 stop; al_alx00 1 on/4 off. Phase weathers are not captured - use the
        // panel's environment weathers (2, 71/72 Beyond Time, 105, 108, 132).
        // All four arenas are always-loaded bg layers stacked on (100,0,100): boss1 = P1 hall, boss2 = P2 gear room,
        // boss3 = P3 stage, boss4 = P4 stage + sky. The server hides the other phases' layers; a client-side load shows
        // them all, which buried P3/P4 inside the P1/P2 halls (live-verified: state changes logged, nothing visible).
        [887] = new[]
        {
            new PhaseDef("P1 有生命活水", -1, new(), TeaClosed(), new[] { "boss2", "boss3", "boss4" }),
            new PhaseDef("P2 正义号 / 狂徒", 0, new(), TeaClosed(), new[] { "boss1", "boss3", "boss4" }),
            new PhaseDef("P3 亚历山大舞台", 0, new(), new()
            {
                ["sgbg_d2az_p3_bos00"] = 1, ["sgbg_d2a0_ac_alx00"] = 4, ["sgbg_d2az_p4_sky0"] = 4,
                ["sgbg_d2az_p4_boss0"] = 4, ["sgbg_d2az_al_alx00"] = 4,
            }, new[] { "boss1", "boss2", "boss4" }),
            new PhaseDef("P3 舞台移动（动画）", 0, new(), new() { ["sgbg_d2az_p3_bos00"] = 32 }, new[] { "boss1", "boss2", "boss4" }),
            new PhaseDef("P4 完美亚历山大", 0, new(), new()
            {
                ["sgbg_d2az_p3_bos00"] = 4, ["sgbg_d2a0_ac_alx00"] = 1, ["sgbg_d2az_p4_sky0"] = 1,
                ["sgbg_d2az_p4_boss0"] = 1, ["sgbg_d2az_al_alx00"] = 1,
            }, new[] { "boss1", "boss2", "boss3" }),
            new PhaseDef("P4 加速", 0, new(), new() { ["sgbg_d2az_p4_sky0"] = 64, ["sgbg_d2az_p4_boss0"] = 64 }, new[] { "boss1", "boss2", "boss3" }),
            new PhaseDef("P4 停止", 0, new(), new() { ["sgbg_d2az_p4_sky0"] = 256, ["sgbg_d2az_p4_boss0"] = 256 }, new[] { "boss1", "boss2", "boss3" }),
        },
        // 幻想龙诗绝境战 (r1fz). Only two gimmick families are map effects: the P4 eyes (a2_gimk2 x8: 1 show, 4 hide,
        // 16 action) and P6 Hraesvelgr (e0_hreas01: 1 show, 4 hide). The rest of each phase's look is weather-driven:
        // Oppression 44-47, Dragonstorms 66/67/170, CutScene 59, 105, 146 - pick them in the environment weathers.
        [968] = new[]
        {
            new PhaseDef("开场（全部收起）", -1, new(), new() { ["sgbg_r1fz_a2_gimk2"] = 4, ["sgbg_r1fz_e0_hreas01"] = 4 }),
            new PhaseDef("P4 眼睛", 0, new(), new() { ["sgbg_r1fz_a2_gimk2"] = 1 }),
            new PhaseDef("P4 眼睛动作", 0, new(), new() { ["sgbg_r1fz_a2_gimk2"] = 16 }),
            new PhaseDef("P6 赫拉斯瓦尔格", 0, new(), new() { ["sgbg_r1fz_e0_hreas01"] = 1 }),
        },
    };

    private static Dictionary<string, ushort> TeaClosed() => new()
    {
        ["sgbg_d2az_p3_bos00"] = 4, ["sgbg_d2a0_ac_alx00"] = 1, ["sgbg_d2az_p4_sky0"] = 4,
        ["sgbg_d2az_p4_boss0"] = 4, ["sgbg_d2az_al_alx00"] = 4,
    };

    private static PhaseDef[] M11Flow() => new[]
    {
        new PhaseDef("开场", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 4, ["sgbg_x6rb_a1_gmc03"] = 4 }),
        new PhaseDef("劈场地（演出）", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 2 }),
        new PhaseDef("分割后场地", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 1 }),
        new PhaseDef("左半场消失", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 32 }),
        new PhaseDef("右半场消失", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 128 }),
        new PhaseDef("陨石落下", 0, new(), new() { ["sgbg_x6rb_a1_gmc03"] = 2 }),
        new PhaseDef("通关", 0, new(), new() { ["sgbg_x6rb_a1_gmc02"] = 4096 }),
    };

    // Opening = the zone-in MapEffect snapshot per slot index (slots past its end are left alone).
    private static ushort[] Rep(int n, ushort v) { var a = new ushort[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    private static ushort[] Opening(params (int Count, ushort State)[] runs)
    {
        var l = new List<ushort>();
        foreach (var (c, s) in runs) l.AddRange(Rep(c, s));
        return l.ToArray();
    }

    private static readonly Dictionary<uint, (ushort[] Opening, PhaseDef[] Phases)> FlowPresets = new()
    {
        // 欧米茄绝境验证战 (TOP, 22 slots). P3/P4 not captured by AnoMech.
        [1122] = (Opening((1, 1), (21, 4)), new[]
        {
            new PhaseDef("P1 开场", -1, new()),
            new PhaseDef("P2", 78, new() { [10] = 1 }),
            new PhaseDef("P5", 174, new()),
            new PhaseDef("P6", 175, new()),
        }),
        // 妖星乱舞绝境战 (36 slots). Slot 0 is the arena controller; 77 is the between-pulls weather.
        [1363] = (Opening((1, 1), (16, 4), (3, 1), (16, 4)), new[]
        {
            new PhaseDef("开场（场间）", 77, new()),
            new PhaseDef("P1", 78, new() { [0] = 0x10 }),
            new PhaseDef("P2", 79, new() { [0] = 0x40 }),
            new PhaseDef("P3", 174, new() { [0] = 0x200, [0x13] = 4 }),
            new PhaseDef("P4", 174, new() { [0] = 0x200, [0x13] = 4 }),
            new PhaseDef("P5", 175, new()
            {
                [0] = 0x800, [0x13] = 4,
                [0x14] = 1, [0x15] = 1, [0x16] = 1, [0x17] = 1, [0x18] = 1, [0x19] = 1, [0x1A] = 1,
                [0x1B] = 1, [0x1C] = 1, [0x1D] = 1, [0x1E] = 1, [0x1F] = 1, [0x20] = 1, [0x21] = 1,
            }),
        }),
        // 巴哈姆特绝境战 (no map effects; the P5 floor is server-spawned EObjs - only the weather is reproducible).
        [733] = (Array.Empty<ushort>(), new[]
        {
            new PhaseDef("P1 开场", -1, new()),
            new PhaseDef("P5 天气", 32, new()),
        }),
        // 究极神兵绝境战 (no map effects).
        [777] = (Array.Empty<ushort>(), new[]
        {
            new PhaseDef("开场", -1, new()),
            new PhaseDef("究极神兵天气", 95, new()),
        }),
        // 光暗未来绝境战 (n4gw, ContentDirectorManagedSG 181, 54 slots, all rest at 4).
        [1238] = (Rep(54, 4), new[]
        {
            new PhaseDef("P1 开场", -1, new()),
            new PhaseDef("P2 冰之场地", 35, new() { [23] = 1 }),
            new PhaseDef("P3 暗之场地", 105, new() { [40] = 1, [41] = 1 }),
            new PhaseDef("P4 双神", 105, new() { [40] = 1, [41] = 1, [46] = 1 }),
            new PhaseDef("P5 潘多拉", 108, new() { [47] = 1 }),
            // Live-verified on CN: the flower field renders under weather 0 ("None"); weather 1 crams a foreign sky
            // through HMS. The floating memory monitors (sgbg_n4gw_a6_gmc01) live in bg layer n4gw_p6-7, not a slot.
            // Slot 47 (LayoutId 10870860) IS sgbg_n4gw_a6_gmc01 - the memory monitors: 1 monitor_on, 4 monitor_off.
            // n4gb_* = the P1 Eden stage (walls, lanterns, doors around the arena) - gone in the victory scene.
            new PhaseDef("通关花海", 1, new() { [47] = 4 }, null, new[] { "n4gw_p6", "n4gb_" }, NativeWeather: true),
        }),
    };

    public sealed class BuiltinPreset
    {
        public string Name = "";
        public HashSet<uint> Hidden = new();
        public HashSet<uint> ForceShown = new();
        public int Weather;                                   // -1 zone default, 0 leave
        public Dictionary<ushort, ushort> Effects = new();    // slot -> state (empty = layer-only preset)
        public bool IsFlow;                                    // duty-flow preset (weather + map effects)
        public bool NativeWeather;                             // write Weather straight to EnvManager
    }

    /// <summary>
    /// The state every slot rests at when the duty starts (what the server's zone-in snapshot sets). Known duties use
    /// captured data; every other duty uses PickOpeningState.
    /// </summary>
    public Dictionary<ushort, ushort> GetOpeningStates(uint territoryType)
    {
        var result = new Dictionary<ushort, ushort>();
        var slots = GetZoneSlots(territoryType);
        if (FlowPresets.TryGetValue(territoryType, out var flow))
        {
            foreach (var s in slots)
                if (s.Slot < flow.Opening.Length && flow.Opening[s.Slot] != 0) result[s.Slot] = flow.Opening[s.Slot];
            return result;
        }
        foreach (var s in slots)
            if (PickOpeningState(s) is { } state) result[s.Slot] = state;
        return result;
    }

    // "x_def", "x_default", "x_def_on" name a default; "x_def2on" is the animation leaving it.
    private static readonly System.Text.RegularExpressions.Regex DefaultStateName =
        new(@"(^|_)def(ault)?(_on|_show)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private const ushort RestState = 4;

    // An explicit default, else what the SGB autoplays, else state 4. Never choose by "off" / "hide" in the name: floors
    // and set pieces are commonly 1 = stage_off / 4 = stage_on, so that removed the ground (1085 big_stg_on, 917
    // btlstg_show, 1154 floor_u_on). A state the SGB lacks is never written.
    private static ushort? PickOpeningState(MapEffectSlotInfo slot)
    {
        ushort? named = null;
        bool hasRest = false;
        foreach (var (state, name) in slot.States)
        {
            if (state == RestState) hasRest = true;
            if (DefaultStateName.IsMatch(name) && (named == null || state == RestState)) named = state;
        }
        if (named != null) return named;
        if (slot.AutoPlayStates.Length > 0)
            return Array.IndexOf(slot.AutoPlayStates, RestState) >= 0 ? RestState : slot.AutoPlayStates[0];
        return hasRest ? RestState : null;
    }

    /// <summary>Layers to hide as soon as the duty loads: those its first built-in flow phase hides.</summary>
    public HashSet<uint> GetOpeningHiddenLayers(uint territoryType)
    {
        var presets = GetBuiltinPresets(territoryType);
        return presets.Count > 0 && presets[0].IsFlow ? presets[0].Hidden : [];
    }

    private readonly Dictionary<uint, List<BuiltinPreset>> builtinCache = [];

    public List<BuiltinPreset> GetBuiltinPresets(uint territoryType)
    {
        if (StageBgOf?.Invoke(territoryType) != null) return [];
        if (builtinCache.TryGetValue(territoryType, out var cached)) return cached;
        var result = new List<BuiltinPreset>();
        builtinCache[territoryType] = result;
        if (SgbFlowPresets.TryGetValue(territoryType, out var sgbFlow))
        {
            var zoneSlots = GetZoneSlots(territoryType);
            foreach (var ph in sgbFlow)
            {
                var p = new BuiltinPreset { Name = ph.Name, Weather = ph.Weather, IsFlow = true, NativeWeather = ph.NativeWeather };
                foreach (var (sgb, state) in ph.BySgb ?? new())
                    foreach (var s in zoneSlots)
                        if (s.SgbName.Equals(sgb, StringComparison.OrdinalIgnoreCase)) p.Effects[s.Slot] = state;
                AddHiddenLayers(territoryType, ph, p);
                if (p.Effects.Count > 0 || p.Hidden.Count > 0) result.Add(p);
            }
            return result;
        }

        if (FlowPresets.TryGetValue(territoryType, out var flow))
        {
            var opening = GetOpeningStates(territoryType);
            foreach (var ph in flow.Phases)
            {
                var p = new BuiltinPreset { Name = ph.Name, Weather = ph.Weather, IsFlow = true, NativeWeather = ph.NativeWeather };
                foreach (var kv in opening) p.Effects[kv.Key] = kv.Value;
                foreach (var kv in ph.Slots) p.Effects[kv.Key] = kv.Value;
                AddHiddenLayers(territoryType, ph, p);
                result.Add(p);
            }
            return result;
        }

        var layers = GetSceneLayers(territoryType);
        if (layers.Count == 0) return result;

        // Auto: group layers by their "_pN" phase tag; "only phase N" hides every other phase-tagged layer.
        var phaseOf = new Dictionary<uint, int>();
        foreach (var l in layers)
        {
            var m = System.Text.RegularExpressions.Regex.Match(l.Name, @"_p(\d{1,2})(?![0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) phaseOf[l.Id] = int.Parse(m.Groups[1].Value);
        }
        var phases = new SortedSet<int>(phaseOf.Values);
        if (phases.Count < 2) return result;
        foreach (var ph in phases)
        {
            var p = new BuiltinPreset { Name = "只显示阶段 p" + ph };
            foreach (var kv in phaseOf) if (kv.Value != ph) p.Hidden.Add(kv.Key);
            result.Add(p);
        }
        var none = new BuiltinPreset { Name = "隐藏全部阶段图层" };
        foreach (var kv in phaseOf) none.Hidden.Add(kv.Key);
        result.Add(none);
        return result;
    }

    private void AddHiddenLayers(uint territoryType, PhaseDef phase, BuiltinPreset preset)
    {
        if (phase.HideLayers == null) return;
        foreach (var layer in GetSceneLayers(territoryType))
            foreach (var prefix in phase.HideLayers)
                if (layer.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { preset.Hidden.Add(layer.Id); break; }
    }

    // ── Layout versions (story progress) ─────────────────────────────────────────────────────────────────────────
    // LGB layers can be gated by layer-set keys (LayerSetReferencedList Include/Exclude). The key a load uses decides
    // which layers instantiate at all - story states of a city, a built vs ruined town, a duty's alternate layout.
    // Hyperborea calls this "Story Progress". HMS already has the loader lever (ZoneLoadService.PendingCastKeyOverride).
    public sealed class LayoutKey
    {
        public uint Key;
        public int Include;                 // layers shown only under this key
        public int Exclude;                 // layers hidden under this key
        public List<string> Samples = new();
        public string Title = "";           // readable name guessed from the layer names (stage tag + content kinds)
        public int Stage = int.MaxValue;    // _vN stage number found in the layer names, for sorting
        public bool PluginDefault;          // HMS forces this key for the zone (ZoneLoadService.ForcedCastKeys)
        internal readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
    }

    // Layer names carry the authoring intent: u5e3_v3_gmc01 = stage v3 gimmicks, lig_v5_00 = stage v5 lighting,
    // env_location_v6 = stage v6 sky/environment. Turn those into "阶段 v3 · 机关、灯光".
    private static readonly System.Text.RegularExpressions.Regex StageTag =
        new(@"(?:^|_)v(\d{1,2})([a-z]?)(?:_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void NameLayoutKey(LayoutKey k)
    {
        var stages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new List<string>();
        void Kind(string s) { if (!kinds.Contains(s)) kinds.Add(s); }
        foreach (var raw in k.Names)
        {
            var n = raw.ToLowerInvariant();
            var m = StageTag.Match(n);
            if (m.Success)
            {
                stages.Add("v" + m.Groups[1].Value + m.Groups[2].Value);
                if (int.TryParse(m.Groups[1].Value, out var v) && v < k.Stage) k.Stage = v;
            }
            if (n.StartsWith("lvd_")) Kind("区域");
            else if (n.StartsWith("lig") || n.Contains("_lig")) Kind("灯光");
            else if (n.StartsWith("env") || n.Contains("_env")) Kind("天空环境");
            else if (n.Contains("gmc") || n.Contains("gimmick") || n.Contains("gimk")) Kind("机关物体");
            else if (n.Contains("npc") || n.Contains("enpc")) Kind("NPC");
            else if (n.Contains("evt") || n.Contains("event")) Kind("剧情物体");
            else if (n.Contains("vfx") || n.StartsWith("fx")) Kind("特效");
            else if (n.Contains("snd") || n.Contains("sound") || n.Contains("bgm")) Kind("音效");
            else Kind("场景");
        }
        string head = stages.Count switch
        {
            0 => "布局变体",
            1 => "阶段 " + stages.Min,
            _ => "阶段 " + string.Join("/", stages),
        };
        k.Title = head + (kinds.Count > 0 ? " · " + string.Join("、", kinds) : "");
    }

    private readonly Dictionary<string, List<LayoutKey>> layoutKeyCache = [];

    public List<LayoutKey> GetLayoutKeys(uint territoryType)
    {
        var cacheKey = SceneCacheKey(territoryType);
        if (layoutKeyCache.TryGetValue(cacheKey, out var cached)) return cached;
        var map = new SortedDictionary<uint, LayoutKey>();
        try
        {
            var bg = SceneBg(territoryType);
            if (!string.IsNullOrEmpty(bg))
            {
                var slash = bg.LastIndexOf('/');
                var dir = slash >= 0 ? bg[..slash] : bg;
                foreach (var file in new[] { "bg", "planmap", "planevent", "planlive", "planner", "vfx", "sound" })
                {
                    byte[]? raw;
                    try { raw = data.GetFile<FileResource>($"bg/{dir}/{file}.lgb")?.Data; }
                    catch { continue; }
                    if (raw == null) continue;
                    foreach (var (name, type, sets, _) in ReadLayerSets(raw))
                    {
                        if (type != 1 && type != 2) continue;   // 1 Include, 2 Exclude
                        foreach (var id in sets)
                        {
                            if (!map.TryGetValue(id, out var k)) map[id] = k = new LayoutKey { Key = id };
                            if (type == 1) k.Include++; else k.Exclude++;
                            if (k.Samples.Count < 4 && name.Length > 0 && !k.Samples.Contains(name)) k.Samples.Add(name);
                            if (name.Length > 0) k.Names.Add(name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [LAYOUTKEY] read failed for territory " + territoryType + ": " + ex.Message);
        }
        // Only keys in the level's scene-filter table can be loaded: with any other key the game finds no filter and
        // instantiates every variant on top of each other.
        var sceneKeys = ReadSceneKeys(data, SceneBg(territoryType));
        if (sceneKeys.Count > 0 && map.Count > 0)
        {
            foreach (var key in new List<uint>(map.Keys))
                if (!sceneKeys.Contains(key)) map.Remove(key);
            foreach (var key in sceneKeys)
                if (!map.ContainsKey(key)) map[key] = new LayoutKey { Key = key };
        }
        var result = new List<LayoutKey>(map.Values);
        uint forced = StageBgOf?.Invoke(territoryType) != null ? 0 : ZoneLoadService.GetForcedCastKey(territoryType);
        foreach (var k in result) { NameLayoutKey(k); k.PluginDefault = k.Key == forced; }
        result.Sort((a, b) => a.Stage != b.Stage ? a.Stage.CompareTo(b.Stage) : a.Key.CompareTo(b.Key));
        layoutKeyCache[cacheKey] = result;
        return result;
    }

    // Keys of the .lvb's FileSceneFilterList, in file order (layout: ClientStructs FileFormat.cs).
    private static List<uint> ReadSceneKeys(IDataManager data, string? bg)
    {
        var keys = new List<uint>();
        if (string.IsNullOrEmpty(bg)) return keys;
        byte[]? b;
        try { b = data.GetFile<FileResource>($"bg/{bg}.lvb")?.Data; }
        catch { return keys; }
        const int scene = 20, filterSize = 0x1C;
        if (b == null || b.Length < scene + 0x10) return keys;
        int offset = BitConverter.ToInt32(b, scene + 0x0C), list = scene + offset;
        if (offset <= 0 || list + 8 > b.Length) return keys;
        int entries = list + BitConverter.ToInt32(b, list), count = BitConverter.ToInt32(b, list + 4);
        for (int i = 0; i < count; i++)
        {
            int at = entries + filterSize * i;
            if (at < 0 || at + filterSize > b.Length) break;
            uint key = BitConverter.ToUInt32(b, at + 4);
            if (key != 0 && !keys.Contains(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// The layer-filter key to swap a cutscene stage in with. The donor's territory matches none of the stage's filters,
    /// and the game then instantiates every variant at once. Picks the variant that keeps the most scenery, as the
    /// first one can be nearly empty (Ship cabin: 1 of 9 objects). 0 = nothing overlaps (no filters, or one variant
    /// already holds all the scenery): load as before.
    /// </summary>
    public static uint GetStageSceneKey(IDataManager data, string bg)
    {
        var keys = ReadSceneKeys(data, bg);
        var slash = bg.LastIndexOf('/');
        if (keys.Count == 0 || slash < 0) return 0;
        var kept = new int[keys.Count];
        int total = 0;
        foreach (var file in new[] { "bg", "planmap", "vfx" })
        {
            byte[]? raw;
            try { raw = data.GetFile<FileResource>($"bg/{bg[..slash]}/{file}.lgb")?.Data; }
            catch { continue; }
            if (raw == null) continue;
            foreach (var (_, type, sets, scenery) in ReadLayerSets(raw))
            {
                total += scenery;
                for (int i = 0; i < kept.Length; i++)
                {
                    bool listed = Array.IndexOf(sets, keys[i]) >= 0;
                    if (type == 1 ? listed : type != 2 || !listed) kept[i] += scenery;
                }
            }
        }
        int best = 0;
        for (int i = 1; i < kept.Length; i++)
            if (kept[i] > kept[best]) best = i;
        return kept[best] < total ? keys[best] : 0;
    }

    // Raw LGB layer-header walk (Lumina here exposes the set count only, not the ids). Layout, verified offline on
    // n4gw/r1fz/d2az: "LGB1" + "LGP1" chunk at 12; chunk body at 20 = {groupId, nameOff, layersOff, layerCount};
    // each layer: {u32 id, i32 nameOff, i32 instOff, i32 instCount, 4 bytes flags, i32 setListOff, ...};
    // set list at layer+setListOff = {i32 type, i32 setsOff, i32 count} then count x u32 ids at list+setsOff.
    private static IEnumerable<(string Name, int Type, uint[] Sets, int Scenery)> ReadLayerSets(byte[] b)
    {
        var result = new List<(string, int, uint[], int)>();
        try
        {
            const int body = 20;
            if (b.Length < body + 16) return result;
            int layersOff = BitConverter.ToInt32(b, body + 8), count = BitConverter.ToInt32(b, body + 12);
            int table = body + layersOff;
            for (int i = 0; i < count && i < 4096; i++)
            {
                int ls = table + BitConverter.ToInt32(b, table + 4 * i);
                if (ls < 0 || ls + 26 > b.Length) continue;
                int nameOff = BitConverter.ToInt32(b, ls + 4);
                int setListOff = BitConverter.ToInt32(b, ls + 20);
                string name = ReadCString(b, ls + nameOff);
                int rl = ls + setListOff;
                if (rl < 0 || rl + 12 > b.Length) continue;
                int type = BitConverter.ToInt32(b, rl), setsOff = BitConverter.ToInt32(b, rl + 4), n = BitConverter.ToInt32(b, rl + 8);
                // Scenery = BgParts (1) and SharedGroups (6), festival layers excluded (they load only on demand).
                int scenery = 0;
                if (BitConverter.ToUInt16(b, ls + 24) == 0)
                {
                    int instances = ls + BitConverter.ToInt32(b, ls + 8), instanceCount = BitConverter.ToInt32(b, ls + 12);
                    for (int k = 0; k < instanceCount; k++)
                    {
                        int slotAt = instances + 4 * k;
                        if (slotAt < 0 || slotAt + 4 > b.Length) break;
                        int at = instances + BitConverter.ToInt32(b, slotAt);
                        if (at < 0 || at + 4 > b.Length) continue;
                        if (BitConverter.ToInt32(b, at) is 1 or 6) scenery++;
                    }
                }
                if (n <= 0 || n > 256) { result.Add((name, type, Array.Empty<uint>(), scenery)); continue; }
                var sets = new uint[n];
                for (int k = 0; k < n; k++)
                {
                    int at = rl + setsOff + 4 * k;
                    if (at + 4 > b.Length) break;
                    sets[k] = BitConverter.ToUInt32(b, at);
                }
                result.Add((name, type, sets, scenery));
            }
        }
        catch { /* malformed file: return what was read */ }
        return result;
    }

    private static string ReadCString(byte[] b, int at)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = Array.IndexOf(b, (byte)0, at);
        if (end < 0) end = b.Length;
        return Encoding.UTF8.GetString(b, at, end - at);
    }

    // ── Environment weathers ───────────────────────────────────────────────────────────────────────────────────────
    // Every level's .lvb names its environment file (.envb); the envb's ENVS table lists every weather the zone can
    // render natively - including duty-only ones the WeatherRate never schedules (FRU's victory weather 1, DSR's
    // Oppression 44-47 / Dragonstorms 66/67/170, TEA's Beyond Time 71/72). Phase scenery is mostly driven by these.
    private readonly Dictionary<string, List<(byte Id, string Name)>> weatherCache = [];

    public List<(byte Id, string Name)> GetZoneWeathers(uint territoryType)
    {
        var cacheKey = SceneCacheKey(territoryType);
        if (weatherCache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = new List<(byte, string)>();
        weatherCache[cacheKey] = result;
        try
        {
            var bg = SceneBg(territoryType);
            if (string.IsNullOrEmpty(bg)) return result;
            var lvb = data.GetFile<FileResource>($"bg/{bg}.lvb")?.Data;
            if (lvb == null) return result;
            var envPath = FindPath(lvb, ".envb");
            if (envPath == null) return result;
            var env = data.GetFile<FileResource>(envPath)?.Data;
            if (env == null) return result;
            int i = IndexOf(env, "ENVS"u8);
            if (i < 0 || i + 0x14 > env.Length) return result;
            var count = BitConverter.ToInt32(env, i + 0x10);
            var sheet = data.GetExcelSheet<Weather>();
            for (int k = 0; k < count && k < 64; k++)
            {
                int at = i + 0x14 + 16 * k + 12;
                if (at + 4 > env.Length) break;
                var id = BitConverter.ToInt32(env, at);
                if (id <= 0 || id > 255) continue;
                var name = sheet?.GetRowOrDefault((uint)id)?.Name.ToString();
                result.Add(((byte)id, string.IsNullOrEmpty(name) ? "天气 " + id : name!));
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [ENVWX] read failed for territory " + territoryType + ": " + ex.Message);
        }
        return result;
    }

    private static int IndexOf(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle);

    // Pulls the first printable path ending with `ext` out of a binary blob.
    private static string? FindPath(byte[] blob, string ext)
    {
        var e = Encoding.ASCII.GetBytes(ext);
        int hit = blob.AsSpan().IndexOf(e);
        if (hit < 0) return null;
        int start = hit;
        while (start > 0 && blob[start - 1] >= 0x21 && blob[start - 1] < 0x7F) start--;
        return Encoding.ASCII.GetString(blob, start, hit + e.Length - start);
    }

    private readonly Dictionary<string, List<ZoneArea>> areaCache = [];

    /// <summary>
    /// Areas worth jumping to in this level, one point per LVD_* layer from planmap/planevent: boss arenas, event rooms
    /// (e.g. a deep dungeon's floor-100 ending room hidden hundreds of yalms below), phase arenas, map ranges. Room /
    /// mob-spawn layers are skipped. Prefers a PopRange (real spawn), then an EventObject, then the largest volume centre.
    /// </summary>
    public List<ZoneArea> GetZoneAreas(uint territoryType)
    {
        var cacheKey = SceneCacheKey(territoryType);
        if (areaCache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = new List<ZoneArea>();
        areaCache[cacheKey] = result;
        try
        {
            var bg = SceneBg(territoryType);
            if (string.IsNullOrEmpty(bg)) return result;
            var slash = bg.LastIndexOf('/');
            var dir = slash >= 0 ? bg[..slash] : bg;

            foreach (var file in new[] { "planmap", "planevent" })
            {
                LgbFile? lgb;
                try { lgb = data.GetFile<LgbFile>($"bg/{dir}/{file}.lgb"); }
                catch { continue; }
                if (lgb == null) continue;

                foreach (var layer in lgb.Layers)
                {
                    var name = layer.Name ?? "";
                    if (!name.StartsWith("LVD_", StringComparison.OrdinalIgnoreCase)) continue;
                    // Deep-dungeon room grids (LVD_Room_*) and mob-spawn layers are noise; boss "rooms" (e.g. the Tower's
                    // LVD_boos3_room_m) are kept.
                    if (name.StartsWith("LVD_Room", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ContainsAny(name, "BNpc", "Summons", "PRange", "fellow")) continue;

                    System.Numerics.Vector3? pop = null, evo = null, vol = null;
                    float bestVol = 0f;
                    foreach (var io in layer.InstanceObjects)
                    {
                        var t = io.Transform.Translation;
                        var p = new System.Numerics.Vector3(t.X, t.Y, t.Z);
                        if (p.LengthSquared() < 0.01f) continue;
                        switch (io.AssetType)
                        {
                            case LayerEntryType.PopRange: pop ??= p; break;
                            case LayerEntryType.EventObject: evo ??= p; break;
                            case LayerEntryType.EventRange:
                            case LayerEntryType.MapRange:
                                var s = io.Transform.Scale;
                                var v = Math.Abs(s.X * s.Z);
                                if (v > bestVol) { bestVol = v; vol = p; }
                                break;
                        }
                    }
                    var pos = pop ?? evo ?? vol;
                    if (pos == null) continue;
                    if (result.Exists(a => System.Numerics.Vector3.Distance(a.Position, pos.Value) < 5f)) continue;
                    result.Add(new ZoneArea { Layer = name, Label = AreaLabel(name) + " · " + name[4..], Position = pos.Value });
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [AREAS] read failed for territory " + territoryType + ": " + ex.Message);
        }
        return result;
    }

    private static bool ContainsAny(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string AreaLabel(string layer)
    {
        if (ContainsAny(layer, "Event")) return "事件 / 终点区域";
        if (ContainsAny(layer, "Boss")) return "Boss 场地";
        if (ContainsAny(layer, "phase")) return "阶段区域";
        if (ContainsAny(layer, "maprange")) return "场地范围";
        if (ContainsAny(layer, "gimmick")) return "机关区域";
        if (ContainsAny(layer, "zone")) return "入口";
        return "区域";
    }

    /// <summary>LayoutIds (= layout InstanceKeys) of every map-effect slot's SharedGroup in this territory.</summary>
    public HashSet<uint> GetSlotInstanceIds(uint territoryType)
    {
        var set = new HashSet<uint>();
        foreach (var s in GetZoneSlots(territoryType)) if (s.LayoutId != 0) set.Add(s.LayoutId);
        return set;
    }

    private void EnsureCfcMap()
    {
        if (cfcToManagedSgRow != null) return;
        cfcToManagedSgRow = [];
        var sheet = data.GetExcelSheet<InstanceContent>();
        if (sheet == null) return;
        foreach (var row in sheet)
        {
            var cfc = row.ContentFinderCondition.RowId;
            var sg = row.ContentDirectorManagedSG.RowId;
            if (cfc == 0 || sg == 0) continue;
            cfcToManagedSgRow.TryAdd(cfc, (ushort)sg);
        }
    }

    private void ResolveSgbPaths(string bg, List<MapEffectSlotInfo> slots)
    {
        if (string.IsNullOrEmpty(bg)) return;
        var slash = bg.LastIndexOf('/');
        var dir = slash >= 0 ? bg[..slash] : bg;

        var wanted = new Dictionary<uint, MapEffectSlotInfo>();
        foreach (var s in slots) wanted.TryAdd(s.LayoutId, s);

        foreach (var name in LgbNames)
        {
            if (wanted.Count == 0) break;
            LgbFile? lgb;
            try { lgb = data.GetFile<LgbFile>($"bg/{dir}/{name}.lgb"); }
            catch { continue; }
            if (lgb == null) continue;

            foreach (var layer in lgb.Layers)
            {
                if (wanted.Count == 0) break;
                foreach (var io in layer.InstanceObjects)
                {
                    if (io.AssetType != LayerEntryType.SharedGroup) continue;
                    if (!wanted.TryGetValue(io.InstanceId, out var slot)) continue;
                    if (io.Object is LayerCommon.SharedGroupInstanceObject sg)
                    {
                        slot.SgbPath = sg.AssetPath;
                        wanted.Remove(io.InstanceId);
                    }
                }
            }
        }
    }

    // Reads the SGB's state -> timeline name table (raw layout, same as Hyperborea; Lumina has no SgbFile yet).
    private ((ushort State, string Name)[] States, ushort[] AutoPlay) ReadStateMappings(string sgbPath)
    {
        byte[]? bytes;
        try { bytes = data.GetFile<FileResource>(sgbPath)?.Data; }
        catch { return ([], []); }
        if (bytes == null || bytes.Length < 60) return ([], []);

        var offsetAnimHandlers = BitConverter.ToInt32(bytes, 52);
        if (offsetAnimHandlers <= 0) return ([], []);
        var configBase = 20 + offsetAnimHandlers;
        if (configBase + 21 > bytes.Length) return ([], []);

        var offsetTimelines = BitConverter.ToInt32(bytes, 36);
        var fileDataBase = 20 + offsetTimelines;
        if (fileDataBase + 8 > bytes.Length) return ([], []);

        var entryOffset = BitConverter.ToInt32(bytes, fileDataBase);
        var count = BitConverter.ToInt32(bytes, fileDataBase + 4);
        if (count <= 0 || count > 128) return ([], []);

        const int timelineSize = 0x2C, autoPlayOffset = 0x20;   // FileSceneTimeline
        var tmlbNames = ExtractTmlbNames(bytes, count);
        var timelines = new Dictionary<int, (string Name, bool AutoPlay)>(count);
        for (var n = 0; n < count; n++)
        {
            var entryStart = fileDataBase + entryOffset + n * timelineSize;
            if (entryStart + autoPlayOffset >= bytes.Length) break;
            var subId = BitConverter.ToInt32(bytes, entryStart);
            if (subId > 0 && n < tmlbNames.Length) timelines.TryAdd(subId, (tmlbNames[n], bytes[entryStart + autoPlayOffset] != 0));
        }

        var states = new List<(ushort, string)>(16);
        var autoPlay = new List<ushort>();
        for (var i = 0; i < 16; i++)
        {
            int subId = bytes[configBase + 5 + i];
            if (subId == 0) continue;
            var state = (ushort)(1 << i);
            bool known = timelines.TryGetValue(subId, out var timeline);
            states.Add((state, known ? timeline.Name : $"SubId={subId}"));
            if (known && timeline.AutoPlay) autoPlay.Add(state);
        }
        return (states.ToArray(), autoPlay.ToArray());
    }

    private static string[] ExtractTmlbNames(byte[] bytes, int limit)
    {
        var markerPos = Array.LastIndexOf(bytes, (byte)0xFF);
        if (markerPos < 0 || markerPos >= bytes.Length - 1) return [];

        var names = new List<string>(limit);
        var pos = markerPos + 1;
        while (pos < bytes.Length && names.Count < limit)
        {
            var start = pos;
            while (pos < bytes.Length && bytes[pos] != 0) pos++;
            if (pos > start) names.Add(Encoding.UTF8.GetString(bytes, start, pos - start));
            pos++;
        }
        return [.. names];
    }
}
