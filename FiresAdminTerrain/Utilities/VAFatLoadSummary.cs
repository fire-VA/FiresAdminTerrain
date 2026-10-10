using System;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAFatLoadSummary — emoji-headed mini-banners that REPLACE per-item
    // load logging for the FAT mod's Awake/FinishInit chatter.
    //
    // Ported from FiresAdminPrefabs/Utilities/VAFapLoadSummary.cs and
    // FiresGhettoNetworking/Utilities/VAGhettoLoadSummary.cs. Same
    // box-drawing format, different emojis tuned for FAT's domain
    // (terrain commands, RPC handlers, tile prefab, hammer compat).
    //
    // Banner format:
    //   ╭── 🔧 COMMANDS ──╮
    //   │ console cmds: 4/4 sets │
    //   │ biome persist: OK      │
    //   ╰────────────────────────╯
    //
    // All lines tagged [LoadSummary] so FiresUnifiedCore's
    // FiresLogColorPatch routes them to a distinct color via the keyword
    // rules.
    public static class VAFatLoadSummary
    {
        private const string Tag = "[LoadSummary]";
        private const int InnerWidth = 28;

        // Verbose-mode predicate. Reuses FiresLogger.VerboseEnabled
        // (which reads ConfigManager.Instance.configVerboseLogging).
        // Call sites use this to decide whether to emit per-line
        // detail (verbose) or rely solely on the banner (default).
        public static bool VerboseEnabled => FiresLogger.VerboseEnabled;

        // 🔧 COMMANDS — FAT.FinishInit's console command sets (biome paint,
        // snow, ice, land snow; client only) and the biome paint
        // persistence load. Each failure is logged by name before the box.
        public static void EmitCommands(bool dedicated, int commandSetsRegistered, int commandSets, bool biomePersistence)
        {
            EmitMiniBox("🔧 COMMANDS", new[]
            {
                dedicated ? "console cmds: client only" : $"console cmds: {commandSetsRegistered}/{commandSets} sets",
                biomePersistence ? "biome persist: OK" : "biome persist: FAILED",
            });
        }

        // 📡 RPCs — terrain RPC handler registration completion banner.
        // Aggregates the RegionScrubberRPC + TileBrushRPC + ConfigSync
        // RPC counts.
        public static void EmitRpcs(int configSync, int scrubber, int tileBrush)
        {
            int total = configSync + scrubber + tileBrush;
            EmitMiniBox("📡 RPCs", new[]
            {
                $"ConfigSync: {configSync}",
                $"scrubber: {scrubber}",
                $"tile brush: {tileBrush}",
                $"{total} handler(s) wired",
            });
        }

        // 🪨 TILE PREFAB — VATile prefab registration completion banner.
        // Replaces the 9-line TileAnchorPrefab.Ensure chatter with a
        // single visual unit.
        public static void EmitTilePrefab(int components, int renderers, int colliders)
        {
            EmitMiniBox("🪨 TILE PREFAB", new[]
            {
                $"comps: {components}",
                $"rend: {renderers} · coll: {colliders}",
                "VATile registered",
            });
        }

        // 🔨 HAMMER COMPAT — InfinityHammer / WEC ruler-compat patch
        // completion banner. Replaces the 5-line TileHammerCompat
        // chatter with one summary.
        public static void EmitHammerCompat(bool ihDetected, int patchesApplied)
        {
            EmitMiniBox("🔨 HAMMER COMPAT", new[]
            {
                ihDetected ? "IH detected ✓" : "IH absent (skipped)",
                $"{patchesApplied} ruler patch(es)",
            });
        }

        // 🪨 TILES RESTORED — debounced batch banner for a wave of
        // tile re-instantiations during zone activation. Called by
        // VATileRestoreBatch.Flush once a quiescence window has
        // elapsed after the last RecordTileRestored. Counters are
        // aggregate across the wave: total tiles, total payload
        // bytes, total height-modification points, total paint-
        // modification points, total water/lava disks restored.
        //
        // Payload size formatted as KB when over 1024 bytes for
        // readability.
        public static void EmitTilesRestored(int tiles, long payloadBytes, int heightMods, int paintMods, int disks)
        {
            string payloadLabel = payloadBytes >= 1024
                ? $"{payloadBytes / 1024.0:F1}KB"
                : $"{payloadBytes}B";

            // The `disks` field here counts per-TILE DiskRecord entries
            // (a TerrainTile internal field) — those are the legacy
            // RPG-maker dirt-floor disk records, NOT custom water/lava
            // disks. The label "water/lava disk(s)" was misleading —
            // it had the user thinking it tracked WaterMeshCombiner
            // disks. Renamed to "terrain disks" to match what it
            // actually counts. Water disks are reported separately
            // by WaterPersistence on load.
            EmitMiniBox("🪨 TILES", new[]
            {
                $"{tiles} restored · {payloadLabel}",
                $"hmods: {heightMods} · pmods: {paintMods}",
                disks > 0 ? $"+ {disks} terrain disk(s)" : "no terrain disks",
            });
        }

        // Generic mini-box renderer. Same width-math + truncation
        // logic as VAFapLoadSummary / VAGhettoLoadSummary — see those
        // for the rationale.
        private static void EmitMiniBox(string title, string[] lines)
        {
            try
            {
                int titleChars = title.Length;
                int fillDashes = Math.Max(0, InnerWidth - titleChars - 5);
                string topFrame = "╭─── " + title + " " + new string('─', fillDashes) + "╮";
                string botFrame = "╰" + new string('─', InnerWidth) + "╯";

                EmitLine(topFrame);
                foreach (var line in lines)
                {
                    string body = line ?? string.Empty;
                    if (body.Length > InnerWidth - 2) body = body.Substring(0, InnerWidth - 2);
                    body = body.PadRight(InnerWidth - 2);
                    EmitLine($"│ {body} │");
                }
                EmitLine(botFrame);
            }
            catch (Exception ex)
            {
                try { EmitLine($"(summary render failed: {ex.Message}) {title}: {string.Join(", ", lines ?? new string[0])}"); }
                catch { }
            }
        }

        // Routes a summary line through the plugin's BepInEx log source (not
        // Debug.Log) so banner lines print once — Debug.Log would also stdout-echo
        // a raw white console duplicate. The {Tag} stays for FUC colouring.
        private static void EmitLine(string frame)
        {
            if (FiresAdminTerrain.Log != null) FiresAdminTerrain.Log.LogInfo($"{Tag} {frame}");
            else FiresLogger.LogInfo($"{Tag} {frame}");
        }
    }
}
