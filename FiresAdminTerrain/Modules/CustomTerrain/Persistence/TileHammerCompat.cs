using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // IH integration for the new tile-anchor terrain system.
    //
    // What this fixes:
    //   * Without this patch IH's ruler ring (the visible circle around the
    //     cursor showing the brush footprint) raycasts from 500m above the
    //     player to find ground. That raycast hits VANILLA terrain below our
    //     floating tile platforms, so the ruler ring stretches down to vanilla
    //     ground instead of sitting on the platform — visually you just see
    //     the center dot at the cursor height with no visible ring at the
    //     platform level.
    //
    // The fix (1:1 port of InfinityHammerCompat.BaseRuler_Snap_Postfix from
    // the original RPG-maker mod): postfix on InfinityTools.BaseRuler.Snap.
    // When the placement ghost is over one of our loaded tiles, override the
    // ruler segment's Y to match the ghost's Y. That keeps the entire ring
    // flat at the cursor height matching the platform the player is aiming at.
    //
    // Detection runtime via Assembly.Load reflection so we don't take a hard
    // BepInDependency on InfinityHammer.
    public static class TileHammerCompat
    {
        private static bool _initialized;
        private static bool _ihLoaded;
        private static Type _baseRulerType;
        private static Assembly _ihAssembly;

        public static bool IsActive => _ihLoaded;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "InfinityHammer") continue;
                _ihAssembly = assembly;
                _ihLoaded = true;
                _baseRulerType = assembly.GetType("InfinityTools.BaseRuler");
                FiresLogger.LogVerbose($"[TileHammerCompat] InfinityHammer detected (assembly {assembly.GetName().Version}) — BaseRuler type {(_baseRulerType != null ? "found" : "MISSING")}");
                break;
            }

            if (!_ihLoaded)
            {
                FiresLogger.LogVerbose("[TileHammerCompat] InfinityHammer not loaded — ruler-snap fix skipped (this is fine if you don't use IH).");
                // Still apply the GetGroundHeight patches: WorldEditCommands
                // (separate from IH) also uses GetGroundHeight for its terrain
                // ops, and vanilla code paths benefit too. The patches are no-op
                // overhead if neither mod is loaded.
                int groundOnly = GroundHeightHooks;
                VerdantsAscent.Utilities.VAFatLoadSummary.EmitHammerCompat(ihDetected: false, patchesApplied: groundOnly);
                return;
            }

            int applied = ApplyRulerSnapPatch() + GroundHeightHooks;
            VerdantsAscent.Utilities.VAFatLoadSummary.EmitHammerCompat(ihDetected: true, patchesApplied: applied);
        }

        private static int ApplyRulerSnapPatch()
        {
            if (_baseRulerType == null)
            {
                FiresLogger.LogWarning("[TileHammerCompat] BaseRuler type missing — can't patch ruler snap. IH ring won't lift onto custom terrain.");
                return 0;
            }

            try
            {
                var harmony = new Harmony("VerdantsAscent.TileHammerCompat.Ruler");

                var snapMethod = _baseRulerType.GetMethod(
                    "Snap",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(Vector3) },
                    modifiers: null);

                if (snapMethod == null)
                {
                    FiresLogger.LogWarning("[TileHammerCompat] BaseRuler.Snap(Vector3) signature not found — IH API may have changed. Ruler-snap fix skipped.");
                    return 0;
                }

                var postfixInfo = typeof(TileHammerCompat).GetMethod(
                    nameof(BaseRuler_Snap_Postfix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                // PREFIX too: sets a flag so GetGroundHeight knows "we're
                // inside a ruler segment query right now" and only then
                // returns our terrain height. Without this gate, vegetation /
                // AI / character grounding queries would also lift onto our
                // skyland (since they all call GetGroundHeight). Postfix
                // clears the flag.
                var prefixInfo = typeof(TileHammerCompat).GetMethod(
                    nameof(BaseRuler_Snap_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(snapMethod,
                    prefix: new HarmonyMethod(prefixInfo),
                    postfix: new HarmonyMethod(postfixInfo));
                FiresLogger.LogVerbose("[TileHammerCompat] Patched InfinityTools.BaseRuler.Snap — ruler ring will now sit on custom tile platforms at the cursor Y.");
                return 1;
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[TileHammerCompat] ApplyRulerSnapPatch threw: {ex}");
                return 0;
            }
        }

        // Tool names that should ALWAYS render the ruler ring flat at the
        // ghost Y (regardless of what's under the cursor). Matches the
        // `name:` fields in infinity_tools.yaml under the HOE section.
        //
        // Without this, the ring only lifts when the ghost is over EXISTING
        // tile terrain — for FIRST placement on vanilla ground each ring
        // segment individually raycasts down and snaps to whatever it hits,
        // producing an uneven ring contour. Our tools need the ring to
        // preview the upcoming flat platform at the ghost height.
        //
        // Vanilla IH tools (Pipette, Admin Buildings, etc.) are NOT in this
        // set so their normal ground-snapping ring behavior is preserved.
        private static readonly HashSet<string> s_ourToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Floor",
            "Raise Terrain", "Raise (Sharp)", "Lower Terrain",
            "Paint Dirt", "Paint Cultivated", "Paint Paved",
            "Clear Brushed Area",
            "Precision Hole Punch", "Patch Hole",
            "Delete Tiles",
            // Biome paint tools — same ring-lift treatment so the ruler
            // sits flush with the brushed tile surface.
            "Biome Meadows", "Biome BlackForest", "Biome Plains",
            "Biome Swamp", "Biome Mountain", "Biome Mistlands",
            "Biome Ashlands (Lava)", "Biome DeepNorth", "Clear Biome",
            // Water placement tools — ruler should sit on the tile surface
            // so water disks spawn at the platform's level rather than
            // pulling down to vanilla terrain.
            "Water", "Lava", "Tar",
            "Boiling Water", "Freezing Water", "Ice",
            "Delete Water",
        };

        // Gate counter. >0 ONLY while we're inside a BaseRuler.Snap call.
        // GetGroundHeight prefix checks this — if zero, falls through to
        // vanilla. Without the gate, GetGroundHeight overrides apply to
        // vegetation placement, character grounding, AI navigation, etc., and
        // we get the "all vegetation in the world raises when ruler is active"
        // bug (which the user spotted 2026-05-24).
        //
        // Counter (not bool) so recursive Snap calls don't drop the gate
        // mid-flight. ThreadStatic so callers from other threads (unlikely
        // but possible — HeightmapBuilder uses a worker thread) don't see
        // false positives.
        [System.ThreadStatic] private static int s_rulerSnapDepth;
        public static bool IsInRulerSnap => s_rulerSnapDepth > 0;

        // World Edit Commands resolves a command's Y through GetGroundHeight before it
        // touches any terrain, so the lift has to be open for that one call too or a
        // command aimed at a FAT tile carries the vanilla ground's Y and WecTerrainBridge
        // cannot tell the tile was the target. Separate counter: the ruler and the
        // command scope open and close independently.
        [System.ThreadStatic] private static int s_terrainCommandDepth;

        internal static void BeginTerrainCommandQuery() => s_terrainCommandDepth++;

        internal static void EndTerrainCommandQuery()
        {
            if (s_terrainCommandDepth > 0) s_terrainCommandDepth--;
        }

        private static bool TileLiftOpen => s_rulerSnapDepth > 0 || s_terrainCommandDepth > 0;

        private static void BaseRuler_Snap_Prefix() => s_rulerSnapDepth++;

        // Postfix runs after vanilla Snap. __result is the snapped position
        // for one ruler segment. We override Y when:
        //   1. One of OUR tools is the active piece — lift unconditionally
        //      so the ring previews the flat platform that's about to drop.
        //   2. (Fallback) Ghost is over an existing loaded tile — same
        //      pattern as InfinityHammerCompat.BaseRuler_Snap_Postfix from
        //      the original RPG-maker mod.
        // Also clears s_inRulerSnap (whether we modified __result or not).
        private static FieldInfo s_placementGhostField;

        private static void BaseRuler_Snap_Postfix(ref Vector3 __result)
        {
            // ALWAYS decrement the gate when we leave Snap, regardless of
            // which return path below fires. Otherwise GetGroundHeight from
            // non-ruler callers (vegetation, AI) would mistakenly think we're
            // in a ruler call. Early-decrement is safe because the postfix
            // body below only reads ghost position — no GetGroundHeight calls.
            if (s_rulerSnapDepth > 0) s_rulerSnapDepth--;

            var player = Player.m_localPlayer;
            if (player == null) return;

            if (s_placementGhostField == null)
            {
                s_placementGhostField = typeof(Player).GetField(
                    "m_placementGhost",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_placementGhostField == null) return;
            }

            var ghost = s_placementGhostField.GetValue(player) as GameObject;
            if (ghost == null) return;

            Vector3 ghostPos = ghost.transform.position;

            // Path A: one of our tools is active. Lift unconditionally so
            // the ring shows where the platform will land — works even on
            // vanilla ground for FIRST placement (no existing tile required).
            if (IsOurToolActive(player))
            {
                __result.y = ghostPos.y;
                return;
            }

            // Path B (fallback): ghost is over a loaded tile that has a
            // modified vertex at the ghost position. Useful for vanilla IH
            // tools (like Area Pipette) when the ring overlaps our terrain —
            // keeps the ring flat at the platform's surface.
            if (TileAnchorRegistry.TrySampleHeightAtWorld(ghostPos, out _))
            {
                __result.y = ghostPos.y;
                return;
            }
            var tileXZ = TileAnchorRegistry.WorldToTileIndex(ghostPos);
            var anchor = TileAnchorRegistry.GetAt(tileXZ);
            if (anchor != null)
            {
                __result.y = ghostPos.y;
                return;
            }

            // Not our tool + not over our terrain — vanilla snap behavior wins.
        }

        // Checks if the player's currently selected build piece is one of
        // ours. Uses Player.m_buildPieces.GetSelectedPiece() (the standard
        // accessor for the current PieceTable selection — same path IH and
        // vanilla both use).
        private static bool IsOurToolActive(Player player)
        {
            var pt = player.m_buildPieces;
            if (pt == null) return false;
            var piece = pt.GetSelectedPiece();
            if (piece == null) return false;
            return s_ourToolNames.Contains(piece.m_name);
        }

        // ── ZoneSystem.GetGroundHeight prefix patches ────────────────────────
        //
        // **THIS IS THE ACTUAL RULER FIX.** The IH ruler ring is drawn by
        // computing N segment positions around a center, then calling
        // ZoneSystem.GetGroundHeight(segmentPos) for each segment to find
        // its Y. By default that hits vanilla terrain → ring stretches down
        // when over our floating platform.
        //
        // The BaseRuler.Snap postfix (above) only fixes the CENTER position,
        // not the segments. Patching GetGroundHeight is the root fix because:
        //   1. Every ring segment routes through it
        //   2. WorldEditCommands' terrain ops also use it
        //   3. It works whether IH is loaded or not
        //
        // Verbatim port of InfinityHammerCompat.ZoneSystem_GetGroundHeight_Prefix
        // from the original RPG-maker mod (Modules/CustomTerrain/Tools/InfinityHammerCompat.cs:388).
        // Both vanilla overloads run these, from FAT's one hook per overload
        // (FatVanillaHooks), after the voxel ground-truth prefix, as when they
        // were patched here by hand.
        private const int GroundHeightHooks = 2;

        // Returning false skips the vanilla method body; __result becomes the
        // value the caller sees. Lift only when the query point is over an
        // EXISTING loaded tile with a modified vertex — fall through to
        // vanilla otherwise.
        //
        // EARLIER VERSION had a "Path 2" that returned ghost Y for any query
        // within a brush radius when our tool was active. That broke the world:
        // GetGroundHeight is called by ZoneSystem.PlaceVegetation, character
        // grounding, AI navigation, and many other systems. As the player
        // walked around with a brush equipped, vegetation in every newly-
        // loaded zone got lifted to the ghost Y. The original RPG-maker
        // InfinityHammerCompat (Modules/CustomTerrain/Tools/InfinityHammerCompat.cs:388)
        // never had this path — it only lifted over EXISTING combiner terrain,
        // and accepted that first-placement ring segments would dip to vanilla
        // on virgin ground. Same trade-off here. The center marker is still
        // lifted via the BaseRuler.Snap postfix; once any tile exists at a
        // query position the ring lifts via the path below.
        internal static bool GroundHeightSingle(Vector3 p, ref float __result)
        {
            // Only override if we're inside an IH ruler snap call. Otherwise
            // vegetation/AI/character grounding queries fall through to vanilla
            // — so vanilla trees stay planted on vanilla ground even when our
            // skyland is above them. This is the fix for the "all vegetation
            // raises with ruler height" bug spotted 2026-05-24.
            if (!TileLiftOpen) return true;

            if (TileAnchorRegistry.TrySampleHeightAtWorld(p, out float h))
            {
                __result = h;
                return false;
            }
            return true;
        }

        internal static bool GroundHeightOut(Vector3 p, ref float height, ref bool __result)
        {
            if (!TileLiftOpen) return true;

            if (TileAnchorRegistry.TrySampleHeightAtWorld(p, out float h))
            {
                height = h;
                __result = true;
                return false;
            }
            return true;
        }
    }
}
