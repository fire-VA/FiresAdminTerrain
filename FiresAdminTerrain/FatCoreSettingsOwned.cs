using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;


namespace VerdantsAscent
{
    /// <summary>
    /// 0.2.1294 (Fire 10-30: "The only configs that should be in core are legitimate core functions"; plan
    /// Tools\CORE_CONFIG_MOVE_PLAN_20261030.md, FAT block + snow): the settings for Core's shared movement and snow code that FAT owns.
    /// Each is bound HERE under Core's own section + key, handed to Core with CoreSettings.Provide (Core's code reads it; without FAT,
    /// Core uses its current default), and on the first load its value is copied once from Core's cfg so nobody's setting resets.
    /// Snow: FAT owns all 10 [Snow] keys ([seasons] agreed: FiresSnowmen binds its own copies only when FAT is absent).
    /// Same shape as RPGClasses' CoreSettingsOwned ([wishbone]); on a Core older than 0.2.1036 it logs once and Core keeps its keys.
    /// </summary>
    internal static class FatCoreSettingsOwned
    {
        private const string Owner = "FiresAdminTerrain";

        internal static void Bind(ConfigFile config)
        {
            try
            {
                // 0.2.1335: the copy from Core's cfg runs ONCE per install (marker file, as OceanKit's import did)
                string marker = Path.Combine(BepInEx.Paths.ConfigPath, CopyMarker);
                s_copyFromCore = !File.Exists(marker);
                BindAll(config);
                if (s_copyFromCore) File.WriteAllText(marker, $"{s_copied} value(s) copied from Core's cfg\n");
                // 0.2.1335 (Fire 11-02, Tools\CONFIG_PROPOSAL_20261102.md): one Info proof line
                FiresAdminTerrain.Log?.LogInfo($"[FatCoreSettings] FAT owns {s_counts.Values.Sum()} Core keys (Snow {Count("Snow")}, Undo {Count("Undo")}, Loot {Count("Loot")}), {s_copied} value(s) copied from Core's cfg this load; Heightmap is Core's, Ocean + movement are APM's (0.2.1335)");
            }
            catch (Exception e) when (e is MissingMethodException || e is TypeLoadException || e is MissingMemberException)
            {
                FiresAdminTerrain.Log?.LogInfo("[CoreSettings] the loaded Core has no CoreSettings (older than 0.2.1036): Core keeps its own snow keys");
            }
        }

        private static void BindAll(ConfigFile config)
        {
            // 0.2.1296 ([wishbone]; Fire's FiresAdvancedPlayerMovement 0.1.0 owns the 8 movement keys now - crawl x3, low ceilings, first
            // person when crawling, camera collision, swimming x2 - and copied their values from FAT's cfg on its first load): FAT no
            // longer binds or provides them. The snow keys stay here.
            // ---- snow (Core's SnowConfig / SnowGround / SnowCamera / CreatureSnowTrail); defaults = Core's current ones ----
            Own(config, "Snow", "Dynamic snow", false, new ConfigDescription(
                "Snow builds up and melts with the seasons (Vedr) and the weather in the Mountains, the Deep North and Winter territories. Fires and dug paths clear it; paths fill back in, faster in a blizzard. Every player computes the same snow, so it costs no network traffic."));
            Own(config, "Snow", "Match snow at zone edges", true, new ConfigDescription(
                "The snow on the edge where two zones meet is always the same on both sides (a trail or path touching a zone edge left a gap there). Turn off only to compare."));
            Own(config, "Snow", "Ground snow fill rate", 1f, new ConfigDescription(
                "How fast the ground snow deepens while it snows (and settles in clear weather). 1 = natural (a snowstorm fills it in about 5 minutes of game time, light snow about 10). Raise it to watch the snow pile up. Changes apply live.",
                new AcceptableValueRange<float>(1f, 100f)));
            Own(config, "Snow", "Max depth Mountain (m)", 0.8f, new ConfigDescription(
                "Mountain ground builds up snow by the same seasons and weather as the Deep North, up to this depth in metres (the Deep North reaches 1.2 m packed, 1.9 m in drifts). Visual and trails only: no Deep North sinking or slowdown on Mountain ground. 0 = no Mountain buildup.",
                new AcceptableValueRange<float>(0f, 1.9f)));
            Own(config, "Snow", "Winter snow depth outside the snow biomes (m)", 0.7f, new ConfigDescription(
                "In winter (Vedr), ground outside the Mountains and the Deep North gets a layer of snow this deep at full winter cover, on the voxel terrain: drawn, shovelled, walked through. Fires melt it; closed rooms stay clear. Deeper than 0.3 m slows you a little, like the game's own deep snow. 0 = no winter snow depth.",
                new AcceptableValueRange<float>(0f, 1f)));
            // Core's default false is turned ON by Core whenever FAT is installed (OwnerGatedConfig.AutoOnWith SNOW): with FAT owning it, true
            Own(config, "Snow", "No snow in closed rooms", true, new ConfigDescription(
                "No snow builds up inside a closed room; under a roof with an opening (a doorway, an open wall) snow drifts in near the opening and fades further inside. Uses the game's own shelter check, only near buildings."));
            Own(config, "Snow", "Snow clears paved ground and meets buildings", true, new ConfigDescription(
                "Paved ground (roads, paved stone) has no snow, and the snow eases down to nothing right next to and under your buildings, instead of standing as a slab with a gap beside them. Turn off only to compare."));
            Own(config, "Snow", "Cache snow ground rule per half metre", true, new ConfigDescription(
                "Remembers the paved / building snow answer for each half-metre spot for a few seconds instead of working it out again for every snow sample (a performance fix; same snow). Turn off only to compare."));
            Own(config, "Snow", "Camera ignores deep snow", true,   // 0.2.1311 (Fire: a fix ships ON; [wishbone] Core 1063 flips its own default too)
                new ConfigDescription(
                "Deep snow never pulls the camera into first person (vanilla starts its camera collision rays below the eye by the snow depth, so in deep snow they start inside the ground). Walls and hills still block the camera; your zoom is kept."));
            Own(config, "Snow", "Creature snow trails", false, new ConfigDescription(
                "Animals and monsters walking through deep Deep North snow carve a path as wide as they are (at least 0.9 m), like the player's trail. The paths fill back in with snowfall."));
            // 0.2.1328 (Fire 11-01: "if it has to do with caves or fucking terrain it goes to fat"; Tools\CORE_CONFIG_INVENTORY_20261101.md):
            // the one bound Core [Snow] key FAT did not own yet (Core 0.2.1085 SnowConfig). Core's 12th [Snow] key, "Winter snow depth (m)",
            // is an orphan Core no longer binds or reads (replaced 0.2.1016), so it is not bound here.
            Own(config, "Snow", "Snow in the natural ground", true, new ConfigDescription(
                "Snow lies in the ground the way the Deep North's does: the season's snow is part of the natural terrain, footsteps and shovels leave trails in it like in the Deep North, and the trails fill back in (faster while it snows, fastest in a storm). Off = the older raised-snow method, only to compare."));
            Own(config, "Snow", "Snow in place on first zone build", true, new ConfigDescription(   // 0.2.1394: Core 0.2.1180 SnowConfig.FirstBuildSnow
                "SERVER. A zone's season snow is already there when the zone first builds, so bare ground does not show before the snow appears. Off = the old first build."));
            Own(config, "Snow", "Snow in place without a second terrain pass", true, new ConfigDescription(   // 0.2.1398: Core 0.2.1187 (mud-cost optimizer)
                "On: the first-build snow rules update only the ground paint they change. Off: the terrain chunk is generated a second time (older, slower). Ignored while Vedr draws the ground snow (Vedr then decides)."));
            Own(config, "Snow", "Snow base refresh skips unchanged chunks", true, new ConfigDescription(   // 0.2.1400: Core 0.2.1189
                "On: when the season snow is refreshed, terrain chunks whose snow did not change are left alone. Off: every chunk is refreshed again (older, slower; only to compare). Ignored while Vedr draws the ground snow (Vedr then decides)."));
            // ---- 0.2.1328: Undo (Core UndoHistory; FAT is its only user) ----
            Own(config, "Undo", "Undo key", KeyCode.Keypad7, new ConfigDescription(
                "Undo the last edit made with a Fires tool (and, with Server Devcommands, any edit in its shared history). Read only when Infinity Hammer isn't installed; it binds numpad 7 to the same undo then."));
            Own(config, "Undo", "Redo key", KeyCode.Keypad9, new ConfigDescription(
                "Redo the last undone edit. Read only when Infinity Hammer isn't installed; it binds numpad 9 to the same redo then."));
            // 0.2.1335: MineralSeeds is not taken here (FLO owns it, proposal 11-02)
            // ---- 0.2.1328: Loot / rich cave ore (Core CaveOreRegrow; server-synced there, so synced by FAT too: SyncServerKeys) ----
            s_caveOreDays = Own(config, "Loot", "Rich cave ore regrow days", 6f, new ConfigDescription(
                "SERVER. In-game days before a mined-out ore deposit in a mineral-rich FAT cave zone grows back (only while no player is within "
                + "40 m). Needs FiresAdminTerrain (it places the cave ore and decides which caves are rich).", new AcceptableValueRange<float>(0.1f, 100f)));
            // ---- 0.2.1329 (Fire 11-01 via [generator]: ALL of [Loot] goes to FAT, "since fat handles world generation"): Core
            // LocationLootRefresh's three keys, same section / key / default / description as Core 0.2.1085, server-synced there and here ----
            s_lootKeys.Add(Own(config, "Loot", "Location loot refresh", false, new ConfigDescription(
                "SERVER. Chests and one-shot pickables that the world placed in locations, dungeons and caves come back after 'Refresh after days': "
                + "a partly looted chest is refilled with a fresh roll, an emptied one removes itself and respawns, a picked item reappears, a broken mud pile or location spawner (crypt draugr piles and the like) is rebuilt. "
                + "Never player-built chests, tombstones, carts, ships or planted crops.")));
            s_lootKeys.Add(Own(config, "Loot", "Refresh after days", 7, new ConfigDescription("SERVER. In-game days before looted location loot comes back.")));
            s_lootKeys.Add(Own(config, "Loot", "No refresh within metres", 40f, new ConfigDescription("SERVER. Nothing is refilled or respawned while a player is this close.")));
            // 0.2.1395 ([wishbone], Core 1182 reads these; Core without them still works).
            Own(config, "Performance", "Cache cooking slot keys", true, new ConfigDescription("Cooking stations reuse their slot key names instead of building them again each check."));
            Own(config, "Performance", "Station range checks without square root", true, new ConfigDescription("Crafting station range checks compare squared distances, which is cheaper and gives the same result."));
            Own(config, "Performance", "One VisEquipment verdict per frame", true, new ConfigDescription("The equipment visual check runs once per frame instead of each time it is asked."));
        }
        private static readonly List<ConfigEntryBase> s_lootKeys = new List<ConfigEntryBase>();
        private static readonly Dictionary<string, ConfigEntryBase> s_ownedEntries = new Dictionary<string, ConfigEntryBase>(StringComparer.Ordinal);   // 0.2.1337
        private static readonly string[] s_syncedSnow = { "Dynamic snow", "Snow in the natural ground", "Ground snow fill rate", "Max depth Mountain (m)", "Winter snow depth outside the snow biomes (m)", "No snow in closed rooms", "Creature snow trails" };
        private const string CopyMarker = "com.Fire.FiresAdminTerrain.coresettings-copied";   // 0.2.1335
        private static bool s_copyFromCore;
        private static int s_copied;

        // 0.2.1328: per-section counts for the Info line; the server-synced key (Core synced it with its own ConfigSync)
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int Count(string section) => s_counts.TryGetValue(section, out int n) ? n : 0;
        private static ConfigEntry<float> s_caveOreDays;

        /// <summary>0.2.1328: adds the server-decided owned key to FAT's ConfigSync (called once FAT's configSync exists).</summary>
        internal static void SyncServerKeys(FiresCore.Sync.ConfigSync configSync)
        {
            // 0.2.1337 ([wishbone]: the config move lost these): the 7 snow keys Core 0.2.1085 SnowConfig synced (:71-78), so a client with a different
            // cfg computes the server's snow field. Same keys, same admin behaviour as FAT's other synced keys. The Debug line proves server beats local.
            if (configSync != null)
            {
                foreach (string key in s_syncedSnow)
                {
                    if (!s_ownedEntries.TryGetValue("Snow/" + key, out var entry)) { FiresAdminTerrain.Log?.LogWarning($"[FatCoreSettings] synced snow key {key} is not bound"); continue; }
                    if (entry is ConfigEntry<bool> b) configSync.AddConfigEntry(b);
                    else if (entry is ConfigEntry<float> f) configSync.AddConfigEntry(f);
                    object local = entry.BoxedValue; string name = key;
                    entry.ConfigFile.SettingChanged += (s, a) =>
                    {
                        if (a.ChangedSetting != entry || Equals(entry.BoxedValue, local)) return;
                        FiresAdminTerrain.Log?.LogDebug($"[FatCoreSettings] synced Snow/{name}: now {entry.BoxedValue}, this peer's cfg said {local} (server value wins when connected; 0.2.1337)");
                    };
                }
            }
            if (configSync != null && s_caveOreDays != null) configSync.AddConfigEntry(s_caveOreDays);
            if (configSync != null)   // 0.2.1329: the three location-loot keys (Core synced them too)
            {
                foreach (var e in s_lootKeys)
                {
                    if (e is ConfigEntry<bool> b) configSync.AddConfigEntry(b);
                    else if (e is ConfigEntry<int> i) configSync.AddConfigEntry(i);
                    else if (e is ConfigEntry<float> f) configSync.AddConfigEntry(f);
                }
            }
        }

        // 0.2.1338: the proof the keys can sync at all: a key that lost its sync tag is left out of the server's package without a word.
        internal static void LogSyncTags(FiresCore.Sync.ConfigSync configSync)
        {
            if (configSync == null) return;
            int all = configSync.allConfigs.Count, tagged = configSync.allConfigs.Count(c => FiresCore.Sync.ConfigSync.configData(c.BaseConfig) != null);
            if (tagged == all) FiresAdminTerrain.Log?.LogDebug($"[FatCoreSettings] {tagged} of {all} FAT synced keys carry their sync tag (0.2.1338)");
            else FiresAdminTerrain.Log?.LogWarning($"[FatCoreSettings] only {tagged} of {all} FAT synced keys carry their sync tag: the others never reach clients");
        }

        // 0.2.1346 (plan: 1338 took synced keys from 7 to 66, unmeasured): one Info line at join: every FAT synced key where the server's value beat this peer's cfg.
        private static void LogSyncedDiff(FiresCore.Sync.ConfigSync sync)
        {
            if (sync == null) return;
            var diff = new List<string>();
            foreach (var c in sync.allConfigs)
            {
                var data = FiresCore.Sync.ConfigSync.configData(c.BaseConfig);
                object local = data?.LocalBaseValue;
                if (local == null || Equals(local, c.BaseConfig.BoxedValue)) continue;
                diff.Add($"{c.BaseConfig.Definition.Section}/{c.BaseConfig.Definition.Key}: server {c.BaseConfig.BoxedValue}, local {local}");
            }
            FiresAdminTerrain.Log?.LogInfo($"[FatCoreSettings] synced keys at join: {diff.Count} of {sync.allConfigs.Count} differ from this peer's cfg{(diff.Count > 0 ? ": " + string.Join("; ", diff.ToArray()) : "")} (0.2.1346)");
        }

        // 0.2.1338: what Core's snow code really reads once in the world, whether or not the value changed (no change raises no SettingChanged).
        [HarmonyLib.HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class ProveEffectiveValue
        {
            private static ZNet s_saidFor;
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || ZNet.instance == null || s_saidFor == ZNet.instance) return;
                s_saidFor = ZNet.instance;
                const string key = "Max depth Mountain (m)";
                float got = FiresCore.Config.CoreSettings.Get("Snow", key, -1f);
                var sync = FiresAdminTerrain.Instance != null ? FiresAdminTerrain.Instance.configSync : null;
                s_ownedEntries.TryGetValue("Snow/" + key, out var entry);
                object local = entry != null ? FiresCore.Sync.ConfigSync.configData(entry)?.LocalBaseValue : null;
                LogSyncedDiff(sync);   // 0.2.1346
                FiresAdminTerrain.Log?.LogDebug($"[FatCoreSettings] effective Snow/{key} through CoreSettings.Get = {got}; this peer's own cfg value held aside by the sync = {local ?? "none (no server value applied)"}; server is the source = {sync != null && !sync.IsSourceOfTruth}, initial sync done = {sync?.InitialSyncDone} (0.2.1338)");
            }
        }

        private static ConfigEntry<T> Own<T>(ConfigFile config, string section, string key, T def, ConfigDescription description)
        {
            var d = new ConfigDefinition(section, key);
            ConfigEntry<T> entry = config.Bind(d, def, description);
            s_ownedEntries[section + "/" + key] = entry;   // 0.2.1337
            // 0.2.1335 (Fire's rule, proposal 11-02 section D; NOT 1329's "not saved yet" test): Core's old value is copied only while FAT's key is at its default
            if (s_copyFromCore && Equals(entry.BoxedValue, entry.DefaultValue) && FiresCore.Config.CoreSettings.TryReadCoreSaved(section, key, out string old))
            {
                try
                {
                    T value = (T)TomlTypeConverter.ConvertToValue(old, typeof(T));
                    if (!Equals(value, entry.Value))
                    {
                        entry.Value = value;
                        s_copied++;
                        FiresAdminTerrain.Log?.LogInfo($"[CoreSettings] copied {section}/{key} from Core: {entry.Value}");
                    }
                }
                catch (Exception ex) { FiresAdminTerrain.Log?.LogWarning($"[CoreSettings] {section}/{key}: Core's saved value '{old}' not read ({ex.Message}); the default stays"); }
            }
            // 0.2.1328: the getter overload, as before (FAT compiles against Core 0.2.1036, which has no ConfigEntry overload)
            // 0.2.1328: priority 10, the owner's rank Core's CoreSettings expects (FiresSnowmen provides at 1); at 0 a lesser provider could win.
            FiresCore.Config.CoreSettings.Provide(section, key, () => entry.Value, Owner, 10);
            s_counts[section] = Count(section) + 1;
            return entry;
        }
    }
}
