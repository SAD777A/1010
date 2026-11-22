using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using HMSync.Services;
using HMSync.Sync;
using Lumina.Excel.Sheets;

namespace HMSync.UI;

// S240: single consolidated /hms window. Two tabs:
//   - Session: connection status + quick command reference (the old status window).
//   - Zones:   the zone directory (the old MapsWindow), searchable + category-tabbed,
//              one-click load via the caller-supplied loader.
// One Begin(), one toggle. /hms opens it; /hms maps opens it focused on the Zones tab.
// MapsWindow is retired - its logic lives here unchanged, just hosted in a tab.
public class HMSyncUI
{
    private readonly HMSyncConfig config;
    private readonly RelaySyncService relay;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;   // S316: WorldToScreen for the carpet orientation rings
    private readonly ITextureProvider textureProvider;   // S322e: emote-browser icons

    // S322e: emote catalog for the Emotes tab - built once from the sheet (id, name, icon, looped).
    private List<(ushort id, string name, uint icon, bool looped)>? emoteCatalog;
    // S322: id → details lookup, for rendering the Favourites / Recently-played rows (which store ids only).
    private Dictionary<ushort, (string name, uint icon, bool looped)>? emoteById;
    private string emoteSearch = "";

    // S322: minion catalog for the Minions tab - built once from the Companion sheet (id, name, icon). The
    // `looped` slot is unused for minions (kept so it shares the emote tuple shape / row helpers).
    private List<(ushort id, string name, uint icon, bool looped)>? minionCatalog;
    private Dictionary<ushort, (string name, uint icon)>? minionById;
    private string minionSearch = "";

    private bool showMain;
    // NB-21: ImGui-style minimise (collapse-to-header). The window is NoTitleBar, so ImGui's built-in
    // title-bar collapse triangle isn't available - this reproduces it by hand. When minimised, the
    // window is clamped to the header band (tab strip + close/min buttons); the tab bodies still submit
    // but are clipped away, so only the header shows - exactly what ImGui's collapse does for a titled
    // window. savedWindowSize preserves the restored height across the collapsed frames.
    private bool minimised;
    private Vector2 savedWindowSize;
    private bool restoreSizePending;
    private float measuredHeaderHeight;
    // v0.7.252: the tear-off carpet control bar - a separate floating ImGui window with the carpet controls you reach
    // for mid-session (toggle / downhill / uphill / rings / settings), so you don't need the main window open. This is
    // the first instance of the planned "HMS hotbar" idea (custom floating bars of plugin actions - summons, mounts,
    // chips - since users can't hand-record game macros for plugin buttons).
    private bool showCarpetBar;
    public void ToggleCarpetBar() { showCarpetBar = !showCarpetBar; }
    private bool showFaceBar;
    // v0.7.465: tear-off state for the Movement and Appearance strips, matching Face Control and Carpet. Opened by
    // clicking the section's own name (see PopOutHeader) rather than a separate button - the header IS the control.
    private bool showMoveBar;
    private bool showAppearanceBar;
    private int summonsChip;   // which collectible sheet is shown on the Summons tab (0=Emotes 1=Mounts 2=Minions 3=Accessories)
    public void ToggleFaceBar() { showFaceBar = !showFaceBar; }
    private string statusText = "Disconnected";

    // v0.7.464 (RMS QA F3, soft tier): transient throttle banner. The relay emits a soft notice at most ~1/3 s
    // while it is dropping our excess ingress; each notice re-arms this for 5 s, so the line stays continuously
    // lit through a burst and self-clears ~5 s after the throttle stops. No user action, no teardown - advisory.
    private DateTime throttleUntil = DateTime.MinValue;
    public void NoteThrottled() => throttleUntil = DateTime.UtcNow.AddSeconds(5);

    // When set, the next Draw selects the Zones tab once (so /hms maps jumps there).
    private bool focusZonesTab;
    private bool focusCarpetTab;   // v0.7.252: the carpet bar's Settings button jumps to the Carpet tab
    private bool focusSessionTab = true;   // S329c: force Session as the default tab on open (not Character/last-used)
    private bool focusConfigTab;   // installer "Settings" button (UiBuilder.OpenConfigUi) jumps to the Config tab

    // Zone directory wiring (moved from MapsWindow).
    // Called when the user clicks Load on a row. Plugin wires this to DoLoad(id).
    public Action<uint>? OnLoadZone;
    public Action<uint>? OnQuickLoad;                  // recent-list quick-load (auto-starts solo when idle)
    // Greys out Load when not hosting / not in a session.
    public Func<bool>? CanLoad;
    // Runs any /hms subcommand from a button. (sub, optional arg).
    public Action<string, string?>? RunCommand;

    // S322: does the local player have an emote unlocked? Set by the plugin (UI avoids unsafe game calls).
    // Used to grey locked emote rows when not in a session. Null → treat everything as usable (no gating).
    public Func<ushort, bool>? CanUseEmote;

    // S322: does the local player have a minion unlocked? Same role as CanUseEmote, for the Minions tab.
    public Func<ushort, bool>? CanUseMinion;

    // S315: direct reference to the carpet service for the Carpet tab (live two-way binding to its
    // tunables). Set by the plugin after construction.
    public CarpetService? Carpet;

    // S248: Glamourer cosmetic-toggle badges. The plugin reads Glamourer state and pushes it here
    // (UI never calls IPC directly). glamourerKnown=false → Glamourer absent or state unreadable
    // (non-human model etc.) → badges hidden, plain command buttons shown instead.
    private bool glamourerAvailable;
    private bool glamourerKnown;
    private bool badgeWeaponVisible;
    private bool badgeHatVisible;
    private bool badgeVisorToggled;

    /// <summary>Plugin pushes fresh Glamourer meta-state for the badges (called on StateChanged + on open).</summary>
    public void SetGlamourerBadges(bool available, bool known, bool weaponVisible, bool hatVisible, bool visorToggled)
    {
        glamourerAvailable = available;
        glamourerKnown = known;
        badgeWeaponVisible = weaponVisible;
        badgeHatVisible = hatVisible;
        badgeVisorToggled = visorToggled;
    }

    /// <summary>Set true while the consolidated window is open, so the plugin knows to refresh badges.</summary>
    public bool IsOpen => showMain;

    // Text-input buffers for arg-taking commands on the Session tab.
    // (S326n: the per-section id fields were merged into the section search boxes - removed emoteIdInput,
    //  minionIdInput, mountIdInput, ornamentIdInput; the search box now doubles as summon/play-by-id input.)

    // S322k: fashion accessory (ornament) tab - id reference + favourites/history grids (Hasel doesn't list these).
    private List<(ushort id, string name, uint icon)>? ornamentCatalog;
    private Dictionary<ushort, (string name, uint icon)>? ornamentById;
    private string ornamentSearch = "";
    public Func<ushort, bool>? CanUseOrnament; // local ornament-unlock check, for greying locked rows out of session

    // S323c: Mounts tab - favourites/history grids over the Mount sheet, same shape as Minions/Accessories.
    private List<(ushort id, string name, uint icon)>? mountCatalog;
    private Dictionary<ushort, (string name, uint icon)>? mountById;
    private string mountSearch = "";
    public Func<ushort, bool>? CanUseMount; // local mount-unlock check, for greying locked rows out of session

    // S326: Map Settings tab. The plugin sets this so the tab can read legal weather / BGM names per territory.
    // Changes route through RunCommand ("mapweather"/"maptime"/"mapbgm"/"npc"/"qbubble") which the plugin applies host-side
    // and broadcasts. (Environment is now scoped to the loaded map - set on Load, adjust live in the Session dash.)
    // S326d: the currently-loaded territory id (from ZoneLoadService.CurrentLoadedZone), so the Map Settings tab can
    // tell "editing the map I'm on" (live apply) from "preparing another map" (store only). 0 = none loaded.
    public Func<uint>? CurrentLoadedZone;
    // The REAL territory the player physically stands in (clientState.TerritoryType), independent of any loaded
    // synthetic map. Used only by the Debug-mode local-cinematic weather picker, which drives weather on the real
    // zone when out of a session (no synthetic map loaded). 0 = not in a zone (login screen etc.).
    public Func<uint>? CurrentRealTerritory;
    // v0.7.340: the active cutscene STAGE name (null when on a plain zone). Lets the Zone: header show the cutscene
    // name instead of the donor territory's name for a swap-loaded stage.
    public Func<string?>? CurrentStageName;
    // NB-37: the active cutscene stage's OWN tag (e.g. "e3e4"), null when on a plain zone. The Zone: header parenthetical
    // uses this instead of the donor territory id for a swap-loaded stage, so a cutscene reads "Doma castle finale (e3e4)"
    // — uniform with a TT's "Ul'dah (130)" — instead of showing the meaningless donor id.
    public Func<string?>? CurrentStageTag;
    // v0.7.262: the single movement capability gate (session-active || debug). UI movement buttons check THIS instead
    // of re-deriving their own condition, so no future button can slip through with a weaker gate.
    public Func<bool>? MovementAllowed;   // v0.7.262 checkbox-level gate; superseded for movement by MovementResearchAllowed (kept wired, currently unused by movement UI)
    // v0.7.445: fly / noclip / carpet all require an HMS-loaded map or cutscene, or research mode
    // (see MovementResearchAllowed in the plugin) - movement on the bare live zone is a teleport cheat.
    public Func<bool>? MovementResearchAllowed;
    // S328am: relay connection state + active URL, for the service picker's live indicator.
    public Func<bool>? ConnectedRelay;
    // Relay key verification: the UI reads the status (grey/green/amber/red) and fires ConfirmRelayKey when the user
    // confirms a pasted key (which locks the field + probes /health?k=<key>). ResetRelayKeyEdit re-opens editing.
    public Func<RelayKeyStatus>? RelayKeyStatusGet;
    public System.Action? ConfirmRelayKey;
    public System.Action? ResetRelayKeyEdit;
    private bool relayKeyLocked;   // when true the key field is non-editable (confirmed); editing re-opens it
    private bool relayKeyLockInit; // NB-5: false until the first key-field draw derives the lock from the saved key
    public Func<string>? ActiveRelayUrl;
    public Func<RelayLight>? RelayLightFn;                 // relay reachability light (green/red/grey) for the uplink dot

    // S326e: session roster (connected peer names) for the "who's here" list on the Session tab, and the
    // live packet-filter state for its status. S326f: upgraded to a structured participant list (Wholist-style).
    public Func<bool>? PacketFilterActive;

    // Config tab (S328p): debug-mode toggle (now config-backed) + say-opcode management.
    public Func<bool>? DebugMode;
    public Action<bool>? SetDebugMode;
    public System.Action? ReloadWeatherDtr;   // b225: called after flipping config.ShowWeatherDtr so the DTR entry is created/removed
    public System.Action? ReloadTrackDtr;     // b240: called after flipping config.ShowTrackDtr so the music DTR entry is created/removed
    public Func<(uint outbound, uint inbound, bool verified, string version)>? SayOpcodeState;
    public Action<uint, uint>? SetSayOpcodes;      // manual key-in: (outbound, inbound)
    public System.Action? VerifySayOpcodes;               // stamp current opcodes verified on the current game version
    public System.Action? RelearnSayOpcodes;              // arm the finder to auto-capture the inbound opcode
    public Func<bool>? SayDriftBanner;             // true when the passthrough auto-shut (drift/patch)
    public System.Action? DismissSayDriftBanner;
    public Func<bool>? MonikerAvailable;           // S328x: grey/green indicator chip for Moniker detection
    public Func<bool>? HdmAvailable;               // FEAT-R2: grey/green chip for HDM (IPC-handshake-backed, like Moniker)
    public System.Action? AccentChanged;           // fired after the user commits an accent change → pushes HMSync.AccentChanged over IPC

    // v0.7.371: Modules panel - presence + one-click open, wired by the plugin to InstalledPluginService.
    // ModulePresent(internalName) → installed AND loaded. ModuleCanOpen → it exposes a window we can open.
    // OpenModule → opens that plugin's own UI (no slash command needed).
    public Func<string, bool>? ModulePresent;
    public Func<string, bool>? ModuleCanOpen;
    public Action<string>? OpenModule;

    // One row of the Modules panel. When the module is installed AND exposes a window, the NAME becomes a clickable
    // link that opens it. Rendered as a borderless button sized to the text so it sits on the same baseline and left
    // edge as the plain-text version - the panel stays aligned whether a module is installed or not.
    private void DrawModuleRow(string label, string internalName, bool present, string tagline)
    {
        var dot = present ? new Vector4(0.35f, 0.85f, 0.42f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.TextColored(dot, "●"); ImGui.SameLine(0, 6);

        bool canOpen = present && (ModuleCanOpen?.Invoke(internalName) ?? false);
        if (canOpen)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.14f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.78f, 1.00f, 1f));   // link blue
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));
            if (ImGui.Button(label + "##openmod" + internalName)) OpenModule?.Invoke(internalName);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("打开" + label);
        }
        else ImGui.TextUnformatted(label);

        ImGui.SameLine();
        if (present) ImGui.TextColored(new Vector4(0.40f, 0.76f, 0.61f, 1f), "  已安装");
        else ImGui.TextDisabled("  未安装");

        ImGui.Indent(18f);
        ImGui.TextDisabled(tagline);
        ImGui.Unindent(18f);
    }

    // Packet inspector (capture tab) wiring.
    public Func<bool>? CaptureActive;                                  // is capture currently on
    public Action<string?>? SetCapture;                               // start capture (arg = opcode filter csv or null); called with "" to just start-all
    public System.Action? StopCapture;                                // stop capture
    public System.Action? ClearCapture;                               // clear the buffer
    public Func<List<PacketFilterService.CapturedPacket>>? SnapshotCapture;   // current captured packets
    public Func<ushort, string>? OpcodeName;                                 // resolve inbound opcode → packet name (or "")
    public Func<string>? OpcodeMapStatus;                                    // opcode-name map source/staleness line
    public Func<uint>? LocalEntityId;                                        // local player's entity id (for the local-only filter)
    public Func<uint, string>? EntityName;                                  // resolve entity id → actor name (or "")

    // S326m: movement-mode state for folded toggle buttons (Fly/Noclip/Carpet show ON/OFF in the button itself).
    public Func<bool>? FlyActive;
    public Func<bool>? NoclipActive;
    public Func<bool>? CarpetActive;

    // S326m: spawn management. HereCoords returns a display string of the live position (the "Show
    // coordinates" readout); CaptureSpawnFor saves current pos+facing as the user spawn for a territory
    // (0 = loaded); RevertSpawnFor clears it; HasUserSpawn reports whether one is set.
    public Func<string?>? HereCoords;
    public Func<uint, bool>? CaptureSpawnFor;
    // CN port: map-effect (arena state) panel
    public Func<uint, List<MapEffectSlotInfo>>? GetMapEffectSlots;
    public Func<uint, List<ZoneArea>>? GetZoneAreas;
    public Func<uint, List<(int Id, List<int> Phases)>>? GetZoneFestivals;
    public Func<List<(ushort Id, ushort Phase)>>? GetActiveFestivals;
    public Action<List<(ushort Id, ushort Phase)>>? SetFestivals;
    public Action<uint, string, List<uint>>? SavePhasePreset;   // territory, name, hidden scene layer ids
    public Action<MapPhasePreset>? ApplyPhasePreset;
    public Action<uint, MapPhasePreset>? DeletePhasePreset;
    private string phasePresetName = "";
    public Func<ushort, ushort?>? MapEffectState;
    public Func<bool>? MapEffectDirectorReady;
    public Action<ushort, ushort>? ApplyMapEffect;
    public Action<ushort, ushort, ushort>? ApplyMapEffectTimeline;   // slot, state, timeline
    public Func<uint, List<SceneLayer>>? GetSceneLayers;
    public Func<uint, List<(byte Id, string Name)>>? GetZoneWeathers;
    public Func<uint, List<MapEffectService.LayoutKey>>? GetLayoutKeys;
    public Func<uint>? CurrentLayoutKey;
    public Action<uint, uint>? LoadLayoutKey;
    public Func<byte>? ActiveWeather;
    public Action<byte>? ApplyEnvWeather;
    public Action<uint, bool>? SetMapEffectObjectsHidden;
    public Func<int>? MapEffectObjectsHiddenCount;
    public Func<uint, List<MapEffectService.BuiltinPreset>>? GetBuiltinPresets;
    public Action<SceneLayer, bool>? SetSceneLayerHidden;            // layer, hide
    private uint hiddenLayersZone;
    private readonly Dictionary<uint, SceneLayer> hiddenLayers = new();
    private string layerFilter = "";
    public Action<uint>? EnsureMapEffectSnapshot;
    public Func<uint, bool>? HasMapEffectSnapshot;
    public Action<uint>? ResetMapEffects;
    public Action<bool>? SetMapEffectsEnabled;
    public Action<uint>? RevertSpawnFor;
    public Func<uint, bool>? HasUserSpawn;
    public Func<Vector3?>? LivePosition;                 // live player position (raw XYZ) for the teleport fields
    public Action<Vector3>? OnTeleport;                  // teleport the player to an XYZ target
    public Action<float>? OnTeleportForward;             // propel the player N units along current facing

    // S326p: "is X currently out" for the dynamic single action buttons (Play/Stop, Summon/Dismiss, Mount/Dismount).
    public Func<bool>? EmotePlaying;
    public Func<bool>? MinionOut;
    public Func<bool>? OrnamentOut;
    public Func<bool>? MountOut;

    // S326f: a participant row for the session table - resolved live per frame from the peer's object.
    public struct ParticipantRow
    {
        public string PeerId;
        public string Name;
        public string World;
        public string Fc;
        public float Distance;     // yalms from local player; -1 if unresolved
        public double? Bearing;    // v0.7.430: camera-relative bearing (radians) for the compass arrow; null = don't draw
        public bool Resolved;      // did we find the live object this frame?
        public bool IsSelf;        // S326h: the local player row - no action buttons, full designator
        public bool IsHost;        // pinned to #1 and amber-tinted
    }
    public Func<List<ParticipantRow>>? SessionParticipants;   // the full participant table
    public Action<string>? SummonPeer;                        // summon a peer to host (by peerId)
    public Action<string>? KickPeer;                          // remove a peer from the room (by peerId)
    public Action<string>? TransferHost;                      // S326h: hand host to a peer (by peerId)
    public Action<string>? TeleportToPeer;                    // teleport local player to a peer's live position (by peerId)

    public MapSettingsService? MapSettings;

    // ── NB-20: granular NPC hide (dot-lens picker, ported from Begone!). The plugin supplies the current rendered
    // EventNpc list (world pos + DataId + name + already-hidden), and receives host-authoritative toggle/restore. The
    // picker overlay draws a clickable dot over each NPC via GetBackgroundDrawList + WorldToScreen; clicking a dot
    // toggles that NPC's DataId in the current map's hidden set. Host-only (CanEditNpcHides gates the UI). ──
    public readonly record struct NpcDotInfo(System.Numerics.Vector3 World, uint DataId, string Name, bool Hidden);
    public Func<IReadOnlyList<NpcDotInfo>>? EnumerateNpcDots;   // rendered EventNpcs this frame
    public Action<uint>? ToggleNpcHide;                        // host toggles one DataId in the current map's hidden set
    public System.Action? RestoreNpcHides;                     // host clears ALL granular hides for the current map
    public Func<int>? HiddenNpcCount;                          // count of granular hides on the current map (button enable/label)
    public Func<bool>? CanEditNpcHides;                        // host authority + a virtual map is loaded
    public Func<bool>? StageLightsActive;                      // b213: current lights-out suppression state (checkbox mirror for /hms stagelights)
    public Func<bool>? VfxHidden;                              // b213: current all-VFX suppression state (checkbox mirror for /hms vfxoff)
    private bool npcPickerActive;                              // the dot-lens overlay is engaged

    public bool TimeDragHold;   // S326u: true while the time slider is being actively dragged (previews live even if not frozen)
    public Func<string>? BgmNowPlaying;   // S326w: title of the currently-selected BGM track (for host + guest display)
    public Action<ushort, byte, bool>? SetHostTime;   // S327g: (hour, minute, forced) - silent host time-set: apply + push epoch, no chat spam
    private string bgmBrowseFilter = "";  // S326w: filter in the BGM browse popup
    // b236 verbatim-label: remember the EXACT label the user picked from the Browse picker so the Music row shows it
    // verbatim, instead of re-deriving a name from the live scene id. When a situation/switch track is played the native
    // director resolves it to a DIFFERENT concrete id than the one the picker offered, and that resolved id has no catalog
    // entry -> the row showed "Track 855" for what the picker called "Garlemald - Day". Keyed by (territory, id) so it
    // self-invalidates on zone change and is dropped by Stop/Reset. Separate slots for the local-cinematic vs host panels.
    private (uint tt, uint id, string label)? bgmPickLocal;
    private (uint tt, uint id, string label)? bgmPickHost;
    // b238: pop-out BGM picker window (like the Zones/maps window) with category chips. `bgmPickerOpen` toggles the
    // floating window; `bgmPickerCat` is the active chip; the per-chip track list is parsed lazily by the service when its
    // chip is first opened (no eager sweep -> no lag). Territory-ids-per-chip come from the Zones `clusters` classification.
    private bool bgmPickerOpen;
    private bool bgmPickerLocal = true;   // selection routing: true = local preview (PlayBgm), false = host (config + broadcast)
    private string bgmPickerCat = "World";   // b242: default chip (no more "All" flat-catalog view)
    private Dictionary<string, List<uint>>? bgmChipZones;   // chip category -> its territory ids (built once from clusters)
    public void OpenBgmPicker(bool local) { bgmPickerOpen = true; bgmPickerLocal = local; }
    // b240: open the picker in the right context automatically (used by the server-info-bar entry, which has no local/host
    // context of its own): host path when this client is hosting a loaded session, else local preview.
    public void OpenBgmPickerAuto()
    {
        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        bool live = (CanLoad?.Invoke() ?? false) && loadedZone != 0;
        OpenBgmPicker(!live);
    }
    // Cached lists for the loaded map's Time & weather (rebuilt when the loaded zone changes).
    private uint mapSettingsCachedTerritory = uint.MaxValue;
    private List<(byte id, string name)>? mapWeatherChoices;   // the map's legal/accepted weathers
    private List<(uint id, string name)>? mapBgmChoices;
    private byte mapDefaultWeather;                      // the territory's native weather (for the "Default -" label)
    // Out-of-session LOCAL-CINEMATIC accent trackers (Debug mode, no session, real zone). The weather grids accent the
    // ACTIVE pick; in-session that reads config.MapWeatherId/Donor (the persisted host pick), but a local cinematic
    // pick is never persisted, so it's tracked here instead. Reset when the acted-on zone changes. (The weather/BGM
    // choice caches above are keyed on the acted-on zone `tt`, so live and local reuse them - they never render at once.)
    private byte localPickWeather;
    private uint localPickDonor;
    private bool weatherPresetsPoppedOut;               // b226: render the Extra-presets grid in its own floating window (DrawWeatherPresetsWindow)
    private List<(byte id, string name, bool legal)>? mapAllWeather;   // lazy full flagged list for the loaded zone
    private string roomPasswordInput = "";              // S326f: room password entry (host) - shared by Host/Join segments

    // Segmented entry mode - which of Solo/Host/Join the idle panel is showing. Purely a UI selection; it doesn't
    // start anything until the mode's action button is pressed.
    private enum IdleMode { Solo, Host, Join }
    private IdleMode idleMode = IdleMode.Solo;
    // S328am: relay-service picker - custom-add input buffers.
    private string customServiceUrl = "";
    private string customServiceName = "";
    private float tpX, tpY, tpZ;                         // teleport target (live readout; editable on double-click)
    private bool coordsEditing;                          // false = live readout, true = user is editing (entered via double-click)
    private int coordFocusField = -1;                    // which coord field to keyboard-focus on entering edit (-1 = none)
    private float tpForwardUnits = 500f;                  // "Teleport forward" distance (editable), along current facing

    // S328am: hide the ?k=<token> in a displayed URL (log/screenshot hygiene - the token is a bearer credential).
    private static string RedactToken(string url)
    {
        if (string.IsNullOrEmpty(url)) return "(none)";
        int i = url.IndexOf("k=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url;
        // keep everything up to k=, then mask
        return url.Substring(0, i + 2) + "••••••••";
    }

    public HMSyncUI(HMSyncConfig config, RelaySyncService relay, IPluginLog log, IDataManager dataManager, IGameGui gameGui, ITextureProvider textureProvider)
    {
        this.config = config;
        this.relay = relay;
        this.log = log;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.textureProvider = textureProvider;
    }

    public void SetStatus(string text) => statusText = text;
    public void ToggleMain() { showMain = !showMain; if (showMain) focusSessionTab = true; }   // S329c: default to Session on open

    // v0.7.400: tab bodies scroll INSIDE a child region, so the tab strip stays PINNED at the top of
    // the window instead of scrolling away with the content - and the scrollbar belongs to the body,
    // so it no longer runs up into the strip. Size (0,0) fills the remaining window area.
    // EndTabBody() must run on EVERY exit path: EndChild is unconditional in ImGui and must match
    // BeginChild one-for-one regardless of what BeginChild returned.
    private static void BeginTabBody(string id)
    {
        // v0.7.405 - the child paints its OWN background via ChildBg, replacing the hand-drawn rect
        // that used to sit in the parent window.
        //
        // Why: that rect was positioned by arithmetic over the PARENT's content region, while the
        // content inside was laid out against the CHILD's. The two never quite agreed - measured on a
        // screenshot, the panel spanned x18..547 while the button row reached 553, overrunning it and
        // cropping "Join", with a 20px left margin against a 3px right one. Painting the background as
        // the child's own means panel edge and content edge are the same rect by construction, and the
        // scrollbar is accounted for automatically. No arithmetic left to get wrong.
        //
        // WindowPadding gives the inner margin so content is not flush against the panel border; it is
        // pushed before BeginChild because that is when padding is read.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.WindowBg));

        // v0.7.406 - AlwaysUseWindowPadding is why v0.7.405's margins did nothing.
        // ImGui FORCES WindowPadding to (0,0) for a borderless child unless this flag is set, so the
        // pushed padding was silently discarded and content stayed flush against the panel edge. The
        // push above was correct; it was being overridden one call later.
        //
        // Height -grip leaves clearance at the bottom for the window's resize corner, which otherwise
        // sits directly on the child's edge with nothing between them.
        float grip = ImGui.GetFrameHeight() * 0.55f;
        ImGui.BeginChild(id, new Vector2(0f, -grip), false, ImGuiWindowFlags.AlwaysUseWindowPadding);

        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        // v0.7.432 - GLOBAL WRAP BOUNDARY. Every tab body passes through here, so pushing a wrap
        // position once makes ALL text inside wrap to the panel's content edge instead of overflowing
        // the border (ImGui.Text/TextDisabled/TextUnformatted/TextColored do NOT wrap on their own -
        // only TextWrapped did, and it was used in ~10 of 135 sites, so long strings overran everywhere).
        // Position 0.0 = wrap at the current content-region right edge, which inside this child IS the
        // panel edge (padding already accounted for). Popped in EndTabBody, one-for-one. Tables suspend
        // this internally (SuspendWrap/ResumeWrap) because a wrap-pos interferes with column auto-sizing;
        // glyph/label sites that must stay on one line push their own PushTextWrapPos(-1) locally.
        ImGui.PushTextWrapPos(0f);
    }

    private static void EndTabBody()
    {
        ImGui.PopTextWrapPos();   // v0.7.432 - matches the PushTextWrapPos(0) in BeginTabBody
        ImGui.EndChild();
    }

    // v0.7.432 - tables set their own per-column wrapping; the global panel-edge wrap boundary (pushed
    // in BeginTabBody) interferes with column auto-sizing and squashes fixed columns. Each table
    // therefore suspends it FOR ITS OWN BODY: SuspendWrap() is called immediately after a successful
    // BeginTable (pushes wrap-off on top of the boundary), ResumeWrap() immediately before EndTable
    // (pops it). Push and pop live in the same table scope, so they can't mismatch, and the boundary
    // is exposed again for any text after the table.
    private static void SuspendWrap() => ImGui.PushTextWrapPos(-1f);
    private static void ResumeWrap() => ImGui.PopTextWrapPos();

    // Open the window on the Zones tab (used by /hms maps).
    public void OpenZones()
    {
        showMain = true;
        focusZonesTab = true;
    }

    // Installer "Open" button (UiBuilder.OpenMainUi): show the window on its default Session tab.
    public void OpenMain()
    {
        showMain = true;
        focusSessionTab = true;
    }

    // b226: DTR weather readout click-action - pop out the weather-presets window (DrawWeatherPresetsWindow). It's an
    // independent tear-off, so this just raises the flag; the window appears whether or not the main HMS window is open.
    public void OpenWeatherPresets()
    {
        weatherPresetsPoppedOut = true;
    }

    // Installer "Settings" button (UiBuilder.OpenConfigUi): show the window and jump to the Config tab.
    public void OpenConfig()
    {
        showMain = true;
        focusSessionTab = false;   // clear any pending default-to-Session so it can't fight the Config jump on first open
        focusConfigTab = true;
    }

    public void Draw()
    {
        if (!showMain) return;

        // Accent the title bar. v0.7.442 - the tab-bar SEPARATOR that EndTabBar draws (the persistent
        // overrun) is coloured ImGuiCol_TabActive (focused) / TabUnfocusedActive (not). TabBarBorderSize
        // (which would disable it) does NOT exist in this Hexa-derived binding - confirmed by compile
        // error - so we kill the line by zeroing those two colours' ALPHA. Side effect: the SELECTED tab
        // loses its accent fill and instead reads as the band surface (alpha 0 → shows the band behind),
        // i.e. the active tab "opens into" the panel while unselected tabs stay a shade darker/recessed
        // (ImGuiCol_Tab, set below). That's a clean, deliberate tab idiom - and our own accent divider
        // (drawn after EndTabBar, clamped to content) is the only line, with no overrun.
        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0f, 0f, 0f, 0f));          // was accent fill; now nil → kills separator
        ImGui.PushStyleColor(ImGuiCol.TabHovered, Darken(acc, 0.72f));
        // v0.7.435 - TAB VALUE LADDER. Previously ImGuiCol.Tab (the UNSELECTED resting colour) was left
        // at ImGui's default grey, which was LIGHTER than the header band and made unselected tabs blend
        // into it. The design fix: unify the band with the body surface (WindowBg, done below), then set
        // unselected tabs a shade DARKER than that surface so they read as recessed "cut into" the bar,
        // with the accented active tab popping forward - separation by value-step, not borders. Derived
        // from WindowBg so it tracks the theme. TabUnfocused* cover the window-not-focused states.
        {
            // v0.7.436 - a fixed dark tab-rest colour a shade below the standard dark-theme WindowBg
            // (~0.06 grey), so unselected tabs recede into the band. (GetStyleColorVec4 returns a
            // Vector4* in this ImGui.NET binding and can't be dereferenced outside unsafe context, so
            // we use a constant rather than reading WindowBg's components - the band still matches the
            // body via GetColorU32(ImGuiCol.WindowBg) below, which needs no components.)
            var tabRest = new Vector4(0.045f, 0.045f, 0.050f, 1f);   // darker than body → recessed tabs
            ImGui.PushStyleColor(ImGuiCol.Tab, tabRest);
            ImGui.PushStyleColor(ImGuiCol.TabUnfocused, tabRest);
            ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, new Vector4(0f, 0f, 0f, 0f));   // v0.7.442 - nil, kills the unfocused-window separator too (active tab opens into panel)
        }

        ImGui.SetNextWindowSize(new Vector2(400, 600), ImGuiCond.FirstUseEver);
        // NB-21: minimise clamp. While minimised, force the window height down to just the header band
        // (measured last un-minimised frame); on restore, push the saved full size back once, then let
        // the user resize freely again. Width is preserved in both directions.
        if (minimised && measuredHeaderHeight > 0f)
            ImGui.SetNextWindowSize(new Vector2(savedWindowSize.X, measuredHeaderHeight), ImGuiCond.Always);
        else if (restoreSizePending)
        {
            ImGui.SetNextWindowSize(savedWindowSize, ImGuiCond.Always);
            restoreSizePending = false;
        }
        // v0.7.400 - the tab strip IS the window header.
        //   NoTitleBar   : no redundant chrome band; name + version moved into the Session strip.
        //   NoBackground : the window paints nothing, so the strip above the tab underline is
        //                  TRANSPARENT. The body background is drawn back manually below, starting
        //                  under the strip - that is what makes only the top band see-through.
        // The window still drags from any empty body area (NoMove is not set); ✕ is a trailing tab.
        // v0.7.403: NoScrollbar/NoScrollWithMouse - since v0.7.400 each tab body scrolls inside its own
        // child, so the WINDOW's scrollbar was a second, redundant one sitting outside the panel. The
        // child keeps its scrollbar; the window no longer competes for the wheel.
        // Stable ImGui id: the window's saved size/position key off the label id, so the version string (shown
        // by the grey header text below) must NOT leak into it. "###WanderlensMain" pins the id across version bumps.
        if (!ImGui.Begin(WindowTitle + "###WanderlensMain", ref showMain,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            ImGui.PopStyleColor(7);
            return;
        }

        // Repaint the body background by hand, from just below the tab strip to the window bottom.
        // Everything drawn afterwards lands on top of it, so no channel splitting is needed.
        // A touch more vertical breathing room between controls than the ImGui default (8,4) - buttons were clustered.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        // v0.7.437 - SQUARE TABS. Default TabRounding (~4px) rounded the tab corners, and the first
        // tab's left rounding overshot the strip edge - that overshoot is the little blue "notch" where
        // ImGui's built-in tab underline pokes left of the divider clamp. Squaring the tabs removes the
        // rounding AND the notch in one move, and reads cleaner against the flat band. Popped with the
        // ItemSpacing var at the end of Draw().
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 0f);
        ImGui.PushTextWrapPos(0f);   // wrap long text at the window/column edge throughout (no horizontal overflow)

        // The strip stays at its natural X - that IS the content region's left edge, and the body
        // panel above is now drawn to match it. Forcing it further left only clips it.
        float tabRowY = ImGui.GetCursorPosY();
        float barBottomY = 0f;

        // v0.7.433 - TAB-STRIP BACKGROUND BAND. The header band was fully transparent (NoBackground),
        // which over a busy game scene made the tabs hard to read. Paint a semi-transparent black band
        // behind the tab row - same tone as the ImGui body - spanning the true window border-to-border
        // width. It stops just ABOVE the accent divider so the intentional gap between tabs and the
        // divider line is preserved. Drawn here (before the tabs submit) so tab text lands on top.
        // v0.7.434 - TAB-STRIP BACKGROUND BAND, aligned to the BODY panel. The band, the accent
        // divider, and the body panel below must all share the SAME left/right extent. The body is a
        // width-0 child, so it spans the CONTENT REGION (window minus WindowPadding on each side) - NOT
        // the full window box. v0.7.433 used winPos.X..winPos.X+winSize.X (the window box), overshooting
        // by WindowPadding.X on both sides - that was the "extends beyond the body" overflow. Use the
        // content-region screen X for all three so their edges line up exactly.
        var winPos = ImGui.GetWindowPos();
        float padX = ImGui.GetStyle().WindowPadding.X;
        float stripLeftX = winPos.X + padX;
        float stripRightX = winPos.X + ImGui.GetWindowContentRegionMax().X;
        // Band spans the TAB ROW exactly: top = row top, bottom = row top + frame height (the tab
        // height). The accent divider sits at that same bottom edge, so the band TOUCHES the line with
        // no gap, and the band height equals the tab height.
        float stripTopScreen = winPos.Y + tabRowY;
        float accentDividerY = winPos.Y + tabRowY + ImGui.GetFrameHeight();
        // NB-21: while un-minimised, record how tall the header band is (window-local), so the minimise
        // clamp above can shrink the window to exactly this next frame. tabRowY is the strip top in
        // window coords; +frame height reaches the accent divider; +bottom WindowPadding leaves the
        // same breathing room under the divider that the full window has, so the collapsed box doesn't
        // crop the divider line.
        if (!minimised)
            measuredHeaderHeight = tabRowY + ImGui.GetFrameHeight() + ImGui.GetStyle().WindowPadding.Y;
        {
            // v0.7.436 - band = the BODY surface exactly (WindowBg), opaque, so header and body read as
            // one continuous panel. Uses the GetColorU32(ImGuiCol) overload that returns a packed uint
            // directly (same idiom as the ChildBg push in BeginTabBody) - no Vector4* to dereference.
            uint bodyBgU = ImGui.GetColorU32(ImGuiCol.WindowBg);
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(stripLeftX, stripTopScreen),
                new Vector2(stripRightX, accentDividerY),
                bodyBgU);
        }

        if (ImGui.BeginTabBar("##hmstabs"))
        {
            // v0.7.404: the bar's bottom edge, captured HERE - immediately after BeginTabBar and
            // BEFORE any tab content is submitted. Measuring after EndTabBar (v0.7.403) read the
            // cursor below the ENTIRE tab body, so the close button was sized to the whole window:
            // a full-panel red button that swallowed clicks and highlighted on hover.
            barBottomY = ImGui.GetCursorPosY();

            // v0.7.446 tab order: Session · Map Control · Zones · Summons · Carpet · [Packets] · Config.
            // Map Control sits next to Session - it holds the loaded-map's live controls (time/weather/
            // BGM/NPC) and spawn point, moved out of Session to keep that tab uncluttered.
            DrawSessionTab();
            DrawMapControlTab();
            DrawZonesTab();
            DrawCharacterTab();     // S326k: Emotes/Minions/Accessories/Mounts folded in as collapsible inventories
            DrawCarpetTab();
            if (DebugMode?.Invoke() ?? false)
                DrawPacketsTab();   // S327x: inbound packet inspector - now gated behind debug mode (Config tab)
            DrawConfigTab();        // S328p: settings + say-opcode management + debug-mode toggle

            ImGui.EndTabBar();
        }

        // v0.7.401: close button, drawn AFTER the bar and positioned by hand on the bar's own row.
        //   • "X" not "✕" - the game font has no U+2715, which rendered as "=".
        //   • TabItemButton with the Trailing flag placed it immediately after the last tab rather
        //     than flush right, so it is a plain button positioned absolutely instead. That also keeps
        //     it correct whatever the tab set is (Packets appears only in debug mode).
        {
            var savePos = ImGui.GetCursorPos();

            // Height of the tab-bar row itself. CLAMPED - a bad measurement must never be able to
            // produce a window-sized button again, so anything outside a plausible range falls back
            // to the frame height.
            float barH = barBottomY > tabRowY
                ? barBottomY - tabRowY - ImGui.GetStyle().ItemSpacing.Y
                : ImGui.GetFrameHeight();
            float fh = ImGui.GetFrameHeight();
            if (barH < fh * 0.5f || barH > fh * 2f) barH = fh;

            // Against contentRegionMax, not window width - past that edge ImGui clips it away entirely,
            // which is where it went in v0.7.401.
            float rightX = ImGui.GetWindowContentRegionMax().X;
            ImGui.SetCursorPos(new Vector2(rightX - barH, tabRowY));

            // Distinct from the tabs: muted red at rest, brighter on hover, so it reads as "close"
            // rather than as one more tab.
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);   // v0.7.437 - square, matching the tabs
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.16f, 0.16f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.72f, 0.22f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.85f, 0.28f, 0.28f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(1f, 0.86f, 0.86f, 1f));
            if (ImGui.Button("X##hmsclose", new Vector2(barH, barH)))
                showMain = false;
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();   // v0.7.437 - FrameRounding

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭");

            // NB-21: MINIMISE button, immediately LEFT of the close button on the same row, same square
            // shape/size. Neutral grey (not red) so it reads as "collapse", not "close". Glyph flips
            // "-" (minimise) / "+" (restore) to mirror ImGui's collapse affordance. Positioned one
            // button-width + a small gap left of the close button.
            const float btnGap = 3f;
            ImGui.SetCursorPos(new Vector2(rightX - barH * 2f - btnGap, tabRowY));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.20f, 0.20f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.34f, 0.34f, 0.38f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.44f, 0.44f, 0.50f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.90f, 0.90f, 0.92f, 1f));
            if (ImGui.Button((minimised ? "+" : "-") + "##hmsmin", new Vector2(barH, barH)))
            {
                if (!minimised)
                {
                    savedWindowSize = ImGui.GetWindowSize();   // remember full size before collapsing
                    minimised = true;
                }
                else
                {
                    minimised = false;
                    restoreSizePending = true;                 // push saved size back next frame
                }
            }
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(minimised ? "恢复" : "最小化");

            ImGui.SetCursorPos(savePos);
        }

        // v0.7.442 - our accent divider, the ONLY line now. ImGui's tab separator is suppressed by
        // zeroing TabActive/TabUnfocusedActive alpha (above) - TabBarBorderSize doesn't exist in this
        // binding. Clamped exactly to the content region, so no overrun past the panel edges.
        {
            var dl = ImGui.GetWindowDrawList();
            var accCol = ImGui.GetColorU32(Darken(acc, 0.58f));
            dl.AddLine(new Vector2(stripLeftX, accentDividerY),
                       new Vector2(stripRightX, accentDividerY), accCol, 1.5f);
        }

        ImGui.PopTextWrapPos();
        ImGui.PopStyleVar(2);   // ItemSpacing + TabRounding
        ImGui.End();
        ImGui.PopStyleColor(7);
    }

    // Tab 1: Session - a STATE MACHINE, not a wall of controls. Three faces:
    //   • Idle   (not connected): the only decision is Host / Join (+ Solo under Host). Nothing else shown.
    //   • Hosting (you own the room, incl. solo): the full authoring surface - room, time & weather, this-map,
    //     maps loader, participants. State is VISIBLE (the status strip), so there's no Status button.
    //   • Guest  (you joined): a stripped surface - participants + a note that the host owns the world.
    private void DrawSessionTab()
    {
        // S329c: honour a pending "default to Session" request (window just opened) - force this tab selected once.
        var sessFlags = ImGuiTabItemFlags.None;
        if (focusSessionTab)
        {
            sessFlags = ImGuiTabItemFlags.SetSelected;
            focusSessionTab = false;
        }
        if (!ImGui.BeginTabItem("会话", sessFlags))
            return;
        BeginTabBody("##sessbody");

        var connected = relay.IsSessionActive;   // S328f: true in solo too, so the authoring surface shows

        // Persistent top - the status strip is ALWAYS present, so pressing a mode/starting a session swaps the panel
        // beneath it without the layout shifting.
        DrawSessionStatusStrip();
        ImGui.Spacing();

        if (!connected) DrawSessionIdle();
        else if (relay.HasMapAuthority) DrawSessionHosting();   // host OR solo → full authoring surface
        else DrawSessionGuest();

        // Debug/dev tools live at the very bottom, behind a checkbox, regardless of state.
        DrawSessionDevTools();

        EndTabBody();
        ImGui.EndTabItem();
    }

    // v0.7.446: MAP CONTROL tab. Time / weather / BGM / NPC cleanup for the currently loaded map, plus the
    // spawn-point controls - moved here from the Session tab, which had grown too busy. These are map-authority
    // actions (host or solo), so a guest sees a short note instead; idle / no-map states are handled by the
    // sub-sections themselves (they show "Load a map..." guidance). The tab is always present so the strip
    // stays stable across states.
    private void DrawMapControlTab()
    {
        if (!ImGui.BeginTabItem("地图控制"))
            return;
        BeginTabBody("##mapctrlbody");

        var connected = relay.IsSessionActive;
        if (connected && !relay.HasMapAuthority)
        {
            // NB-16: guest map state (zone / weather / time / music) shown here READ-ONLY. The host owns synced map
            // state, but a peer should still SEE it - moved out of the Session tab to sit right beside where the host's
            // authoring surface lives (same tab, same "Map control" panel, just non-interactive). Easy "where am I /
            // what's the scene" lookup.
            BeginPanel("地图控制");
            ImGui.TextDisabled("只读：时间、天气和音乐由房主控制。");
            ImGui.Spacing();
            DrawGuestMapReadOnly();
            EndPanel();

            // NB-8: spawn + teleport are NOT synced map control - they're private, local-only conveniences. A peer
            // can privately tag a spawn point while exploring and teleport to get around the map quicker, without
            // touching anyone else's session. So a guest gets the same Spawn point panel as host/solo.
            BeginPanel("出生点");
            DrawThisMapControls();
            EndPanel();
        }
        else
        {
            // Host / solo / idle - the full authoring surface. DrawTimeAndWeather and DrawThisMapControls
            // each self-guard when no map is loaded, so idle degrades to "Load a map..." cleanly.
            BeginPanel("地图控制");
            DrawTimeAndWeather();
            EndPanel();

            BeginPanel("出生点");
            DrawThisMapControls();
            EndPanel();

            BeginPanel("场地机关与区域");
            DrawMapEffects();
            EndPanel();
        }

        EndTabBody();
        ImGui.EndTabItem();
    }

    // Streamlined status header. Line 1 is the headline - relay reachability from the /health probe (online / offline
    // / checking). Line 2 appears only in a session: your role (Host / Peer / Solo) and the packet filter state.
    // Idle shows just the relay line - "not connected / not in session" was redundant. Dots carry state colour; text
    // is white when active, grey when dormant. The session-exit controls (Leave / Stop) live in the Room section.
    private void DrawSessionStatusStrip()
    {
        var white = new Vector4(0.92f, 0.92f, 0.94f, 1f);
        var grey  = new Vector4(0.52f, 0.52f, 0.55f, 1f);   // dormant - dot and text
        var green = new Vector4(0.30f, 0.85f, 0.40f, 1f);   // active
        var red   = new Vector4(0.90f, 0.38f, 0.38f, 1f);   // relay down

        bool pf = PacketFilterActive?.Invoke() ?? false;
        bool solo = relay.SoloMode;
        bool inSession = relay.IsSessionActive;
        var lightState = RelayLightFn?.Invoke() ?? RelayLight.Grey;

        // "● label" with independently-coloured dot and text, drawn inline.
        void Ind(Vector4 dot, Vector4 txt, string label)
        {
            ImGui.TextColored(dot, "●");
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(txt, label);
        }

        // v0.7.402: name + version on their own line at the top of the strip, plain left-aligned.
        // Right-aligning it fought the window-wide PushTextWrapPos(0f) and clipped to "HM"; a normal
        // left-aligned line has none of those failure modes and reads fine as a header.
        ImGui.TextColored(grey, WindowTitle);

        // Line 1 - relay reachability (+ offline helper). Always shown.
        if (lightState == RelayLight.Green) Ind(green, white, "中继：在线");
        else if (lightState == RelayLight.Red)
        {
            Ind(red, red, "中继：离线");
            ImGui.SameLine(0f, 6f); ImGui.TextColored(grey, "（只能使用单人会话）");
        }
        else Ind(grey, grey, "中继：检测中…");


        // Line 2 - packet filter: lit green when engaged (synthetic), dormant grey until then. Always shown.
        Ind(pf ? green : grey, pf ? white : grey, pf ? "封包过滤：开" : "封包过滤：关");

        // Line 3 - session role: lit green with just the TYPE in a session, dormant grey when idle. Always shown.
        if (inSession) Ind(green, white, solo ? "单人" : (relay.IsHost ? "房主" : "成员"));
        else Ind(grey, grey, "无会话");

        // Line 4 - SOFT relay throttle. CONDITIONAL: only while a throttle is live, so the strip's normal height is
        // unchanged. Uses the same Ind() idiom as the three lines above, so it inherits their left edge and baseline
        // rather than introducing a differently-aligned control.
        if (DateTime.UtcNow < throttleUntil)
        {
            var amber = new Vector4(0.95f, 0.72f, 0.25f, 1f);
            Ind(amber, amber, "中继限流，部分更新被丢弃");
        }

        ImGui.Separator();
    }

    // Single exit control (Stop == Leave). Leaving hands host to the next peer if you're the host; the room closes
    // when the last person leaves. Restrained warm-red - destructive, but not shouting.
    private void DrawSessionExitControls()
    {
        var red  = new Vector4(0.42f, 0.20f, 0.20f, 1f);
        var redH = new Vector4(0.52f, 0.26f, 0.26f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, red);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, redH);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, redH);
        if (ImGui.Button("离开会话", new Vector2(-1, 0f))) RunCommand?.Invoke("leave", null);
        ImGui.PopStyleColor(); ImGui.PopStyleColor(); ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("离开房间。你是房主时会移交给下一位成员；最后一人离开后房间关闭。");
    }

    // IDLE - Host (Solo | Group) / Join. Fixed-height boxes, no scrollbars. Solo and Group are separate flows:
    // Group opens a room others join (password required - it's the room's identifier); Solo is local-only, no relay,
    // and skips password/lock entirely (there's no room to join). Solo's backend (local-no-relay) is a P0 item not
    // built yet, so its button is disabled with a note - the affordance is visible without faking capability.
    // IDLE - one shared "Room password" field, then Host / Solo / Join as an equal-width row. The password IS the
    // room's key: Host opens a room keyed by it, Join enters a friend's, Solo ignores it. Declaring it up front
    // (not after opening) means you land in the lobby already secured, not idling behind the firewall while you
    // configure. Host and Join need the field; Solo doesn't.
    private void DrawSessionIdle()
    {
        // Segmented mode selector (iPhone-style): one tap picks the mode; the panel below swaps to only that mode's
        // fields. Every mode's panel is one line + one button, so the action button never jumps between modes.
        // v0.7.405: avail/3 three times rounds UP past the available width, which pushed the third
        // segment over the edge and clipped "Join". Floor the first two and give the last the exact
        // remainder, so the row fits the content region precisely.
        float segAvail = ImGui.GetContentRegionAvail().X;
        float segW = MathF.Floor(segAvail / 3f);
        float segLastW = segAvail - segW * 2f;
        void Segment(string label, IdleMode mode, float w)
        {
            bool sel = idleMode == mode;
            var acc = Accent();
            ImGui.PushStyleColor(ImGuiCol.Button,        sel ? Darken(acc, 0.42f) : new Vector4(0.15f, 0.16f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, sel ? Darken(acc, 0.52f) : new Vector4(0.21f, 0.22f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  sel ? Darken(acc, 0.52f) : new Vector4(0.21f, 0.22f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          sel ? Lighten(acc, 1.05f) : new Vector4(0.72f, 0.74f, 0.78f, 1f));
            if (ImGui.Button(label, new Vector2(w, 0f))) idleMode = mode;
            ImGui.PopStyleColor(4);
        }
        Segment("单人", IdleMode.Solo, segW);
        ImGui.SameLine(0f, 0f); Segment("房主", IdleMode.Host, segW);
        ImGui.SameLine(0f, 0f); Segment("加入", IdleMode.Join, segLastW);

        ImGui.Spacing();

        if (idleMode == IdleMode.Solo)
        {
            ImGui.TextDisabled("一个人直接进，不需要房间和房间码。");
            if (PrimaryButton("开始单人会话", new Vector2(-1, 0f))) RunCommand?.Invoke("startsolo", null);
        }
        else if (idleMode == IdleMode.Host)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##roomsecret", "房间密码（留空自动生成）", ref roomPasswordInput, 64);
            if (PrimaryButton("创建房间", new Vector2(-1, 0f))) RunCommand?.Invoke("start", roomPasswordInput.Trim());
        }
        else // Join
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##roomsecret", "房间码", ref roomPasswordInput, 64);
            bool hasPw = roomPasswordInput.Trim().Length > 0;
            if (PrimaryButton("加入房间", new Vector2(-1, 0f), hasPw)) RunCommand?.Invoke("join", roomPasswordInput.Trim());
        }

        ImGui.Spacing(); ImGui.Separator();
        DrawRecentMaps();
    }

    // HOSTING - the full authoring surface. Order: status strip → room → time & weather → this map → maps → participants.
    private void DrawSessionHosting()
    {
        // (the status strip + session title are drawn by DrawSessionTab, so the top stays stable across states)

        // Room password (host only - solo has no room). The shareable key, auto-generated on Host if you left the
        // field blank - read it out or copy it to your party.
        if (!relay.SoloMode)
        {
            ImGui.TextDisabled("密码");
            ImGui.SameLine();
            var pw = relay.CurrentPassword.Length > 0 ? relay.CurrentPassword : "-";
            ImGui.TextColored(new Vector4(0.55f, 0.80f, 0.55f, 1f), pw);
            ImGui.SameLine();
            if (ImGui.SmallButton("复制##roompw") && relay.CurrentPassword.Length > 0) ImGui.SetClipboardText(relay.CurrentPassword);
            ImGui.Spacing();
        }
        DrawSessionExitControls();

        // Participants - host only (solo is just you; the list is redundant).
        if (!relay.SoloMode)
        {
            ImGui.Spacing(); ImGui.Separator();
            DrawParticipantsSection();
        }

        // Recent maps (quick-load)
        ImGui.Spacing(); ImGui.Separator();
        DrawRecentMaps();

        // Movement + Appearance + Face Control (your own toggles) - HIGH-FREQUENCY use (fly/noclip, face control),
        // so they sit near the top just under Recent Maps.
        ImGui.Spacing(); ImGui.Separator();
        DrawMovementToggles();
        ImGui.Spacing();
        DrawAppearanceToggles();

        // v0.7.446: Map control (time/weather/BGM/NPC) and Spawn point moved OUT of the Session tab to
        // their own "Map Control" tab - the Session tab had grown too busy. See DrawMapControlTab.
    }

    // GUEST - stripped to what a guest can actually do. No time/weather/maps (host owns those).
    private void DrawSessionGuest()
    {
        // (status strip + session title drawn by DrawSessionTab)
        DrawSessionExitControls();

        // Participants (lobby / "who's here") at the TOP - matching the host layout, where the roster sits just under
        // the room controls. In-session, the list of participants is the first thing you check, so it leads here too.
        ImGui.Spacing(); ImGui.Separator();
        DrawParticipantsSection();

        ImGui.Spacing(); ImGui.Separator();
        DrawMovementToggles();
        ImGui.Spacing();
        DrawAppearanceToggles();

        // NB-16: the read-only map state mirror (zone / weather / time / music) that used to live here moved to the
        // Map Control tab, next to where the host's authoring surface sits - see DrawGuestMapReadOnly. A guest's
        // "what's the scene" lookup now lives in one predictable place rather than split across two tabs.
    }

    // NB-16: read-only sight on the synced map state (zone / weather / time / music) for a guest. Mirrors the live game
    // state so a peer can SEE the scene settings even though the host owns them. Rendered inside the Map Control tab's
    // "Map control" panel, mirroring the host's DrawTimeAndWeather layout but non-interactive.
    private void DrawGuestMapReadOnly()
    {
        if (MapSettings == null)
            return;

        // Zone header - identical to the host's "Zone: <name> (ID)".
        uint gz = CurrentLoadedZone?.Invoke() ?? 0;
        if (gz != 0)
        {
            string? stg = CurrentStageName?.Invoke();
            string? stgTag = CurrentStageTag?.Invoke();
            string gzn = !string.IsNullOrEmpty(stg) ? stg : MapSettings.GetZoneName(gz);
            string gzId = !string.IsNullOrEmpty(stgTag) ? stgTag! : gz.ToString();   // NB-37: cutscene's tag, not donor id
            ImGui.TextDisabled("地图："); ImGui.SameLine();
            ImGui.TextUnformatted((string.IsNullOrEmpty(gzn) ? "未命名" : gzn) + " (" + gzId + ")");
            ImGui.Spacing();
        }
        byte liveW = MapSettings.GetActiveWeather();
        string wName = liveW == 0 ? "无 / 环境" : MapSettings.WeatherName(liveW);
        ImGui.TextDisabled("天气："); ImGui.SameLine(); ImGui.TextUnformatted(wName);

        bool tOverride = MapSettings.IsTimeOverridden();
        var (eh, em) = MapSettings.GetEorzeaTimeOfDay();
        // Read-only time slider for visual orientation (guests can't edit - the host drives it). Shows the same
        // value the host sees once time-sync is holding.
        int gTotal = (eh * 60 + em) % 1440;
        string gLabel = eh.ToString("D2") + ":" + em.ToString("D2") + (tOverride ? " (frozen)" : "");
        ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("##guesttime", ref gTotal, 0, 1439, gLabel);
        ImGui.EndDisabled();

        // Peer Music display: read the LIVE playing track (what's actually audible here) - the peer's config pick is
        // not synced to the host, so reading it showed a stale/wrong name that never refreshed. The live read always
        // reflects reality and updates as the host changes the track.
        uint peerLive = MapSettings.GetCurrentBgm();
        string bgm = peerLive != 0 ? MapSettings.BgmName(peerLive) : "无";
        ImGui.TextDisabled("音乐："); ImGui.SameLine(); ImGui.TextUnformatted(bgm);
    }

    // v0.7.465: a section header that IS its own pop-out control. Replaces the separate grey "Pop out" button that
    // used to sit under Face Control - the label carries the affordance instead of a competing widget, so the strip
    // keeps one visual weight per section.
    //
    // Resting state is pixel-identical to the plain TextDisabled headers it replaces, so nothing shifts. On hover the
    // label lifts to the accent and gains a one-pixel underline drawn directly (no font glyph - an icon would need an
    // atlas entry we can't verify, and a trailing caret would change the resting layout). While the pop-out is open
    // the label stays lit, so the docked strip always shows whether its tear-off is live.
    //
    // Hover is computed from the text rect BEFORE drawing so the colour is correct this frame rather than one behind,
    // and gated on IsWindowHovered so an overlapping window can't light it through.
    private void PopOutHeader(string label, ref bool popped, string tip)
    {
        var grey = new Vector4(0.52f, 0.52f, 0.55f, 1f);   // matches TextDisabled's weight in this theme
        var acc  = Accent();
        var pos  = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(label);
        bool hov = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(pos, new Vector2(pos.X + size.X, pos.Y + size.Y));
        bool lit = hov || popped;

        var col = lit ? Lighten(acc, 1.05f) : grey;
        ImGui.TextColored(col, label);
        if (hov)
        {
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(pos.X, pos.Y + size.Y),
                new Vector2(pos.X + size.X, pos.Y + size.Y),
                ImGui.GetColorU32(col), 1f);
            ImGui.SetTooltip(popped ? "关闭" + label.ToLower() + " 弹出窗口" : tip);
        }
        if (ImGui.IsItemClicked()) popped = !popped;
    }

    // Quick-access self controls: Fly / Noclip / Carpet + Weapon / Helmet / Visor, ALL as fixed-length dot-indicator
    // buttons - a leading ● that's green when on, red when off, on a neutral button (matching the movement-toggle
    // style). Fixed width so nothing swells. Shown in host & guest (your own state is always yours to drive).
    private void DrawMovementToggles()
    {
        PopOutHeader("移动", ref showMoveBar, "弹出移动控制窗口");
        DrawMovementBody();
    }

    // Body only - shared verbatim by the docked strip and the tear-off window, so the two can't drift.
    private void DrawMovementBody()
    {
        // v0.7.445: fly / noclip / carpet all gate together on MovementResearchAllowed - true on an
        // HMS-loaded map or cutscene (movement is legitimate there) OR under research mode. On the bare
        // live zone they're a teleport-to-target cheat, so they're disabled unless research mode is on.
        bool moveBlocked = !(MovementResearchAllowed?.Invoke() ?? false);
        var labels = new[] { "飞行", "穿墙", "地毯" };
        var subs = new[] { "fly", "noclip", "carpet" };
        var on = new[] { FlyActive?.Invoke() ?? false, NoclipActive?.Invoke() ?? false, CarpetActive?.Invoke() ?? false };
        FlexButtons(3, 78f, 6f, (i, w) =>
        {
            // A toggle already ON stays interactive so it can always be turned OFF (never trap an active
            // state behind the gate).
            bool blk = moveBlocked && !on[i];
            if (blk) ImGui.BeginDisabled();
            // Carpet: enabling it here also pops the tear-off control bar, so the controls are visible
            // immediately (instant learning - no need to open the tab and read). Only on enable.
            System.Action? extra = (i == 2 && !on[2]) ? () => showCarpetBar = true : null;
            PillToggle(labels[i], subs[i], on[i], w, extra);
            if (blk) ImGui.EndDisabled();
        });
        if (moveBlocked)
            ImGui.TextDisabled("加载地图或过场场景后才能使用移动功能。");
        if (on[1])
        {
            float speed = config.NoclipSpeedMultiplier;
            ImGui.SetNextItemWidth(160f * ImGui.GetIO().FontGlobalScale);
            if (ImGui.SliderFloat("穿墙速度##noclipspeed", ref speed, 1f, NoclipService.MaxSpeedMultiplier, "%.1fx"))
                config.NoclipSpeedMultiplier = speed;
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }
    }

    private void DrawAppearanceToggles()
    {
        PopOutHeader("外观", ref showAppearanceBar, "弹出外观控制窗口");
        DrawAppearanceBody();

        // Dynamic Face Control - its own named section, same style/padding as Movement & Appearance. Its header is
        // the pop-out control too (v0.7.465), replacing the grey "Pop out" button that used to sit below the body.
        ImGui.Spacing();
        PopOutHeader("面部控制", ref showFaceBar, "弹出面部控制窗口");
        DrawFaceControlBody(false);
    }

    // Body only - shared verbatim by the docked strip and the tear-off window.
    private void DrawAppearanceBody()
    {
        bool glam = glamourerAvailable && glamourerKnown;
        var labels = new[] { "武器", "头盔", "面甲" };
        var subs = new[] { "displayarms", "displayhead", "visor" };
        var on = new[] { glam && badgeWeaponVisible, glam && badgeHatVisible, glam && badgeVisorToggled };
        FlexButtons(3, 78f, 6f, (i, w) => PillToggle(labels[i], subs[i], on[i], w));
    }

    // A compact toggle "pill" - the state IS the pill's own colour (a green tint when on, dim when off), so there's
    // no separate lead dot. Equal-width across the row so a set of three fills it and stays aligned.
    private void PillToggle(string label, string sub, bool on, float width, System.Action? extra = null)
    {
        var acc = Accent();
        var bg    = on ? Darken(acc, 0.42f) : new Vector4(0.16f, 0.17f, 0.20f, 1f);
        var bgHov = on ? Darken(acc, 0.52f) : new Vector4(0.22f, 0.24f, 0.28f, 1f);
        var txt   = on ? Lighten(acc, 1.05f) : new Vector4(0.62f, 0.66f, 0.72f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, bgHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, bgHov);
        ImGui.PushStyleColor(ImGuiCol.Text, txt);
        if (ImGui.Button(label + "##pill" + sub, new Vector2(width, 0f)))
        {
            RunCommand?.Invoke(sub, null);
            extra?.Invoke();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleColor();
        ImGui.PopStyleColor();
        ImGui.PopStyleColor();
    }

    // ── Accent palette ──────────────────────────────────────────────────────────────────────────────────────────
    // Primary is the user-set accent (action buttons, active toggles); Neutral (grey) and Danger (warm-red) are fixed
    // in the UI. Hover and text-on-accent are DERIVED so any accent the user picks stays legible.
    private Vector4 Accent()
    {
        var a = config.AccentColor;
        return (a != null && a.Length >= 4) ? new Vector4(a[0], a[1], a[2], a[3]) : new Vector4(0.83f, 0.62f, 0.20f, 1f);
    }
    private static Vector4 Lighten(Vector4 c, float f) => new Vector4(Math.Min(c.X * f, 1f), Math.Min(c.Y * f, 1f), Math.Min(c.Z * f, 1f), c.W);
    private static Vector4 Darken(Vector4 c, float f) => new Vector4(c.X * f, c.Y * f, c.Z * f, c.W);
    // Auto-contrast ink: dark on a light accent, light on a dark one (perceptual luminance).
    private static Vector4 TextOn(Vector4 bg)
    {
        float lum = 0.299f * bg.X + 0.587f * bg.Y + 0.114f * bg.Z;
        return lum > 0.55f ? new Vector4(0.10f, 0.09f, 0.04f, 1f) : new Vector4(0.97f, 0.97f, 0.99f, 1f);
    }
    // Primary (accent) action button. Returns true only on a real (enabled) click.
    private bool PrimaryButton(string label, Vector2 size, bool enabled = true)
    {
        if (!enabled) ImGui.BeginDisabled();
        var a = Accent();
        ImGui.PushStyleColor(ImGuiCol.Button, a);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(a, 1.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(a, 1.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, TextOn(a));
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        if (!enabled) ImGui.EndDisabled();
        return clicked && enabled;
    }

    // Weather-chip tint (extra presets + city sky variants). Both grids speak ONE hue — the user's accent — with state
    // expressed as a GRADIENT of it, per UX convention (don't pit two saturated colours against each other):
    //   • ACTIVE (the live pick)                       → full accent — the brightest step;
    //   • AVAILABLE but not active (baked preset /      → a darkened accent — same family, recedes behind the active one;
    //     captured donor set)
    //   • UNAVAILABLE (no baked preset)                → neutral grey — deliberately OUTSIDE the accent family, so
    //     "can't back this" reads as absence of accent, not a dim accent that could be mistaken for available.
    // Text auto-contrasts (TextOn) so any accent the user picks stays legible on the fill. Caller pops 4 colours.
    private void PushChipColors(bool active, bool available)
    {
        var acc = Accent();
        Vector4 btn = active ? acc : available ? Darken(acc, 0.42f) : new Vector4(0.18f, 0.19f, 0.22f, 1f);
        Vector4 hov = active ? Lighten(acc, 1.12f) : available ? Darken(acc, 0.60f) : new Vector4(0.24f, 0.25f, 0.29f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, btn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, hov);
        ImGui.PushStyleColor(ImGuiCol.Text, (active || available) ? TextOn(btn) : new Vector4(0.72f, 0.75f, 0.80f, 1f));
    }

    // Reflowing equal-width button row: packs as many per row as fit at `minWidth`, each filling its row; wraps the
    // rest to new lines. `drawButton(i, width)` renders item i at the computed width.
    private void FlexButtons(int count, float minWidth, float gap, Action<int, float> drawButton)
    {
        if (count <= 0) return;
        float avail = ImGui.GetContentRegionAvail().X;
        int perRow = Math.Max(1, (int)((avail + gap) / (minWidth + gap)));
        if (perRow > count) perRow = count;
        float w = (avail - gap * (perRow - 1)) / perRow;
        for (int i = 0; i < count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine(0f, gap);
            drawButton(i, w);
        }
    }

    // Boxed panel with a section label above it. Content between BeginPanel/EndPanel is enclosed by a drawn border
    // that auto-fits the content height (no BeginChild, so no fixed height needed). Content is inset for margins.
    private Vector2 panelStart;
    private float panelWidth;
    private const float PanelPad = 8f;
    private void BeginPanel(string label)
    {
        ImGui.TextDisabled(label);
        panelStart = ImGui.GetCursorScreenPos();
        panelWidth = ImGui.GetContentRegionAvail().X;
        // Top inner padding. The bottom edge accrues an extra ItemSpacing.Y before EndPanel captures its cursor, so the
        // top must add the same to stay visually equidistant (previously top looked narrower than bottom).
        ImGui.Dummy(new Vector2(0f, 3f + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Indent(PanelPad);              // left inner margin (full-width content in panels uses -PanelPad for the right)
    }
    private void EndPanel()
    {
        ImGui.Unindent(PanelPad);
        ImGui.Dummy(new Vector2(0f, 3f));    // bottom inner padding
        var end = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRect(panelStart, new Vector2(panelStart.X + panelWidth, end.Y),
            ImGui.GetColorU32(new Vector4(0.32f, 0.34f, 0.40f, 0.85f)), 6f);
        ImGui.Spacing();
    }

    // A CollapsingHeader whose frame respects the panel's right inset (PanelPad) instead of bleeding past the border.
    // CollapsingHeader always spans to the window WorkRect.Max.x and ignores SetNextItemWidth, so full-width headers
    // inside a BeginPanel (which only left-indents by PanelPad) bleed PanelPad past the right edge of the drawn border.
    //
    // We previously inset the right edge by wrapping the header in a 1-frame-tall BeginChild. That worked visually but
    // created a NESTED child window inside the scrolling tab body — its own nav/scroll context. Clicking the header made
    // it the NavId inside that sub-window, and ImGui repeatedly scrolled the sub-window "back into view" near the top of
    // the tab body, yanking the whole tab body to the top every frame. Result: with "Extra presets" expanded you could
    // not scroll down to the controls below it. Fix: keep the header a PLAIN item in the parent window (no child, no
    // sub-context, so no scroll fight) and just CLIP its frame draw to the inset width. The header's hit-rect still spans
    // full width, but the visible frame stops at the border — same look, no nested scroll context.
    private bool InsetCollapsingHeader(string label)
    {
        float w = ImGui.GetContentRegionAvail().X - PanelPad;
        if (w < 1f) w = 1f;
        Vector2 p = ImGui.GetCursorScreenPos();
        ImGui.PushClipRect(p, new Vector2(p.X + w, p.Y + ImGui.GetFrameHeight()), true);
        bool open = ImGui.CollapsingHeader(label);
        ImGui.PopClipRect();
        return open;
    }

    // b223: a horizontal divider that stops at the panel's right inset (PanelPad). ImGui.Separator() spans to the window
    // WorkRect.Max.x and so bleeds PanelPad past the drawn panel border — the same overrun InsetCollapsingHeader clips. We
    // draw the rule manually at the inset width and advance the cursor by 1px so layout still flows like a Separator.
    private void InsetSeparator()
    {
        float w = ImGui.GetContentRegionAvail().X - PanelPad;
        if (w < 1f) w = 1f;
        Vector2 p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, new Vector2(p.X + w, p.Y), ImGui.GetColorU32(ImGuiCol.Separator), 1f);
        ImGui.Dummy(new Vector2(0f, 1f));
    }

    // Recent-5 quick-load. Tapping a row loads that map; from idle it silently starts a solo session first (see
    // DoQuickLoad). Names resolve via MapSettings; unknown ids fall back to the number.
    private void DrawRecentMaps()
    {
        ImGui.TextDisabled("最近地图");
        if (config.RecentPlaces.Count == 0)
        {
            ImGui.TextDisabled("（空；加载过的地图会显示在这里）");
        }
        else if (MapSettings != null)
        {
            // Clickable chips - tap a place to load it (natural-width, wrapping to new lines). A place is either a plain
            // zone (StageBg==null → GetZoneName + OnQuickLoad) or a swap cutscene stage (StageBg set → resolve its name
            // from CutsceneEntries and load by index via OnLoadCutscene). From the user's view both are just "places I
            // visited" - the load routing is invisible.
            float avail = ImGui.GetContentRegionAvail().X;
            float x = 0f; const float gap = 6f;
            bool first = true;
            int chipN = 0;
            foreach (var place in config.RecentPlaces.Take(5))
            {
                string label; int csIndex = -1;
                if (place.StageBg != null)
                {
                    // Match the recorded bg to a cutscene entry for its display name + load index.
                    int found = -1;
                    for (int i = 0; i < CutsceneEntries.Count; i++)
                        if (CutsceneEntries[i].Bg == place.StageBg) { found = i; break; }
                    if (found >= 0)
                    {
                        // v0.7.352: show the stage TAG in parens (e.g. "o1e1") to match zone chips' "name (id)" format.
                        // Tag = the bg path's last '/'-segment.
                        string bg = place.StageBg;
                        int hash = bg.IndexOf('#'); if (hash >= 0) bg = bg[..hash];   // b271: dressed key bg#row -> show the stage tag only
                        int slash = bg.LastIndexOf('/');
                        string tag = slash >= 0 && slash < bg.Length - 1 ? bg.Substring(slash + 1) : bg;
                        label = CutsceneEntries[found].Name + "  (" + tag + ")";
                        csIndex = CutsceneEntries[found].Index;
                    }
                    else continue;   // stage no longer in the catalog - skip rather than show a broken chip
                }
                else
                {
                    string name = MapSettings.GetZoneName(place.TerritoryId);
                    label = (string.IsNullOrEmpty(name) ? ("Zone " + place.TerritoryId) : name) + "  (" + place.TerritoryId + ")";
                }

                float cw = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
                if (!first)
                {
                    if (x + gap + cw > avail) x = 0f;                 // wrap to a new line
                    else { ImGui.SameLine(0f, gap); x += gap; }
                }
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.19f, 0.22f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                if (ImGui.Button(label + "##recent" + chipN))
                {
                    if (csIndex >= 0) OnLoadCutscene?.Invoke(csIndex);
                    else OnQuickLoad?.Invoke(place.TerritoryId);
                }
                ImGui.PopStyleColor(3);
                x += cw;
                first = false;
                chipN++;
            }
        }
        // From idle, tapping a recent map spins up a solo session first (see DoQuickLoad); in a session it just loads.
        if (!relay.IsSessionActive)
            ImGui.TextDisabled("点击地图会在单人会话中加载。");
    }

    // b226: rebuild the weather/BGM choice caches when the acted-on zone changes. Keyed on `tt` (the loaded synthetic
    // zone when live, the real current zone when local), so the live-tab and the local-cinematic path share one
    // invalidation. Idempotent: a no-op once cached for the current tt. Also resets the local-cinematic accent trackers
    // so a stale pick can't accent a chip after a zone hop. Called by BOTH DrawTimeAndWeather and DrawWeatherPresetsWindow
    // (the pop-out) so a torn-off window still tracks zone changes when the Map Control tab isn't the one being drawn.
    private void RefreshWeatherCacheFor(uint tt)
    {
        if (MapSettings == null || tt == mapSettingsCachedTerritory) return;
        mapSettingsCachedTerritory = tt;
        mapWeatherChoices = MapSettings.GetLegalWeather(tt);
        mapDefaultWeather = MapSettings.GetDefaultWeather(tt);
        mapBgmChoices = MapSettings.GetBgmChoices(tt, MapSettings.GetDefaultBgm(tt));
        mapAllWeather = null;   // lazy-rebuilt if "Extra presets" is open
        localPickWeather = 0; localPickDonor = 0;   // drop the local-cinematic accent pick on a zone change
    }

    // b226: apply an EXTRA-PRESET tap (the A-Z cram grid). Promoted from the old inline closure so the pop-out preset
    // window can drive the SAME route. asterisked/`hasKf` = a day-night graft (SetWeatherOrGraft); else a static cram
    // (SetWeatherUnified). LIVE persists to config + broadcasts via RunCommand("mapweather", …); LOCAL runs the same
    // MapSettings route on this client only and records the pick for chip accenting (no persist, no broadcast).
    private void ApplyWeatherPresetImpl(bool local, byte wid, bool hasKf)
    {
        if (MapSettings == null) return;
        if (local)
        {
            localPickWeather = wid; localPickDonor = 0;
            if (hasKf) MapSettings.SetWeatherOrGraft(wid);
            else MapSettings.SetWeatherUnified(wid);
            return;
        }
        config.MapWeatherId = wid; config.MapWeatherDonor = 0; config.Save();
        if (hasKf) MapSettings.SetWeatherOrGraft(wid);   // b183: engage the embedded day-night graft (donor 0 = first available)
        else MapSettings.SetWeatherUnified(wid);
        RunCommand?.Invoke("mapweather", wid.ToString());
    }

    // b226: apply a CITY-VARIANT tap (weather × donor day-night graft). Promoted from the old inline closure so the
    // pop-out preset window can drive the SAME route. LIVE persists (weather, donor) + broadcasts "mapweather <w> <d>";
    // LOCAL runs the graft on this client only and records the pick for chip accenting.
    private void ApplyCityGraftImpl(bool local, byte wid, uint donor)
    {
        if (MapSettings == null) return;
        if (local)
        {
            localPickWeather = wid; localPickDonor = donor;
            MapSettings.SetWeatherOrGraft(wid, donor);
            return;
        }
        config.MapWeatherId = wid; config.MapWeatherDonor = donor; config.Save();
        MapSettings.SetWeatherOrGraft(wid, donor);
        RunCommand?.Invoke("mapweather", wid + " " + donor);
    }

    // b226: the shared "Extra presets" body - the ~70-chip crammable-weather A-Z grid plus the city-variant day-night
    // donor sub-block. Extracted from DrawTimeAndWeather so BOTH the inline collapsible AND the torn-off window
    // (DrawWeatherPresetsWindow) render the identical grid and drive the same apply path (ApplyWeatherPresetImpl /
    // ApplyCityGraftImpl). `local` selects the local-cinematic apply route; pickW/pickD drive the chip accents.
    private void DrawWeatherPresetBody(uint tt, uint loadedZone, bool local, byte pickW, uint pickD)
    {
        if (MapSettings == null) return;
        // Rebuild the full flagged list if it's null (invalidated by a zone change) so the grid persists across hops.
        if (mapAllWeather == null) mapAllWeather = MapSettings.GetZoneWeathers(loadedZone, true);
        if (mapAllWeather != null)
        {
            var extras = new System.Collections.Generic.List<(byte wid, string name)>();
            foreach (var (ewid, ename, elegal) in mapAllWeather)
                // b106: skip the bank-less six (91/36-39/83) — no env bank anywhere carries them, so they can never be
                // baked or crammed; they'd sit as permanently-grey never-satisfiable chips. Uncapturable, not unbaked.
                if (!elegal && ewid != 0 && ewid != mapDefaultWeather && !MapSettingsService.BankLessWeatherIds.Contains(ewid))
                    extras.Add((ewid, ename));
            // v0.7.474: alphabetical, then by id. The natural order is the sheet's (grouped by id), which is a
            // fine data order and a poor reading order across ~70 chips. Note the game reuses names across ids
            // (three "Termination", two "Gales"), so identical labels now sit adjacent instead of scattered -
            // they are genuinely different weathers; the ImGui id suffix (##wc{id}) already keeps them distinct.
            extras.Sort((a, b) =>
            {
                int c = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.wid.CompareTo(b.wid);
            });
            float cavail = ImGui.GetContentRegionAvail().X;
            float cx = 0f; const float cgap = 6f;
            bool cfirst = true;
            char curLetter = '\0';   // b168: A–Z section headers over the alphabetised grid (~70 chips is a wall otherwise)
            foreach (var e in extras)
            {
                // b168: a trailing "*" (after the closing bracket) marks weathers we have a day-night KEYFRAME set for —
                // tapping one engages the graft (b170) and travels the sun through a full day, not just a fixed snapshot.
                // b170: TRUTH-GATE via HasTimeMarchingSet (not merely HasKeyframeSet): a set only earns the "*" if its sky
                // floats actually MOVE across the captured day. A flat donor capture (e.g. Kugane's static CutScene 59)
                // has a stored set but no motion, so it no longer wears the "*" it couldn't honour — "as advertised".
                bool hasKf = MapSettings.HasTimeMarchingSet(e.wid);
                // Ask 3 (2026-08-16): show the id on the chip. The game reuses names across ids (three "Gales", two
                // "Gloom"), which are genuinely DISTINCT weathers needing separate bakes — the bare label made them
                // indistinguishable. "Name (id)" disambiguates them at a glance; ##wc{id} still keeps ImGui ids unique.
                string clabel = e.name + " (" + e.wid + ")" + (hasKf ? "*" : "");

                // b168: emit a letter header whenever the initial letter changes (the grid is alphabetised above). It
                // sits on its own line; the next chip falls below it (cfirst reset), so each letter forms its own block.
                char letter = e.name.Length > 0 ? char.ToUpperInvariant(e.name[0]) : '#';
                if (letter != curLetter)
                {
                    curLetter = letter;
                    if (!cfirst) ImGui.Spacing();               // gap between groups (not before the first)
                    ImGui.TextDisabled(letter.ToString());
                    cx = 0f; cfirst = true;
                }

                float cw = ImGui.CalcTextSize(clabel).X + ImGui.GetStyle().FramePadding.X * 2f;
                if (!cfirst)
                {
                    if (cx + cgap + cw > cavail - PanelPad) cx = 0f;   // wrap, leaving the right margin
                    else { ImGui.SameLine(0f, cgap); cx += cgap; }
                }
                // Chip tint = a gradient of the user's ACCENT (PushChipColors): full accent = ACTIVE (the live pick), a
                // darkened accent = a baked preset available but not active, neutral grey = no preset. (b184 replaced the
                // old fixed green/blue with the accent family so the grid tracks the user's theme; the active step is a
                // brighter gradient of the SAME hue, not a clashing colour — UX convention.)
                // b184 (supersedes the b110 "no accent" note): the accent is keyed off the LIVE pick
                // (config.MapWeatherId/Donor), which resets to 0 on every zone hop (HMSyncPlugin ~L3415) — so it marks
                // the genuinely-active preset, not the "stuck last-tapped" frozen state b110 rightly warned against. The
                // Donor==0 guard keeps a city-variant pick (donor!=0, accented in its own grid below) from ALSO lighting
                // the plain chip for the same weather.
                bool hasPreset = MapSettings.HasPreset(e.wid);
                bool isActive = pickW == e.wid && pickD == 0;
                PushChipColors(isActive, hasPreset);
                // b183: asterisked (genuinely-cycling) → day-night graft; else a static cram. LIVE broadcasts; LOCAL
                // applies the same route on this client only (see ApplyWeatherPresetImpl). Embedded keyframe library +
                // shared Eorzea clock keep a grafted sky in lockstep across peers.
                if (ImGui.Button(clabel + "##wc" + e.wid, new Vector2(cw, 0f)))
                    ApplyWeatherPresetImpl(local, e.wid, hasKf);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((MapSettingsService.AvfxSafeWeatherIds.Contains(e.wid)
                        ? "天气 " + e.wid + " — 特效安全：点击会生成原生场景装饰" + (hasPreset ? " 在强制天空下。" : "（需烘焙预设才能在其下显示正确天空）。")
                        : hasPreset
                            ? "天气 " + e.wid + " — 有已烘焙预设；点击会用防崩溃的重写方式渲染。"
                            : "天气 " + e.wid + " — 本地图自带则原生显示，否则需烘焙预设（在来源地图上用 wxbake）后强制套用。")
                        + (hasKf ? "\n* 带昼夜（时间推进）设置 — 让太阳走完一整天。" : ""));
                ImGui.PopStyleColor(4);
                cx += cw;
                cfirst = false;
            }
        }

        // ── City sky variants (b175). Path I: the same spine weather (Clear Skies, Fog, Rain…) renders a subtly different
        // sky per city, so wxkfcities captures a donor-TAGGED full-day keyframe set for each (weather × city) pair. Here we
        // surface those as sub-chips grouped by weather: "Clear Skies → [Limsa] [Kugane] [Gridania] …". Tapping a city chip
        // engages that specific donor's day-night graft (travels the sun through that city's full day). Only appears once
        // wxkfcities has populated at least one cycling donor set — the section is invisible on a fresh install (no walls).
        var cityWeathers = MapSettings.WeathersWithDonorVariants;
        // b223: FOLDED into the "Extra presets" collapsible (was its own "City sky variants##cityvariants" CollapsingHeader).
        // The two set-once weather collapsibles were redundant, so the day-night donor skies now NEST under the same header
        // as the static extra presets, as a grouped sub-block below the A–Z grid — one expandable weather section instead of
        // two. Kept keyed off WeathersWithDonorVariants (NOT the extras' zone-legal filter): a donor'd spine weather can be
        // native/legal in THIS zone and thus absent from the crammable grid above, yet its city donors must still be offered.
        if (cityWeathers != null && cityWeathers.Count > 0)
        {
            ImGui.Spacing();
            InsetSeparator();
            ImGui.TextDisabled("城市天空变体 — 昼夜来源天空");
            ImGui.Spacing();
            float dvAvail = ImGui.GetContentRegionAvail().X;
            const float dvGap = 6f;
            foreach (var w in cityWeathers)
            {
                var donors = MapSettings.TimeMarchingDonorsForWeather(w);   // only donors whose set actually cycles earn a chip
                if (donors == null || donors.Count == 0) continue;
                ImGui.TextDisabled(MapSettings.WeatherName(w));
                float dvx = 0f; bool dvfirst = true;
                foreach (var d in donors)
                {
                    // Chip label = the stored donor set name (e.g. "Limsa · Clear Skies"); fall back to the raw tt if unnamed.
                    string dvName = MapSettings.DonorSetName(w, d) ?? ("tt" + d);
                    string dvLabel = dvName;
                    float dvw = ImGui.CalcTextSize(dvLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
                    if (!dvfirst)
                    {
                        if (dvx + dvGap + dvw > dvAvail - PanelPad) dvx = 0f;   // wrap, leaving the right margin
                        else { ImGui.SameLine(0f, dvGap); dvx += dvGap; }
                    }
                    // b184: accent the ACTIVE city graft (this exact weather×donor is the live pick). config resets on a
                    // zone hop, so the accent tracks the genuinely-running graft, not a stale last-tap. Every city chip
                    // has a captured donor set, so all are "available" → same accent-gradient language as the extra-preset
                    // grid (full accent = active, darkened accent = available), one consistent "this is on" look.
                    bool dvActive = pickW == w && pickD == d;
                    PushChipColors(dvActive, available: true);
                    // b183: engage this city's day-night graft. LIVE broadcasts (weather, donor); LOCAL runs the same
                    // graft on this client only (see ApplyCityGraftImpl). Embedded keyframe library + shared clock keep it
                    // in lockstep across peers when synced.
                    if (ImGui.Button(dvLabel + "##dv" + w + "_" + d, new Vector2(dvw, 0f)))
                        ApplyCityGraftImpl(local, w, d);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(dvName + " — 昼夜嫁接：让太阳走完这座城市的一整天。");
                    ImGui.PopStyleColor(4);
                    dvx += dvw;
                    dvfirst = false;
                }
                ImGui.Spacing();
            }
        }
    }

    // b226: the torn-off "Extra presets" window. Registered as its own UiBuilder.Draw callback (like the Carpet / Face /
    // Movement / Appearance tear-offs), so it survives tab switches and doesn't vanish when the Map Control tab isn't the
    // active one. Renders only when weatherPresetsPoppedOut. It recomputes the same live/local context DrawTimeAndWeather
    // does (so the grid tracks the loaded synthetic zone when live, the real zone when local) and drives the shared body.
    // b258: live-map time controls (reset / HH:MM slider / Freeze) as ONE shared row, so the Map Control panel and the
    // torn-off Weather-presets window drive the IDENTICAL path (no divergence). `local` = out-of-session local-cinematic
    // (Brio time hook: ApplyTime/FreezeAtCurrent/DisableTimeOverride) vs in-session host (SetHostTime + epoch push).
    private void DrawTimeControls(bool local)
    {
        if (MapSettings == null) return;
        // Time: HH:MM slider (0..1439) + Freeze + reset. Moving the slider AUTO-FREEZES (pins) the time - RP scenes need
        // a static sky, and the Eorzea clock races (~20x real), so dragging to a time you want and having it hold is the
        // desired default. Uncheck Freeze (or hit reset) to release the override and let the real clock resume.
        // DISPLAY: always read the LIVE Eorzea clock (GetEorzeaTimeOfDay reads ClientTime.EorzeaTime). When frozen the
        // clock IS the held value (the Brio hook pins it), when not it's the real marching time. Reading the live clock
        // on BOTH host and peer means the displayed integers match exactly - they read the same underlying field, which
        // the freeze holds identically on every client.
        int totalMin;
        {
            var (lh, lm) = MapSettings.GetEorzeaTimeOfDay();
            totalMin = (lh * 60 + lm) % 1440;
        }
        int sliderVal = totalMin;
        string hhmm = (sliderVal / 60).ToString("D2") + ":" + (sliderVal % 60).ToString("D2");
        // Reset button - release the freeze, real clock resumes.
        if (ImGuiComponents.IconButton("##timereset", FontAwesomeIcon.UndoAlt))
        {
            if (local) MapSettings.DisableTimeOverride();   // release the local freeze - the real clock resumes
            else SetHostTime?.Invoke(config.MapEorzeaHour, config.MapEorzeaMinute, false);   // forced=false → unfreeze
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("恢复为正常流动的艾欧泽亚时间。");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(210);
        bool sliderChanged = ImGui.SliderInt("##maptime", ref sliderVal, 0, 1439, hhmm);
        if (sliderChanged)
        {
            // Dragging pins the time (auto-freeze). One silent path: SetHostTime updates config + applies locally +
            // bumps the map-state epoch so peers get THIS value on the next transform. LOCAL: just pin the Brio freeze.
            if (local) MapSettings.ApplyTime((ushort)(sliderVal / 60), (byte)(sliderVal % 60));
            else SetHostTime?.Invoke((ushort)(sliderVal / 60), (byte)(sliderVal % 60), true);
        }
        TimeDragHold = ImGui.IsItemActive();
        ImGui.SameLine();
        bool timeForced = local ? MapSettings.IsTimeOverridden() : config.MapTimeForced;
        if (ImGui.Checkbox("冻结", ref timeForced))
        {
            if (timeForced)
            {
                // Freeze pins the time showing RIGHT NOW (capture the live clock, not a stale stored value).
                if (local) MapSettings.FreezeAtCurrent();
                else { var (nh, nm) = MapSettings.GetEorzeaTimeOfDay(); SetHostTime?.Invoke((ushort)nh, (byte)nm, true); }
            }
            else if (local) MapSettings.DisableTimeOverride();
            else SetHostTime?.Invoke(config.MapEorzeaHour, config.MapEorzeaMinute, false);
        }
        bool timeFrozenNow = local ? MapSettings.IsTimeOverridden() : config.MapTimeForced;
        if (timeFrozenNow)
            ImGui.TextDisabled("时间冻结于 " + hhmm + "。取消“冻结”或点重置即可恢复真实时间。");
        else
            ImGui.Dummy(new Vector2(0f, ImGui.GetTextLineHeightWithSpacing()));   // a slider + time is self-explanatory; keep the space, no how-to text
    }

    public void DrawWeatherPresetsWindow()
    {
        if (!weatherPresetsPoppedOut || MapSettings == null) return;
        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        bool live = (CanLoad?.Invoke() ?? false) && loadedZone != 0;
        bool local = !live && !relay.IsSessionActive && (CurrentRealTerritory?.Invoke() ?? 0) != 0;   // b241: local-cinematic music/weather no longer requires Debug mode
        uint tt = live ? loadedZone : (CurrentRealTerritory?.Invoke() ?? 0);

        // Accent the title bar so the tear-off follows the accent config, like the other tear-offs.
        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(360, 460), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("天气预设##hmswxpresets", ref weatherPresetsPoppedOut))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }
        if (!live && !local)
            ImGui.TextDisabled("加载地图后才能调整天气预设。");
        else
        {
            RefreshWeatherCacheFor(tt);
            // b258: time controls at the top of the pop-out (above the A-Z presets), so the DTR-launched window can set
            // time-of-day too (HMS changes live-map time like Weatherman) without opening the main HMS window.
            DrawTimeControls(local);
            ImGui.Separator();
            byte pickW = local ? localPickWeather : config.MapWeatherId;
            uint pickD = local ? localPickDonor : config.MapWeatherDonor;
            DrawWeatherPresetBody(tt, loadedZone, local, pickW, pickD);
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    // Time & weather for the CURRENTLY LOADED map (the "load it, adjust, save" model - no territory selector). Time as
    // HH:MM with Freeze on the same row; weather from the loaded map's legal set.
    private void DrawTimeAndWeather()
    {
        if (MapSettings == null) { ImGui.TextDisabled("不可用。"); return; }
        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        bool live = (CanLoad?.Invoke() ?? false) && loadedZone != 0;
        // OUT-OF-SESSION LOCAL CINEMATIC (no session; b241: no longer Debug-gated): time / weather / BGM / lights / VFX are ALL
        // purely-local scene writes (EnvManager weather, the Brio time-freeze hook, scene-0 BGM playback, the layout
        // light/VFX suppressor), so with no map loaded they can still drive the REAL zone the player stands in - the
        // same cosmetic surface Weatherman / Orchestrion offer, mogging both. In this mode nothing is persisted as a
        // map setting and nothing is broadcast (there are no peers); the NPC-hide and spawn/teleport controls stay
        // loaded-map / session features and are omitted below. `local` selects that path; `tt` is the zone the
        // controls act on either way (the loaded synthetic zone when live, the real current zone when local).
        bool local = !live && !relay.IsSessionActive && (CurrentRealTerritory?.Invoke() ?? 0) != 0;   // b241: local-cinematic music/weather no longer requires Debug mode
        if (!live && !local) { ImGui.TextDisabled("加载地图后才能调整时间和天气。"); return; }
        uint tt = live ? loadedZone : (CurrentRealTerritory?.Invoke() ?? 0);

        // Rebuild the loaded map's weather list when the loaded zone changes. (Weather is settled to default on the
        // load event itself in DoLoad, so a stale weather can't bleed across a map change - no sanitise needed here.)
        RefreshWeatherCacheFor(tt);   // b226: shared with the pop-out preset window so both invalidate the caches identically

        // Weather apply helpers - the ONE difference between live and local. LIVE writes the pick to config + Save +
        // broadcasts via RunCommand("mapweather", …) (host-authoritative, synced, applied by DoMapWeather). LOCAL runs
        // the SAME crash-safe MapSettings route directly, records the pick for grid accenting only, and neither
        // persists nor broadcasts (there are no peers). The combos/grids below call these, so their layout is single-sourced.
        byte pickW = local ? localPickWeather : config.MapWeatherId;   // "active pick" for chip accents
        uint pickD = local ? localPickDonor : config.MapWeatherDonor;
        void ApplyWeatherStatic(byte wid)   // plain static pick (donor 0): SetWeatherOrGraft(wid,0) stops any graft too
        {
            if (local) { localPickWeather = wid; localPickDonor = 0; MapSettings.SetWeatherOrGraft(wid, 0); return; }
            config.MapWeatherId = wid; config.MapWeatherDonor = 0; config.Save();
            MapSettings.SetWeatherOrGraft(wid, 0);
            RunCommand?.Invoke("mapweather", wid.ToString());
        }
        void ApplyWeatherNative(byte wid)   // promoted native-bank state: must NOT fall through the cram path (dark-map bug)
        {
            if (local) { localPickWeather = wid; localPickDonor = 0; MapSettings.StopKfGraft(); MapSettings.SetWeatherNativeOnly(wid); return; }
            config.MapWeatherId = wid; config.MapWeatherDonor = 0; config.Save();
            MapSettings.StopKfGraft();
            MapSettings.SetWeatherNativeOnly(wid);
            RunCommand?.Invoke("mapweather", wid.ToString());
        }
        // b226: the extra-preset + city-graft apply logic moved to ApplyWeatherPresetImpl / ApplyCityGraftImpl (private
        // methods) so the pop-out preset window (DrawWeatherPresetsWindow) can drive the SAME apply path as the inline
        // grid. ApplyWeatherStatic / ApplyWeatherNative stay local closures (only the native combo above uses them).

        // Zone header - the map these controls act on. "Zone: <name> (ID)".
        {
            string? stg2 = local ? null : CurrentStageName?.Invoke();
            string? stg2Tag = local ? null : CurrentStageTag?.Invoke();
            string zn = !string.IsNullOrEmpty(stg2) ? stg2 : MapSettings.GetZoneName(tt);
            string znId = !string.IsNullOrEmpty(stg2Tag) ? stg2Tag! : tt.ToString();   // NB-37: cutscene's tag, not donor id
            ImGui.TextDisabled("地图：");
            ImGui.SameLine();
            ImGui.TextUnformatted((string.IsNullOrEmpty(zn) ? "未命名" : zn) + " (" + znId + ")");
        }
        ImGui.Spacing();

        DrawTimeControls(local);   // b258: shared time row (reset / HH:MM slider / Freeze), also used by the Weather-presets pop-out

        // Weather. The dropdown's DISPLAYED value is the LIVE weather the engine is rendering (GetActiveWeather), so it
        // always matches the sky - even the map's natural weather on load, which the host never explicitly picked.
        // Picking applies LIVE (ApplyWeather writes EnvManager directly). "None - Atmospheric" (0) is a synthetic forced
        // blank the host can choose; it only reads as the selected row when the live weather is actually 0.
        // b295: one truth for every control. GetDisplayWeather = the pick HMS is applying (an extra preset or graft while it
        // runs), else the engine's weather; the DTR readout uses the same. Before b295 the dropdown showed the engine byte,
        // which a preset never moves, so it read "Blizzards" under an Annealing Winds sky.
        byte curW = MapSettings.GetDisplayWeather();
        bool curIsMapWeather = curW == 0 || curW == mapDefaultWeather || MapSettings.IsWeatherInLoadedBank(curW)
            || (mapWeatherChoices != null && mapWeatherChoices.Exists(c => c.Item1 == curW));
        string curWName = curW == 0 ? "无 - 环境音"
            : curW == mapDefaultWeather ? MapSettings.WeatherName(curW) + " (native)"
            : MapSettings.WeatherName(curW) + (curIsMapWeather ? "" : " (extra)");
        ImGui.TextDisabled("天气");
        ImGui.SetNextItemWidth(-PanelPad);
        // v0.7.474: HeightLarge. The default combo popup is ~8 rows; 958 has 9 legal weathers once CutScene is
        // promoted, so the promoted entry - appended last by design - fell below the scroll fold and read as
        // "the promotion didn't work". Promotions will always land last, so this must not clip.
        if (ImGui.BeginCombo("##mapweather", curWName, ImGuiComboFlags.HeightLarge))
        {
            void Pick(byte wid) => ApplyWeatherStatic(wid);   // live: config+broadcast · local: direct EnvManager write (see ApplyWeatherStatic)

            if (ImGui.Selectable("无 - 环境音", curW == 0)) Pick(0);
            if (mapDefaultWeather != 0 && ImGui.Selectable(MapSettings.WeatherName(mapDefaultWeather) + "（原生）", curW == mapDefaultWeather))
                Pick(mapDefaultWeather);
            ImGui.Separator();

            if (mapWeatherChoices != null)
                foreach (var (wid, wname) in mapWeatherChoices)
                {
                    if (wid == 0 || wid == mapDefaultWeather) continue;
                    if (ImGui.Selectable(wname + "##w" + wid, wid == curW)) Pick(wid);
                }

            // v0.7.475 (2026-08-17): promote the LOADED ZONE'S NATIVE ENV-BANK states into the picker. WeatherRate
            // lists only the weathers the game RANDOMLY rolls; a zone's env bank (EnvScene.WeatherIds[32], read live)
            // carries MORE — trial/story "phase" weathers that render NATIVELY (in-bank → ApplyWeather is safe, no
            // resource-loader fault) yet never appear in the rate table. Medias Res carries ~10 such states but
            // WeatherRate shows only Fair Skies. These are NOT guessable exotics (that's the "extra presets" grid for
            // FOREIGN weathers needing a cram) — they're native to THIS map, so they belong in the ordinary dropdown,
            // shown for everyone. Deduped against everything already listed above; live-read so it tracks the loaded
            // zone regardless of the sheet-derived mapWeatherChoices.
            var wShown = new System.Collections.Generic.HashSet<byte> { 0 };
            if (mapDefaultWeather != 0) wShown.Add(mapDefaultWeather);
            if (mapWeatherChoices != null) foreach (var (wid, _) in mapWeatherChoices) wShown.Add(wid);
            // Native-ONLY pick for promoted states: these render natively and must never fall through to the cram path.
            // (Routing them through Pick/SetWeatherUnified caused the dark-map bug — see MapSettings.SetWeatherNativeOnly.)
            void PickNative(byte wid) => ApplyWeatherNative(wid);   // promoted native-bank state (see ApplyWeatherNative)
            bool bankHdr = false;
            foreach (var bid in MapSettings.GetLoadedBankWeatherIds())
            {
                if (!wShown.Add(bid)) continue;   // already offered above
                if (!bankHdr) { ImGui.Separator(); ImGui.TextDisabled("本地图状态"); bankHdr = true; }
                // Name (id): env-bank phases reuse generic names ("Fair Skies" ×N) — the id disambiguates distinct states.
                if (ImGui.Selectable(MapSettings.WeatherName(bid) + " (" + bid + ")##wb" + bid, bid == curW)) PickNative(bid);
            }
            ImGui.EndCombo();
        }
        // b259: "Extra presets" (the ~70-chip cram grid + city-variant sub-block) ALWAYS opens in its own window - never an
        // inline expander. An expanded in-panel grid buried Music / Map control / Spawn point under a long scroll, so the
        // old docked collapsible + Dock/Pop-out toggle (and the showAllWeather gate) is gone: one button opens/focuses the
        // tear-off, and the grid lives ONLY in DrawWeatherPresetsWindow now. b168: no longer DEBUG-gated (cram proven).
        ImGui.Spacing();
        if (weatherPresetsPoppedOut)
            ImGui.TextDisabled("更多预设已在独立窗口中打开。");
        else
        {
            if (ImGui.Button("更多预设##openpresets"))
                weatherPresetsPoppedOut = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("在独立窗口中打开完整天气预设（可强制套用的列表 + 城市天空变体），\n避免挤占地图控制面板。");
        }

        // ── Music (BGM). Play/Stop toggle + reset-to-zone-default (single curved arrow) + Browse. The row shows the
        // CURRENTLY-PLAYING track (read live) so a name is present even before the host picks one, and it STAYS when
        // stopped (stopping silences playback but the row keeps naming the track). Friendly titles need the Orchestrion
        // community CSV (deferred) - "Track N" for now. Playback mechanism (scene-0 write) also deferred.
        ImGui.Spacing();
        // Track to NAME: the host's explicit pick if set, else the zone's STATIC default (GetDefaultBgm - now resolves
        // instanced zones correctly via CFC→InstanceContent). We do NOT use the live scene read for display: mid-load it
        // returns the previous zone's track (the stale-entry bug). The static default is reliable and refreshes with the
        // loaded zone. config.MapBgmId=0 after load (reset), so a fresh map shows its own default immediately.
        uint liveTrack = MapSettings.GetCurrentBgm();
        // Track to NAME. LIVE: the host's explicit pick, else the zone STATIC default (not the live scene read - mid-load
        // it returns the previous zone's track). LOCAL (no persisted pick): the currently-PLAYING track if it's real,
        // else the zone default - so the row names what you'd hear.
        uint shownTrack = local
            ? (liveTrack != 0 && liveTrack != 1 ? liveTrack : MapSettings.GetDefaultBgm(tt))
            : (config.MapBgmId != 0 ? config.MapBgmId : MapSettings.GetDefaultBgm(tt));
        // b236 verbatim-label: if the user picked a track from the Browse picker for THIS panel + zone, echo that exact
        // label. The picker's id may resolve (via BGMSituation/BGMSwitch) to a different concrete scene id at play time,
        // so re-deriving from the live id showed "Track N"; the remembered label is what they actually chose. Stop/Reset
        // clear the pick, and the zone-scope check drops it on a zone change - both then fall back to the derived name.
        var pick = local ? bgmPickLocal : bgmPickHost;
        string nowBgm = pick.HasValue && pick.Value.tt == tt
            ? pick.Value.label
            : (shownTrack != 0 ? MapSettings.BgmName(shownTrack) : "无");
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("音乐：");
        ImGui.SameLine();
        // Play (▶): resume the map's music. Play the PICKED track if it's a real track; if nothing is picked OR the pick
        // is the SILENCE sentinel (1, set by Stop), fall through to the zone default - otherwise Play after Stop just
        // replays silence and looks dead (the bug). LOCAL plays the track directly (scene-0 write); no persist/broadcast.
        if (ImGuiComponents.IconButton("##bgmplay", FontAwesomeIcon.Play))
        {
            if (local)
            {
                uint toPlay = (liveTrack != 0 && liveTrack != 1) ? liveTrack : MapSettings.GetDefaultBgm(tt);
                MapSettings.PlayBgm(toPlay);
            }
            else
            {
                uint picked = config.MapBgmId;
                uint toPlay = (picked != 0 && picked != 1) ? picked : MapSettings.GetDefaultBgm(tt);
                config.MapBgmId = toPlay; config.Save(); RunCommand?.Invoke("mapbgm", toPlay.ToString());
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("播放选中的曲目（未选择或停止后则播放地图默认音乐）。");
        ImGui.SameLine();
        // Stop (■): actual SILENCE. LIVE broadcasts the null track (BGM 1) so peers go quiet too; LOCAL silences here only.
        // Distinct from Reset, which restores the zone's own default music. (Writing 0 would re-resolve the default = not silence.)
        if (ImGuiComponents.IconButton("##bgmstop", FontAwesomeIcon.Stop))
        {
            if (local) { MapSettings.StopBgm(); bgmPickLocal = null; }
            else { config.MapBgmId = 1; config.Save(); RunCommand?.Invoke("mapbgm", "1"); bgmPickHost = null; }   // b236: drop the remembered pick -> row shows "Silence"
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止：关闭地图音乐（对所有人）。");
        ImGui.SameLine();
        // Reset to the zone's default track.
        if (ImGuiComponents.IconButton("##bgmreset", FontAwesomeIcon.UndoAlt))
        {
            var def = MapSettings.GetDefaultBgm(tt);
            if (local) { MapSettings.PlayBgm(def); bgmPickLocal = null; }
            else { config.MapBgmId = def; config.Save(); RunCommand?.Invoke("mapbgm", def.ToString()); bgmPickHost = null; }   // b236: drop the pick -> row shows the zone-default name
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("恢复为地图默认音乐。");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(nowBgm);
        // b238: opens the pop-out picker window (category chips + lazy per-category parse) instead of the old inline popup.
        if (ImGui.Button("浏览音乐##bgm", new Vector2(-PanelPad, 0f))) { bgmBrowseFilter = ""; OpenBgmPicker(local); }

        // NPC scene-cleanup (host-authoritative), folded in here so all scene-presentation controls live together. LOCAL
        // cinematic omits it - hiding NPCs on the REAL live zone is a session/loaded-map feature, not a cosmetic sky tweak.
        if (!local)
        {
        ImGui.Spacing();
        bool npcHide = config.MapRemoveNpcs;
        if (ImGui.Checkbox("隐藏 NPC", ref npcHide))
        { config.MapRemoveNpcs = npcHide; config.Save(); RunCommand?.Invoke("npc", npcHide ? "on" : "off"); }
        ImGui.SameLine();
        bool signHide = config.MapHideQuestSigns;
        if (ImGui.Checkbox("隐藏任务标记", ref signHide))
        { config.MapHideQuestSigns = signHide; config.Save(); RunCommand?.Invoke("qbubble", signHide ? "on" : "off"); }

        // NB-20: granular per-NPC hide. A dot-lens picker (ported from Begone!): toggle it on, then click any NPC's dot
        // in the world to hide/show that NPC kind on this map. Host-authoritative and synced; recorded per map.
        bool canEdit = CanEditNpcHides?.Invoke() ?? false;
        int hiddenCount = HiddenNpcCount?.Invoke() ?? 0;
        ImGui.Spacing();
        if (!canEdit) ImGui.BeginDisabled();
        bool picking = npcPickerActive;
        if (ImGui.Checkbox("选择要隐藏的 NPC", ref picking)) npcPickerActive = picking;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("打开场景叠加层，然后点击 NPC 的圆点来隐藏 / 显示。\n隐藏一个会隐藏本地图中该 NPC 的所有副本，并同步到会话。");
        ImGui.SameLine();
        if (hiddenCount == 0) ImGui.BeginDisabled();
        if (ImGui.Button("恢复已隐藏（" + hiddenCount + ")")) RestoreNpcHides?.Invoke();
        if (hiddenCount == 0) ImGui.EndDisabled();
        if (!canEdit) ImGui.EndDisabled();
        if (npcPickerActive && canEdit)
            ImGui.TextDisabled("选择模式已开启：点击场景中的圆点。绿 = 显示，红 = 隐藏。");
        }   // end if (!local) - NPC-hide controls

        // b213: atmosphere toggles, same row layout as Hide NPCs / Hide quest markers above. These mirror the
        // /hms stagelights and /hms vfxoff runtime toggles (map-global, no host gate — they work solo out of session
        // too, self-gating to instanced content), so the checkbox reflects the live suppression state and a click
        // flips it via the command (which is allow-listed out of session). Kept in BOTH the live and local paths.
        ImGui.Spacing();
        bool stageOut = StageLightsActive?.Invoke() ?? false;
        if (ImGui.Checkbox("切换环境灯光", ref stageOut)) RunCommand?.Invoke("stagelights", null);
        ImGui.SameLine();
        bool vfxHidden = VfxHidden?.Invoke() ?? false;
        if (ImGui.Checkbox("隐藏特效", ref vfxHidden)) RunCommand?.Invoke("vfxoff", null);

        if (local)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("仅本地：给当前地图换一个电影感天空，不保存也不同步。");
        }
    }

    /// <summary>NB-20: the dot-lens NPC picker overlay (ported from Begone!). Registered as a standalone UiBuilder.Draw
    /// handler so it renders over the game world even when the main window is on another tab. Draws a clickable dot at
    /// each rendered EventNpc; hover within ~12px highlights, a click toggles that NPC's DataId in the map's hidden set.
    /// Only active while the picker checkbox is on AND the host can edit (a virtual map is loaded).</summary>
    public void DrawNpcPickerOverlay()
    {
        if (!npcPickerActive) return;
        if (!(CanEditNpcHides?.Invoke() ?? false)) { npcPickerActive = false; return; }
        var dots = EnumerateNpcDots?.Invoke();
        if (dots == null || dots.Count == 0) return;

        var draw = ImGui.GetBackgroundDrawList();
        var mouse = ImGui.GetMousePos();
        bool clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        uint toToggle = 0;
        bool anyHover = false;

        foreach (var d in dots)
        {
            if (!gameGui.WorldToScreen(d.World, out var screen)) continue;   // behind camera / off-screen
            float dx = screen.X - mouse.X, dy = screen.Y - mouse.Y;
            bool hover = (dx * dx + dy * dy) <= (12f * 12f);
            if (hover) anyHover = true;

            // Green = currently shown (click to hide), red = currently hidden (click to show). Brighter/larger on hover.
            uint col = d.Hidden
                ? ImGui.GetColorU32(new Vector4(0.90f, 0.25f, 0.25f, hover ? 1f : 0.75f))
                : ImGui.GetColorU32(new Vector4(0.30f, 0.85f, 0.35f, hover ? 1f : 0.75f));
            float r = hover ? 7f : 5f;
            draw.AddCircleFilled(new Vector2(screen.X, screen.Y), r, col);
            draw.AddCircle(new Vector2(screen.X, screen.Y), r, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)), 0, 1.5f);

            if (hover)
            {
                string label = (string.IsNullOrEmpty(d.Name) ? "NPC" : d.Name) + "  #" + d.DataId + (d.Hidden ? "  (hidden)" : "");
                var ts = ImGui.CalcTextSize(label);
                var tp = new Vector2(screen.X + 10f, screen.Y - ts.Y * 0.5f);
                draw.AddRectFilled(new Vector2(tp.X - 3f, tp.Y - 2f), new Vector2(tp.X + ts.X + 3f, tp.Y + ts.Y + 2f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.70f)));
                draw.AddText(tp, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), label);
                if (clicked) toToggle = d.DataId;
            }
        }

        // Claim the click so it doesn't also target/interact with the game world underneath the dot.
        if (anyHover) ImGui.SetNextFrameWantCaptureMouse(true);
        if (toToggle != 0) ToggleNpcHide?.Invoke(toToggle);
    }

    // b238: the chip categories for the BGM picker. Reuses the Zones-tab classification verbatim (so a chip == a map
    // category and the territory list is free), plus the "Orchestrion" roll catalog chip. Order mirrors the Zones pill row.
    // NOTE: lazily built on first ACCESS, not as a static field-initializer. `Categories` is declared later in the file, so
    // a `= BuildBgmChips()` field-initializer would run inside the type initializer BEFORE `Categories` is set -> NRE that
    // crashes the whole plugin's render (b238 regression). Deferring to first use (well after the cctor) is order-safe.
    private static string[]? bgmChipsCache;
    private static string[] BgmChips => bgmChipsCache ??= BuildBgmChips();
    private static string[] BuildBgmChips()
    {
        var list = new List<string>(Categories.Length + 1);
        // b240: no Cutscenes chip - cutscene music isn't reachable through the territory/LGB machinery (director-driven per
        // scene, not bound to a browsable territory). b242: no "All" chip - it built the whole flat catalog on open (the
        // launch-stutter path); every chip is now a bounded lazy category. Orchestrion appended as its own chip.
        foreach (var c in Categories) { if (c == "All" || c == "Cutscenes") continue; list.Add(c); }
        list.AddRange(Services.MapSettingsService.AttributedBgmChips);   // b315/b316: titled tracks with no duty or zone, by kind
        list.Add("Orchestrion");
        return list.ToArray();
    }

    // b238: territory ids per chip category, built once from the Zones clusters. "Orchestrion" isn't zone-derived.
    private List<uint> BgmChipTerritories(string cat)
    {
        if (bgmChipZones == null)
        {
            if (clusters == null) BuildClusters();
            bgmChipZones = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
            if (clusters != null)
                foreach (var c in clusters)
                {
                    if (!bgmChipZones.TryGetValue(c.Category, out var l)) { l = new List<uint>(); bgmChipZones[c.Category] = l; }
                    foreach (var v in c.Variants) l.Add(v.Id);
                }
        }
        return bgmChipZones.TryGetValue(cat, out var ids) ? ids : new List<uint>();
    }

    // b238: the pop-out BGM picker window. Category chips across the top; the active chip's tracks below, parsed LAZILY by
    // the service (no eager all-zones sweep -> no lag). "Orchestrion" shows the roll catalog; any map category parses just
    // its own territories' LGBs on first open (cached after). Selection mirrors the inline Music row: local preview plays
    // via PlayBgm, host writes config.MapBgmId + broadcasts, and the picked label is remembered verbatim (b236). Rendered
    // every frame from HMSyncPlugin's UiBuilder.Draw; no-op unless popped out.
    public void DrawBgmPickerWindow()
    {
        if (!bgmPickerOpen || MapSettings == null) return;
        var swDraw = System.Diagnostics.Stopwatch.StartNew();   // b312: slow-frame instrument (reported once per chip)
        try { DrawBgmPickerWindowBody(); }
        finally { MapSettings?.NotePickerDraw(bgmPickerCat ?? "", lastPickerRows, swDraw.Elapsed.TotalMilliseconds); }
    }

    private int lastPickerRows;
    private void DrawBgmPickerWindowBody()
    {
        if (MapSettings == null) return;
        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        bool live = (CanLoad?.Invoke() ?? false) && loadedZone != 0;
        uint tt = live ? loadedZone : (CurrentRealTerritory?.Invoke() ?? 0);
        bool local = bgmPickerLocal;
        uint zoneDefault = tt != 0 ? MapSettings.GetDefaultBgm(tt) : 0;

        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("浏览音乐##hmsbgmpicker", ref bgmPickerOpen))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }

        // Search box.
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##bgmpickersearch", "按名称搜索…", ref bgmBrowseFilter, 128);
        var q = bgmBrowseFilter.Trim();
        bool searching = q.Length > 0;

        // Category pill row (single-select, reflowing) - mirrors the Zones tab.
        {
            float avail0 = ImGui.GetContentRegionAvail().X, x = 0f; const float gap = 6f;
            for (int i = 0; i < BgmChips.Length; i++)
            {
                string lbl = BgmChips[i];
                float w = ImGui.CalcTextSize(CatLabel(lbl)).X + ImGui.GetStyle().FramePadding.X * 2f + 4f;
                if (i > 0) { if (x + gap + w > avail0) x = 0f; else { ImGui.SameLine(0f, gap); x += gap; } }
                bool on = bgmPickerCat == lbl;
                ImGui.PushStyleColor(ImGuiCol.Button, on ? Darken(acc, 0.5f) : new Vector4(0.15f, 0.16f, 0.19f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Text, on ? Lighten(acc, 1.05f) : new Vector4(0.72f, 0.75f, 0.80f, 1f));
                if (ImGui.Button(CatLabel(lbl) + "##bgmchip" + i, new Vector2(w, 0f))) bgmPickerCat = lbl;
                ImGui.PopStyleColor(4);
                x += w;
            }
        }
        ImGui.Separator();

        uint activeSel = local ? MapSettings.GetCurrentBgm() : config.MapBgmId;

        if (ImGui.BeginChild("##bgmpickerlist", new Vector2(0f, 0f)))
        {
            if (bgmPickerCat == "Orchestrion")
            {
                DrawBgmPickerOrchestrion(q, searching, activeSel, local);
            }
            else
            {
                // Zone default + None affordances (top of every track chip).
                if (zoneDefault != 0 && (!searching || "zone default".Contains(q, StringComparison.OrdinalIgnoreCase)))
                    BgmPickerRow(zoneDefault, "Zone default (" + MapSettings.BgmName(zoneDefault) + ")", activeSel, tt, local);
                if (!searching || "none".Contains(q, StringComparison.OrdinalIgnoreCase))
                    BgmPickerRow(0, "None", activeSel, tt, local);

                // The chip's tracks: parse this category's territories lazily (background, cached), then list its bucket
                // sorted by name. b242: no "All" flat-catalog branch any more - every chip is a bounded lazy category.
                MapSettings.EnsureCategoryParsed(bgmPickerCat, BgmChipTerritories(bgmPickerCat));
                if (!MapSettings.IsCategoryReady(bgmPickerCat))
                    ImGui.TextDisabled("正在加载 " + CatLabel(bgmPickerCat) + " 曲目…");
                lastPickerRows = 0;   // b312
                foreach (var kv in MapSettings.GetCategoryTracks(bgmPickerCat)
                             .Where(kv => kv.Key != zoneDefault)
                             .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                {
                    lastPickerRows++;
                    if (searching && !kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    BgmPickerRow(kv.Key, kv.Value, activeSel, tt, local);
                }
            }
        }
        ImGui.EndChild();
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    // b238: one selectable track row in the pop-out picker. Same routing as the inline Music row: local preview via
    // PlayBgm, host via config.MapBgmId + broadcast; the picked label is remembered verbatim (b236). id 0 = "None".
    private void BgmPickerRow(uint id, string name, uint activeSel, uint tt, bool local)
    {
        if (ImGui.Selectable(name + "##bgmpick" + id, activeSel == id))
        {
            if (local) { MapSettings!.PlayBgm(id); bgmPickLocal = (tt, id, name); }
            else { config.MapBgmId = id; config.Save(); RunCommand?.Invoke("mapbgm", id.ToString()); bgmPickHost = (tt, id, name); }
        }
    }

    // b238: the Orchestrion section, grouped by the game's genre categories (mirrors the inline popup's orchestrion block).
    private void DrawBgmPickerOrchestrion(string q, bool searching, uint activeSel, bool local)
    {
        var orch = MapSettings!.GetOrchestrionChoices();
        string curCat = "";
        foreach (var (sel, title, category, catOrder, order) in orch)
        {
            if (searching && !title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            if (category != curCat)
            {
                curCat = category;
                if (!string.IsNullOrEmpty(category))
                    ImGui.TextColored(new Vector4(0.55f, 0.70f, 0.90f, 1f), category);
            }
            if (ImGui.Selectable(title + "##bgmpickorch" + sel, activeSel == sel))
            {
                if (local) MapSettings.PlayBgm(sel);
                else { config.MapBgmId = sel; config.Save(); RunCommand?.Invoke("mapbgm", sel.ToString()); }
            }
        }
    }

    // "This map" spawn-point editor (loaded-map scoped). BGM moved into Time & weather; Hide-NPCs into the
    // maps-menu filter row - spawn is what remains. Set-from-here + a same-size revert arrow; the live
    // coordinate readout is toggle-gated and rides a permanently-reserved line so toggling never reflows.
    // CN port: duty arena MapEffect panel. Each slot is one switchable set piece (phase floor, barrier, lights...);
    // its states come from the SGB's timeline table. Applying needs the duty director, which is only created on load
    // when the toggle is on.
    private void DrawMapEffects()
    {
        bool enabled = config.EnableMapEffects;
        if (ImGui.Checkbox("加载副本时启用场地机关", ref enabled)) SetMapEffectsEnabled?.Invoke(enabled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启后，加载副本地图时会创建副本控制器，才能切换转场场地、屏障、灯光等机关。\n副作用：屏幕上会出现“任务信息”栏。改动后需重新加载地图生效。");

        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        if (loadedZone == 0) { ImGui.TextDisabled("先加载一张地图。"); return; }

        // Area jump: boss arenas, event / ending rooms (e.g. a deep dungeon's floor 100), phase arenas - read from the
        // level's LGB layers, so hidden rooms far below/above the spawn are one click away.
        var areas = GetZoneAreas?.Invoke(loadedZone);
        if (areas != null && areas.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("区域跳转（" + areas.Count + "）");
            foreach (var a in areas)
            {
                ImGui.PushID("area" + a.Layer);
                if (ImGui.Button("传送##go", new Vector2(52f, 0f))) OnTeleport?.Invoke(a.Position);
                ImGui.SameLine(0f, 8f);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(a.Label);
                ImGui.SameLine(0f, 8f);
                ImGui.TextDisabled(a.Position.X.ToString("F0") + ", " + a.Position.Y.ToString("F0") + ", " + a.Position.Z.ToString("F0"));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(a.Layer + "\n位置可能在空中或地下，掉落时先开 /wlens fly 再传送。");
                ImGui.PopID();
            }
        }

        DrawEnvWeathers(loadedZone);
        DrawLayoutKeys(loadedZone);
        DrawPhasePresets(loadedZone);
        DrawFestivals(loadedZone);
        DrawSceneLayers(loadedZone);

        var slots = GetMapEffectSlots?.Invoke(loadedZone);
        if (MapEffectDirectorReady?.Invoke() ?? false) EnsureMapEffectSnapshot?.Invoke(loadedZone);
        ImGui.Spacing();
        bool canReset = HasMapEffectSnapshot?.Invoke(loadedZone) ?? false;
        if (!canReset) ImGui.BeginDisabled();
        if (ImGui.Button("复位机关")) ResetMapEffects?.Invoke(loadedZone);
        if (!canReset) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("把所有机关恢复到加载地图时的状态（不重载地图）。");
        if (slots == null || slots.Count == 0) { ImGui.TextDisabled("这张地图没有可切换的机关。"); return; }

        bool ready = MapEffectDirectorReady?.Invoke() ?? false;
        if (!ready)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f),
                enabled ? "副本控制器未就绪，请重新加载这张地图。" : "勾选上方开关并重新加载地图后可用。");
        }

        // Gimmick objects (hourglasses, towers, orbs...) are hidden after each load by default; picking a slot state
        // un-hides that one. Works without the director (it only hides geometry).
        bool hideObj = config.HideMapEffectObjects;
        if (ImGui.Checkbox("加载后隐藏机关物体", ref hideObj)) { config.HideMapEffectObjects = hideObj; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("本地加载时所有机关物体（沙漏、塔、球体等）都会按默认状态显示。\n开启后加载地图会自动隐藏它们；给某个槽位选状态时只显示那一个。");
        ImGui.SameLine();
        if (ImGui.SmallButton("全部隐藏##meobjhide")) SetMapEffectObjectsHidden?.Invoke(loadedZone, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("全部显示##meobjshow")) SetMapEffectObjectsHidden?.Invoke(loadedZone, false);
        ImGui.SameLine();
        ImGui.TextDisabled("已隐藏 " + (MapEffectObjectsHiddenCount?.Invoke() ?? 0));

        ImGui.TextDisabled("共 " + slots.Count + " 个机关槽位。点选状态即刻切换。");
        if (!ready) ImGui.BeginDisabled();
        foreach (var s in slots)
        {
            ImGui.PushID("me" + s.Slot);
            var cur = MapEffectState?.Invoke(s.Slot);
            var curName = cur.HasValue ? StateLabel(s, cur.Value) : "?";
            ImGui.SetNextItemWidth(-PanelPad);
            if (ImGui.BeginCombo("##st", "#" + s.Slot + "  " + s.SgbName + "  —  " + curName))
            {
                if (ImGui.Selectable("0（复位）", cur == 0)) ApplyMapEffect?.Invoke(s.Slot, 0);
                foreach (var (state, name) in s.States)
                    if (ImGui.Selectable(state + "：" + (string.IsNullOrEmpty(name) ? "（无名）" : name), cur == state))
                        ApplyMapEffect?.Invoke(s.Slot, state);
                if (s.States.Length == 0)
                    foreach (ushort st in new ushort[] { 1, 2, 4, 8, 16, 32 })
                        if (ImGui.Selectable(st + "（未解析）", cur == st)) ApplyMapEffect?.Invoke(s.Slot, st);
                // Timeline playback (Hyperborea's timeline override): keep the current state but play another
                // timeline - transitions (floor breaking, gates opening) often live in a timeline that is not a
                // resting state of its own.
                if (s.States.Length > 0 && cur.HasValue && cur.Value != 0)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled("保持当前状态，只播放动画：");
                    foreach (var (state, name) in s.States)
                        if (ImGui.Selectable("播放 " + state + "：" + (string.IsNullOrEmpty(name) ? "（无名）" : name) + "##tl" + state))
                            ApplyMapEffectTimeline?.Invoke(s.Slot, cur.Value, state);
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(s.SgbPath)) ImGui.SetTooltip(s.SgbPath);
            ImGui.PopID();
        }
        if (!ready) ImGui.EndDisabled();
    }

    // CN port: Hyperborea-style phase presets - save the current arena state (all slot states + weather + festivals)
    // under a name, re-apply it in one click. Stored per territory in HMSyncConfig.MapPhasePresets.
    private void DrawPhasePresets(uint zone)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("阶段预设");
        ImGui.SetNextItemWidth(-110f);
        ImGui.InputTextWithHint("##phasename", "预设名称（例如 P5 结束）", ref phasePresetName, 64);
        ImGui.SameLine(0f, 6f);
        if (ImGui.Button("保存当前##phasesave", new Vector2(-PanelPad, 0f)))
        {
            SavePhasePreset?.Invoke(zone, phasePresetName, new List<uint>(hiddenLayers.Keys));
            phasePresetName = "";
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("把当前所有机关状态、天气、节日装饰和隐藏的场景图层保存为一个预设。");

        // Built-in presets: scene-layer visibility per phase (hand-tuned for known duties, auto from "_pN" layer tags).
        var builtin = GetBuiltinPresets?.Invoke(zone);
        if (builtin != null && builtin.Count > 0)
        {
            for (int i = 0; i < builtin.Count; i++)
            {
                var b = builtin[i];
                ImGui.PushID("bphase" + i);
                if (ImGui.Button("应用##bap", new Vector2(52f, 0f)))
                {
                    var bp = new MapPhasePreset
                    {
                        Name = b.Name, HiddenLayers = new List<uint>(b.Hidden),
                        ForceShownLayers = new List<uint>(b.ForceShown), IsBuiltin = true,
                        Weather = b.Weather > 0 ? (byte)b.Weather : (byte)0, WeatherZoneDefault = b.Weather == -1,
                        WeatherNone = b.Weather == -2, WeatherNative = b.NativeWeather,
                    };
                    foreach (var kv in b.Effects) bp.Effects.Add(new MapPhaseEffect { Slot = kv.Key, State = kv.Value });
                    ApplyPresetLayers(zone, bp);
                    ApplyPhasePreset?.Invoke(bp);
                }
                ImGui.SameLine(0f, 8f);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(b.Name);
                ImGui.SameLine(0f, 8f);
                ImGui.TextDisabled(b.IsFlow
                    ? "内置流程 · 天气 " + (b.Weather == -1 ? "默认" : b.Weather == -2 ? "无" : b.Weather.ToString()) + " · " + b.Effects.Count + " 机关"
                      + (b.Hidden.Count > 0 ? " · 隐藏 " + b.Hidden.Count + " 图层" : "")
                    : "内置 · 隐藏 " + b.Hidden.Count + " 图层");
                ImGui.PopID();
            }
            ImGui.TextDisabled(builtin.Exists(x => x.IsFlow)
                ? "内置流程来自实战录制（AnoMech），切换天气和机关；需开启“加载副本时启用场地机关”。"
                : "内置预设按图层名推断，只切换场景图层；可再调整机关后另存。");
        }

        if (!config.MapPhasePresets.TryGetValue(zone, out var list) || list.Count == 0)
        {
            ImGui.TextDisabled("（这张地图还没有预设）");
            return;
        }
        MapPhasePreset? toDelete = null;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            ImGui.PushID("phase" + i);
            if (ImGui.Button("应用##ap", new Vector2(52f, 0f)))
            {
                ApplyPresetLayers(zone, p);
                ApplyPhasePreset?.Invoke(p);
            }
            ImGui.SameLine(0f, 8f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(p.Name);
            ImGui.SameLine(0f, 8f);
            ImGui.TextDisabled(p.Effects.Count + " 机关 · 天气 " + p.Weather + " · " + p.Festivals.Count + " 节日 · " + p.HiddenLayers.Count + " 图层");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - 52f);
            bool ctrl = ImGui.GetIO().KeyCtrl;
            if (!ctrl) ImGui.BeginDisabled();
            if (ImGui.Button("删除##del", new Vector2(52f, 0f))) toDelete = p;
            if (!ctrl) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("按住 Ctrl 再点删除。");
            ImGui.PopID();
        }
        if (toDelete != null) DeletePhasePreset?.Invoke(zone, toDelete);
    }

    // CN port: every weather the zone's environment file defines (incl. duty-only phase / victory weathers that the
    // regular weather list never offers). Phase scenery is mostly weather-driven, so this is the generic "switch the
    // look of this map" control for free exploration. Writes natively (no sky cram).
    private void DrawEnvWeathers(uint zone)
    {
        var list = GetZoneWeathers?.Invoke(zone);
        if (list == null || list.Count == 0) return;
        ImGui.Spacing();
        ImGui.TextDisabled("环境天气（" + list.Count + "）· 转场 / 通关场景多由天气切换");
        byte cur = ActiveWeather?.Invoke() ?? 0;
        var acc = Accent();
        float avail = ImGui.GetContentRegionAvail().X, x = 0f; const float gap = 6f;
        void Btn(byte id, string label)
        {
            float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f + 4f;
            if (x > 0f) { if (x + gap + w > avail) x = 0f; else { ImGui.SameLine(0f, gap); x += gap; } }
            bool on = cur == id;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, Darken(acc, 0.5f));
            if (ImGui.Button(label + "##envwx" + id, new Vector2(w, 0f))) ApplyEnvWeather?.Invoke(id);
            if (on) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("天气 ID " + id + (on ? "（当前）" : ""));
            x += w;
        }
        Btn(0, "无");
        foreach (var (id, name) in list) Btn(id, id + " " + name);
    }

    // CN port: Hyperborea-style "Story Progress" - pick which layer-set key the map loads with (story states of a
    // city, built vs ruined towns, alternate duty layouts). Re-loads the map cold with that key.
    private void DrawLayoutKeys(uint zone)
    {
        var keys = GetLayoutKeys?.Invoke(zone);
        if (keys == null || keys.Count == 0) return;
        uint cur = CurrentLayoutKey?.Invoke() ?? 0;
        ImGui.Spacing();
        ImGui.TextDisabled("剧情进度 / 布局版本（" + keys.Count + "）");
        string Label(MapEffectService.LayoutKey k) => k.Title + (k.PluginDefault ? "（插件默认）" : "");
        var forced = keys.Find(k => k.PluginDefault);
        string defLabel = forced != null ? "默认（插件固定为 " + forced.Title + "）" : "默认（游戏原本的版本）";
        var curKey = keys.Find(k => k.Key == cur);
        ImGui.SetNextItemWidth(-PanelPad);
        if (ImGui.BeginCombo("##layoutkey", cur == 0 || curKey == null ? defLabel : Label(curKey)))
        {
            if (ImGui.Selectable(defLabel, cur == 0)) LoadLayoutKey?.Invoke(zone, 0);
            foreach (var k in keys)
            {
                if (ImGui.Selectable(Label(k) + "##lk" + k.Key, cur == k.Key)) LoadLayoutKey?.Invoke(zone, k.Key);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("编号 " + k.Key + "：新增 " + k.Include + " 个图层，隐藏 " + k.Exclude + " 个图层"
                                     + (k.Samples.Count > 0 ? "\n图层：" + string.Join(", ", k.Samples) : ""));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("名称按图层名推断：阶段 vN 通常是剧情推进的先后，数字越大越靠后。\n切换会重新加载地图（已在本图时先经过旅馆再回来）。");
    }

    // CN port: scene layer toggles. Phase scenery in many duties (FRU P2-P5 floors, debris, lighting...) is whole bg
    // layers the server shows/hides per phase; a client-side load shows them all, so hiding the other phases' layers
    // reproduces a given phase. Hidden state is local and dropped when another map loads.
    private void DrawSceneLayers(uint zone)
    {
        if (hiddenLayersZone != zone)
        {
            foreach (var l in hiddenLayers.Values) SetSceneLayerHidden?.Invoke(l, false);   // release the per-frame hold
            hiddenLayers.Clear();
            hiddenLayersZone = zone;
        }
        var layers = GetSceneLayers?.Invoke(zone);
        if (layers == null || layers.Count == 0) return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("场景图层（" + layers.Count + "，已隐藏 " + hiddenLayers.Count + "）##scenelayers")) return;
        ImGui.TextDisabled("取消勾选即隐藏该图层。转阶段的场景常按图层切换，例如光暗未来的 n4gw_p2-3、p4-5、p6-7。");
        ImGui.SetNextItemWidth(-90f);
        ImGui.InputTextWithHint("##layerfilter", "按图层名筛选，例如 p4", ref layerFilter, 64);
        ImGui.SameLine(0f, 6f);
        bool any = hiddenLayers.Count > 0;
        if (!any) ImGui.BeginDisabled();
        if (ImGui.Button("全部显示##layershowall", new Vector2(-PanelPad, 0f)))
        {
            foreach (var l in hiddenLayers.Values) SetSceneLayerHidden?.Invoke(l, false);
            hiddenLayers.Clear();
        }
        if (!any) ImGui.EndDisabled();

        foreach (var l in layers)
        {
            if (layerFilter.Length > 0 && l.Name.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool shown = !hiddenLayers.ContainsKey(l.Id);
            if (ImGui.Checkbox(l.Name + "##ly" + l.Id, ref shown))
            {
                if (shown) hiddenLayers.Remove(l.Id); else hiddenLayers[l.Id] = l;
                SetSceneLayerHidden?.Invoke(l, !shown);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(l.InstanceIds.Count + " 个物体");
        }
    }

    // Brings the scene-layer visibility in line with a preset: shows layers it doesn't hide, hides the ones it does.
    private void ApplyPresetLayers(uint zone, MapPhasePreset p)
    {
        var layers = GetSceneLayers?.Invoke(zone);
        if (layers == null) return;
        var want = new HashSet<uint>(p.HiddenLayers);
        foreach (var id in new List<uint>(hiddenLayers.Keys))
            if (!want.Contains(id)) { SetSceneLayerHidden?.Invoke(hiddenLayers[id], false); hiddenLayers.Remove(id); }
        foreach (var l in layers)
            if (want.Contains(l.Id) && !hiddenLayers.ContainsKey(l.Id)) { hiddenLayers[l.Id] = l; SetSceneLayerHidden?.Invoke(l, true); }
        // Force-show scenery the game keeps drawn-invisible until the server reveals it (e.g. FRU's clear flower field).
        var force = new HashSet<uint>(p.ForceShownLayers);
        foreach (var l in layers)
            if (force.Contains(l.Id) && !want.Contains(l.Id)) SetSceneLayerHidden?.Invoke(l, false);
        hiddenLayersZone = zone;
    }

    // CN port: festival decoration toggles (Hyperborea feature). Lists the festivals whose layers exist in this level;
    // each combo picks Off or a phase, applied to the live layout immediately. Local only; reloading the map resets.
    private void DrawFestivals(uint zone)
    {
        var fests = GetZoneFestivals?.Invoke(zone);
        if (fests == null || fests.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled("节日装饰（" + fests.Count + "）");
        bool showUnsafe = config.ShowUnsafeFestivals;
        ImGui.SameLine();
        if (ImGui.Checkbox("显示不安全的##unsafefest", ref showUnsafe)) { config.ShowUnsafeFestivals = showUnsafe; config.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hyperborea 标记为不安全的节日（多为地区限定），切换可能出错或崩溃。");

        var active = GetActiveFestivals?.Invoke() ?? new List<(ushort, ushort)>();
        foreach (var (id, phases) in fests)
        {
            FestivalNames.All.TryGetValue(id, out var info);
            if (info != null && info.Unsafe && !showUnsafe) continue;
            int cur = active.FindIndex(a => a.Id == id);
            string name = info?.Name ?? ("节日 #" + id);
            string PhaseName(int ph) => ph == 0 ? "开启" : (info != null && info.Phases.TryGetValue(ph, out var pn) ? pn : "阶段 " + ph);
            string preview = cur < 0 ? "关闭" : PhaseName(active[cur].Phase);

            ImGui.PushID("fest" + id);
            ImGui.SetNextItemWidth(-PanelPad);
            if (ImGui.BeginCombo("##f", name + "  —  " + preview))
            {
                if (ImGui.Selectable("关闭", cur < 0))
                {
                    var next = new List<(ushort, ushort)>(active);
                    next.RemoveAll(a => a.Item1 == id);
                    SetFestivals?.Invoke(next);
                }
                foreach (var ph in phases)
                {
                    if (ImGui.Selectable(PhaseName(ph) + "##p" + ph, cur >= 0 && active[cur].Phase == ph))
                    {
                        var next = new List<(ushort, ushort)>(active);
                        next.RemoveAll(a => a.Item1 == id);
                        if (next.Count < 8) next.Add(((ushort)id, (ushort)ph));
                        SetFestivals?.Invoke(next);
                    }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("节日 ID " + id + (info?.Unsafe == true ? "（不安全）" : ""));
            ImGui.PopID();
        }
    }

    private static string StateLabel(MapEffectSlotInfo s, ushort state)
    {
        if (state == 0) return "0";
        foreach (var (st, name) in s.States)
            if (st == state) return st + "：" + name;
        return state.ToString();
    }

    private void DrawThisMapControls()
    {
        uint loadedZone = CurrentLoadedZone?.Invoke() ?? 0;
        // NB-8: spawn/teleport is a LOCAL, peer-inclusive convenience - gate only on "a virtual zone is loaded on
        // THIS client" (CurrentLoadedZone!=0 tracks IsZoneLoaded 1:1), NOT on map authority. CanLoad = HasMapAuthority
        // is false for a peer, so requiring it here left peers stuck on "Load a map..." even while standing in the
        // host's loaded map. Time & weather (DrawTimeAndWeather) stays authority-gated - that's synced map control.
        bool live = loadedZone != 0;

        if (!live) { ImGui.TextDisabled("加载地图后才能设置出生点。"); return; }

        // Status: the SAVED custom spawn for this map vs the game default. HasUserSpawn is stage-aware (checks the
        // swap-stage bg key as well as the territory key) - the reset button gates on it. For the coordinate readout we
        // can only cheaply show the territory-keyed value; on a swap stage we show a generic "custom set" line (the
        // exact coords live under the bg key, which the panel doesn't resolve - the reset button still works).
        bool hasTerrSpawn = config.UserSpawns.TryGetValue(loadedZone, out var us) && us.Length >= 3;
        bool hasSpawn = HasUserSpawn?.Invoke(loadedZone) ?? hasTerrSpawn;
        if (hasTerrSpawn)
            // v0.7.340: display in native X, Y, Z order (elevation is Y in the game). Stored array is [X,Y,Z,facing];
            // print [0],[1],[2] straight through - no transposition (reverted the old X,Z,Y display).
            ImGui.TextColored(new Vector4(0.40f, 0.76f, 0.61f, 1f),
                "自定义出生点  " + us![0].ToString("F1") + "  " + us![1].ToString("F1") + "  " + us![2].ToString("F1"));
        else if (hasSpawn)
            ImGui.TextColored(new Vector4(0.40f, 0.76f, 0.61f, 1f), "此场景已设置自定义出生点");
        else
            ImGui.TextDisabled("使用默认出生点");

        // Set + reset. Reset is a tidy square - default IconButton, same UndoAlt glyph and dimensions as the
        // time/BGM reverts - greyed when there's nothing to reset.
        ImGui.Spacing();
        if (ImGui.Button("设置出生点")) CaptureSpawnFor?.Invoke(loadedZone);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("把当前位置和朝向保存为这张地图的出生点。");
        ImGui.SameLine(0f, 8f);
        if (!hasSpawn) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton("##spawnresetmenu", FontAwesomeIcon.UndoAlt))
            RevertSpawnFor?.Invoke(loadedZone);
        if (!hasSpawn) ImGui.EndDisabled();
        if (hasSpawn && ImGui.IsItemHovered()) ImGui.SetTooltip("重置出生点");

ImGui.Spacing();

        // Live coordinates: always shown (NB-8: the "Show coordinates" toggle is gone - coords are useful enough,
        // for host/solo and for exploring peers, that gating them behind a tickbox was pure friction). The fields
        // show your position continuously; DOUBLE-CLICK one to edit it into a teleport target, then Teleport. No
        // "Set to here" - the fields already read "here". (Only reachable with a zone loaded, so teleport can't move
        // you on an un-loaded real map.)
        {
            var livePos = LivePosition?.Invoke();
            if (!coordsEditing && livePos.HasValue) { tpX = livePos.Value.X; tpY = livePos.Value.Y; tpZ = livePos.Value.Z; }

            var cf = coordsEditing ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly;

            // v0.7.340: native X, Y, Z order (Y = elevation, as the game stores it) - reverted the old X, Z, Y display
            // transposition. Labels sit exactly on each box's left edge: capture the cursor X at the label row, then
            // place the three boxes on the next row at the SAME three X positions (box width 76 + 6px gap = 82px
            // stride). Anchoring to the live cursor X (not absolute window offsets) keeps labels aligned regardless of
            // panel indent - the standing GUI alignment rule.
            float col0 = ImGui.GetCursorPosX();
            float stride = 82f;   // 76 box + 6 gap
            ImGui.TextDisabled("X");
            ImGui.SameLine(); ImGui.SetCursorPosX(col0 + stride);      ImGui.TextDisabled("Y");
            ImGui.SameLine(); ImGui.SetCursorPosX(col0 + stride * 2f); ImGui.TextDisabled("Z");

            if (coordsEditing && coordFocusField == 0) ImGui.SetKeyboardFocusHere();
            ImGui.SetCursorPosX(col0);
            ImGui.SetNextItemWidth(76); ImGui.InputFloat("##tpx", ref tpX, 0f, 0f, "%.3f", cf);
            if (!coordsEditing && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { coordsEditing = true; coordFocusField = 0; }
            ImGui.SameLine(0f, 6f);
            if (coordsEditing && coordFocusField == 1) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(76); ImGui.InputFloat("##tpy", ref tpY, 0f, 0f, "%.3f", cf);
            if (!coordsEditing && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { coordsEditing = true; coordFocusField = 1; }
            ImGui.SameLine(0f, 6f);
            if (coordsEditing && coordFocusField == 2) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(76); ImGui.InputFloat("##tpz", ref tpZ, 0f, 0f, "%.3f", cf);
            if (!coordsEditing && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { coordsEditing = true; coordFocusField = 2; }

            // Focus applied for one frame on entering edit, then cleared. Edit mode PERSISTS until Teleport so a set
            // target isn't lost by clicking elsewhere; Teleport resumes the live readout.
            if (coordFocusField >= 0) coordFocusField = -1;

            ImGui.Spacing();
            if (PrimaryButton("传送", new Vector2(-PanelPad, 0f))) { OnTeleport?.Invoke(new Vector3(tpX, tpY, tpZ)); coordsEditing = false; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(coordsEditing ? "传送到上方坐标（X、Y、Z）。" : "双击坐标可编辑（X、Y、Z），然后点“传送”。");

            // Forward hop: propel the actor N units along its current facing (Y preserved). Editable distance + button
            // on one row. Handy for punching through a wall/gap without editing raw coords. Local-only, like Teleport.
            ImGui.Spacing();
            ImGui.SetNextItemWidth(76);
            if (ImGui.InputFloat("##tpfwd", ref tpForwardUnits, 0f, 0f, "%.0f")) { if (tpForwardUnits < 0f) tpForwardUnits = 0f; }
            ImGui.SameLine(0f, 6f);
            if (ImGui.Button("向前传送", new Vector2(-PanelPad, 0f))) OnTeleportForward?.Invoke(tpForwardUnits);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("向前推进 " + tpForwardUnits.ToString("F0") + " 个单位（沿面朝方向）。");
        }
    }

    // Participants table (shared by host & guest). Host sees per-peer right-click actions; guest sees just the roster.
    private void DrawParticipantsSection()
    {
        var parts = SessionParticipants?.Invoke() ?? new List<ParticipantRow>();
        int cap = relay.RoomCap;   // relay-authoritative; 0 = don't show a ceiling
        string occ = cap > 0 ? parts.Count + "/" + cap : parts.Count.ToString();
        if (!ImGui.CollapsingHeader("成员（" + occ + " 在会话中）", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var tableH = config.DashParticipantsHeight;
        var pflags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY
                     | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("##participants", 5, pflags, new Vector2(0f, tableH)))
        {
            SuspendWrap();   // v0.7.432
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("服务器", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("部队", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("距离", ImGuiTableColumnFlags.WidthFixed, 68f);
            ImGui.TableHeadersRow();
            if (parts.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextDisabled("-");
                ImGui.TableNextColumn(); ImGui.TextDisabled("等待成员加入…");
                ImGui.TableNextColumn(); ImGui.TextDisabled("-");
                ImGui.TableNextColumn(); ImGui.TextDisabled("-");
                ImGui.TableNextColumn(); ImGui.TextDisabled("-");
            }
            else
            {
                int n = 0;
                foreach (var p in parts)
                {
                    n++;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextDisabled(n.ToString());
                    ImGui.TableNextColumn();
                    var display = string.IsNullOrWhiteSpace(p.Name) ? "(resolving…)" : p.Name;
                    if (p.IsHost) display += "  (host)";
                    if (p.IsHost) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f, 0.65f, 0.25f, 1f));
                    ImGui.Selectable(display + "##row" + (string.IsNullOrEmpty(p.PeerId) ? "自己" + n : p.PeerId),
                        false, ImGuiSelectableFlags.SpanAllColumns);
                    if (p.IsHost) ImGui.PopStyleColor();
                    // Right-click menu. "Teleport to" is a purely local self-move (no host authority, no relay), so
                    // it's offered to every member - guests get lost on the big map too. Host-only actions (transfer
                    // host / kick) sit below a separator, gated on relay.IsHost inside the same popup.
                    if (!p.IsSelf && ImGui.BeginPopupContextItem("##ctx" + p.PeerId))
                    {
                        // Gate on the SAME condition as fly/noclip/carpet (MovementResearchAllowed: a loaded HMS map/
                        // cutscene, sandboxed behind the packet filter, OR research mode via /hms debug). On the bare
                        // live server zone teleporting to a peer is a real-server teleport cheat, so it's blocked there
                        // unless the debug magic is on. Still greys until the peer's live body is resolved this frame.
                        bool tpAllowed = MovementResearchAllowed?.Invoke() ?? false;
                        bool tpEnabled = p.Resolved && tpAllowed;
                        if (!tpEnabled) ImGui.BeginDisabled();
                        if (ImGui.MenuItem("传送到")) TeleportToPeer?.Invoke(p.PeerId);
                        if (!tpEnabled) ImGui.EndDisabled();
                        if (!tpAllowed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip("请先加载地图或过场场景。");
                        if (relay.IsHost)
                        {
                            ImGui.Separator();
                            if (ImGui.MenuItem("移交房主")) TransferHost?.Invoke(p.PeerId);
                            if (ImGui.MenuItem("踢出")) KickPeer?.Invoke(p.PeerId);
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(p.World ?? "");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(string.IsNullOrWhiteSpace(p.Fc) ? "-" : p.Fc);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Distance >= 0 ? p.Distance.ToString("F0") + "y" : "-");
                    // v0.7.430 - compass arrow after the distance, same baseline (alignment rule). Points the
                    // way to turn the screen to face the peer. Adapted from Wholist DrawDirectionArrow.
                    if (p.Bearing is { } bearing)
                    {
                        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                        DrawCompassArrow(bearing);
                    }
                }
            }
            ResumeWrap();
            ImGui.EndTable();
        }
        GridResizeHandle("##partsresize", () => config.DashParticipantsHeight,
            v => config.DashParticipantsHeight = v, () => config.Save());
        if (parts.Count > 1)
            ImGui.TextDisabled(relay.IsHost
                ? "右键成员可传送到其身边，或执行房主操作（移交房主 / 踢出）。"
                : "右键成员可传送到其身边。");
    }

    // v0.7.430 - compass arrow, adapted from Wholist (UserInterface/.../NearbyPlayers.window.cs
    // DrawDirectionArrow). Draws a filled triangle inside a text-height square, rotated to `bearing`
    // (radians, camera-relative). Reserves a Dummy of one text line so it shares the row baseline with
    // the distance number (the HMSync alignment rule). Colour follows the current text colour so it
    // inherits the host-amber tint when on the host row.
    private static void DrawCompassArrow(double bearing)
    {
        const double AngleShift = 0.5235987755982988;   // 30° half-spread of the arrowhead
        ImGui.Dummy(new Vector2(ImGui.GetTextLineHeight()));
        var col = ImGui.GetColorU32(ImGuiCol.Text);
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var mid = Vector2.Lerp(rectMin, rectMax, 0.5f);
        var half = (rectMax - rectMin) * 0.4f;
        var main = ArrowUnit(bearing);
        var head = mid + (half * main);
        var tail = mid - (half * main * 0.3333333f);
        var dl = ImGui.GetWindowDrawList();
        dl.AddTriangleFilled(head, tail, mid - (half * ArrowUnit(bearing + AngleShift)), col);
        dl.AddTriangleFilled(tail, head, mid - (half * ArrowUnit(bearing - AngleShift)), col);
    }

    private static Vector2 ArrowUnit(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new Vector2((float)cos, (float)sin);
    }

    // Debug/dev tools - behind a checkbox, at the very bottom, any state.
    // Command reference at the base of the Session tab - the common user commands always, the dev/debug set as a
    // quiet list (not the old button grid) when Debug mode is on. Reference only; type them in chat.
    private void DrawSessionDevTools()
    {
        ImGui.Spacing(); ImGui.Separator();
        // v0.7.432: dropped the faint "Commands" / "Debug commands (toggle…)" caption lines - the command
        // references below are self-explanatory and the captions were redundant helper text.
        ImGui.TextDisabled("/wlens start · /wlens join <房间码> · /wlens starts");
        ImGui.TextDisabled("/wlens load <地图> · /wlens leave · /wlens stop");
        // v0.7.456: the debug-command list moved to a documented "Debug commands" panel at the bottom of the
        // Config tab (shown when Debug mode is on). The Session tab keeps just the everyday user commands.
    }

    // Tab: Config - settings, say-opcode management, debug-mode toggle (S328p).
    private uint opcodeInOut, opcodeInIn;   // manual key-in scratch fields
    private bool opcodeFieldsInit;
    private void DrawConfigTab()
    {
        var tabFlags = ImGuiTabItemFlags.None;
        if (focusConfigTab) { tabFlags = ImGuiTabItemFlags.SetSelected; focusConfigTab = false; }   // installer "Settings" jump
        if (!ImGui.BeginTabItem("设置", tabFlags)) return;
        BeginTabBody("##configbody");

        // ── Relay service (v0.7.248 UX pass) ── Normal mode: one relay (Enceladeum), name + status on one line, a
        // key field, and the closed-beta note. The dropdown, localhost, and custom-service controls are DEBUG-ONLY
        // (nobody self-hosts this architecture for multibox) - they live in the Developer panel below.
        bool debugMode = DebugMode?.Invoke() ?? false;
        BeginPanel("中继");
        {
            var services = config.RelayServices;
            int sel = config.SelectedRelayService;
            if (sel < 0 || sel >= services.Count) sel = 0;
            var keyStat = RelayKeyStatusGet?.Invoke() ?? RelayKeyStatus.NoKey;

            // The status dot reflects the KEY-handshake result (green only once the relay actually upgrades us to a
            // WebSocket): grey = no key, green = accepted (HTTP 101), amber = the handshake failed - which is bad key
            // OR server down (indistinguishable over the Cloudflare tunnel, so we don't assert "invalid key"), red =
            // the configured URL couldn't be parsed to even try. Draws next to the relay name.
            void KeyStatusDot()
            {
                switch (keyStat)
                {
                    case RelayKeyStatus.Accepted: ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.42f, 1f), "● 密钥有效"); break;
                    case RelayKeyStatus.Invalid:
                        ImGui.TextColored(new Vector4(0.90f, 0.68f, 0.30f, 1f), "● 无法连接");
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("请检查密钥，或者服务器可能已离线。");
                        break;
                    case RelayKeyStatus.Unreachable: ImGui.TextColored(new Vector4(0.85f, 0.42f, 0.42f, 1f), "● 无法访问"); break;
                    case RelayKeyStatus.Checking: ImGui.TextDisabled("○ 检测中…"); break;
                    default: ImGui.TextDisabled("○ 无密钥"); break;
                }
            }

            if (debugMode)
            {
                // Full picker (debug): choose among Enceladeum / Local / custom.
                string preview = services.Count > 0 ? services[sel].Name : "(none)";
                ImGui.SetNextItemWidth(-PanelPad);
                if (ImGui.BeginCombo("##relaysvc", preview))
                {
                    for (int i = 0; i < services.Count; i++)
                    {
                        bool isSel = i == sel;
                        if (ImGui.Selectable(services[i].Name + (services[i].BuiltIn ? "" : "  （自定义）"), isSel))
                        { config.SelectedRelayService = i; config.SyncSelectedRelayUrl(); config.Save(); relayKeyLocked = false; }
                        if (isSel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                KeyStatusDot();
            }
            else
            {
                // Normal: the Enceladeum name + key status on one line.
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Enceladeum");
                ImGui.SameLine(0f, 10f);
                KeyStatusDot();
            }

            // Key field + confirm button. Paste the key, hit the confirm arrow → the field locks (non-editable) and
            // the key is probed against /health?k=<key> to light the status. Editing again (unlock) re-opens it.
            if (sel >= 0 && sel < services.Count)
            {
                var svc = services[sel];
                // NB-5: on the first draw after a client restart, relayKeyLocked is a fresh runtime bool (false), so a
                // saved-and-validated key showed as editable again. Derive the lock once from the saved key: if a key is
                // already stored, start locked (confirmed). Manual edit/confirm/service-switch drive it thereafter.
                if (!relayKeyLockInit)
                {
                    relayKeyLocked = !string.IsNullOrWhiteSpace(svc.Key);
                    relayKeyLockInit = true;
                }
                string keyEdit = svc.Key ?? "";
                float btnW = 30f;
                ImGui.SetNextItemWidth(-(PanelPad + btnW + 6f));
                ImGui.BeginDisabled(relayKeyLocked);
                if (ImGui.InputTextWithHint("##svckey", "粘贴你的密钥", ref keyEdit, 256))
                { svc.Key = keyEdit.Trim(); config.SyncSelectedRelayUrl(); config.Save(); }
                ImGui.EndDisabled();
                ImGui.SameLine(0f, 6f);
                if (relayKeyLocked)
                {
                    // Locked → show an edit (unlock) button to re-open the field.
                    if (ImGuiComponents.IconButton("##keyedit", FontAwesomeIcon.Pen)) { relayKeyLocked = false; ResetRelayKeyEdit?.Invoke(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("编辑密钥");
                }
                else
                {
                    // Unlocked → confirm (link) button: lock the field + probe the key.
                    bool canConfirm = !string.IsNullOrWhiteSpace(svc.Key);
                    ImGui.BeginDisabled(!canConfirm);
                    if (ImGuiComponents.IconButton("##keyconfirm", FontAwesomeIcon.Link)) { relayKeyLocked = true; ConfirmRelayKey?.Invoke(); }
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("确认密钥并向中继校验");
                }
            }

            // v0.7.338 (#5): muted effective-connection line - the RESOLVED endpoint actually in use, so there's no
            // confusion about which relay you're on (the friend was silently on localhost). Parse the composed
            // RelayUrl; show the host, and call out localhost distinctly (dev-only, won't reach the beta relay).
            {
                string eff = config.RelayUrl ?? "";
                string host = eff;
                try { var u = new System.Uri(eff); host = u.Host + (u.IsDefaultPort || u.Port <= 0 ? "" : ":" + u.Port); } catch { }
                bool isLocal = host.Contains("localhost") || host.Contains("127.0.0.1");
                ImGui.Spacing();
                ImGui.TextDisabled("连接：");
                ImGui.SameLine();
                if (isLocal)
                    ImGui.TextColored(new Vector4(0.85f, 0.68f, 0.30f, 1f), host + "（本地开发，不是中继）");
                else
                    ImGui.TextColored(new Vector4(0.50f, 0.55f, 0.62f, 1f), host);
            }

            // Closed-beta note.
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + panelWidth - PanelPad * 2f);
            ImGui.TextColored(new Vector4(0.62f, 0.66f, 0.74f, 1f),
                "HMapSync 官方中继目前处于封闭测试。有密钥请粘贴到上方，没有也可以先用单人会话探索地图！");
            ImGui.PopTextWrapPos();
        }
        EndPanel();

        // Drift/patch banner - shown prominently if the say passthrough auto-shut. A rounded box sized to its
        // content (mirrors BeginPanel/EndPanel, with a red wash). Was a fixed-height BeginChild that clipped its
        // text and grew a scrollbar; now the content flows naturally and the fill+border are drawn on a lower
        // draw-list channel so the wash sits BEHIND the immediate-mode text (whose height isn't known up front).
        if (SayDriftBanner?.Invoke() ?? false)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);                                            // content on the top channel
            var bStart = ImGui.GetCursorScreenPos();
            float bWidth = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(new Vector2(0f, 3f + ImGui.GetStyle().ItemSpacing.Y));    // top inner padding (mirrors BeginPanel)
            ImGui.Indent(PanelPad);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + bWidth - PanelPad * 2f);
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "⚠ /say 放行已关闭");
            ImGui.TextColored(new Vector4(0.85f, 0.86f, 0.90f, 1f), "聊天 opcode 不再像聊天数据，通常是游戏更新改了它。在下方重新学习即可恢复会话成员间的 /say，其他功能不受影响。");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            if (ImGui.Button("忽略")) DismissSayDriftBanner?.Invoke();
            ImGui.Unindent(PanelPad);
            ImGui.Dummy(new Vector2(0f, 3f));                                     // bottom inner padding
            var bEnd = ImGui.GetCursorScreenPos();
            dl.ChannelsSetCurrent(0);                                            // background + border on the bottom channel
            var bMax = new Vector2(bStart.X + bWidth, bEnd.Y);
            dl.AddRectFilled(bStart, bMax, ImGui.GetColorU32(new Vector4(0.35f, 0.12f, 0.12f, 0.5f)), 6f);
            dl.AddRect(bStart, bMax, ImGui.GetColorU32(new Vector4(0.55f, 0.28f, 0.28f, 0.85f)), 6f);
            dl.ChannelsMerge();
            ImGui.Spacing();
        }

        // ── Accent colour ── the action-button & active-toggle colour. Neutral (grey) and Danger (warm-red) stay
        // fixed; hover and text-on-accent are derived, so any accent you pick stays legible.
        BeginPanel("外观");
        {
            ImGui.TextDisabled("强调色");
            var acc = config.AccentColor ?? new[] { 0.83f, 0.62f, 0.20f, 1f };
            var accV = new Vector4(acc[0], acc[1], acc[2], 1f);

            void PresetAccent(Vector4 c)
            {
                // Ring the currently-selected swatch (mockup style).
                bool isSel = Math.Abs(c.X - accV.X) < 0.01f && Math.Abs(c.Y - accV.Y) < 0.01f && Math.Abs(c.Z - accV.Z) < 0.01f;
                var p0 = ImGui.GetCursorScreenPos();
                if (ImGui.ColorButton("##pa" + c.X.ToString("F2") + c.Y.ToString("F2"), c, ImGuiColorEditFlags.NoTooltip, new Vector2(26, 26)))
                { config.AccentColor = new[] { c.X, c.Y, c.Z, 1f }; config.Save(); AccentChanged?.Invoke(); }
                if (isSel)
                    ImGui.GetWindowDrawList().AddRect(new Vector2(p0.X - 2, p0.Y - 2), new Vector2(p0.X + 28, p0.Y + 28),
                        ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.98f, 1f)), 4f);
                ImGui.SameLine(0f, 8f);
            }
            PresetAccent(new Vector4(0.83f, 0.62f, 0.20f, 1f));   // gold (default)
            PresetAccent(new Vector4(0.86f, 0.52f, 0.30f, 1f));   // amber-orange
            PresetAccent(new Vector4(0.82f, 0.42f, 0.52f, 1f));   // rose
            PresetAccent(new Vector4(0.55f, 0.48f, 0.85f, 1f));   // violet
            PresetAccent(new Vector4(0.32f, 0.60f, 0.86f, 1f));   // azure
            PresetAccent(new Vector4(0.30f, 0.72f, 0.58f, 1f));   // emerald
            // Free picker kept, sized to match the preset swatches (26px).
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 0f));
            var customV = accV;
            if (ImGui.ColorButton("##accentcustom", customV, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(26, 26)))
                ImGui.OpenPopup("##accentpick");
            ImGui.PopStyleVar();
            if (ImGui.BeginPopup("##accentpick"))
            {
                if (ImGui.ColorPicker4("##accentpicker", ref accV, ImGuiColorEditFlags.NoAlpha))
                { config.AccentColor = new[] { accV.X, accV.Y, accV.Z, 1f }; config.Save(); AccentChanged?.Invoke(); }
                ImGui.EndPopup();
            }
            ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("自定义");
        }
        EndPanel();

        // ── Server info bar ── b225: optional weather readout in the DTR bar (Dalamud's server-info strip), like the
        // Weatherman plugin. Off by default. Flipping it calls ReloadWeatherDtr so the entry appears/disappears at once.
        BeginPanel("服务器信息栏");
        {
            bool wxDtr = config.ShowWeatherDtr;
            if (ImGui.Checkbox("显示天气", ref wxDtr))
            {
                config.ShowWeatherDtr = wxDtr;
                config.Save();
                ReloadWeatherDtr?.Invoke();
            }
            ImGui.SameLine(); ImGui.TextDisabled("在服务器信息栏显示当前天气");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在 Dalamud 服务器信息栏（右上角）显示当前天气，点击可弹出天气预设。");

            // b240: matching music readout - the current track name in the DTR bar; click it to open the BGM picker.
            bool trkDtr = config.ShowTrackDtr;
            if (ImGui.Checkbox("显示音乐", ref trkDtr))
            {
                config.ShowTrackDtr = trkDtr;
                config.Save();
                ReloadTrackDtr?.Invoke();
            }
            ImGui.SameLine(); ImGui.TextDisabled("在服务器信息栏显示当前曲目");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在 Dalamud 服务器信息栏（右上角）显示当前音乐，点击可打开音乐选择器。");
        }
        EndPanel();

        // ── Modules ── display-only availability. Installed modules are CLICKABLE: the name opens that plugin's own
        // window via Dalamud's IExposedPlugin.OpenMainUi, so there's no need to type its command.
        BeginPanel("模块");
        {
            // Moniker - HMS integrates with it for real, so prefer its own IPC-backed availability flag; fall back to
            // plugin presence if the delegate isn't wired.
            bool mk = MonikerAvailable?.Invoke() ?? ModulePresent?.Invoke("Moniker") ?? false;
            DrawModuleRow("Moniker", "Moniker", mk, "在会话中同步自定义角色名。");

            // b195: nameplate sync in the LOBBY (out of a loaded map). Inside a map, Moniker names already ride
            // the session sync; this extends it to peers gathered in the lobby before anyone loads a map. ON by default
            // (b198) so it matches in-session behaviour; user can untick.
            ImGui.Indent(18f);
            if (!mk) ImGui.BeginDisabled();
            bool lobbyNames = config.SyncLobbyNameplates;
            if (ImGui.Checkbox("在大厅同步名牌", ref lobbyNames))
            {
                config.SyncLobbyNameplates = lobbyNames;
                config.Save();
            }
            if (!mk) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在大厅集合、尚未加载地图时，互相显示 Moniker 自定义名称。需要 Moniker 插件。");

            // b200: also replace the name shown in CHAT (not just the nameplate). Chat lines carry the real character
            // name; this restamps shown say/yell/shout AND /em emotes with the synced Moniker name so chat matches the plate.
            if (!mk) ImGui.BeginDisabled();
            bool chatNames = config.ReplaceChatNames;
            if (ImGui.Checkbox("聊天中使用自定义名称", ref chatNames))
            {
                config.ReplaceChatNames = chatNames;
                config.Save();
            }
            if (!mk) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("除了名牌，也在聊天记录（说话 / 呼喊 / 喊话和 /em 情感动作）中显示 Moniker 名称，对你和会话成员都生效。需要 Moniker 插件。");

            // b209: per-channel-group granularity — untick a bucket to exclude those channels from the restamp (e.g.
            // keep the disguise name in IC party/CWLS but show your real name in OOC /say). All gated under the master
            // toggle above; disabled when Moniker is absent OR the master is off.
            ImGui.Indent(18f);
            bool subDisabled = !mk || !chatNames;
            if (subDisabled) ImGui.BeginDisabled();
            bool rSay = config.RestampMapAudibles;
            if (ImGui.Checkbox("地图聊天（说话 / 呼喊 / 喊话 / 情感动作）", ref rSay)) { config.RestampMapAudibles = rSay; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在附近频道（/say、/yell、/shout）和 /em 情感动作中替换为自定义名称。如果你用 /say 聊场外话题，请关闭。");
            bool rParty = config.RestampParty;
            if (ImGui.Checkbox("小队", ref rParty)) { config.RestampParty = rParty; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在小队和跨服小队聊天中替换为自定义名称。");
            bool rCwls = config.RestampCwls;
            if (ImGui.Checkbox("跨服通讯贝", ref rCwls)) { config.RestampCwls = rCwls; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在 8 个跨服通讯贝中替换为自定义名称。");
            bool rOther = config.RestampOther;
            if (ImGui.Checkbox("其他频道", ref rOther)) { config.RestampOther = rOther; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在部队、团队、通讯贝 1-8、新人频道和 PVP 战队中替换为自定义名称。");
            if (subDisabled) ImGui.EndDisabled();
            ImGui.Unindent(18f);
            ImGui.Unindent(18f);

            // HDM (mob disguise) - HMS integrates with it via IPC (the disguise-sync bridge), so prefer its own
            // handshake-backed availability flag, falling back to plugin presence. Supersedes the old "Outfits" row
            // (HOutfits had only limited NPC support; HDM is the full appearance + spawnable-NPC module now).
            ImGui.Spacing();
            bool hdm = HdmAvailable?.Invoke() ?? ModulePresent?.Invoke("HDM") ?? false;
            DrawModuleRow("HDM", "HDM", hdm, "应用外观，生成并管理 NPC。");

            // World Editor - placeholder for a not-yet-released module. Grey dot, "coming soon" tagline.
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "●"); ImGui.SameLine(0, 6);
            ImGui.TextUnformatted("世界编辑器");
            ImGui.SameLine();
            ImGui.TextDisabled("  未安装");
            ImGui.Indent(18f);
            ImGui.TextDisabled("在会话中编辑、导出和加载游戏地图（即将推出）。");
            ImGui.Unindent(18f);
        }
        EndPanel();

        // ── Developer ── debug toggle + the advanced relay controls (localhost, custom services) that don't belong in
        // front of a normal user. Renamed from "General" (a one-item false category).
        BeginPanel("开发者");
        {
            bool dbg = DebugMode?.Invoke() ?? false;
            if (ImGui.Checkbox("调试模式", ref dbg)) SetDebugMode?.Invoke(dbg);
            ImGui.SameLine(); ImGui.TextDisabled("详细日志、数据包查看、开发中继");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("显示数据包查看页、调试命令按钮，以及下方的本地 / 自定义中继设置。");

            if (dbg)
            {
                var services = config.RelayServices;
                int sel = config.SelectedRelayService;
                if (sel < 0 || sel >= services.Count) sel = 0;

                ImGui.Spacing();
                ImGui.TextDisabled("中继服务（高级）");
                // Show the selected service's base URL (editable) - for localhost / custom endpoints.
                if (sel >= 0 && sel < services.Count)
                {
                    string urlEdit = services[sel].Url ?? "";
                    ImGui.SetNextItemWidth(-PanelPad);
                    if (ImGui.InputTextWithHint("##svcurledit", "wss://relay.example.com/", ref urlEdit, 256))
                    { services[sel].Url = urlEdit; config.SyncSelectedRelayUrl(); config.Save(); }
                }
                // Add a custom service.
                if (ImGui.TreeNode("添加自定义服务"))
                {
                    ImGui.SetNextItemWidth(-PanelPad);
                    ImGui.InputTextWithHint("##newsvcurl", "wss://relay.example.com/", ref customServiceUrl, 256);
                    ImGui.SetNextItemWidth(-PanelPad);
                    ImGui.InputTextWithHint("##newsvcname", "服务名称", ref customServiceName, 64);
                    bool canAdd = !string.IsNullOrWhiteSpace(customServiceUrl) && !string.IsNullOrWhiteSpace(customServiceName);
                    if (!canAdd) ImGui.BeginDisabled();
                    if (PrimaryButton("添加服务", new Vector2(140f, 0f)))
                    {
                        config.RelayServices.Add(new RelayService { Name = customServiceName.Trim(), Url = customServiceUrl.Trim(), BuiltIn = false });
                        config.SelectedRelayService = config.RelayServices.Count - 1;
                        config.SyncSelectedRelayUrl(); config.Save();
                        customServiceUrl = ""; customServiceName = "";
                    }
                    if (!canAdd) ImGui.EndDisabled();
                    if (sel >= 0 && sel < services.Count && !services[sel].BuiltIn)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("删除选中", new Vector2(140f, 0f)))
                        { services.RemoveAt(sel); config.SelectedRelayService = 0; config.SyncSelectedRelayUrl(); config.Save(); }
                    }
                    ImGui.TreePop();
                }
            }
        }
        EndPanel();

        // ── Say passthrough - opcodes ──
        BeginPanel("聊天放行：opcode");
        {
            var st = SayOpcodeState?.Invoke() ?? (300u, 912u, true, "");
            if (!opcodeFieldsInit) { opcodeInOut = st.Item1; opcodeInIn = st.Item2; opcodeFieldsInit = true; }

            // Header row: advisory note + verified/unverified pill (inline, no right-align math).
            ImGui.TextDisabled("高级 · 仅在 /say 同步失效时修改");
            ImGui.SameLine();
            {
                var pill = st.Item3 ? new Vector4(0.30f, 0.72f, 0.58f, 1f) : new Vector4(1f, 0.6f, 0.35f, 1f);
                string pillText = st.Item3 ? "verified" : "unverified";
                var p0 = ImGui.GetCursorScreenPos();
                var tsz = ImGui.CalcTextSize(pillText);
                ImGui.GetWindowDrawList().AddRect(new Vector2(p0.X - 1f, p0.Y - 1f),
                    new Vector2(p0.X + tsz.X + 12f, p0.Y + tsz.Y + 3f),
                    ImGui.GetColorU32(new Vector4(pill.X, pill.Y, pill.Z, 0.8f)), 8f);
                ImGui.Dummy(new Vector2(6f, 0f)); ImGui.SameLine(0f, 0f);
                ImGui.TextColored(pill, pillText);
                ImGui.SameLine(0f, 8f); ImGui.Dummy(new Vector2(1f, 0f));
            }

            ImGui.TextWrapped("这些 opcode 让会话成员能互相听到 /say、/yell 和 /shout。游戏更新可能改变它们，那时聊天放行会自动关闭，需要在这里重新学习。");
            ImGui.Spacing();

            // CURRENT - aligned rows (label | value | hex | purpose).
            ImGui.TextDisabled("当前");
            void OpRow(string label, uint val, string purpose)
            {
                float c0 = ImGui.GetCursorPosX();
                ImGui.TextUnformatted(label);
                ImGui.SameLine(); ImGui.SetCursorPosX(c0 + 90f);
                ImGui.TextColored(Lighten(Accent(), 1.05f), val.ToString());
                ImGui.SameLine(); ImGui.SetCursorPosX(c0 + 140f);
                ImGui.TextDisabled("(0x" + val.ToString("X3") + ")");
                ImGui.SameLine(); ImGui.SetCursorPosX(c0 + 200f);
                ImGui.TextDisabled(purpose);
            }
            OpRow("发送", st.Item1, "发送你的 /say");
            OpRow("接收", st.Item2, "接收他人的 /say");
            if (!string.IsNullOrEmpty(st.Item4)) ImGui.TextDisabled("已在游戏版本确认：" + st.Item4);

            ImGui.Spacing();

            // Recovery block. When BROKEN (unverified), show the full plain-language two-person procedure as visible
            // body text (not a tooltip) - auto-capture is the ONLY recovery path (there's no community chat-opcode list),
            // and it strictly needs a second person, so that requirement is stated up front, not buried.
            if (!st.Item3)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + panelWidth - PanelPad * 2f);
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.35f, 1f), "/say 同步已关闭：游戏更新改变了聊天 opcode。");
                ImGui.TextColored(new Vector4(0.80f, 0.82f, 0.88f, 1f), "重新学习需要一位和你站在同一位置的好友。游戏不会把你自己的 /say 发回给你，所以“接收” opcode 无法单人捕获。");
                ImGui.PopTextWrapPos();
                ImGui.Spacing();
                ImGui.TextDisabled("1.  你们俩都不在任何会话中（过滤器关闭）。");
                ImGui.TextDisabled("2.  点“重新学习”，会显示一条 /say 标记。");
                ImGui.TextDisabled("3.  你输入这条标记，好友也输入同样的内容。");
                ImGui.TextDisabled("4.  插件会自动捕获并校验。");
                ImGui.Spacing();
                if (PrimaryButton("重新学习", new Vector2(120f, 0f))) RelearnSayOpcodes?.Invoke();
            }
            else
            {
                // Working - compact, but the procedure is VISIBLE (short line beside/above the button), not tooltip-only,
                // so the button isn't a lone wide control with hidden meaning.
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + panelWidth - PanelPad * 2f);
                ImGui.TextDisabled("版本更新导致同步失效时重新学习：你和站在同一位置的好友各 /say 一次显示的标记（你捕获发送，对方捕获接收）。");
                ImGui.PopTextWrapPos();
                ImGui.Spacing();
                if (PrimaryButton("重新学习", new Vector2(120f, 0f))) RelearnSayOpcodes?.Invoke();
            }

            ImGui.Spacing();
            if (ImGui.TreeNode("手动输入 opcode"))
            {
                ImGui.TextDisabled("适合已经知道当前 opcode 的高级用户。");
                int oOut = (int)opcodeInOut, oIn = (int)opcodeInIn;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("发送##op", ref oOut)) opcodeInOut = (uint)Math.Max(0, oOut);
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("接收##op", ref oIn)) opcodeInIn = (uint)Math.Max(0, oIn);
                if (PrimaryButton("应用并校验", new Vector2(140f, 0f)))
                {
                    SetSayOpcodes?.Invoke(opcodeInOut, opcodeInIn);
                    VerifySayOpcodes?.Invoke();
                }
                ImGui.SameLine();
                if (ImGui.Button("校验当前", new Vector2(140f, 0f)))
                    VerifySayOpcodes?.Invoke();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("将当前 opcode 标记为本游戏版本可用，并重新开启聊天放行。");
                ImGui.TreePop();
            }
        }
        EndPanel();

        // v0.7.457: DEBUG COMMANDS REFERENCE - the full inventory, documented so they don't have to be
        // remembered (typed in chat; shown only when Debug mode is on). Placed LAST in Config, after the
        // opcode panel. Grouped by what they do; the log tags (in [BRACKETS]) are where their output lands.
        if (DebugMode?.Invoke() ?? false)
        {
            BeginPanel("调试命令");
            {
                ImGui.TextWrapped("完整命令列表，在聊天框输入即可。诊断输出写入 /xllog，带方括号标签。此面板只在调试模式显示，下方标签注明每条命令的实际条件：(debug) 需要调试模式；(out-of-session) 无需会话；(host) 需要房主权限；(testing) 仅测试版可用。无标签 = 需要处于会话中。");
                ImGui.Spacing();

                void CmdRow(string cmd, string desc)
                {
                    float c0 = ImGui.GetCursorPosX();
                    ImGui.TextColored(Lighten(Accent(), 1.05f), cmd);
                    ImGui.SameLine(); ImGui.SetCursorPosX(c0 + 190f);
                    // v0.7.460: wrap the description a PanelPad short of the panel's right edge so it doesn't
                    // brush the border (mirrors the left inset BeginPanel's Indent gives). panelWidth is the
                    // panel's content width captured by BeginPanel; c0 is the row's left, so the wrap x is the
                    // right inner margin in the same cursor space.
                    ImGui.PushTextWrapPos(c0 + panelWidth - PanelPad * 2f);
                    ImGui.TextDisabled(desc);
                    ImGui.PopTextWrapPos();
                }

                ImGui.TextDisabled("会话与生命周期");
                CmdRow("/wlens start [code]", "开始 / 主持会话（无需会话）；不带房间码 = 新建房间");
                CmdRow("/wlens starts", "开始单人会话（别名 startsolo）（无需会话）");
                CmdRow("/wlens join <code>", "用房间码加入会话（无需会话）");
                CmdRow("/wlens load <tt|name|stage>", "快速加载地图 / 过场（空闲时自动单人）（无需会话）");
                CmdRow("/wlens reload", "重新加载当前地图");
                CmdRow("/wlens leave", "离开会话（保持连接）");
                CmdRow("/wlens stop", "彻底退出：还原所有调试改动并卸载");
                CmdRow("/wlens status", "输出会话 / 连接状态（无需会话）");
                CmdRow("/wlens maps", "打开 Wanderlens 窗口的地图页（无需会话）");
                CmdRow("/wlens memo", "记录当前地图的出生点");
                CmdRow("/wlens here", "输出当前位置和地图");

                ImGui.Spacing();
                ImGui.TextDisabled("房主控制");
                CmdRow("/wlens mapweather <id>", "强制设置房间天气（广播并应用）（房主）");
                CmdRow("/wlens maptime <H:M|off>", "锁定 / 设置艾欧泽亚时间（房主）");
                CmdRow("/wlens mapbgm <id>", "设置房间音乐，0 = 无（房主）");
                CmdRow("/wlens npc <on|off>", "移除所有事件 NPC（房主）");
                CmdRow("/wlens qbubble <on|off>", "隐藏头顶任务气泡（房主）");
                CmdRow("/wlens roompassword <pw>", "设置房间密码（房主）");
                CmdRow("/wlens roomlock <on|off>", "锁定房间，禁止新成员加入（房主）");
                CmdRow("/wlens transferhost <peer>", "把房主权限移交给成员（房主）");

                ImGui.Spacing();
                ImGui.TextDisabled("自身 / 外观");
                CmdRow("/wlens emote <id>", "播放并同步情感动作（未解锁的需在会话中）（无需会话）");
                CmdRow("/wlens minion <id>", "召唤并同步宠物（未解锁的需在会话中）（无需会话）");
                CmdRow("/wlens accessory <id>", "装备并同步时尚配饰（无需会话）");
                CmdRow("/wlens mount [id]", "骑乘坐骑；不带参数 = 最近使用或下坐骑，0 = 下坐骑");
                CmdRow("/wlens visor", "切换面罩（无需会话）");
                CmdRow("/wlens displayhead", "切换头部装备显示（无需会话）");
                CmdRow("/wlens displayarms", "切换武器显示（无需会话）");

                ImGui.Spacing();
                ImGui.TextDisabled("移动");
                CmdRow("/wlens fly", "切换飞行（需已加载地图 / 过场或调试模式）");
                CmdRow("/wlens noclip", "切换穿墙");
                CmdRow("/wlens carpet", "在无碰撞的表面行走（地毯）");
                CmdRow("/wlens facecamdump", "导出面部镜头 / 头部追踪状态");

                ImGui.Spacing();
                ImGui.TextDisabled("诊断");
                CmdRow("/wlens diag", "本地状态快照（调试）");
                CmdRow("/wlens diagpeer", "各成员同步状态（调试）");
                CmdRow("/wlens netdiag", "中继带宽（实时速率 + 重置）（调试）");
                CmdRow("/wlens lanecensus", "通信通道字段映射 HOT/WARM/COLD/HOST（调试）");
                CmdRow("/wlens locodiag", "接收端移动解析追踪（调试）");
                CmdRow("/wlens gposediag", "GPose 状态诊断（调试）");
                CmdRow("/wlens housingdiag", "房屋 / 家具管理器状态（调试）");
                CmdRow("/wlens furndiag", "家具隐藏详情（调试）");
                CmdRow("/wlens mapdiag", "地图探索：AgentMap + 区域标记位（调试）");
                CmdRow("/wlens mounthud", "坐骑 HUD 挂接探测（骑乘时运行）");
                CmdRow("/wlens pktcap", "数据包查看器捕获（无需会话）");
                CmdRow("/wlens senddiag", "发送数据包观察，不拦截（无需会话）");
                CmdRow("/wlens rosterdump", "只读导出生成 / 消失结构（无需会话）");
                CmdRow("/wlens wiredump [n]", "捕获 N 个二进制通信帧，发送 / 接收（调试）");
                CmdRow("/wlens dumpstructs", "把 ClientStructs 结构偏移写入日志（调试）");

                ImGui.Spacing();
                ImGui.TextDisabled("场景 / 图层导出");
                CmdRow("/wlens lgbdump <tt>", "导出本地图的布局（LGB）实例（调试）");
                CmdRow("/wlens layerscan [path]", "运行时图层状态（按 LayerId 显示 / 隐藏 / 缺失）");
                CmdRow("/wlens layerhide <substr>", "按资源名片段隐藏流式加载的模型（例如 dst）");
                CmdRow("/wlens layershow <substr>", "恢复被 layerhide 隐藏的模型（或用 /wlens stop）");
                CmdRow("/wlens sgdump [substr]", "SharedGroup 时间轴 / 阶段状态（重建中的建筑）");
                CmdRow("/wlens idprobe <ids>", "探测文件 InstanceId 到运行时的对应（759 场景桥接，只读）");
                CmdRow("/wlens idshowall", "恢复所有被调试 / 战斗改动隐藏的内容（与会话无关）");
                CmdRow("/wlens adv759drive", "759 干净推进：强制显示 8 座设施的子物体（不循环、不播放时间轴），加载 759 时自动执行");
                CmdRow("/wlens battlehide [on|off]", "隐藏战斗废墟，显示和平时的城市（1185/1186 自动启用）");
                CmdRow("/wlens dropcolliders [on|off]", "移除所有碰撞盒以便自由穿行（跨地图保留）");
                CmdRow("/wlens doordump", "门 / 事件物体清单");
                CmdRow("/wlens roaddump [term]", "道路 / 路径（BgPart）实例导出");
                CmdRow("/wlens vfxdump [term]", "特效 .avfx 路径及屏蔽匹配");
                CmdRow("/wlens vfxlist", "试运行：把地图所有特效写入日志，火焰类会标注（无需会话）");
                CmdRow("/wlens stagelights", "副本关灯开关（主持时广播）（无需会话）");
                CmdRow("/wlens vfxoff", "副本通用特效屏蔽（主持时广播）（无需会话）");

                ImGui.Spacing();
                ImGui.TextDisabled("过场动画 / 场景");
                CmdRow("/wlens firecut <rowId>", "准备捕获过场，请在旅馆运行（无需会话）");
                CmdRow("/wlens cutstop", "过场安全退出，任何地方可用（无需会话）");
                CmdRow("/wlens stagestate [name|next]", "切换过场场景的共面 A/B 布景");
                CmdRow("/wlens casttest <tt> <key>", "用强制图层筛选键冷加载地图，显示受键控制的模型 / 预设（例如隐藏布景）。先离开地图；在此测量后写入 ForcedCastKeys（调试）");
                CmdRow("/wlens questprobe", "切换任务读取诊断日志（伪装按地图自动进行）（调试）");
                CmdRow("/wlens debug", "切换调试模式：下次加载时创建副本控制器并显示任务信息栏（调试）");

                ImGui.Spacing();
                ImGui.TextDisabled("地图全开");
                CmdRow("/wlens mapreveal", "揭开当前地图迷雾，会先保存快照（调试）");
                CmdRow("/wlens maprestore", "撤销揭开，恢复快照（调试）");

                ImGui.Spacing();
                ImGui.TextDisabled("天气");
                // b134: setweather is an UNGATED prod verb (works with debug off), listed for reference. Applies any
                // weather id on the current zone - native in-bank, or a crammed foreign sky/doodads via preset; when
                // hosting it also broadcasts to peers. The wx* family below is the research pipeline behind it.
                CmdRow("/wlens setweather <id>", "设置任意天气 ID（原生或强制）；主持时广播给成员");

                ImGui.Spacing();
                // Weather-cram research family (~40 levers). Folded so the common surface above stays readable; the
                // exhaustive list + provenance lives in WORKING-CHANGELOG.md Appendix A.
                if (InsetCollapsingHeader("天气强制套用研究 (wx*)##wxfamily"))
                {
                    ImGui.TextDisabled("探测（只读）");
                    CmdRow("/wlens weatherdiag", "导出天气选择器的输入 [WXDIAG]");
                    CmdRow("/wlens envbprobe", "运行时 .envb / EnvScene.WeatherIds 探测 [ENVB-PROBE]");
                    CmdRow("/wlens envsetdump", "查找解析后的分组参数存储 [ENVSET]");
                    CmdRow("/wlens wxresolve", "渲染路径解析钩子，每次过渡触发 [WXRESOLVE]（测试版）");
                    CmdRow("/wlens wxenvprobe", "来源天气库注入点映射 [WXENV]");
                    CmdRow("/wlens wxloadverify [path]", "验证 GetResourceSync 能加载 .envb [WXLOAD]");
                    CmdRow("/wlens wxskydiag", "+0x90 强制套用遗漏的天空 / 环境资源 [WXSKY]");
                    CmdRow("/wlens wxstardiag", "星空 / 云层渲染器实时状态 [WXSTAR]");
                    CmdRow("/wlens wxstarsrc", "查找星空亮度输入 [WXSRC]");
                    CmdRow("/wlens wxenvdump", "查找随时间变化的 EnvState 浮点值 [WXENVDUMP]");
                    CmdRow("/wlens wxsimdump", "查找 EnvSimulator 重新插值的开关 [WXSIMDUMP]");
                    CmdRow("/wlens wxfreezeprobe [sweep]", "重新插值冻结的决定性测试 [WXFREEZE]");
                    CmdRow("/wlens wxtimescan", "找出随时钟变化的 EnvState 偏移 [WXTIMESCAN]");

                    ImGui.Spacing();
                    ImGui.TextDisabled("捕获 / 烘焙");
                    CmdRow("/wlens wxcapture", "为当前 EnvState 拍快照（配合 wxreplay）");
                    CmdRow("/wlens wxbake [id]", "把当前 EnvState 存为烘焙预设（本地库）");
                    CmdRow("/wlens wxbakeall [force|stop]", "无人值守批量烘焙本地图所有天气 [WXBAKEALL]");
                    CmdRow("/wlens wxkfcap", "在当前时刻捕获一个 EnvState 昼夜关键帧");
                    CmdRow("/wlens wxkfsweep [min|stop]", "自动捕获来源地图一整天的关键帧");
                    CmdRow("/wlens wxkfsave [name]", "把内存中的关键帧组烘焙到本地库");
                    CmdRow("/wlens wxkfload <id>", "把已保存的关键帧组载入内存");
                    CmdRow("/wlens wxkflist", "列出已保存的关键帧组");
                    CmdRow("/wlens wxkfclear", "丢弃捕获的关键帧组并停止嫁接");
                    CmdRow("/wlens wxkfdecim <N|full>", "保真度测试：用 N 个关键帧重放密集帧组");
                    CmdRow("/wlens wxkftour [N|force|stop]", "无人值守捕获整座城市的昼夜变化，逐个天气");

                    ImGui.Spacing();
                    ImGui.TextDisabled("应用 / 重放");
                    CmdRow("/wlens wxpreset <id>|off", "按天气 ID 渲染已烘焙的外来天空");
                    CmdRow("/wlens wxreplay [on|off]", "在本地图逐帧重放捕获的 EnvState");
                    CmdRow("/wlens wxkfreplay [on|off]", "在本地图嫁接昼夜关键帧（至少 2 帧）");
                    CmdRow("/wlens wxcyclecram <path> [id] [spd]|off", "句柄替换：原生采样来源 .envb，得到完整周期和特效 [WXCYCLE]");
                    CmdRow("/wlens wxskyswap <tex>|off", "替换残留的天空立方体贴图 [WXSKY]");
                    CmdRow("/wlens wxambswap <amb>|off", "替换残留的环境组 [WXSKY]");
                    CmdRow("/wlens wxstarforce [off]", "强制使用黄金港夜晚的星空亮度参数 [WXSTAR]");
                    CmdRow("/wlens wxcity [off]", "一键启用强制套用的黄金港晴夜天空");
                    CmdRow("/wlens wxcoldtest [strip]", "用自建描述符生成场景装饰 [WXCOLD]");
                    CmdRow("/wlens wxtrans <id> [fade]", "调用真实的 WeatherManager 过渡（特效路径，可能崩溃）");
                    CmdRow("/wlens wxnative <id>", "直接写入原生天气，无保护和强制套用（仅限特效安全的 ID）");

                    ImGui.Spacing();
                    ImGui.TextDisabled("场景装饰 / 扫描");
                    CmdRow("/wlens wxdood [id|off <id>|clear]", "管理特效安全的原生场景装饰白名单");
                    CmdRow("/wlens wxdoodall [on|off]", "总开关：开关所有预设场景装饰（207/208 始终拒绝）");
                    CmdRow("/wlens wxsweep [start [unguarded]|stop|status]", "可在崩溃后续跑的场景装饰天气自动扫描");
                    CmdRow("/wlens wxdooddiag [arm|off|status]", "207/208 崩溃的 VEH 异常取证 [WXFAULT]");
                    CmdRow("/wlens linevfx [scan|near|gfx|off|destroy|on|auto]", "屏蔽 Boss 场地边界线（类型 59）[LINEVFX]");

                    ImGui.Spacing();
                    ImGui.TextDisabled("采集巡游（仅测试版）");
                    CmdRow("/wlens wxbaketour [force|stop]", "跨地图覆盖式烘焙扫描 [WXTOUR]（测试版）");
                    CmdRow("/wlens wxkftourall [force|stop]", "跨地图城市关键帧巡游 [CITYKF]（测试版）");
                    CmdRow("/wlens wxkfcities [force|stop]", "全城市来源标记主干嫁接巡游 [CITYGRAFT]（测试版）");
                    CmdRow("/wlens wxdifftour [stop|report]", "跨城市主干天气差异对比 [WXDIFF]（测试版）");
                    CmdRow("/wlens wxmissing", "烘焙覆盖报告：还缺哪些 ID 及来源地图（测试版）");
                }

                ImGui.Spacing();
                ImGui.TextDisabled("维护");
                CmdRow("/wlens teardownhousing", "强制卸载室内地图（仅应急）（调试）");
            }
            EndPanel();
        }

        EndTabBody();
        ImGui.EndTabItem();
    }


    // Tab 2: Zones - territory browser. Two-key classification (ContentType for duties, else TerritoryIntendedUse),
    // place-name folding (near-identical maps collapse into one expandable cluster), region/expansion section headers,
    // ★ favourites, search, and the name itself as the hot Load affordance. No per-row Load/reset buttons.
    private struct ZoneRow
    {
        public uint Id;
        public string Name;      // place name
        public string Region;    // PlaceNameRegion
        public uint Use;         // TerritoryIntendedUse
        public byte Ex;          // ExVersion (0..5)
        public string Category;  // resolved chip category
        public string CfcName;   // ContentFinderCondition name (carries "(Hard)"/"(Extreme)") or ""
        public int SortKey;      // CFC SortKey (Duty-Finder order) or 0
    }

    private sealed class ZoneCluster
    {
        public string Key = "";                       // place name (the fold key)
        public ZoneRow Primary;                       // the canonical entry (Load target)
        public List<ZoneRow> Variants = new();        // all entries for this place name, id-sorted
        public string Category = "Other";
        public string Region = "";
        public byte Ex;
        public string TypeName = "";                   // humanised kind, for the Other tab's type sections
    }

    private List<ZoneCluster>? clusters;
    private string filter = "";
    private string activeCat = "All";
#pragma warning disable CS0414 // retained for map-editor re-enable; checkbox hidden v0.7.229
    private bool onlyNamed = true;                     // retained (checkbox hidden v0.7.229; re-enable with map editor)
#pragma warning restore CS0414
    private readonly HashSet<string> expandedClusters = new();

    // CN port: display names for the category keys (keys stay English - they drive classification logic).
    private static readonly Dictionary<string, string> CatLabels = new()
    {
        ["All"] = "全部", ["World"] = "野外", ["City"] = "城市", ["Inn"] = "旅馆", ["Housing"] = "房屋",
        ["Solo Instances"] = "单人场景", ["Solo Duty"] = "单人任务", ["Dungeon"] = "迷宫", ["Variant & Criterion"] = "多变 / 异闻",
        ["Trial"] = "讨伐战", ["Raid"] = "大型任务", ["Deep Dungeon"] = "深层迷宫", ["PvP"] = "PVP", ["Waiting Room"] = "等候室",
        ["Seasonal"] = "季节活动", ["Treasure Map"] = "藏宝图", ["Cosmic Exploration"] = "宇宙探索", ["Field Operations"] = "特殊场景探索",
        ["Gold Saucer"] = "金碟游乐场", ["Seaships"] = "船只", ["Cutscenes"] = "过场动画", ["Other"] = "其他", ["Orchestrion"] = "管弦乐琴",
        ["Cutscene themes"] = "过场主题", ["Mount themes"] = "坐骑主题", ["Event & misc"] = "活动与杂项",
    };
    private static string CatLabel(string key) => CatLabels.TryGetValue(key, out var v) ? v : key;

    private static readonly string[] Categories =
    {
        "All", "World", "City", "Inn", "Housing", "Solo Instances", "Solo Duty",
        "Dungeon", "Variant & Criterion", "Trial", "Raid", "Deep Dungeon", "PvP",
        "Waiting Room", "Seasonal", "Treasure Map", "Cosmic Exploration", "Field Operations", "Gold Saucer", "Seaships", "Cutscenes", "Other",
    };

    // v0.7.235: curated Seaships chip - ship-deck and voyage territories (thematic, RP-friendly, not something you'd
    // want to hunt for by ID). Ship cutscene stages (o1e1 Endless Ocean, s1e7 Limsa intro ship, o3e2 Seaship) are
    // appended in the Seaships view like the All-tab cutscene fold-in - a stage can live in BOTH its region group and
    // here, since the Seaships chip collects sailing venues regardless of their home region (NB-38).
    private static readonly HashSet<uint> SeashipTerritories = new() { 1142, 708, 680, 900, 1206 };
    private static readonly HashSet<string> SeashipCutsceneBgs = new()
    {
        "ffxiv/ocn_o1/evt/o1e1/level/o1e1",   // Endless Ocean (o1e1)
        "ffxiv/sea_s1/evt/s1e7/level/s1e7",   // Limsa Lominsa intro ship (s1e7)
        "ex2/03_ocn_o3/evt/o3e2/level/o3e2",  // Seaship (o3e2) - NB-38
    };
    // Cutscene stages (populated by the plugin from CutsceneStageService). OnLoadCutscene loads by index.
    public struct CutsceneEntry { public string Name; public string NameEn; public string Region; public string Quest; public string Code; public string Bg; public uint Id; public int Index; public bool Lab; public bool Dressed; public bool SetPiece; }   // b287 SetPiece: a scene built from bundles, listed in its own subsection   // b263 Lab (auto-ranked set) / b266 Dressed (has a cutscene dressing)
    public List<CutsceneEntry> CutsceneEntries = new();

    // b266: dressed entries indexed by territory, rebuilt whenever the plugin swaps in a new CutsceneEntries list.
    private List<CutsceneEntry>? dressingIndexSource;
    private Dictionary<uint, List<CutsceneEntry>> dressingIndex = new();
    private static readonly List<CutsceneEntry> NoDressings = new();
    private List<CutsceneEntry> DressingsFor(uint territoryId)
    {
        if (!ReferenceEquals(dressingIndexSource, CutsceneEntries))
        {
            dressingIndexSource = CutsceneEntries;
            dressingIndex = CutsceneEntries.Where(e => e.Dressed && e.Id != 0)
                .GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Lab).ThenBy(e => e.Quest).ToList());
        }
        return dressingIndex.TryGetValue(territoryId, out var l) ? l : NoDressings;
    }

    // v0.7.232: build version in the window title so it's never a guess which build is running (e.g. the Dalamud
    // assembly-cache cases where the plugin list shows a version the running code doesn't match). Computed once.
    // NB-8/NB-9: the "· Testing b<N>" suffix is compiled in ONLY when HMS_TESTING is defined (see the testing
    // HMSync.csproj). Prod's csproj does NOT define it, so even though this source file is copied verbatim on
    // promotion, the suffix #if-compiles-out and the shipped build's stamp stays clean. Do NOT promote the
    // HMS_TESTING DefineConstants line.
#if HMS_TESTING
    // Internal build number baked by the testing csproj (AssemblyMetadata "InternalBuild"). Post-GA counter,
    // monotonic, never reset - a separate lineage from the pre-release S###/v0.7.### markers. Bumped by ONE in the
    // testing csproj each time a build is cut for testing/handoff (logged in WORKING-CHANGELOG). Declared BEFORE
    // WindowTitle: static field initializers run in textual order, and WindowTitle reads this.
    private static readonly string InternalBuild =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "InternalBuild")?.Value ?? "?";
#endif
    private static readonly string WindowTitle =
        "Wanderlens 漫游镜  v" +
#if HMS_TESTING
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?")
        + "  · Testing b" + InternalBuild
#else
        // Full 4-part version (HMS's patch number is the 4th component, e.g. 1.0.0.4): ToString(), not ToString(3),
        // or the prod header freezes at "1.0.0" across every patch.
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?")
#endif
        ;
    public Action<int>? OnLoadCutscene;

    // Territory -> quest/duty name (from the cutscene-territory-index join). Replaces the generic "Quest battle"/
    // "Solo duty" variant tag with the actual quest for searchability.
    private static readonly Dictionary<uint, string> QuestNames = new() { [128]="The Gridanian Envoy", [129]="My First Daggers", [130]="The Gridanian Envoy", [131]="In Thal's Name", [132]="Close to Home", [133]="Spear of the Fearless", [134]="Just Deserts", [137]="Peasants by Day, Ninjas by Night", [138]="Slave to the Code", [140]="Operation Archon", [141]="Way Down in the Hole", [145]="Keeping the Flame Alive", [146]="The Wrath of Qarn", [148]="Spirithold Broken", [152]="You Have Selected Regicide", [153]="Her Last Vow", [154]="Sweet Dreams Are Made of Peace", [155]="The Path of the Righteous", [156]="Magiteknical Difficulties", [176]="A Reoccurring Bug", [180]="Quake Me Up Before You O'Ghomoro", [181]="Coming to Limsa Lominsa", [198]="A Mizzenmast Repast", [204]="To Guard a Guardian", [205]="Renewing the Covenant", [210]="Duty, Honor, Country", [212]="All Good Things", [225]="Spirithold Broken", [226]="To Guard a Guardian", [227]="Leia's Legacy", [228]="Violators Will Be Shot", [229]="To Catch a Poacher", [230]="Homecoming", [231]="The One That Got Away", [232]="Nouveau Riche", [233]="Chasing Shadows", [234]="Trial by Water", [235]="In Nature's Embrace", [236]="The One That Got Away", [237]="A Dangerous Proposition", [238]="Lance of Destiny", [239]="Proof of Might", [240]="Proof of Might", [248]="Way Down in the Hole", [249]="Victory in Peril", [250]="Curious Gorge Meets His Match", [251]="Return of the Holyfist", [252]="Lurkers in the Grotto", [253]="Ul'dah's Most Wanted", [254]="That Old Familiar Feeling", [255]="The Face of Thal", [256]="On Holy Ground", [257]="The Rematch", [258]="The Spirit Is Willing", [259]="Keeping the Spirit Alive", [260]="Star-crossed Rivals", [261]="Return of the Holyfist", [262]="Axe in the Stone", [263]="The Mountain That Strides", [264]="Bleeder of the Pack", [265]="Bringing Down the Mountain", [266]="The Threat of Superiority", [267]="The Threat of Perplexity", [268]="The Hidden Chapter", [269]="Facing Your Demons", [270]="Underneath the Sultantree", [271]="Duty, Honor, Country", [272]="Just Deserts", [273]="Oh Captain, My Captain", [274]="Into a Copper Hell", [275]="Lord of the Inferno", [277]="The Company You Keep (Twin Adder)", [278]="The Company You Keep (Immortal Flames)", [279]="The Company You Keep (Maelstrom)", [280]="Feint and Strike", [285]="Tactical Planning", [286]="Over the Rails", [287]="Pincer Maneuver", [288]="Sinking Doesmaga", [289]="Trial by Wind", [290]="Lance of Destiny", [291]="Like Mother, Like Daughter", [301]="In the Eyes of Gods and Men", [302]="The Heretic among Us", [303]="Brotherly Love", [304]="Notorious Biggs", [305]="Escape from Castrum Centri", [306]="Big Trouble in Little Ala Mhigo", [307]="The Lominsan Way", [308]="Fool Me Twice", [309]="Every Little Thing She Does Is Magitek", [310]="Pride and Duty (Will Take You from the Mountain)", [311]="How to Quit You", [312]="Parley in the Sagolii", [313]="Keeping the Oath", [314]="Brother from Another Mother", [315]="Five Easy Pieces", [316]="Into the Dragon's Maw", [317]="The Voidgate Breathes Gloomy", [318]="Always Bet on Black", [319]="Seer Folly", [320]="Heart of the Forest", [321]="Doing It the Bard Way", [322]="Requiem for the Fallen", [323]="Austerities of Flame", [324]="Austerities of Earth", [325]="Austerities of Wind", [326]="Primal Burdens", [327]="Forgotten but Not Gone", [328]="The Consequences of Anger", [329]="The Beast Within", [330]="History Repeating", [331]="Lady of the Vortex", [335]="Escape from Castrum Centri", [339]="Where the Heart Is (Mist)", [340]="Where the Heart Is (The Lavender Beds)", [341]="Where the Heart Is (The Goblet)", [351]="An Uninvited Ascian", [379]="Guardian of Eorzea", [392]="The Ties That Bind", [393]="The Ties That Bind", [395]="The Wyrm's Roar", [397]="Over the Wall", [398]="Where the Chocobos Roam", [399]="A Great New Nation", [400]="Mourn in Passing", [401]="Onwards and Upwards", [402]="The First Flight of the Excelsior", [403]="Return of the Bull", [404]="Stray into the Shadows", [405]="Stifled Screams", [406]="Slave to the Code", [407]="Grinners in the Mist", [408]="Sweet Sorrows", [409]="Cloying Victory", [410]="The Reason Roaille", [411]="My First Mudra", [412]="Once Upon a Time in Doma", [413]="Ninja Bathin'", [414]="The Crow Knows", [415]="Master and Student", [418]="Coming to Ishgard", [419]="A Knight's Calling", [427]="Divine Reckoning", [428]="The Sins of Antiquity", [433]="To Siege or Not to Siege", [439]="Landing a Stable Job", [453]="And My Axe", [454]="Forward, the Royal Marines", [455]="A Series of Unfortunate Events", [456]="Divine Intervention", [457]="Sounding Out the Amphitheatre", [458]="Fire and Blood", [459]="Close Encounters of the VIth Kind", [460]="Keeping the Flame Alive", [461]="Familiar Faces", [462]="Lord of the Hive", [463]="In the Eye of the Beholder", [464]="Hands of Healing", [465]="The Defiant Ones", [466]="Quarantine", [467]="Destruction in the Name of Justice", [468]="Duty and the Beast", [469]="A Journey of Purification", [470]="Sworn Upon a Lance", [471]="Blood on the Sands", [472]="A Joye-ful Reunion", [473]="Fortune Favors the Bole", [474]="Slings and Arrows", [475]="Spearheading Initiatives", [476]="The Hands of Fate", [477]="Dragoon's Fate", [478]="A Great New Nation", [479]="At the End of Our Hope", [480]="Ewer Right", [481]="Feather in the Cap", [482]="When Gnaths Cry", [483]="Against the Shadow", [484]="Ninja Assassin", [485]="An Illuminati Incident", [486]="Master of Marksmanship", [487]="Securing the Locks", [488]="The Power of a Tourney", [489]="Rise of the Machinists", [490]="In Her Defense", [491]="Appetite for Destruction", [492]="The Ballad of Oblivion", [493]="This Little Sword of Mine", [494]="Heroic Reprise", [495]="Declaration of Blood", [496]="Kindred Spirits", [497]="Absolution", [498]="Big Sollerets to Fill", [499]="Ishgardian Justice", [500]="Our Answer", [501]="The Flame in the Abyss", [502]="I Could Have Tranced All Night", [503]="A Flare for the Dramatic", [504]="A World Away", [505]="The Pulsing Heart", [507]="Heavensward", [513]="As Goes Light, So Goes Darkness", [533]="A Spectacle for the Ages", [553]="One Step Behind", [560]="The Fate of Stars", [567]="The Weeping City", [569]="An End to the Song", [588]="Judgment Day", [592]="One Life for One World", [611]="Think of the Children", [612]="A Beacon for Bad Things", [613]="Once More, to the Ruby Sea", [614]="A Silence in Three Parts", [620]="A Familiar Face Forgotten", [621]="Upon the Great Loch's Shore", [622]="In the Footsteps of Bardam the Brave", [628]="Come Rain or Shrine", [633]="Fly Free, My Pretty", [634]="His Forgotten Home", [635]="Lyse Takes the Lead", [636]="Fly Free, My Pretty", [639]="By the Grace of Lord Lolorito", [640]="A Beacon for Bad Things", [641]="I Dream of Shirogane", [647]="Choices and Paths", [648]="The Power to Protect", [657]=" A Test of Courage", [658]="Return to the Rift", [659]="In Crimson It Began", [664]="Come Rain or Shrine", [665]="It's Probably a Trap", [666]="Master Musosai", [667]="Foxfire", [668]="The Crimson Duelist", [669]="Shades of Shatotto", [670]="Best Served with Cold Steel", [671]="Rhalgr's Beacon", [672]="Stained in Scarlet", [673]="One Golem to Rule Them All", [675]="A Vermilion Vendetta", [676]="Nightkin", [678]="What She Always Wanted", [680]="Not without Incident", [681]="A Glimpse of Madness", [682]="The World Turned Upside Down", [683]="The Lady of Bliss", [684]="The Resonant", [685]="The Time between the Seconds", [686]="The Key to Victory", [687]="The Measure of His Reach", [688]="Naadam", [690]="The Hunt for Omega", [699]="Release the Hounds", [700]="The Mongrel and the Knight", [701]="An Egi-stential Crisis", [702]="An Art for the Living", [703]="One Autumn's Secret", [704]="Sweet Dreams Are Made of Peace", [705]="In Thal's Name", [706]="Raising the Sword", [707]="With Heart and Steel", [708]="Blood on the Deck", [709]="The Face of True Evil", [710]="Matsuba Mayhem", [711]="The Battle on Bekko", [713]="Dark as the Night Sky", [714]="Dragon Sound", [715]="The Orphans and the Broken Blade", [716]="Our Compromise", [717]="Curious Gorge Meets His Match", [718]="The Heart of the Problem", [721]="In Loving Memory", [722]="Our Unsung Heroes", [723]="When Clans Collide", [724]="The Hunt for Omega", [726]="A Game of Life and Death", [727]="Stormblood", [735]="A City Fallen", [736]="Dramatis Personae", [737]="Return of the Bull", [738]="Echoes of an Echo", [744]="Storm on the Horizon", [756]="Return to the Rift", [757]="Hope on the Waves", [759]="The Primary Agreement", [760]=" Schism between Sisters", [764]="An Auspicious Encounter", [769]="Emissary of the Dawn", [781]="Tortoise in Time", [786]="The Primary Agreement", [787]="Desire", [797]="The Will of the Moon", [807]="In the Beginning, There Was Chaos", [808]="In the End, There Is Omega", [809]="The Sinister Soirée", [812]="In the End, There Is Omega", [813]="The Syrcus Trench", [814]="The Soul of Temperance", [815]="In Search of Alisaie", [816]="The Hardened Heart", [817]="The Lost and the Found", [818]="To Storm-tossed Seas", [819]="The Syrcus Trench", [820]="City of Final Pleasures", [829]="Parley on the Front Lines", [830]="A Requiem for Heroes", [833]="Messenger of the Winds", [834]="Messenger of the Winds", [839]="In the Dark of Night", [842]="The Syrcus Trench", [844]="Travelers of Norvrandt", [857]="Deploy the Core", [859]="Legend of the Not-so-hidden Temple", [860]="Full Steam Ahead", [861]="The Oracle of Light", [862]="When It Rains", [863]="A Feast of Lies", [864]="A-Digging We Will Go", [865]="Hired Gunblades", [866]="Steel against Steel", [867]="Gamboling for Gil", [868]="Save the Last Dance for Me", [869]="To Have Loved and Lost", [870]="The Soul of Temperance", [871]="Courage Born of Fear", [872]="A Tearful Reunion", [873]="The Hardened Heart", [874]="The Lost and the Found", [875]="The Hunter's Legacy", [876]="Nyelbert's Lament", [877]="The Syrcus Trench", [878]="In the Middle of Nowhere", [880]="Extinguishing the Last Light", [881]="Shadowbringers", [886]="Towards the Firmament", [889]=" Manic Pixie Dream Realm", [890]=" Manic Pixie Dream Realm", [891]=" Sustenance for the Soul", [892]=" As the Heart Bids", [893]="Vows of Virtue, Deeds of Cruelty", [894]=" As the Heart Bids", [895]="On the Threshold", [911]="The Bozja Incident", [914]="A Sleep Disturbed", [915]="Path to the Past", [918]="Beneath the Surface", [919]="Sleep Now in Sapphire", [920]="Time to Focus", [921]="Pretty in Peaches", [925]="Sleep Now in Sapphire", [926]="Sleep Now in Sapphire", [928]="Brave New World", [931]="Hope's Confluence", [932]="Faded Memories", [954]="The Great Ship Vylbrand", [955]="Fit for a Queen", [956]="A Labyrinthine Descent", [957]="For Thavnair Bound", [958]="A Frosty Reception", [959]="A Trip to the Moon", [960]="Unto the Heavens", [961]="Hope Upon a Flower", [962]="The Next Ship to Sail", [963]="Skies Aflame", [964]="Fit for a Queen", [967]="Blood of Emerald", [971]="The Killer Instinct", [977]="Death Unto Dawn", [979]="Ascending to Empyreum", [987]="Skies Aflame", [991]="Duty in the Sky with Diamond", [1001]="Our Aching Souls", [1010]="A Frosty Reception", [1011]="In from the Cold", [1012]="As the Heavens Burn", [1013]="Endwalker", [1014]="Worthy of His Back", [1015]="A Path Unveiled", [1016]="To Calmer Seas", [1017]="Laid to Rest", [1018]="Ever March Heavensward", [1019]="The Gift of Mercy", [1020]="The Harvest Begins", [1021]="The Killing Art", [1022]="Sage's Focus", [1023]="Life Ephemeral, Path Eternal", [1024]="Gateway of the Gods", [1025]="Where Familiars Dare", [1026]="Endwalker", [1027]="You're Not Alone", [1028]="The Martyr", [1029]="Endwalker", [1030]="Her Children, One and All", [1031]="Hope Upon a Flower", [1049]="Operation Archon", [1051]="Forlorn Glory", [1052]="The Ultimate Weapon", [1053]="The Ultimate Weapon", [1056]="Alzadaal's Legacy", [1057]="Restricted Reading", [1061]="A Mission in Mor Dhona", [1068]="The Steps of Faith", [1073]=" Signs of the Past", [1077]="The Wind Rises", [1078]="A World with Light and Life", [1079]="Eater of Souls", [1089]="In Search of Azdaja", [1091]="Where Everything Begins", [1092]="The Wind Rises", [1093]="One Final Wish", [1094]="Be Our Guest", [1115]="Generational Bonding", [1119]="King of the Mountain", [1120]="An Unforeseen Bargain", [1125]="Desires Untold", [1158]="Pandæmonium Awakens", [1159]="Abyssal Dark", [1161]="Going Haam", [1162]="Back to Action", [1166]="The Path Infernal", [1170]="And the Land Would Tremble", [1171]="On the Cloud", [1177]="The Game Is Afoot", [1181]="Down in the Dark", [1182]="The Heart of the Myth", [1183]="Gentlemen at Heart", [1184]="Down in the Dark", [1185]="A New World to Explore", [1186]="Solution Nine", [1187]="To Urqopacha", [1188]="To Kozama'uka", [1189]="The Leap to Yak T'el", [1190]="The Long Road to Xak Tural", [1191]="All Aboard", [1192]="Through the Gate of Gold", [1197]="Just Crowning Around", [1206]="A New World to Explore", [1210]="A Father First", [1211]="Taking a Stand", [1212]="The Feat of the Brotherhood", [1213]="The Protector and the Destroyer", [1214]="Dreams of a New Day", [1215]="An Antidote for Anarchy", [1216]="A Hunter True", [1217]="The Mightiest Shield", [1218]="Heroes and Pretenders", [1219]="All Aboard", [1220]="The Resilient Son", [1221]="Dawntrail", [1222]="Through the Gate of Gold", [1223]="Twisted Vengeance", [1224]="A New Challenger Appears", [1233]="Mind over Manor", [1234]="Somewhere Only She Knows", [1235]="Fangs of the Viper", [1236]="Vengeance of the Viper", [1237]="A Cosmic Homecoming", [1244]="The Warmth of Family", [1246]="Bar the Passage", [1253]="Spreading the Warmth and Cheer", [1254]="In Search of the Past", [1255]="Picking Up the Torch", [1264]="An Otherworldly Encounter", [1265]="The Hollow Promise", [1268]="A Glimmer of the Past", [1269]="One Last Hurrah", [1274]="Descent to the Foundation", [1275]="Descent to the Foundation", [1276]="Twisted Vengeance", [1277]="One Last Hurrah", [1291]="Go Forth, Brave Explorers", [1299]="Preservation Their Purpose", [1301]="The White Wanderer", [1305]="Frights of Fancy", [1310]="Mission of Gravity", [1312]="A Terminal Invitation", [1319]="The Forests of Paradise", [1328]="Where We Call Home", [1332]="Beyond the Mountains", [1334]="Through the Thunder", [1337]="A Spellbinding Read", [1338]="Where We Call Home", [1369]="守護天節と面妖なかくれんぼ", [1373]="A Grave Presentiment" };
    // Residential districts sit under City by IntendedUse (Town), but belong in Housing.
    // CN port: resolved from TerritoryType PlaceName at build time instead of English literals, so the names match the
    // client language. Mist 339, The Lavender Beds 340, The Goblet 341, Shirogane 641, Empyreum 979.
    private static readonly uint[] HousingDistrictTerritories = { 339, 340, 341, 641, 979 };
    private static readonly HashSet<string> HousingDistricts = new();
    private static readonly string[] ExShort = { "重生", "苍天", "红莲", "暗影", "晓月", "金曦" };
    private static readonly string[] ExLong =
        { "重生之境", "苍穹之禁城", "红莲之狂潮", "暗影之逆焰", "晓月之终焉", "金曦之遗辉" };

    private enum GroupKind { None, Region, Expansion, Type }
    private static GroupKind GroupKindFor(string cat) => cat switch
    {
        "World" or "City" or "Housing" or "Solo Instances" or "Solo Duty" or "All" => GroupKind.Region,
        "Dungeon" or "Variant & Criterion" or "Trial" or "Raid" => GroupKind.Expansion,
        "Other" => GroupKind.Type,   // Other sub-groups by its distinct kinds (Barracks, Hall of the Novice, …)
        _ => GroupKind.None,         // Inn, Deep Dungeon, PvP, Waiting Room, Seasonal, Treasure Map, Cosmic, Gold Saucer → flat
    };

    // Two-key: a duty ContentType (the Duty-Finder classifier) wins; otherwise TerritoryIntendedUse. Merges Raid1/2/
    // Alliance/Chaotic → Raid, Variant/Criterion → one, Gold-Saucer content (GATEs etc.) → Gold Saucer, exploratory
    // zones → Field Operations, and (as requested) Mordion Gaol → Housing.
    // CN port: keyed by ContentType RowId instead of its (localised) Name, so the classifier works on non-English
    // clients. 2 Dungeons, 4 Trials, 5 Raids, 28 Ultimate Raids, 37 Chaotic Alliance Raid, 21 Deep Dungeons, 6 PvP,
    // 30 V&C Dungeon Finder, 19 Gold Saucer.
    private static string CategoryFor(uint use, uint contentType) => contentType switch
    {
        2 => "Dungeon",
        4 => "Trial",
        5 or 28 or 37 => "Raid",
        21 => "Deep Dungeon",
        6 => "PvP",
        30 => "Variant & Criterion",
        19 => "Gold Saucer",
        _ => use switch
        {
            0 => "City",
            1 => "World",
            2 => "Inn",
            13 or 14 => "Housing",
            5 => "Housing",                        // Mordion Gaol (as requested)
            15 or 54 => "Solo Instances",
            29 => "Solo Duty",
            3 => "Dungeon",
            4 or 57 or 58 => "Variant & Criterion",
            7 or 10 => "Trial",
            8 or 16 or 17 or 36 => "Raid",
            31 => "Deep Dungeon",
            12 => "Waiting Room",
            32 or 34 or 40 or 63 => "Seasonal",
            33 => "Treasure Map",
            60 => "Cosmic Exploration",
            18 or 28 or 37 or 39 => "PvP",
            20 or 23 or 25 or 44 => "Gold Saucer",                     // Chocobo Racing, Gold Saucer, LoV, Leap of Faith
            26 or 38 or 41 or 47 or 48 or 61 => "Field Operations",    // Exploratory/Diadem, Eureka, Bozja/Zadnor, Occult
            52 or 53 => "Raid",                                        // Delubrum Reginae (Normal/Savage) - a 48-man duty
            _ => "Other",
        },
    };

    // Humanised kind name, for grouping the Other tab by its distinct types.
    private static string UseName(uint use) => use switch
    {
        6 => "初始区域",
        22 => "婚礼",
        27 => "新人训练所",
        30 => "军队兵营",
        35 or 50 or 51 => "九宫幻卡",
        45 => "假面狂欢",
        46 => "出海垂钓",
        52 or 53 => "女王古殿",
        59 => "假日游乐园",
        _ => "杂项",
    };

    // v0.7.229: Cosmic Exploration, Field Operations, and Gold Saucer are their own chips again (previously folded into
    // Other). Gold Saucer carries a lot of adventuring maps; the other two are RP-relevant field zones. Empty set =
    // nothing folds into Other, so the classifier's categories surface directly. Consolidate() is now a pass-through
    // but kept so the call sites don't churn and re-folding later is a one-line change.
    private static readonly HashSet<string> ConsolidatedIntoOther = new();
    private static string Consolidate(string cat) => ConsolidatedIntoOther.Contains(cat) ? "Other" : cat;

    // The canonical entry for a cluster: an Overworld/Town entry, else a base duty (CFC name without a tier suffix),
    // else the lowest id. So Hullbreaker → the dungeon, The Navel → normal, Central Shroud → the overworld.
    /// <summary>
    /// v0.7.361: resolve a free-text zone name to a territory ID for "/hms load &lt;name&gt;".
    /// Reuses the map picker's cluster list (territories folded by place name) and its PickPrimary heuristic, so a
    /// name that has several territory rows (Kugane = the city plus 4 quest copies) resolves to the canonical one
    /// rather than an arbitrary duplicate.
    ///
    /// Ranking, best first:
    ///   1. exact name match (case-insensitive)      "kugane castle" → Kugane Castle
    ///   2. whole-word prefix match                   "kugane"        → Kugane  (beats "Kugane Castle"/"Kugane Ohashi")
    ///   3. substring match                           "ohashi"        → Kugane Ohashi
    /// Within a tier: prefer a City/overworld category, then the shorter name (the plain place beats the compound
    /// one), then the lower territory ID. Returns false with a null name when nothing matches.
    /// </summary>
    public bool ResolveZoneByName(string query, out uint territoryId, out string resolvedName, out int otherMatches)
    {
        territoryId = 0; resolvedName = ""; otherMatches = 0;
        if (string.IsNullOrWhiteSpace(query)) return false;
        if (clusters == null) BuildClusters();
        if (clusters == null || clusters.Count == 0) return false;

        var q = query.Trim().ToLowerInvariant();

        // tier: 0 = exact, 1 = prefix, 2 = substring; lower is better
        (ZoneCluster c, int tier)? best = null;
        int matchCount = 0;
        foreach (var c in clusters)
        {
            var key = c.Key.ToLowerInvariant();
            int tier;
            if (key == q) tier = 0;
            else if (key.StartsWith(q, StringComparison.Ordinal)) tier = 1;
            else if (key.Contains(q, StringComparison.Ordinal)) tier = 2;
            else continue;

            matchCount++;
            if (best == null || Better(c, tier, best.Value.c, best.Value.tier)) best = (c, tier);
        }
        if (best == null) return false;

        territoryId = best.Value.c.Primary.Id;
        resolvedName = best.Value.c.Key;
        otherMatches = matchCount - 1;
        return true;
    }

    // Is (ca,ta) a better resolution than (cb,tb)? Tier first, then city-ness, then shorter name, then lower id.
    private static bool Better(ZoneCluster ca, int ta, ZoneCluster cb, int tb)
    {
        if (ta != tb) return ta < tb;
        bool cityA = IsMainish(ca), cityB = IsMainish(cb);
        if (cityA != cityB) return cityA;
        if (ca.Key.Length != cb.Key.Length) return ca.Key.Length < cb.Key.Length;
        return ca.Primary.Id < cb.Primary.Id;
    }

    // "Main" places a user most likely means: cities and overworld field zones (TerritoryIntendedUse 0/1 is what
    // PickPrimary already treats as canonical), plus anything the picker categorised as a City.
    private static bool IsMainish(ZoneCluster c)
        => c.Category == "City" || c.Primary.Use == 0 || c.Primary.Use == 1;

    private static ZoneRow PickPrimary(List<ZoneRow> lst)
    {
        foreach (var r in lst) if (r.Use == 0 || r.Use == 1) return r;
        foreach (var r in lst) if (r.CfcName.Length > 0 && !r.CfcName.Contains('(')) return r;
        return lst[0];
    }

    private void BuildClusters()
    {
        clusters = new List<ZoneCluster>();
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) { log.Warning("[Wanderlens] [MAPS] TerritoryType sheet null"); return; }

            string PlaceNameOf(uint id, string fallback)
                => sheet.GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } n ? n : fallback;
            HousingDistricts.Clear();
            foreach (var id in HousingDistrictTerritories) HousingDistricts.Add(PlaceNameOf(id, ""));
            var empyreumName = PlaceNameOf(979, "Empyreum");
            var limsaUpperName = PlaceNameOf(128, "Limsa Lominsa Upper Decks");

            var byName = new Dictionary<string, List<ZoneRow>>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                var name = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;   // fold key is the place name; unnamed are skipped
                var region = row.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
                var use = row.TerritoryIntendedUse.RowId;
                var ex = (byte)row.ExVersion.RowId;
                string cfcName = ""; uint ctype = 0; int sort = 0;
                var cfc = row.ContentFinderCondition.ValueNullable;
                if (cfc.HasValue)
                {
                    cfcName = cfc.Value.Name.ToString();
                    ctype = cfc.Value.ContentType.RowId;
                    sort = cfc.Value.SortKey;
                }
                var zr = new ZoneRow
                {
                    Id = row.RowId, Name = name, Region = region, Use = use, Ex = ex,
                    Category = CategoryFor(use, ctype), CfcName = cfcName, SortKey = sort,
                };
                if (zr.Id == 886) { zr.Name = empyreumName; zr.Category = "Housing"; }   // Firmament folds under Empyreum
                if (zr.Id == 181) { zr.Name = limsaUpperName; zr.Category = "City"; }   // Limsa opening folds under the city
                if (SeashipTerritories.Contains(zr.Id)) zr.Category = "Seaships";       // v0.7.235: curated thematic chip - ship decks/voyages, great for RP
                if (HousingDistricts.Contains(zr.Name)) zr.Category = "Housing";                    // residential districts → Housing
                if (!byName.TryGetValue(zr.Name, out var lst)) { lst = new List<ZoneRow>(); byName[zr.Name] = lst; }
                lst.Add(zr);
            }
            foreach (var kv in byName)
            {
                kv.Value.Sort((a, b) => a.Id.CompareTo(b.Id));
                var primary = PickPrimary(kv.Value);
                clusters.Add(new ZoneCluster
                {
                    Key = kv.Key, Primary = primary, Variants = kv.Value,
                    Category = Consolidate(primary.Category), Region = primary.Region, Ex = primary.Ex,
                    TypeName = ConsolidatedIntoOther.Contains(primary.Category) ? primary.Category : UseName(primary.Use),
                });
            }
            log.Information("[Wanderlens] [MAPS] " + clusters.Count + " clusters from named territories");
        }
        catch (Exception ex)
        {
            log.Error("[Wanderlens] [MAPS] cluster build failed: " + ex.Message);
        }
    }

    private void DrawZonesTab()
    {
        var tabFlags = ImGuiTabItemFlags.None;
        if (focusZonesTab) { tabFlags = ImGuiTabItemFlags.SetSelected; focusZonesTab = false; }
        if (!ImGui.BeginTabItem("地图", tabFlags)) return;
        BeginTabBody("##zonesbody");
        DrawMapInventory(0f);
        EndTabBody();
        ImGui.EndTabItem();
    }

    // The tier/instance tag shown on a variant sub-row (the parent already carries location).
    private static string VariantTag(ZoneRow v)
    {
        if (v.Id == 886) return "天穹街";   // The Firmament - festival face of the Empyreum housing district
        if (v.CfcName.Length > 0 && v.CfcName.IndexOf('(') >= 0)
        {
            int tp = v.CfcName.IndexOf('(');
            return v.CfcName.Substring(tp).Trim('(', ')', ' ');
        }
        if (v.CfcName.Length > 0)
        {
            int p = v.CfcName.IndexOf('(');
            if (p >= 0) return v.CfcName.Substring(p).Trim('(', ')', ' ');
            // CN port: CN duty names carry no "(...)" tier suffix, so show the full duty name - otherwise same-place
            // variants (e.g. 诗想空间: 968 DSR vs 1166) were indistinguishable "副本" rows.
            return v.CfcName + " · " + v.Id;
        }
        if (QuestNames.TryGetValue(v.Id, out var questName)) return SceneNames.Quest(questName) + " · " + v.Id;
        return v.Use switch
        {
            0 => "城镇副本",
            1 => "野外副本",
            6 => "初始区域",
            9 => "任务战",
            15 or 54 => "单人场景",
            29 => "单人任务",
            4 => "多变迷宫",
            57 or 58 => "异闻迷宫",
            _ => "副本",
        };
    }

    private string InfoText(ZoneCluster c)
    {
        if (activeCat == "All") return CatLabel(c.Category);                          // mixed view → the kind
        if (c.Category == "Deep Dungeon") return c.Variants.Count + " 组楼层";
        if (GroupKindFor(c.Category) == GroupKind.Expansion)
            return string.IsNullOrEmpty(c.Region) ? "" : c.Region;         // content grouped by exp → location
        return c.Ex < ExShort.Length ? ExShort[c.Ex] : "";                 // places/flat → expansion
    }

    private void DrawMapInventory(float tableHeight)
    {
        if (clusters == null) BuildClusters();

        // Search + Clear.
        ImGui.SetNextItemWidth(-70f);
        ImGui.InputTextWithHint("##mapfilter", "按名称、地区或 ID 筛选…", ref filter, 128);
        ImGui.SameLine();
        bool hasFilter = !string.IsNullOrEmpty(filter);
        if (!hasFilter) ImGui.BeginDisabled();
        if (ImGui.Button("清空##mapfilter", new Vector2(-1, 0f))) filter = "";
        if (!hasFilter) ImGui.EndDisabled();
        // v0.7.229: "Named maps only" checkbox hidden. The named/unnamed split is a hand-maintained list, so the toggle
        // was inert (onlyNamed is read nowhere) - and unchecking would only surface black, mesh-less, unlit void maps
        // that are useless to browse. Users can still `hms load <id>` an unnamed zone manually. Re-enable this (and add
        // custom chip naming) once the map editor lands and empty maps become usable skeletons for custom builds.
        // ImGui.Checkbox("只显示有名称的地图", ref onlyNamed);
        ImGui.Spacing();

        // Category pill row (single-select), reflowing across the width.
        {
            var acc = Accent();
            float avail0 = ImGui.GetContentRegionAvail().X, x = 0f; const float gap = 6f;
            for (int i = 0; i < Categories.Length; i++)
            {
                string lbl = Categories[i];
                float w = ImGui.CalcTextSize(CatLabel(lbl)).X + ImGui.GetStyle().FramePadding.X * 2f + 4f;
                if (i > 0) { if (x + gap + w > avail0) x = 0f; else { ImGui.SameLine(0f, gap); x += gap; } }
                bool on = activeCat == lbl;
                ImGui.PushStyleColor(ImGuiCol.Button, on ? Darken(acc, 0.5f) : new Vector4(0.15f, 0.16f, 0.19f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Text, on ? Lighten(acc, 1.05f) : new Vector4(0.72f, 0.75f, 0.80f, 1f));
                if (ImGui.Button(CatLabel(lbl) + "##cat" + i, new Vector2(w, 0f))) activeCat = lbl;
                ImGui.PopStyleColor(4);
                x += w;
            }
        }
        ImGui.Spacing();

        if (activeCat == "Cutscenes") { DrawCutscenes(tableHeight); return; }

        // Filter clusters.
        var f = filter.Trim();
        IEnumerable<ZoneCluster> shownE = clusters!;
        if (activeCat != "All") shownE = shownE.Where(c => c.Category == activeCat);
        if (f.Length > 0)
        {
            if (uint.TryParse(f, out var fid))
                shownE = shownE.Where(c => c.Variants.Any(v => v.Id == fid) || c.Key.Contains(f, StringComparison.OrdinalIgnoreCase));
            else
                shownE = shownE.Where(c => c.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
                                        || c.Region.Contains(f, StringComparison.OrdinalIgnoreCase)
                                        || c.Variants.Any(v => QuestNames.TryGetValue(v.Id, out var q) && (q.Contains(f, StringComparison.OrdinalIgnoreCase) || SceneNames.Quest(q).Contains(f, StringComparison.OrdinalIgnoreCase)))
                                        || c.Variants.Any(v => DressingsFor(v.Id).Any(e => (e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.NameEn.Contains(f, StringComparison.OrdinalIgnoreCase))
                                                                                     || (e.Quest ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))));   // b266
        }
        var list = shownE.ToList();

        var fav = config.FavouriteZones;
        var acc2 = Accent();
        var gk = GroupKindFor(activeCat);

        // The table's right edge (for spanning section labels across columns, Excel-style).
        float rowRightEdge;
        { var tl0 = ImGui.GetCursorScreenPos(); rowRightEdge = tl0.X + ImGui.GetContentRegionAvail().X - 12f; }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.Sortable | ImGuiTableFlags.PadOuterX;
        if (ImGui.BeginTable("##zonetbl", 4, flags, new Vector2(0f, tableHeight)))
        {
            SuspendWrap();   // v0.7.432
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("\u2605##star", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize, 22f);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 52f);
            ImGui.TableSetupColumn("地区 / 地图名", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("信息", ImGuiTableColumnFlags.WidthFixed, 132f);
            ImGui.TableHeadersRow();

            int sortCol = 1; bool sortAsc = true;
            unsafe
            {
                var sp = ImGui.TableGetSortSpecs();
                var raw = sp.Handle;
                if (raw != null && raw->SpecsCount > 0 && raw->Specs != null)
                {
                    sortCol = raw->Specs->ColumnIndex;
                    sortAsc = raw->Specs->SortDirection != ImGuiSortDirection.Descending;
                }
            }
            System.Func<ZoneCluster, object> skey = sortCol switch
            {
                2 => c => c.Key,
                3 => c => c.Category,
                _ => c => c.Primary.Id,
            };
            IEnumerable<ZoneCluster> Sort(IEnumerable<ZoneCluster> src) => sortAsc ? src.OrderBy(skey) : src.OrderByDescending(skey);

            void DrawStar(uint id)
            {
                bool s = fav.Contains(id);
                ImGui.AlignTextToFramePadding();
                ImGui.PushStyleColor(ImGuiCol.Text, s ? acc2 : new Vector4(0.34f, 0.35f, 0.39f, 1f));
                if (ImGui.Selectable((s ? "\u2605" : "\u2606") + "##star" + id, false, ImGuiSelectableFlags.None, new Vector2(16f, 0f)))
                { if (s) fav.Remove(id); else fav.Add(id); config.Save(); }
                ImGui.PopStyleColor();
            }


            // Outline pill for the name; a fixed toggle slot before EVERY pill keeps all pill left-edges aligned
            // (the disclosure triangle sits in that slot for clusters; the slot is just empty space otherwise -
            // no placeholder glyph, which would only add noise). depth 1 = a nested variant row.
            void DrawNamePill(string label, uint id, bool multi, bool open, string cluKey, int depth, int csIndex = -1)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 2f));
                float slot = ImGui.GetFrameHeight();
                if (depth > 0) { ImGui.Dummy(new Vector2(slot + 16f, 1f)); ImGui.SameLine(0f, 4f); }
                if (multi)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.60f, 0.63f, 0.70f, 1f));
                    if (ImGui.ArrowButton("##exp" + id, open ? ImGuiDir.Down : ImGuiDir.Right))
                    { if (open) expandedClusters.Remove(cluKey); else expandedClusters.Add(cluKey); }
                    ImGui.PopStyleColor(4);
                    ImGui.SameLine(0f, 4f);
                }
                else if (depth == 0) { ImGui.Dummy(new Vector2(slot, 1f)); ImGui.SameLine(0f, 4f); }

                var acc = acc2;
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Darken(acc, 0.55f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(acc, 0.42f));
                ImGui.PushStyleColor(ImGuiCol.Text, Lighten(acc, 1.05f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(acc.X, acc.Y, acc.Z, 0.30f));
                if (csIndex >= 0) { if (ImGui.Button(label + "##dress" + csIndex)) OnLoadCutscene?.Invoke(csIndex); }   // b266: a dressing row
                else if (ImGui.Button(label + "##load" + id)) OnQuickLoad?.Invoke(id);
                ImGui.PopStyleColor(5);
                ImGui.PopStyleVar();
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.PopStyleVar();
            }
            void InfoCell(string text) { ImGui.TableSetColumnIndex(3); if (!string.IsNullOrEmpty(text)) { ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(text); } }

            // b266: cutscene dressings on a territory, listed under it like its TT variants. Curated rows keep their name
            // ("Valnain") with the quest as info; auto-ranked rows are named by quest (their name is just the place).
            void DressingRows(uint tt)
            {
                foreach (var e in DressingsFor(tt)) DressingRow(e);   // b288: set-piece scenes list with the rest (no extra nesting)

                void DressingRow(CutsceneEntry e)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); DrawCutsceneStar(e.Bg, acc2);
                    ImGui.TableSetColumnIndex(1); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(tt.ToString());
                    // b273: map edits (not cutscenes) show their own name and say what they are
                    bool mapEdit = e.Quest == HMSync.Features.CutsceneDressing.MapEdits.Quest;   // SceneNames.Quest passes this marker through untranslated
                    string label = !mapEdit && e.Lab && !string.IsNullOrEmpty(e.Quest) ? e.Quest : e.Name;
                    ImGui.TableSetColumnIndex(2); DrawNamePill("\u2726 " + label, tt, false, false, "", 1, e.Index);
                    InfoCell(mapEdit ? "地图编辑" : e.Lab || string.IsNullOrEmpty(e.Quest) ? "过场动画" : e.Quest);
                }
            }

            void ClusterRows(ZoneCluster c)
            {
                bool hasDressings = c.Variants.Any(v => DressingsFor(v.Id).Count > 0);
                bool multi = c.Variants.Count > 1 || hasDressings;
                bool open = multi && expandedClusters.Contains(c.Key);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); DrawStar(c.Primary.Id);
                ImGui.TableSetColumnIndex(1); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(c.Primary.Id.ToString());
                ImGui.TableSetColumnIndex(2); DrawNamePill(c.Key, c.Primary.Id, multi, open, c.Key, 0);
                InfoCell(InfoText(c));
                if (open)
                {
                    DressingRows(c.Primary.Id);   // b266: the primary's dressings first
                    foreach (var v in c.Variants)
                    {
                        if (v.Id == c.Primary.Id) continue;   // primary is the parent row - don't list it twice
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0); DrawStar(v.Id);
                        ImGui.TableSetColumnIndex(1); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(v.Id.ToString());
                        ImGui.TableSetColumnIndex(2); DrawNamePill(v.Name, v.Id, false, false, c.Key, 1);
                        InfoCell(VariantTag(v));
                        DressingRows(v.Id);
                    }
                }
            }
            void SectionRow(string label, bool pinned)
            {
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(pinned ? new Vector4(0.20f, 0.16f, 0.06f, 1f) : new Vector4(0.14f, 0.15f, 0.18f, 1f)));
                ImGui.TableSetColumnIndex(0);
                var p = ImGui.GetCursorScreenPos();
                float lh = ImGui.GetTextLineHeight();
                ImGui.Dummy(new Vector2(1f, lh));
                var dl = ImGui.GetWindowDrawList();
                var cmin = dl.GetClipRectMin();
                var cmax = dl.GetClipRectMax();
                dl.PushClipRect(new Vector2(cmin.X, cmin.Y), new Vector2(rowRightEdge, cmax.Y), false);
                dl.AddText(new Vector2(p.X + 5f, p.Y), ImGui.GetColorU32(new Vector4(0.66f, 0.70f, 0.78f, 1f)), label);
                dl.PopClipRect();
            }

            // One cutscene row (star + code + accent name-pill + info), rendered inside this zone table. Shared by the
            // "All" Pinned section and the Cutscene-locations/Seaships fold-in so a cutscene row looks identical to a
            // zone row wherever it appears.
            void CutsceneRow(CutsceneEntry c)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); DrawCutsceneStar(c.Bg, acc2);
                ImGui.TableSetColumnIndex(1); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(c.Code ?? "");
                ImGui.TableSetColumnIndex(2);
                var accCs = acc2;
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 2f));
                // Indent the pill by the fold-arrow slot a single zone row reserves (DrawNamePill's depth==0 spacer),
                // so cutscene names line up under the zone name pills instead of sitting flush against the id column.
                float csSlot = ImGui.GetFrameHeight();
                ImGui.Dummy(new Vector2(csSlot, 1f)); ImGui.SameLine(0f, 4f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Darken(accCs, 0.55f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(accCs, 0.42f));
                ImGui.PushStyleColor(ImGuiCol.Text, Lighten(accCs, 1.05f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accCs.X, accCs.Y, accCs.Z, 0.30f));
                if (ImGui.Button(c.Name + "##allcs" + c.Index)) OnLoadCutscene?.Invoke(c.Index);
                ImGui.PopStyleColor(5);
                ImGui.PopStyleVar(2);
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                InfoCell(string.IsNullOrEmpty(c.Quest) ? c.Region : (c.Region + " · " + c.Quest));
            }

            // Favourited cutscenes pin into "All" exactly like favourited zones (they already pin in the Cutscenes tab).
            // Respect the search box, same as zone favourites (which are derived from the already-filtered `list`).
            List<CutsceneEntry> favCutscenes = new();
            if (activeCat == "All" && CutsceneEntries.Count > 0)
            {
                favCutscenes = CutsceneEntries.Where(c => !string.IsNullOrEmpty(c.Bg) && config.FavouriteCutsceneBgs.Contains(c.Bg)).ToList();
                if (f.Length > 0)
                    favCutscenes = favCutscenes.Where(c => (c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || c.NameEn.Contains(f, StringComparison.OrdinalIgnoreCase))
                        || c.Region.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (c.Quest ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (c.Code ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var favClusters = list.Where(c => fav.Contains(c.Primary.Id)).ToList();
            if (favClusters.Count > 0 || favCutscenes.Count > 0)
            {
                SectionRow("置顶", true);
                foreach (var c in Sort(favClusters)) ClusterRows(c);
                foreach (var c in favCutscenes.OrderBy(c => c.Name)) CutsceneRow(c);
            }
            var rest = list.Where(c => !fav.Contains(c.Primary.Id)).ToList();

            static bool UnknownRegion(string r) => string.IsNullOrEmpty(r) || r == "???";

            if (gk == GroupKind.None)
                foreach (var c in Sort(rest)) ClusterRows(c);
            else if (gk == GroupKind.Region)
            {
                var groups = rest.GroupBy(c => UnknownRegion(c.Region) ? "\u2014" : c.Region).ToList();
                groups.Sort((a, b) =>
                {
                    bool au = a.Key == "\u2014", bu = b.Key == "\u2014";
                    if (au != bu) return au ? 1 : -1;
                    return a.Min(c => c.Primary.Id).CompareTo(b.Min(c => c.Primary.Id));
                });
                foreach (var g in groups) { SectionRow(g.Key, false); foreach (var c in Sort(g)) ClusterRows(c); }
            }
            else if (gk == GroupKind.Expansion)
            {
                foreach (var g in rest.GroupBy(c => (int)c.Ex).OrderBy(g => g.Key))
                {
                    SectionRow(g.Key >= 0 && g.Key < ExLong.Length ? ExLong[g.Key] : "\u2014", false);
                    foreach (var c in Sort(g)) ClusterRows(c);
                }
            }
            else   // Type - Other, grouped by its distinct kinds (Barracks, Hall of the Novice, Triple Triad, …)
            {
                foreach (var g in rest.GroupBy(c => string.IsNullOrEmpty(c.TypeName) ? "杂项" : c.TypeName)
                                      .OrderBy(g => g.Key == "\u6742\u9879" ? "\uFFFF" : g.Key))
                {
                    SectionRow(g.Key, false);
                    foreach (var c in Sort(g)) ClusterRows(c);
                }
            }

            // v0.7.232b/235: cutscene stages are places too. In "All" WITH a filter, surface matches by search; in the
            // curated "Seaships" chip, surface the two ship cutscene stages always. Render as rows INSIDE this
            // scrollable table (drawing after EndTable() would land them off-screen - the table fills the window).
            bool csShown = false;
            List<CutsceneEntry> csMatches = new();
            if (activeCat == "All" && f.Length > 0 && CutsceneEntries.Count > 0)
            {
                csMatches = CutsceneEntries.Where(c =>
                    (c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || c.NameEn.Contains(f, StringComparison.OrdinalIgnoreCase))
                    || c.Region.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (c.Quest ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (c.Code ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (activeCat == "Seaships" && CutsceneEntries.Count > 0)
            {
                csMatches = CutsceneEntries.Where(c => SeashipCutsceneBgs.Contains(c.Bg)).ToList();
                if (f.Length > 0)   // respect the search box within the Seaships chip too
                    csMatches = csMatches.Where(c => (c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || c.NameEn.Contains(f, StringComparison.OrdinalIgnoreCase))
                        || (c.Code ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            // Drop any cutscene already shown in the "All" Pinned section above (no double-listing, mirrors how favourited
            // zones appear only under Pinned and not again in the body).
            if (favCutscenes.Count > 0)
            {
                var pinnedBgs = new HashSet<string>(favCutscenes.Select(c => c.Bg));
                csMatches = csMatches.Where(c => !pinnedBgs.Contains(c.Bg)).ToList();
            }
            if (csMatches.Count > 0)
            {
                csShown = true;
                SectionRow(activeCat == "Seaships" ? "船只过场" : "过场地点", false);
                foreach (var c in csMatches) CutsceneRow(c);
            }

            if (list.Count == 0 && !csShown && favCutscenes.Count == 0) { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("没有匹配的地图。"); }
            ResumeWrap();
            ImGui.EndTable();
        }
    }

    // Cutscene twin of DrawZones' DrawStar - same glyph/colour/hit-slot, but keyed by bg path (FavouriteCutsceneBgs)
    // since cutscenes share a donor territory id and can't live in FavouriteZones. Shared by both cutscene surfaces
    // (the All/Seaships fold-in rows and the dedicated Cutscenes tab) so a cutscene's star looks identical to a zone's.
    private void DrawCutsceneStar(string bg, Vector4 acc)
    {
        bool s = !string.IsNullOrEmpty(bg) && config.FavouriteCutsceneBgs.Contains(bg);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, s ? acc : new Vector4(0.34f, 0.35f, 0.39f, 1f));
        if (ImGui.Selectable((s ? "\u2605" : "\u2606") + "##csstar" + bg, false, ImGuiSelectableFlags.None, new Vector2(16f, 0f)))
            config.ToggleFavouriteCutscene(bg);
        ImGui.PopStyleColor();
    }

    // Cutscene-stage list (own render path): star + region sections, name-as-load pill, quest inline as helper.
    // Favourited stages pin to a "Pinned" section on top, mirroring the zone chip.
    private void DrawCutscenes(float tableHeight)
    {
        if (CutsceneEntries.Count == 0) { ImGui.TextDisabled("过场场景不可用。"); return; }
        var f = filter.Trim();
        // b266: the Cutscenes chip lists stages WITHOUT a territory of their own (bg-swap stages, dressed or not). A dressing
        // on a real territory lives in the zone tree under that territory, next to its TT variants (DressingsFor).
        IEnumerable<CutsceneEntry> shown = CutsceneEntries.Where(e => e.Id == 0);
        if (f.Length > 0)
            shown = shown.Where(e => (e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.NameEn.Contains(f, StringComparison.OrdinalIgnoreCase))
                                  || (e.Code ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || (e.Quest ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || (e.Region ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));
        var list = shown.ToList();
        var acc2 = Accent();
        var favCs = config.FavouriteCutsceneBgs;

        // The table's right edge (for spanning section labels across columns), matching DrawZones' SectionRow.
        float rowRightEdge;
        { var tl0 = ImGui.GetCursorScreenPos(); rowRightEdge = tl0.X + ImGui.GetContentRegionAvail().X - 12f; }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.PadOuterX;
        if (ImGui.BeginTable("##csstbl", 4, flags, new Vector2(0f, tableHeight)))
        {
            SuspendWrap();   // v0.7.432
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("\u2605##csstarcol", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 22f);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableSetupColumn("地区 / 场景", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("任务", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableHeadersRow();

            void NamePill(string label, int idx, bool indented = false)
            {
                var acc = acc2;
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 2f));
                // Match the zone tab's single-row indent (fold-arrow slot) so the cutscene tab reads consistently.
                // b267: skipped when the caller already drew the fold arrow or a child indent.
                float slot = ImGui.GetFrameHeight();
                if (!indented) { ImGui.Dummy(new Vector2(slot, 1f)); ImGui.SameLine(0f, 4f); }
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Darken(acc, 0.55f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(acc, 0.42f));
                ImGui.PushStyleColor(ImGuiCol.Text, Lighten(acc, 1.05f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(acc.X, acc.Y, acc.Z, 0.30f));
                if (ImGui.Button(label + "##cs" + idx)) OnLoadCutscene?.Invoke(idx);
                ImGui.PopStyleColor(5);
                ImGui.PopStyleVar(2);
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            void SectionRow(string label, bool pinned)
            {
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(pinned ? new Vector4(0.20f, 0.16f, 0.06f, 1f) : new Vector4(0.14f, 0.15f, 0.18f, 1f)));
                ImGui.TableSetColumnIndex(0);
                var p = ImGui.GetCursorScreenPos();
                float lh = ImGui.GetTextLineHeight();
                ImGui.Dummy(new Vector2(1f, lh));
                var dl = ImGui.GetWindowDrawList();
                var cmin = dl.GetClipRectMin();
                var cmax = dl.GetClipRectMax();
                dl.PushClipRect(new Vector2(cmin.X, cmin.Y), new Vector2(rowRightEdge, cmax.Y), false);
                dl.AddText(new Vector2(p.X + 5f, p.Y), ImGui.GetColorU32(new Vector4(0.66f, 0.70f, 0.78f, 1f)), label);
                dl.PopClipRect();
            }

            void EntryRow(CutsceneEntry e, int depth = 0, bool hasChildren = false, string groupKey = "")
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); DrawCutsceneStar(e.Bg, acc2);
                ImGui.TableSetColumnIndex(1);
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(e.Id != 0 ? e.Id.ToString() : (string.IsNullOrEmpty(e.Code) ? "\u2014" : e.Code));
                ImGui.TableSetColumnIndex(2);
                // b267: a stage with variants gets the zone tree's fold arrow; its variants indent beneath it.
                if (hasChildren)
                {
                    bool open = expandedClusters.Contains(groupKey);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.60f, 0.63f, 0.70f, 1f));
                    if (ImGui.ArrowButton("##csexp" + e.Index, open ? ImGuiDir.Down : ImGuiDir.Right))
                    { if (open) expandedClusters.Remove(groupKey); else expandedClusters.Add(groupKey); }
                    ImGui.PopStyleColor(4);
                    ImGui.SameLine(0f, 4f);
                }
                else if (depth > 0) { ImGui.Dummy(new Vector2(ImGui.GetFrameHeight() + 16f, 1f)); ImGui.SameLine(0f, 4f); }
                string label = depth > 0 ? "\u2726 " + (e.Lab && !string.IsNullOrEmpty(e.Quest) ? e.Quest : e.Name) : e.Name;
                NamePill(label, e.Index, indented: hasChildren || depth > 0);
                ImGui.TableSetColumnIndex(3);
                if (!string.IsNullOrEmpty(e.Quest)) { ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(depth > 0 && e.Lab ? "过场动画" : e.Quest); }
            }

            // b267: entries sharing a stage bg (Bg before '#') form one group: the undressed stage (or the first) is the
            // parent row, the dressed/lab variants fold under it, exactly like a territory and its TT variants.
            static string BaseBg(CutsceneEntry e) { var i = e.Bg.IndexOf('#'); return i < 0 ? e.Bg : e.Bg[..i]; }
            void GroupRows(IEnumerable<CutsceneEntry> entries)
            {
                foreach (var grp in entries.GroupBy(BaseBg).OrderBy(gr => gr.Min(en => en.Name)))
                {
                    var members = grp.OrderBy(en => en.Dressed).ThenBy(en => en.Lab).ThenBy(en => en.Name).ToList();
                    if (members.Count == 1) { EntryRow(members[0]); continue; }
                    string gk = "cs:" + grp.Key;
                    EntryRow(members[0], 0, true, gk);
                    if (expandedClusters.Contains(gk))
                        foreach (var m in members.Skip(1)) EntryRow(m, 1);
                }
            }

            // Pinned favourites first (across all regions), mirroring the zone chip's "Pinned" section.
            var pinned = list.Where(e => !string.IsNullOrEmpty(e.Bg) && favCs.Contains(e.Bg)).ToList();
            if (pinned.Count > 0)
            {
                SectionRow("置顶", true);
                foreach (var e in pinned.OrderBy(en => en.Name)) EntryRow(e);   // pinned rows stay flat (each was starred on its own)
            }
            var rest = list.Where(e => string.IsNullOrEmpty(e.Bg) || !favCs.Contains(e.Bg)).ToList();

            static int ExpOrd(string x) => x switch { "ARR" => 0, "HW" => 1, "SB" => 2, "ShB" => 3, "EW" => 4, "DT" => 5, _ => 9 };
            foreach (var g in rest.GroupBy(e => string.IsNullOrEmpty(e.Region) ? "\u2014" : e.Region).OrderBy(gr => ExpOrd(gr.Key)))
            {
                SectionRow(g.Key, false);
                GroupRows(g);   // b267: same-stage entries fold under one parent
            }
            if (list.Count == 0) { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("没有匹配的过场场景。"); }
            ResumeWrap();
            ImGui.EndTable();
        }
    }

    // S326: class-level section header (the `Section` used elsewhere is a local function scoped to DrawSessionTab).
    private static void SectionHeader(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled(label);
    }


    // Tab: Emotes - laid out like the in-game emote menu: two columns, icon + name, with the id appended for
    // reference. Favourites + Recently-played sit on top as collapsible two-column grids; the full searchable
    // catalogue fills the rest. Click a name to play it (routes through /hms emote → synced in a session).
    // Locked emotes are greyed + inert out of session. This whole tab is the layout template for the mount list.
    // Tab: Character - 2×2 quadrant grid of the collectible pickers, replacing the old flat stack of collapsibles
    // (which got noisy). Layout: Emotes | Mounts on top, Minions | Accessories below. Each quadrant is a bordered,
    // titled fixed-height box holding its search row + nested All/Fav/History. The TOP row height is adjustable via a
    // divider handle; the bottom row fills the remainder, so dragging trades space between the vertical neighbours.
    private void DrawCharacterTab()
    {
        if (!ImGui.BeginTabItem("召唤"))
            return;
        BeginTabBody("##summonsbody");

        // Chip-selected single sheet: one of Emotes/Mounts/Minions/Accessories at a time (mirrors the Zones chip
        // pattern - same styling). Each body is self-contained (its own search + action button via
        // DrawCollectibleBody), so they slot into one sheet cleanly. Far less cramped than the old 2×2 quadrant grid.
        string[] chips = { "情感动作", "坐骑", "宠物", "时尚配饰" };
        var acc = Accent();
        for (int i = 0; i < chips.Length; i++)
        {
            bool on = summonsChip == i;
            ImGui.PushStyleColor(ImGuiCol.Button, on ? Darken(acc, 0.5f) : new Vector4(0.15f, 0.16f, 0.19f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, on ? Lighten(acc, 1.05f) : new Vector4(0.72f, 0.75f, 0.80f, 1f));
            if (ImGui.Button(chips[i] + "##summonchip")) summonsChip = i;
            ImGui.PopStyleColor(4);
            if (i < chips.Length - 1) ImGui.SameLine(0f, 6f);
        }
        ImGui.Separator();

        // The selected sheet fills the remaining space.
        switch (summonsChip)
        {
            case 0: DrawEmotesBody(); break;
            case 1: DrawMountsBody(); break;
            case 2: DrawMinionsBody(); break;
            case 3: DrawAccessoriesBody(); break;
        }

        EndTabBody();
        ImGui.EndTabItem();
    }

    // Tab: Packets - inbound packet inspector, modeled on Dalamud's Network Monitor. Lets us learn what specific opcodes
    // carry on the live client (e.g. the 103/356 that Hyperborea/AnoMech allow through) without an external tool. Capture
    // OBSERVES only: in a session it still drops after logging; out of session it passes packets through. Use in an inn
    // (clean stream) and optionally filter to specific opcodes.
    private string pktFilterInput = "";
    private string pktDisplayFilter = "";
    private bool pktLocalOnly;
    private void DrawPacketsTab()
    {
        if (!ImGui.BeginTabItem("数据包"))
            return;
        BeginTabBody("##packetsbody");

        bool active = CaptureActive?.Invoke() ?? false;

        // Toggle + opcode filter + clear, on one control row.
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("停止捕获")) StopCapture?.Invoke();
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.45f, 0.20f, 1f));
            if (ImGui.Button("开始捕获")) SetCapture?.Invoke(string.IsNullOrWhiteSpace(pktFilterInput) ? null : pktFilterInput);
            ImGui.PopStyleColor();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputTextWithHint("##pktopcodes", "opcode，例如 103,356", ref pktFilterInput, 64);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只捕获这些接收 opcode（逗号分隔）。留空 = 全部接收（数据量很大，建议在旅馆里用）。");
        ImGui.SameLine();
        if (ImGui.Button("清空")) ClearCapture?.Invoke();

        // Status line + a display-only filter (narrows what's shown without restarting capture).
        var packets = SnapshotCapture?.Invoke() ?? new List<PacketFilterService.CapturedPacket>();
        ImGui.TextDisabled((active ? "捕获中" : "已停止") + " - " + packets.Count + " 个数据包在缓冲区（最多 500）。");
        var mapStatus = OpcodeMapStatus?.Invoke();
        if (!string.IsNullOrEmpty(mapStatus)) ImGui.TextDisabled(mapStatus);
        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##pktdisp", "按 opcode 筛选显示行…", ref pktDisplayFilter, 64);
        ImGui.SameLine();
        ImGui.Checkbox("仅自己的角色", ref pktLocalOnly);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只显示角色 ID 与你匹配的数据包（payload+0），人多的地方很有用。");

        uint localEid = pktLocalOnly ? (LocalEntityId?.Invoke() ?? 0u) : 0u;

        HashSet<ushort>? dispSet = null;
        if (!string.IsNullOrWhiteSpace(pktDisplayFilter))
        {
            dispSet = new HashSet<ushort>();
            foreach (var p in pktDisplayFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (ushort.TryParse(p, out var op)) dispSet.Add(op);
            if (dispSet.Count == 0) dispSet = null;
        }

        ImGui.Separator();

        // The table - Index / Time / OpCode / Hex / Name / EntityId / Payload. Newest at the bottom (auto-scroll).
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("##pkttable", 7, flags, new Vector2(0f, 0f)))
        {
            SuspendWrap();   // v0.7.432
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed, 66f);
            ImGui.TableSetupColumn("Op", ImGuiTableColumnFlags.WidthFixed, 46f);
            ImGui.TableSetupColumn("Hex", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("内容（前 32 字节）- 点击复制", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (int i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                if (dispSet != null && !dispSet.Contains(p.Opcode)) continue;
                if (pktLocalOnly && localEid != 0 && p.EntityId != localEid) continue;
                string name = OpcodeName?.Invoke(p.Opcode) ?? "";
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted((i + 1).ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.WallClock.ToString("HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Opcode.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted("0x" + p.Opcode.ToString("X3"));
                ImGui.TableNextColumn();
                if (name.Length > 0) ImGui.TextUnformatted(name);
                else ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (p.EntityId != 0)
                {
                    string an = EntityName?.Invoke(p.EntityId) ?? "";
                    if (an.Length > 0) ImGui.TextUnformatted(an);
                    else ImGui.TextDisabled("0x" + p.EntityId.ToString("X8"));
                }
                else ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (ImGui.Selectable(p.PayloadHex + "##pk" + i, false, ImGuiSelectableFlags.SpanAllColumns))
                    ImGui.SetClipboardText("op=" + p.Opcode + " (0x" + p.Opcode.ToString("X3") + ") " + (name.Length > 0 ? name + " " : "") + "eid=0x" + p.EntityId.ToString("X8") + " ts=" + p.Timestamp + " payload=" + p.PayloadHex);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("点击复制此数据包（opcode + 名称 + 内容）到剪贴板。");
            }

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f)
                ImGui.SetScrollHereY(1f);

            ResumeWrap();
            ImGui.EndTable();
        }

        EndTabBody();
        ImGui.EndTabItem();
    }

    // One quadrant: a titled, bordered child region of the given height wrapping a body draw. The body scrolls within.
    private void DrawQuadrant(string title, string id, float height, System.Action body, string? hint = null)
    {
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.90f, 1f), title);
        if (!string.IsNullOrEmpty(hint))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.58f, 1f));   // fainter than TextDisabled
            ImGui.TextUnformatted(hint);
            ImGui.PopStyleColor();
        }
        if (ImGui.BeginChild(id, new Vector2(0f, height), true))
            body();
        ImGui.EndChild();
    }

    // Fixed-width action button whose LABEL flips with state (Play/Stop, Summon/Dismiss, Mount/Dismount). Fixed size so
    // the swap doesn't make the button swell when the "off" word is longer. `active` = something is currently out:
    // pressing it runs the OFF command (id "0"/"stop"); when inactive it runs the ON command with the search text.
    private const float ActionBtnW = 84f;
    private void ActionButton(string idTag, bool active, string onLabel, string offLabel, string onArg, string cmd, string offArg)
    {
        var label = (active ? offLabel : onLabel) + "##act" + idTag;
        if (ImGui.Button(label, new Vector2(ActionBtnW, 0f)))
        {
            if (active) RunCommand?.Invoke(cmd, offArg);
            else if (onArg.Trim().Length > 0) RunCommand?.Invoke(cmd, onArg.Trim());
        }
    }

    // Option A merged list: search box + one dynamic action button, then a SINGLE list - Recent-then-All when the search
    // is empty (a faint "Recent" / "All" divider between), or just filtered results (no divider) when searching. The
    // quadrant child is the only scroll surface - the grid renders at natural height (0), so there is NO nested scroll.
    // `recent` is the history list (most-recent-first); `catalog` yields (id,name) for the full set + filter.
    private void DrawCollectibleBody(
        string idTag, string searchHint, ref string searchBuf,
        bool active, string onLabel, string offLabel, string cmd, string offArg,
        IEnumerable<(ushort id, string name)> catalog,
        IReadOnlyList<uint> recent,
        System.Action<string, IReadOnlyList<uint>, bool, string, float> drawGrid,
        bool inSession)
    {
        // Search + dynamic action button on one row. Fixed button width keeps the row stable across the label swap.
        ImGui.SetNextItemWidth(-(ActionBtnW + 8f));
        ImGui.InputTextWithHint("##search" + idTag, searchHint, ref searchBuf, 64);
        ImGui.SameLine();
        ActionButton(idTag, active, onLabel, offLabel, searchBuf, cmd, offArg);
        ImGui.Spacing();

        var q = searchBuf.Trim();
        bool numeric = ushort.TryParse(q, out var qid);

        if (q.Length == 0)
        {
            // Browsing: Recent pinned on top (short), then All beneath - one continuous scroll.
            if (recent.Count > 0)
            {
                ImGui.TextDisabled("最近");
                drawGrid("##recent" + idTag, recent, inSession, "r" + idTag, 0f);
                ImGui.Dummy(new Vector2(0f, 2f));
                ImGui.TextDisabled("全部");
            }
            var all = new List<uint>();
            foreach (var e in catalog) all.Add(e.id);
            drawGrid("##all" + idTag, all, inSession, "a" + idTag, 0f);
        }
        else
        {
            // Searching: hunting, not browsing - drop the Recent header, just show filtered matches.
            var filtered = new List<uint>();
            foreach (var e in catalog)
            {
                bool match = numeric ? e.id == qid : e.name.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (match) filtered.Add(e.id);
            }
            if (filtered.Count == 0) ImGui.TextDisabled("没有匹配项。");
            else drawGrid("##filt" + idTag, filtered, inSession, "f" + idTag, 0f);
        }
    }

    private void DrawEmotesBody()
    {
        BuildEmoteCatalog();
        bool inSession = relay.IsSessionActive;
        DrawCollectibleBody(
            "E", "名称或 ID…", ref emoteSearch,
            EmotePlaying?.Invoke() ?? false, "播放", "停止", "emote", "stop",
            emoteCatalog!.Select(e => (e.id, e.name)),
            HMSyncConfig.BuildPinnedRecent(config.FavouriteEmotes, config.RecentEmotes),
            (id, ids, s, tag, h) => DrawEmoteGrid(id, ids, s, tag, h),
            inSession);
    }

    // S322: two-column emote grid (icon + name + id per cell), replicating the in-game menu layout. Used by the
    // full catalogue AND the Favourites / Recently-played sections, and the row template for the mount list.
    // height 0 = fill remaining space; >0 = fixed height with its own scroll. idTag keeps ImGui ids unique
    // across the three grids (the same emote can appear in all of them).
    private void DrawEmoteGrid(string tableId, IReadOnlyList<uint> ids, bool inSession, string idTag, float height)
    {
        // height 0 = grow naturally and let the parent (the quadrant child) scroll - NO ScrollY, or we'd nest scrolls.
        // height >0 = a self-contained scroll region (legacy callers).
        var flags = ImGuiTableFlags.RowBg | (height > 0 ? ImGuiTableFlags.ScrollY : ImGuiTableFlags.None);
        if (!ImGui.BeginTable(tableId, 2, flags, new Vector2(0, height)))
            return;

        SuspendWrap();   // v0.7.432
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        int shown = 0;
        foreach (var id in ids)
        {
            if (shown % 2 == 0) ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawEmoteCell((ushort)id, inSession, idTag);
            shown++;
        }

        ResumeWrap();
        ImGui.EndTable();
    }

    // S322: one emote as a grid cell - [star] [icon] Name #id. The star toggles favourite; the name plays on
    // click. A locked emote out of session is greyed + inert (forcing it would only fake a local unlock; in a
    // session it syncs to peers). idTag disambiguates the ImGui ids between the favourites/recents/full grids.
    private void DrawEmoteCell(ushort id, bool inSession, string idTag)
    {
        if (emoteById == null || !emoteById.TryGetValue(id, out var info))
            return;

        DrawFavStar(id, idTag);
        ImGui.SameLine(0, 3);
        if (info.icon > 0)
        {
            DrawEmoteIcon(info.icon, 18f);
            ImGui.SameLine(0, 4);
        }

        bool unlocked = CanUseEmote?.Invoke(id) ?? true;
        ImGui.AlignTextToFramePadding();
        if (inSession || unlocked)
        {
            if (ImGui.Selectable(info.name + "  #" + id + "##cell" + idTag + id))
                RunCommand?.Invoke("emote", id.ToString());
        }
        else
        {
            ImGui.TextDisabled(info.name + "  #" + id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("不在会话中");
        }
    }

    // S322: clickable star toggling an emote's favourite state (gold = favourited, hollow = not). Fixed width
    // so it sits inline before the icon/name. idTag keeps the id unique across the favourites/recents/full grids.
    private void DrawFavStar(ushort id, string idTag)
    {
        bool fav = config.FavouriteEmotes.Contains(id);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text,
            fav ? new Vector4(1f, 0.82f, 0.2f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f));
        if (ImGui.Selectable((fav ? "\u2605" : "\u2606") + "##fav" + idTag + id, false, ImGuiSelectableFlags.None, new Vector2(16, 0)))
            config.ToggleFavouriteEmote(id);
        ImGui.PopStyleColor();
    }

    // S322: draw an emote's game icon at a square size (no-op if missing).
    private void DrawEmoteIcon(uint icon, float size)
    {
        if (icon > 0
            && textureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var sharedTex)
            && sharedTex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(size, size));
    }

    // S322: a thin full-width handle drawn directly under a fixed-height grid; drag it to resize that grid.
    // The height is mutated live while dragging (get/set close over the section's config field) and persisted
    // once on release (onRelease). Clamped to a sane band. Reused by both quick grids on both tabs.
    private void GridResizeHandle(string id, Func<float> get, System.Action<float> set, System.Action onRelease)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.32f, 0.32f, 0.36f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.45f, 0.45f, 0.52f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.55f, 0.55f, 0.62f, 1f));
        ImGui.Button(id, new Vector2(-1f, 4f));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (ImGui.IsItemActive())
            set(Math.Clamp(get() + ImGui.GetIO().MouseDelta.Y, 40f, 400f));
        if (ImGui.IsItemDeactivated())
            onRelease();
    }

    private void BuildEmoteCatalog()
    {
        if (emoteCatalog != null) return;
        emoteCatalog = new List<(ushort, string, uint, bool)>();
        emoteById = new Dictionary<ushort, (string, uint, bool)>();

        var sheet = dataManager.GetExcelSheet<Emote>();
        if (sheet == null) return;

        // v0.7.389 - exclude POSTURE-EXIT TRANSITIONS from the clickable list.
        // EmoteMode rows pair a posture with its exit: {StartEmote 50 Sit, EndEmote 51 Stand Up} and
        // {StartEmote 52 Sit on Ground, EndEmote 53 Stand Up}. 51 and 53 are not standalone emotes -
        // they are the second half of a state-machine transition, they carry NO TextCommand (the game
        // never lets a player invoke them directly), and firing one from the grid initiates a hidden
        // stand-up whose FIRST FRAME is seated. The engine then finishes it and tries to return to a
        // "default" that is now corrupted: the character re-seats itself and slides on movement.
        //
        // That is engine behaviour rather than an HMS bug - nobody stands up via an emote in normal
        // play, they use movement keys, jump or mouse - so the fix is to stop offering the button.
        // Still reachable via `/hms emote 51` for anyone deliberately probing.
        //
        // Derived, not hardcoded: any emote that appears as an EmoteMode.EndEmote and never as a
        // StartEmote. Across the whole sheet that is exactly {51, 53, 89} - 89 being a nameless row
        // already dropped by the name check below. Self-maintaining if a new posture pair is added.
        var exitTransitions = new HashSet<uint>();
        var starts = new HashSet<uint>();
        var modes = dataManager.GetExcelSheet<EmoteMode>();
        if (modes != null)
        {
            foreach (var m in modes) { if (m.StartEmote.RowId != 0) starts.Add(m.StartEmote.RowId); }
            foreach (var m in modes)
            {
                var end = m.EndEmote.RowId;
                if (end != 0 && !starts.Contains(end)) exitTransitions.Add(end);
            }
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            if (exitTransitions.Contains(row.RowId)) continue;   // posture-exit half - see above
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Skip placeholder rows with no animation timeline.
            if (row.ActionTimeline[0].RowId == 0) continue;

            bool looped = (row.EmoteMode.ValueNullable?.ConditionMode ?? 0) != 0;
            emoteCatalog.Add(((ushort)row.RowId, name, (uint)row.Icon, looped));
            emoteById[(ushort)row.RowId] = (name, (uint)row.Icon, looped);
        }
    }

    // S322: Minions tab - same interface as Emotes (favourites + recently-summoned grids over the full
    // Companion catalogue). Summoning routes through /hms minion, syncs to peers via the MinionId wire field,
    // and the host's already-summoned minion replicates on join through the same capture path. Locked minions
    // are greyed + inert out of session; summonable + synced inside one. (Shares the row shape with the Emotes
    // tab - both fold into one shared picker at the character-management consolidation.)
    private void DrawMinionsBody()
    {
        BuildMinionCatalog();
        bool inSession = relay.IsSessionActive;
        DrawCollectibleBody(
            "M", "名称或 ID…", ref minionSearch,
            MinionOut?.Invoke() ?? false, "召唤", "收回", "minion", "0",
            minionCatalog!.Select(e => (e.id, e.name)),
            HMSyncConfig.BuildPinnedRecent(config.FavouriteMinions, config.RecentMinions),
            (id, ids, s, tag, h) => DrawMinionGrid(id, ids, s, tag, h),
            inSession);
    }

    // S322: two-column minion grid - mirror of DrawEmoteGrid. Folds into the shared picker at consolidation.
    private void DrawMinionGrid(string tableId, IReadOnlyList<uint> ids, bool inSession, string idTag, float height)
    {
        var flags = ImGuiTableFlags.RowBg | (height > 0 ? ImGuiTableFlags.ScrollY : ImGuiTableFlags.None);
        if (!ImGui.BeginTable(tableId, 2, flags, new Vector2(0, height)))
            return;

        SuspendWrap();   // v0.7.432
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        int shown = 0;
        foreach (var id in ids)
        {
            if (shown % 2 == 0) ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawMinionCell((ushort)id, inSession, idTag);
            shown++;
        }

        ResumeWrap();
        ImGui.EndTable();
    }

    // S322: one minion as a grid cell - [star] [icon] Name #id. Mirror of DrawEmoteCell, against the minion
    // favourites + the minion unlock hook + the /hms minion verb. Reuses DrawEmoteIcon (generic icon draw).
    private void DrawMinionCell(ushort id, bool inSession, string idTag)
    {
        if (minionById == null || !minionById.TryGetValue(id, out var info))
            return;

        bool fav = config.FavouriteMinions.Contains(id);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text,
            fav ? new Vector4(1f, 0.82f, 0.2f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f));
        if (ImGui.Selectable((fav ? "\u2605" : "\u2606") + "##fav" + idTag + id, false, ImGuiSelectableFlags.None, new Vector2(16, 0)))
            config.ToggleFavouriteMinion(id);
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 3);
        if (info.icon > 0)
        {
            DrawEmoteIcon(info.icon, 18f);
            ImGui.SameLine(0, 4);
        }

        bool unlocked = CanUseMinion?.Invoke(id) ?? true;
        ImGui.AlignTextToFramePadding();
        if (inSession || unlocked)
        {
            if (ImGui.Selectable(info.name + "  #" + id + "##cell" + idTag + id))
                RunCommand?.Invoke("minion", id.ToString());
        }
        else
        {
            ImGui.TextDisabled(info.name + "  #" + id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("不在会话中");
        }
    }

    private void BuildMinionCatalog()
    {
        if (minionCatalog != null) return;
        minionCatalog = new List<(ushort, string, uint, bool)>();
        minionById = new Dictionary<ushort, (string, uint)>();

        var sheet = dataManager.GetExcelSheet<Companion>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            var name = row.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Minion icons live in the 64000+ range (HaselDebug ref: 64000 + Companion.Icon).
            uint icon = 64000u + (uint)row.Icon;
            minionCatalog.Add(((ushort)row.RowId, name, icon, false));
            minionById[(ushort)row.RowId] = (name, icon);
        }
    }

    // S322k: ornament catalogue for the Accessories tab - id + name from the Ornament sheet. Minimal filter
    // (valid id + non-empty name) so nothing real is hidden; the whole point is finding ids Hasel doesn't show.
    private void BuildOrnamentCatalog()
    {
        if (ornamentCatalog != null) return;
        ornamentCatalog = new List<(ushort, string, uint)>();
        ornamentById = new Dictionary<ushort, (string, uint)>();

        var sheet = dataManager.GetExcelSheet<Ornament>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            var name = row.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            uint icon = (uint)row.Icon;   // Ornament sheet stores the full icon id (unlike Companion's 64000+offset)
            ornamentCatalog.Add(((ushort)row.RowId, name, icon));
            ornamentById[(ushort)row.RowId] = (name, icon);
        }
    }

    // S322k: Accessories (fashion accessory / ornament) tab - same layout as Emotes/Minions: favourites +
    // history grids over the full catalogue, two columns, icons, star-to-bookmark. Hasel doesn't expose ornament
    // ids, so this doubles as the id reference. Equipping routes through /hms accessory and syncs via OrnamentId.
    private void DrawAccessoriesBody()
    {
        BuildOrnamentCatalog();
        bool inSession = relay.IsSessionActive;
        DrawCollectibleBody(
            "O", "名称或 ID…", ref ornamentSearch,
            OrnamentOut?.Invoke() ?? false, "装备", "卸下", "accessory", "0",
            ornamentCatalog!.Select(e => (e.id, e.name)),
            HMSyncConfig.BuildPinnedRecent(config.FavouriteOrnaments, config.RecentOrnaments),
            (id, ids, s, tag, h) => DrawOrnamentGrid(id, ids, s, tag, h),
            inSession);
    }

    // S322k: two-column ornament grid - mirror of DrawMinionGrid. Folds into the shared picker at consolidation.
    private void DrawOrnamentGrid(string tableId, IReadOnlyList<uint> ids, bool inSession, string idTag, float height)
    {
        var flags = ImGuiTableFlags.RowBg | (height > 0 ? ImGuiTableFlags.ScrollY : ImGuiTableFlags.None);
        if (!ImGui.BeginTable(tableId, 2, flags, new Vector2(0, height)))
            return;

        SuspendWrap();   // v0.7.432
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        int shown = 0;
        foreach (var id in ids)
        {
            if (shown % 2 == 0) ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawOrnamentCell((ushort)id, inSession, idTag);
            shown++;
        }

        ResumeWrap();
        ImGui.EndTable();
    }

    // S322k: one ornament as a grid cell - [star] [icon] Name #id. Mirror of DrawMinionCell, against the ornament
    // favourites + the ornament unlock hook + the /hms accessory verb. Reuses DrawEmoteIcon (generic icon draw).
    private void DrawOrnamentCell(ushort id, bool inSession, string idTag)
    {
        if (ornamentById == null || !ornamentById.TryGetValue(id, out var info))
            return;

        bool fav = config.FavouriteOrnaments.Contains(id);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text,
            fav ? new Vector4(1f, 0.82f, 0.2f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f));
        if (ImGui.Selectable((fav ? "\u2605" : "\u2606") + "##fav" + idTag + id, false, ImGuiSelectableFlags.None, new Vector2(16, 0)))
            config.ToggleFavouriteOrnament(id);
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 3);
        if (info.icon > 0)
        {
            DrawEmoteIcon(info.icon, 18f);
            ImGui.SameLine(0, 4);
        }

        bool unlocked = CanUseOrnament?.Invoke(id) ?? true;
        ImGui.AlignTextToFramePadding();
        if (inSession || unlocked)
        {
            if (ImGui.Selectable(info.name + "  #" + id + "##cell" + idTag + id))
                RunCommand?.Invoke("accessory", id.ToString());
        }
        else
        {
            ImGui.TextDisabled(info.name + "  #" + id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("不在会话中");
        }
    }

    // S323c: mount catalogue for the Mounts tab - id + name + icon from the Mount sheet. Minimal filter
    // (valid id + non-empty name) so nothing real is hidden; mirrors BuildMinionCatalog / BuildOrnamentCatalog.
    private void BuildMountCatalog()
    {
        if (mountCatalog != null) return;
        mountCatalog = new List<(ushort, string, uint)>();
        mountById = new Dictionary<ushort, (string, uint)>();

        var sheet = dataManager.GetExcelSheet<Mount>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            var name = row.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            uint icon = (uint)row.Icon;   // Mount sheet stores the full icon id (like Ornament; unlike Companion)
            mountCatalog.Add(((ushort)row.RowId, name, icon));
            mountById[(ushort)row.RowId] = (name, icon);
        }
    }

    // S323c: Mounts tab - same layout as Emotes/Minions/Accessories: favourites + history grids over the full
    // catalogue, two columns, icons, star-to-bookmark. Mounting routes through /hms mount and syncs via MountId;
    // the front-page mount line stays as the quick id entry. Folds into the shared picker at consolidation.
    private void DrawMountsBody()
    {
        BuildMountCatalog();
        bool inSession = relay.IsSessionActive;
        DrawCollectibleBody(
            "T", "名称或 ID…", ref mountSearch,
            MountOut?.Invoke() ?? false, "骑乘", "下坐骑", "mount", "0",
            mountCatalog!.Select(e => (e.id, e.name)),
            HMSyncConfig.BuildPinnedRecent(config.FavouriteMounts, config.RecentMounts),
            (id, ids, s, tag, h) => DrawMountGrid(id, ids, s, tag, h),
            inSession);
    }

    // S323c: two-column mount grid - mirror of DrawMinionGrid. Folds into the shared picker at consolidation.
    private void DrawMountGrid(string tableId, IReadOnlyList<uint> ids, bool inSession, string idTag, float height)
    {
        var flags = ImGuiTableFlags.RowBg | (height > 0 ? ImGuiTableFlags.ScrollY : ImGuiTableFlags.None);
        if (!ImGui.BeginTable(tableId, 2, flags, new Vector2(0, height)))
            return;

        SuspendWrap();   // v0.7.432
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        int shown = 0;
        foreach (var id in ids)
        {
            if (shown % 2 == 0) ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawMountCell((ushort)id, inSession, idTag);
            shown++;
        }

        ResumeWrap();
        ImGui.EndTable();
    }

    // S323c: one mount as a grid cell - [star] [icon] Name #id. Mirror of DrawMinionCell, against the mount
    // favourites + the mount unlock hook + the /hms mount verb. Reuses DrawEmoteIcon (generic icon draw).
    private void DrawMountCell(ushort id, bool inSession, string idTag)
    {
        if (mountById == null || !mountById.TryGetValue(id, out var info))
            return;

        bool fav = config.FavouriteMounts.Contains(id);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text,
            fav ? new Vector4(1f, 0.82f, 0.2f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f));
        if (ImGui.Selectable((fav ? "\u2605" : "\u2606") + "##fav" + idTag + id, false, ImGuiSelectableFlags.None, new Vector2(16, 0)))
            config.ToggleFavouriteMount(id);
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 3);
        if (info.icon > 0)
        {
            DrawEmoteIcon(info.icon, 18f);
            ImGui.SameLine(0, 4);
        }

        bool unlocked = CanUseMount?.Invoke(id) ?? true;
        ImGui.AlignTextToFramePadding();
        if (inSession || unlocked)
        {
            if (ImGui.Selectable(info.name + "  #" + id + "##cell" + idTag + id))
                RunCommand?.Invoke("mount", id.ToString());
        }
        else
        {
            ImGui.TextDisabled(info.name + "  #" + id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("不在会话中");
        }
    }

    // Tab 3: Carpet - the ported HCollider ground-carpet (walk on surfaces with no collision mesh).
    // Toggle + the baked-in flat preset, then finer controls. Sliders bind live to the service so
    // adjustments take effect on the next dropped patch. (Placeholder home: this will likely fold into
    // a wider character-management section later.)
    private void DrawCarpetTab()
    {
        var carpetFlags = ImGuiTabItemFlags.None;
        if (focusCarpetTab) { carpetFlags = ImGuiTabItemFlags.SetSelected; focusCarpetTab = false; }
        if (!ImGui.BeginTabItem("地毯", carpetFlags))
            return;
        BeginTabBody("##carpetbody");

        var c = Carpet;
        if (c == null)
        {
            ImGui.TextDisabled("地毯功能不可用。");
            EndTabBody();
            ImGui.EndTabItem();
            return;
        }

        ImGui.TextWrapped("在没有碰撞的地方行走：界外屋顶、缝隙、虚空。" +
            "每帧在脚下铺一块平面碰撞地板，仅本地可见。");
        ImGui.Spacing();

        bool carpetBlocked = !(MovementResearchAllowed?.Invoke() ?? false);   // v0.7.445: same gate as fly/noclip
        bool on = c.On;

        // A consistent slider+number-box unit used by every tunable: [ slider ][ box ]. Box is wide enough for
        // signed decimals like -0.050. One helper keeps the rhythm identical across all rows.
        float NumBoxW = 68f;
        void SliderRow(string label, string help, System.Func<float> get, System.Action<float> set, float lo, float hi, float clampLo, float clampHi, string fmt)
        {
            ImGui.TextDisabled(label); ImGui.SameLine(); HelpMarker(help);
            float v = get();
            // Reserve PanelPad on the right so the number box doesn't clip the window edge (left already inset by BeginPanel).
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - NumBoxW - 6f - PanelPad);
            if (ImGui.SliderFloat("##sl" + label, ref v, lo, hi, fmt)) { set(v); carpetDirty = true; }
            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(NumBoxW);
            if (ImGui.InputFloat("##bx" + label, ref v, 0f, 0f, fmt)) { set(Math.Clamp(v, clampLo, clampHi)); carpetDirty = true; }
        }

        // master toggle: ON/OFF button on the LEFT, status text following. Accent-congruent; state shown on the button.
        BeginPanel(on ? "地毯 开" : "地毯 关");
        {
            if (carpetBlocked) ImGui.BeginDisabled();
            float bw = 60f;
            var tgl = on ? new Vector4(0.30f, 0.72f, 0.42f, 1f) : new Vector4(0.40f, 0.40f, 0.44f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, tgl);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(tgl, 1.12f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(tgl, 0.9f));
            if (ImGui.Button((on ? "开" : "关") + "##carpettoggle", new Vector2(bw, 0))) c.Toggle();
            ImGui.PopStyleColor(3);
            if (carpetBlocked) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(carpetBlocked ? "请先加载地图或过场场景" : (on ? "正在脚下铺设地板" : "点击启用"));
        }
        EndPanel();

        // Rings toggle + pop-out, grouped together on the left (no far-apart stray controls).
        bool showRings = c.ShowRings;
        if (ImGui.Checkbox("显示圆圈", ref showRings)) { c.ShowRings = showRings; carpetDirty = true; }
        ImGui.SameLine(); HelpMarker("在每块生成的地板处画圈（红 = 脚下，绿 = 前方）。熟悉后可以关掉。");
        ImGui.SameLine(0f, 16f);
        {
            string popLabel = showCarpetBar ? "关闭工具栏" : "弹出工具栏";
            if (ImGui.Button(popLabel)) showCarpetBar = !showCarpetBar;
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.66f, 0.74f, 1f));
        ImGui.TextWrapped("骑上坐骑飞到想要的高度，开启后下坐骑走路，地毯会在脚下延伸。");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        // Pitch: walking slope. Preset buttons in the SAME order as the bar (Downhill, Uphill, Flat, Drop here),
        // equal-width cells, active preset highlighted. Then the fine slider+box unit.
        BeginPanel("坡度：行走斜坡");
        {
            float g = 6f;
            // Reserve PanelPad on the right so the 4th button (Drop here) doesn't clip the panel edge.
            float pbw = (ImGui.GetContentRegionAvail().X - g * 3f - PanelPad) / 4f;
            bool isFlat = Math.Abs(c.Pitch - CarpetService.DefaultPitch) < 0.001f;
            bool isUp = Math.Abs(c.Pitch - CarpetService.UphillPitch) < 0.001f;
            bool isDown = Math.Abs(c.Pitch - CarpetService.DownhillPitch) < 0.001f;
            // Status-dot buttons (matching the tear-off bar): neutral button, an accent dot marks the active pitch -
            // no full-button colour fill.
            var accP = Accent();
            var dimP = new Vector4(0.42f, 0.44f, 0.48f, 1f);
            void PitchBtn(string label, bool active, bool showDot, string id, System.Action onClick)
            {
                var p0 = ImGui.GetCursorScreenPos();
                if (ImGui.Button((showDot ? "   " : "") + label + id, new Vector2(pbw, 0))) onClick();
                if (showDot)
                    ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(p0.X + 10f, p0.Y + ImGui.GetFrameHeight() * 0.5f), 3.5f,
                        ImGui.GetColorU32(active ? accP : dimP));
            }
            PitchBtn("下坡 \u25BC", isDown, true, "##pdown", () => c.SetPitchDownhill()); ImGui.SameLine(0f, g);
            PitchBtn("上坡 \u25B2", isUp, true, "##pup", () => c.SetPitchUphill()); ImGui.SameLine(0f, g);
            PitchBtn("平地", isFlat, true, "##pflat", () => c.SetPitchFlat()); ImGui.SameLine(0f, g);
            PitchBtn("在此放下", false, false, "##pdrop", () => c.AdjustActiveTileY());
            ImGui.Spacing();
            SliderRow("微调", "每块地板相对脚下的高度。-0.05 = 平地；正值上升，负值下降。在当前半径和步长下，±0.40 是能跨过的最陡台阶。",
                () => c.Pitch, x => c.Pitch = x, -1f, 1f, -50f, 50f, "%.3f");
        }
        EndPanel();

        // Drop offset: first patch only.
        BeginPanel("下沉偏移：仅第一块");
        {
            SliderRow("脚下高度", "第一块地板相对脚下的高度（-0.05 = 脚下）。设得远低于脚下，可以从高空电影般落地；之后的地板按坡度延续。",
                () => c.DropOffset, x => c.DropOffset = x, -50f, 5f, -1000f, 1000f, "%.3f");
            var a = Accent();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(a.X, a.Y, a.Z, 0.20f));
            if (ImGui.Button("重置到脚下", new Vector2(140f, 0f))) c.ResetDropOffset();
            ImGui.PopStyleColor();
        }
        EndPanel();

        // Finer controls: always visible (no collapse), same slider+box rhythm.
        BeginPanel("精细控制");
        {
            SliderRow("半径", "地板大小（米）。最小 1.0；太小时横移会走出边缘掉下去。",
                () => c.Radius, x => c.Radius = x, 1.0f, 10f, 1.0f, 100f, "%.2f");
            SliderRow("步长", "每移动这么远放一块新地板。最小 1.0。",
                () => c.Step, x => c.Step = x, 1.0f, 6f, 1.0f, 50f, "%.2f");
            // Trail is an int: slider+int box unit.
            ImGui.TextDisabled("轨迹"); ImGui.SameLine(); HelpMarker("身后最多保留的地板块数。");
            int trail = c.Trail;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - NumBoxW - 6f - PanelPad);
            if (ImGui.SliderInt("##sltrail", ref trail, 2, 12)) { c.Trail = trail; carpetDirty = true; }
            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(NumBoxW);
            if (ImGui.InputInt("##bxtrail", ref trail, 0, 0)) { c.Trail = Math.Clamp(trail, 1, 64); carpetDirty = true; }
            SliderRow("基础前置", "静止时地板中心在脚前多远。",
                () => c.LeadBase, x => c.LeadBase = x, 0f, 5f, 0f, 50f, "%.2f");
            SliderRow("速度前置", "每单位速度额外前置的距离（快速移动时保证地板边缘在前方）。",
                () => c.LeadPerSpeed, x => c.LeadPerSpeed = x, 0f, 2f, 0f, 10f, "%.2f");
            ImGui.Spacing();
            var ra = Accent();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(ra.X, ra.Y, ra.Z, 0.20f));
            if (ImGui.Button("恢复默认", new Vector2(160f, 0f))) c.ResetToDefaults();
            ImGui.PopStyleColor();
        }
        EndPanel();

        // Persist once the user finishes interacting (not every frame mid-drag).
        if (carpetDirty && !ImGui.IsAnyItemActive())
        {
            c.SaveTunables();
            carpetDirty = false;
        }

        EndTabBody();
        ImGui.EndTabItem();
    }

    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    // S316: world-overlay ring renderer for the carpet, subscribed to UiBuilder.Draw SEPARATELY from
    // Draw() so it renders even when the main window is closed (people orient by the rings while using
    // the tool, before they're comfortable turning them off). Draws a translucent ring at each live patch
    // centre - the patch you're standing on (oldest, index 0) is red-ish, the lead patches green - by
    // sampling points around the circle and projecting via WorldToScreen onto the background draw list.
    private readonly List<Vector3> carpetRingBuf = new();
    private bool carpetDirty;   // S319: a carpet tunable changed this frame; flush to config on release
    // ── Dynamic Face Control ──────────────────────────────────────────────────────────────────────────────────
    // Brio-style per-slot gaze: Eyes / Body / Head, each a [toggle] + [set-to-camera] + [X/Y/Z]. Self-actor; the
    // state is broadcast per-actor (FaceControlState → capture → WARM → updateLookAt on peers). "Set to camera" fills
    // that slot's point from the live camera eye; coords can also be keyed manually. Rows are left-aligned per the
    // GUI standing rule. Shared body - rendered both in the Character tab section and the tear-off window.
    private static unsafe System.Numerics.Vector3 CameraEyeWorld()
    {
        var camMgr = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null) return default;
        var m = camMgr->Camera->SceneCamera.ViewMatrix;
        return new System.Numerics.Vector3(
            -(m.M11 * m.M41 + m.M12 * m.M42 + m.M13 * m.M43),
            -(m.M21 * m.M41 + m.M22 * m.M42 + m.M23 * m.M43),
            -(m.M31 * m.M41 + m.M32 * m.M42 + m.M33 * m.M43));
    }

    private void DrawFaceControlBody(bool compact)
    {
        if (!compact)
            ImGui.TextDisabled("按准星按钮让部位朝向你的视角，会广播给所有人。");

        // Aligned columns across the three rows: [label] [crosshair] [XYZ] [x]. Label column fixed width so the
        // crosshair/drag/x line up row-to-row (GUI standing rule). Crosshair = set-to-camera; x = clear that slot.
        float labelCol = 52f;
        void Row(string label, ref bool on, ref System.Numerics.Vector3 vec)
        {
            // Align the label text to the frame padding so its baseline lines up with the icon button + drag on the
            // same row (the standard ImGui idiom - no manual Y juggling). Fixed label column keeps rows aligned.
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(labelCol);
            if (ImGuiComponents.IconButton(label + "cam", FontAwesomeIcon.LocationCrosshairs))
            {
                vec = CameraEyeWorld();
                on = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("瞄准 " + label.ToLower() + " 朝向镜头");
            ImGui.SameLine();
            var v = vec;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 32);
            if (ImGui.DragFloat3("##facexyz" + label, ref v, 0.1f)) { vec = v; on = true; }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(label + "clr", FontAwesomeIcon.Times)) { on = false; vec = default; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("清除 " + label.ToLower());
        }

        bool e = FaceControlState.EyesOn; var ev = FaceControlState.Eyes;
        Row("眼睛", ref e, ref ev); FaceControlState.EyesOn = e; FaceControlState.Eyes = ev;
        bool b = FaceControlState.BodyOn; var bv = FaceControlState.Body;
        Row("身体", ref b, ref bv); FaceControlState.BodyOn = b; FaceControlState.Body = bv;
        bool h = FaceControlState.HeadOn; var hv = FaceControlState.Head;
        Row("头部", ref h, ref hv); FaceControlState.HeadOn = h; FaceControlState.Head = hv;

        // "Hold coords": lock the gaze to its world-point so it tracks through walking/pivoting (aim at an airship,
        // portrait, star, and keep looking at it as you move). Off = fire-and-forget (moving clears the gaze).
        bool locked = FaceControlState.Locked;
        if (ImGui.Checkbox("锁定坐标", ref locked)) FaceControlState.Locked = locked;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("移动时一直注视固定点（不自动取消）");

        // v0.7.465: the docked view's "Pop out" button is gone - the section header itself is the pop-out control
        // (PopOutHeader), so there's no second widget competing with it. `compact` still earns its keep above: it
        // suppresses the one-line help text in the tear-off, where the window title already supplies the context.
    }

    // v0.7.465: the tear-off Movement and Appearance windows. Same chrome as the Face Control tear-off (accented title
    // bar, first-use width, close via the window's own X) so the three read as one family. Each renders the SAME body
    // method the docked strip calls - one implementation, two mounts, no chance of the pair drifting.
    public void DrawMovementBar()
    {
        if (!showMoveBar) return;
        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(300, 0), ImGuiCond.FirstUseEver);
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("移动##hmsmovebar", ref showMoveBar, flags))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }
        DrawMovementBody();
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    public void DrawAppearanceBar()
    {
        if (!showAppearanceBar) return;
        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(300, 0), ImGuiCond.FirstUseEver);
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("外观##hmsappearancebar", ref showAppearanceBar, flags))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }
        DrawAppearanceBody();
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    // The tear-off Face Control window. Separate floating window; renders only when showFaceBar.
    public void DrawFaceControlBar()
    {
        if (!showFaceBar) return;
        // Accent the title bar so the tear-off follows the accent config, like the main window.
        var acc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(acc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(acc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("面部控制##hmsface", ref showFaceBar))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }
        DrawFaceControlBody(true);
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    // v0.7.252: the tear-off carpet control bar. Separate floating window; renders only when showCarpetBar. The five
    // controls from the design: Carpet toggle · Downhill · Uphill · Rings · Settings (opens the main window's Carpet tab).
    public void DrawCarpetBar()
    {
        if (!showCarpetBar) return;
        var c = Carpet;
        if (c == null) return;

        // Accent the title bar so the tear-off follows the accent config, like the main window.
        var barAcc = Accent();
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Darken(barAcc, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Darken(barAcc, 0.55f));
        ImGui.SetNextWindowSize(new Vector2(330, 0), ImGuiCond.FirstUseEver);
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("地毯##hmsbar", ref showCarpetBar, flags))
        {
            ImGui.End();
            ImGui.PopStyleColor(2);
            return;
        }

        bool carpetBlocked = !(MovementResearchAllowed?.Invoke() ?? false);   // v0.7.445: same gate as fly/noclip
        bool on = c.On;

        // Accent-congruent with the main GUI. Active state is shown by a small STATUS-LIGHT dot in the label, NOT a
        // full-button colour fill (which read too rich). Buttons keep standard styling; the dot carries state.
        float gap = 6f;
        float colW = (ImGui.GetContentRegionAvail().X - gap * 2f) / 3f;
        var accent = Accent();
        var dimDot = new Vector4(0.42f, 0.44f, 0.48f, 1f);
        var onDot = new Vector4(0.35f, 0.85f, 0.45f, 1f);

        // A button with a leading status dot: [● label]. dotOn picks the lit colour; when off, a dim grey dot.
        bool DotButton(string label, string id, bool lit, Vector4 litColor, float width, bool disabled)
        {
            if (disabled) ImGui.BeginDisabled();
            var p0 = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Button("   " + label + id, new Vector2(width, 0));   // leading space reserves room for the dot
            var dl = ImGui.GetWindowDrawList();
            float r = 3.5f;
            var dc = new Vector2(p0.X + 10f, p0.Y + ImGui.GetFrameHeight() * 0.5f);
            dl.AddCircleFilled(dc, r, ImGui.GetColorU32(lit ? litColor : dimDot));
            if (disabled) ImGui.EndDisabled();
            return clicked;
        }

        // Row 1: Carpet toggle · Rings · Settings.
        if (DotButton(on ? "地毯" : "地毯", "##bartog", on, onDot, colW, carpetBlocked)) c.Toggle();
        ImGui.SameLine(0f, gap);
        if (DotButton("圆圈", "##barrings", c.ShowRings, onDot, colW, false)) { c.ShowRings = !c.ShowRings; carpetDirty = true; }
        ImGui.SameLine(0f, gap);
        if (ImGui.Button("设置##barset", new Vector2(colW, 0))) { showMain = true; focusCarpetTab = true; }

        // Row 2: Downhill · Uphill · Flat (sequence matches the tab; active pitch shows an accent dot).
        bool isFlat = Math.Abs(c.Pitch - CarpetService.DefaultPitch) < 0.001f;
        bool isUp = Math.Abs(c.Pitch - CarpetService.UphillPitch) < 0.001f;
        bool isDown = Math.Abs(c.Pitch - CarpetService.DownhillPitch) < 0.001f;
        if (DotButton("下坡 \u25BC", "##bardown", isDown, accent, colW, carpetBlocked)) c.SetPitchDownhill();
        ImGui.SameLine(0f, gap);
        if (DotButton("上坡 \u25B2", "##barup", isUp, accent, colW, carpetBlocked)) c.SetPitchUphill();
        ImGui.SameLine(0f, gap);
        if (DotButton("平地", "##barflat", isFlat, accent, colW, carpetBlocked)) c.SetPitchFlat();

        if (carpetBlocked)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("请先加载地图或过场场景");
        }

        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    // S316: in-world orientation rings for the carpet patches (renders even when the main window is closed).
    public void DrawCarpetOverlay()
    {
        var c = Carpet;
        if (c == null || !c.On || !c.ShowRings) return;

        int n = c.SnapshotCenters(carpetRingBuf);
        if (n == 0) return;

        float radius = c.Radius;
        var dl = ImGui.GetBackgroundDrawList();
        for (int i = 0; i < n; i++)
        {
            var center = carpetRingBuf[i];
            uint ringCol = i == 0 ? 0xFF4040FFu : 0xFF40FF40u; // oldest (standing-on) red-ish, leads green
            Vector2 prev = default;
            bool havePrev = false;
            for (int a = 0; a <= 24; a++)
            {
                float ang = a / 24f * MathF.Tau;
                var wp = new Vector3(center.X + MathF.Cos(ang) * radius, center.Y, center.Z + MathF.Sin(ang) * radius);
                if (gameGui.WorldToScreen(wp, out var sp))
                {
                    if (havePrev) dl.AddLine(prev, sp, ringCol, 1.5f);
                    prev = sp;
                    havePrev = true;
                }
                else havePrev = false;
            }
        }
    }
}
