using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Block vanilla TerrainOp from mutating the vanilla heightmap when the
    // player uses the vanilla hoe / cultivator (or any other terrain tool)
    // over our floating tile.
    //
    // 1:1 port of InfinityHammerCompat.TerrainOp_Awake_Prefix from the original
    // RPG-maker mod (Modules/CustomTerrain/Tools/InfinityHammerCompat.cs:546).
    // Different routing — instead of forwarding to DirtFloorTerrainManager
    // (which the legacy mod used), we route to TileBrush.* and destroy the
    // vanilla op so the vanilla TerrainComp heightmap is untouched.
    //
    // Why this matters: if a user has a floating skyland at Y=60 and absent-
    // mindedly hoes the vanilla ground at Y=10 directly below, the vanilla
    // op modifies vanilla heightmap and they get vanilla terrain deformation
    // they didn't want. The block reroutes the intent to OUR brush so the
    // skyland gets the modification instead, and the vanilla terrain stays
    // pristine.
    //
    // Detection: TrySampleHeightAtWorld returns true only when there's a
    // MODIFIED vertex at the position. So a tile with no terrain at that XZ
    // doesn't block vanilla ops — they pass through to vanilla heightmap.
    // This keeps the block scoped to "actual floating terrain underfoot",
    // not "any zone that has a tile anchor".
    public static class TileTerrainOpBlocker
    {
        // Larger radius than the op's footprint so corner-clicks on the tile
        // edge still block. 4m matches the original mod's check_radius.
        private const float OP_CHECK_RADIUS = 4f;

        // From FAT's one TerrainOp.Awake hook (FatVanillaHooks), a Normal-priority
        // prefix after the zone-mode refusal.
        internal static bool AllowTerrainOp(TerrainOp op) => OverTile.Allow(op);

        private static TerrainOp s_lastRoutedOp;

        // Later prefixes on the same hook still run after this one returns false, so
        // they ask this before treating the op as unclaimed.
        internal static bool WasRoutedToTile(TerrainOp op) => op != null && ReferenceEquals(op, s_lastRoutedOp);

        private static class OverTile
        {
            // Returning false = skip vanilla Awake = the op never registers
            // with TerrainComp and won't mutate any heightmap. We also destroy
            // the GameObject so its m_onPlacedEffect doesn't echo.
            internal static bool Allow(TerrainOp __instance)
            {
                if (__instance == null) return true;
                if (VerdantsAscent.Terrain.Zones.ZoneModeRefusals.WasCancelled(__instance)) return false;
                Vector3 position = __instance.transform.position;

                // 2026-05-30 round-54 — phantom-op guard. Some external
                // mods (FAP_ClutterClearTool, etc.) fire transient
                // TerrainOps at world origin (0,0,0) or similar fixed
                // positions as a no-op marker / clutter trigger. Our
                // blocker was indiscriminately routing those into
                // BrushPaint/BrushClear at the wrong XZ, causing
                // TileAnchor.Spawn to try to create anchors for tiles
                // around (0,0) — those spawns end up with invalid
                // ZNetViews because ZDOMan rejects them at that
                // moment, and the log floods with "invalid ZNetView"
                // + "Can't remove ZNetView" warnings.
                //
                // A legitimate player-initiated TerrainOp is always
                // within arm's reach of the player. Skipping routing
                // when the op is >25m from the local player filters
                // out the phantom ops without affecting normal use.
                // Server-driven ops (no local player) pass through to
                // the original lookup path — dedicated server case.
                var localP = Player.m_localPlayer;
                if (localP != null)
                {
                    Vector3 pPos = localP.transform.position;
                    float dx = position.x - pPos.x;
                    float dz = position.z - pPos.z;
                    float distSq = dx * dx + dz * dz;
                    if (distSq > 625f) // 25m
                    {
                        // Don't route — let vanilla handle it (which is
                        // probably a no-op anyway since it's a phantom).
                        return true;
                    }
                }

                // Fast XZ tile lookup: is there an anchor at this tile?
                // ALSO check 8 neighbour tiles so brushes near the border
                // route to our system even if the op center landed on the
                // adjacent vanilla tile. Matches the "any custom terrain in
                // the vicinity" lenient check RPG-maker's
                // DirtFloorTerrainManager.IsPositionOverCustomTerrain used.
                var tileXZ = TileAnchorRegistry.WorldToTileIndex(position);
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null || anchor.Tile == null)
                {
                    for (int dz = -1; dz <= 1 && (anchor == null || anchor.Tile == null); dz++)
                    for (int dx = -1; dx <= 1 && (anchor == null || anchor.Tile == null); dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var neighbour = TileAnchorRegistry.GetAt(new Vector2i(tileXZ.x + dx, tileXZ.y + dz));
                        if (neighbour != null && neighbour.Tile != null)
                            anchor = neighbour;
                    }
                }
                if (anchor == null || anchor.Tile == null)
                {
                    FiresLogger.LogInfo($"[TileTerrainOpBlocker] vanilla TerrainOp at {position} → no tile anchor at tileXZ=({tileXZ.x},{tileXZ.y}) or 8 neighbours, passing through to vanilla.");
                    return true; // no tile anywhere nearby → vanilla op runs normally
                }

                // 2026-05-25 round-14: STRICT modified-vertex check removed.
                // RPG-maker's InfinityHammerCompat just asks "is there custom
                // terrain at this XZ?" via DirtFloorTerrainManager.IsPositionOverCustomTerrain
                // — once it gets a yes, EVERY terrain op routes to the
                // custom system regardless of which vertex is modified.
                // Our older IsAnyVertexModifiedNear gate failed for fresh
                // / unmodified parts of a tile, so IH Pave on a flat tile
                // surface silently fell through and modified the vanilla
                // heightmap underneath instead.

                // We're over our floating terrain. Re-route the intent
                // to our brush so the user's input has SOME effect (vs just
                // being silently no-op'd), then destroy the vanilla op.
                //
                // TileBrushRPC's receivers permit only some ops to a non-admin, so an op
                // this peer may not send whole would half-apply after the vanilla op was
                // already destroyed. Say so instead, and leave the vanilla ground alone.
                if (!PeerMayApplyEveryFlag(__instance.m_settings, out string deniedFlag))
                {
                    s_lastRoutedOp = __instance;
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, NonAdminMessage);
                    FiresLogger.LogInfo($"[TileTerrainOpBlocker] refused a vanilla TerrainOp at {position} - this peer may not {deniedFlag} a FAT tile.");
                    UnityEngine.Object.Destroy(__instance.gameObject);
                    return false;
                }

                s_lastRoutedOp = __instance;
                string routeDesc = RouteToOurBrush(__instance, position);

                // UNIFIED PAINT (the hard sand edge on tile borders): the route above
                // only writes tiles that ALREADY exist, and destroying the vanilla op
                // used to throw away the rest — a stroke crossing off anchored terrain
                // painted NOTHING on the vanilla side (paint stopped dead on the border).
                // If this op PAINTS, mirror the stroke into the vanilla system through
                // vanilla's OWN owner-RPC (paint-only settings — exact vanilla math,
                // multiplayer-correct, and vanilla heights can never deform: the skyland
                // protection stands in full). The tile side got the stroke from
                // RouteToOurBrush above with the SAME falloff formula, so both sides of
                // any boundary are one continuous curve. Under-tile vanilla paint sits
                // occluded beneath the tile mesh — harmless, and it means removing a
                // tile later reveals correctly-painted ground instead of a bald patch.
                var st = __instance.m_settings;
                if (st != null && st.m_paintCleared)
                {
                    int mirrored = VerdantsAscent.Terrain.Paint.FiresUnifiedPaint.MirrorPaint(
                        position, Mathf.Max(0.5f, st.m_paintRadius), st.m_paintType, st.m_paintHeightCheck);
                    if (mirrored > 0)
                        FiresLogger.LogVerbose($"[TileTerrainOpBlocker] paint mirrored to {mirrored} vanilla comp(s) at {position}.");
                }

                // Mute the effect so we don't get a phantom dirt-splat at the
                // vanilla terrain Y when the player is way up on a skyland.
                try { __instance.m_onPlacedEffect?.Create(position, Quaternion.identity); }
                catch { /* ok if it throws — we still want to destroy */ }

                UnityEngine.Object.Destroy(__instance.gameObject);
                FiresLogger.LogInfo($"[TileTerrainOpBlocker] Routed vanilla TerrainOp to tile=({tileXZ.x},{tileXZ.y}) pos={position} → {routeDesc}");
                return false;
            }
        }

        // Sample a small grid around the op center to decide if we're "over
        // our terrain". Returns true on the first hit. Vertex spacing is 1m,
        // so a 4m radius is 9x9 = 81 vertex checks worst case — cheap.
        private static bool IsAnyVertexModifiedNear(TileAnchor anchor, Vector3 center, float radius)
        {
            int steps = Mathf.CeilToInt(radius);
            for (int dz = -steps; dz <= steps; dz++)
            for (int dx = -steps; dx <= steps; dx++)
            {
                Vector3 probe = new Vector3(center.x + dx, center.y, center.z + dz);
                if (TileAnchorRegistry.TrySampleHeightAtWorld(probe, out _))
                    return true;
            }
            return false;
        }

        private const float MinOpRadius = 0.5f;
        private const float MinRaiseFalloff = 1f;
        private const float VanillaSmoothStrength = 0.25f;
        private const string NonAdminMessage = "Only admins can reshape this terrain. You can still level, smooth and paint it.";

        // One vanilla op can carry several flags, and m_settings is the shared prefab
        // instance - stripping a flag would corrupt the prefab for the whole session. So
        // an op is applied whole or refused whole, in vanilla's flag order.
        private static bool PeerMayApplyEveryFlag(TerrainOp.Settings s, out string deniedFlag)
        {
            deniedFlag = null;
            if (s == null) return true;
            if (s.m_level && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Level)) deniedFlag = "level";
            else if (s.m_raise && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Raise)) deniedFlag = "raise";
            else if (s.m_smooth && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Smooth)) deniedFlag = "smooth";
            else if (s.m_paintCleared && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Paint)) deniedFlag = "paint";
            return deniedFlag == null;
        }

        // Sends every terrain flag the vanilla op carries - level, raise, smooth,
        // paint - in TerrainComp.InternalDoOperation's order, through TileBrushRPC so
        // the tile's owner applies it. Returns a description for logging.
        private static string RouteToOurBrush(TerrainOp op, Vector3 position)
        {
            var s = op.m_settings;
            if (s == null) return "no settings (no-op)";

            // Vanilla lifts level and smooth by m_levelOffset; raise and paint use the op point.
            Vector3 levelPosition = position + Vector3.up * s.m_levelOffset;

            var routed = new List<string>();
            if (s.m_level)
            {
                float radius = Mathf.Max(s.m_levelRadius, MinOpRadius);
                TileBrushRPC.SendFlat(levelPosition, radius, levelPosition.y, square: false);
                routed.Add($"level r={radius:F1} y={levelPosition.y:F1}");
            }
            if (s.m_raise)
            {
                float radius = Mathf.Max(s.m_raiseRadius, MinOpRadius);
                TileBrushRPC.SendRaise(position, radius, s.m_raiseDelta, Mathf.Max(s.m_raisePower, MinRaiseFalloff));
                routed.Add($"raise r={radius:F1} delta={s.m_raiseDelta:F2}");
            }
            if (s.m_smooth)
            {
                float radius = Mathf.Max(s.m_smoothRadius, MinOpRadius);
                TileBrushRPC.SendSmooth(levelPosition, radius, VanillaSmoothStrength);
                routed.Add($"smooth r={radius:F1}");
            }
            if (s.m_paintCleared)
            {
                float radius = Mathf.Max(s.m_paintRadius, MinOpRadius);
                TileBrushRPC.SendPaint(position, radius, MapPaintTypeToColor(s.m_paintType), square: false);
                routed.Add($"paint {s.m_paintType} r={radius:F1}");
            }
            return routed.Count == 0 ? "no terrain flags (no-op)" : string.Join(", ", routed);
        }

        // Vanilla TerrainModifier.PaintType → our R/G/B/A paint-mask
        // convention. Matches the EXACT colors vanilla Heightmap uses
        // (m_paintMaskDirt/Cultivated/Paved/Nothing/ClearVegetation).
        //
        // 2026-05-25 round-16: fixed Reset/Grass mapping. Old Reset
        // returned (255,255,255,0) — transparent white — which the shader
        // displayed as "vegetation cleared" not "default grass restored".
        // Vanilla "no paint" = m_paintMaskNothing = (0,0,0,1) and that's
        // what the Reset / Grass cases now produce. Painting grass now
        // correctly clears any prior paved/dirt/cultivated and brings
        // back the meadows-grass appearance.
        private static Color32 MapPaintTypeToColor(TerrainModifier.PaintType vanillaType)
        {
            // Color32 packs values as 0-255. The vanilla statics use
            // Color (0-1 float) — multiply by 255 mentally:
            //   m_paintMaskDirt        = (1, 0, 0, 1) → (255, 0,   0,   255)
            //   m_paintMaskCultivated  = (0, 1, 0, 1) → (0,   255, 0,   255)
            //   m_paintMaskPaved       = (0, 0, 1, 1) → (0,   0,   255, 255)
            //   m_paintMaskNothing     = (0, 0, 0, 1) → (0,   0,   0,   255)
            //   m_paintMaskClearVegetation = (0, 0, 0, 0) -> (0, 0, 0, 0)
            //   m_paintMaskDeepSnow    = (1, 1, 1, 1) -> (255, 255, 255, 255)
            switch (vanillaType)
            {
                case TerrainModifier.PaintType.Dirt:      return new Color32(255, 0,   0,   255);
                case TerrainModifier.PaintType.Cultivate: return new Color32(0,   255, 0,   255);
                case TerrainModifier.PaintType.Paved:     return new Color32(0,   0,   255, 255);
                case TerrainModifier.PaintType.DeepSnow:  return new Color32(255, 255, 255, 255);
                case TerrainModifier.PaintType.ClearVegetation: return new Color32(0, 0, 0, 0);
                // Reset / Grass / anything else → restore default look
                // (no paint, vegetation present). Same effect as "paint
                // grass" in IH and the vanilla hoe's reset op.
                case TerrainModifier.PaintType.Reset:
                default:                                  return new Color32(0,   0,   0,   255);
            }
        }
    }
}
