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

        [HarmonyPatch(typeof(TerrainOp), "Awake")]
        private static class TerrainOp_Awake_BlockOverTile
        {
            // Returning false = skip vanilla Awake = the op never registers
            // with TerrainComp and won't mutate any heightmap. We also destroy
            // the GameObject so its m_onPlacedEffect doesn't echo.
            private static bool Prefix(TerrainOp __instance)
            {
                if (__instance == null) return true;
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
                string routeDesc = RouteToOurBrush(__instance, position);

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

        // Map the vanilla op's settings to one of our brush ops. Best-effort —
        // vanilla terrain ops have a richer parameter set than our 4 brush
        // primitives; we collapse to the closest equivalent. Returns a short
        // description of what was routed for logging.
        private static string RouteToOurBrush(TerrainOp op, Vector3 position)
        {
            var s = op.m_settings;
            if (s == null) return "no settings (no-op)";

            // Level (flatten to op Y) → BrushFlat at op.position.y
            if (s.m_level)
            {
                float radius = Mathf.Max(s.m_levelRadius, 0.5f);
                int touched = TileBrush.BrushFlat(position, radius, position.y);
                return $"Level r={radius:F1} y={position.y:F1} → BrushFlat touched {touched} tile(s)";
            }
            // Raise/Lower (delta) → BrushRaise with signed delta
            if (s.m_raise)
            {
                float radius = Mathf.Max(s.m_raiseRadius, 0.5f);
                float delta = s.m_raiseDelta;
                float falloff = Mathf.Max(s.m_raisePower, 1f);
                int touched = TileBrush.BrushRaise(position, radius, delta, falloff);
                return $"Raise r={radius:F1} delta={delta:F2} → BrushRaise touched {touched} tile(s)";
            }
            // Smooth — closest analogue is "clear" (reset to unmodified, lets
            // adjacent terrain bleed through). Not a perfect smooth but it
            // does break the sharp-edge artifact a vanilla smooth was trying
            // to remove.
            if (s.m_smooth)
            {
                float radius = Mathf.Max(s.m_smoothRadius, 0.5f);
                int touched = TileBrush.BrushClear(position, radius);
                return $"Smooth r={radius:F1} → BrushClear touched {touched} tile(s)";
            }
            // Paint — map vanilla paint type to RGB color
            if (s.m_paintCleared)
            {
                float radius = Mathf.Max(s.m_paintRadius, 0.5f);
                Color32 color = MapPaintTypeToColor(s.m_paintType);
                int touched = TileBrush.BrushPaint(position, radius, color);
                return $"Paint {s.m_paintType} r={radius:F1} → BrushPaint touched {touched} tile(s)";
            }
            return $"unhandled op (level={s.m_level} raise={s.m_raise} smooth={s.m_smooth} paint={s.m_paintCleared})";
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
            switch (vanillaType)
            {
                case TerrainModifier.PaintType.Dirt:      return new Color32(255, 0,   0,   255);
                case TerrainModifier.PaintType.Cultivate: return new Color32(0,   255, 0,   255);
                case TerrainModifier.PaintType.Paved:     return new Color32(0,   0,   255, 255);
                // Reset / Grass / anything else → restore default look
                // (no paint, vegetation present). Same effect as "paint
                // grass" in IH and the vanilla hoe's reset op.
                case TerrainModifier.PaintType.Reset:
                default:                                  return new Color32(0,   0,   0,   255);
            }
        }
    }
}
