using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1095 (Fire: "Lava, islands + ground"): Ashlands water - generated sheets on the ground and on sky islands, and placed
    // water - is LAVA where its 1 m column is hot (0.2.1097: WaterStateField, per column; was per body). Not water: no liquid level (no swimming; VoxelWaterSurface
    // and the placed-water level scans skip it), no fish, lilies or water life (FatWaterChecks.TryWaterAt says dry).
    // Damage is VANILLA's own lava path: Character.UpdateLava (assembly_valheim 1.0.15 Character.cs:573) only heats a character
    // standing on a Heightmap whose GetLava mask is set (Heightmap.cs:757), which our lava surfaces are not. This postfix feeds that
    // same path where a character is in or on our lava: m_lavaProximity = 1, the heat build-up vanilla adds over lava
    // (m_heatBuildupBase x dt x (1 - heat resistance), :599), m_lavaTimer / m_aboveOrInLavaTimer reset. Vanilla's own
    // UpdateHeatDamage (:653+) then applies the "Burning" status effect and AshlandsLava damage, with the heat distortion and
    // fire-resistance gear exactly as on vanilla lava.
    internal static class FatLava
    {
        private const float AboveSurface = 0.4f, BelowSurface = 4f;

        // 0.2.1097: lava per 1 m column (WaterStateField): generated or placed water whose lava amount is at or over _FiresLavaHot.
        // Cooled crust (below it) is walkable ground and never burns.
        internal static bool LavaAt(Vector3 p, out float surface)
        {
            surface = float.NaN;
            if (GenLavaAt(p.x, p.z, p.y, out surface)) return true;
            if (PlacedWaterField.TrySurface(p.x, p.z, p.y - BelowSurface, p.y + 2f, PlacedWaterField.Consumer.Sheet, out float s, out _)
                && WaterStateField.LavaHotAt(p.x, p.z, s)) { surface = s; return true; }
            return false;
        }

        // Generated water whose column is hot lava (the state at the water's own height: the island's biome on a sky island).
        internal static bool GenLavaAt(float x, float z, float y, out float surface)
        {
            surface = float.NaN;
            if (!GenWaterField.Ready) return false;
            if (!GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) || depth <= 0.05f) return false;
            if (!WaterStateField.LavaHotAt(x, z, level)) return false;
            surface = level;
            return true;
        }

        // Generated water whose column is solid ice, cooled crust or hot lava: no liquid level there (VoxelWaterSurface).
        internal static bool GenNotLiquidAt(float x, float z)
        {
            if (!GenWaterField.Ready) return false;
            if (!GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) || depth <= 0.05f) return false;
            return WaterStateField.NotLiquidAt(x, z, level);
        }

        // Generated water that is open water (no slush, crust or lava): fish, lilies and wet ground only there.
        internal static bool GenPureWaterAt(float x, float z, float level) => WaterStateField.PureWaterAt(x, z, level);

        private static readonly AccessTools.FieldRef<Character, float> s_proximity = AccessTools.FieldRefAccess<Character, float>("m_lavaProximity");
        private static readonly AccessTools.FieldRef<Character, float> s_heat = AccessTools.FieldRefAccess<Character, float>("m_lavaHeatLevel");
        private static readonly AccessTools.FieldRef<Character, float> s_timer = AccessTools.FieldRefAccess<Character, float>("m_lavaTimer");
        private static readonly AccessTools.FieldRef<Character, float> s_aboveTimer = AccessTools.FieldRefAccess<Character, float>("m_aboveOrInLavaTimer");
        private static readonly System.Reflection.MethodInfo s_resist = AccessTools.Method(typeof(Character), "GetEquipmentHeatResistanceModifier");
        private struct CacheEntry { public float At, Surface; public bool Lava; }
        private static readonly System.Collections.Generic.Dictionary<Character, CacheEntry> s_cache = new System.Collections.Generic.Dictionary<Character, CacheEntry>();
        private static float s_logAt;
        private static int s_ticks;

        [HarmonyPatch(typeof(Character), "UpdateLava")]
        private static class Patch_UpdateLava
        {
            private static void Postfix(Character __instance, float dt)
            {
                if (__instance == null) return;
                Vector3 p = __instance.transform.position;
                // the lava lookup runs at most every 0.25 s per character (per-physics-step cost stays a dictionary read)
                if (!s_cache.TryGetValue(__instance, out var e) || Time.time - e.At > 0.25f)
                {
                    e.At = Time.time; e.Lava = LavaAt(p, out e.Surface);
                    if (s_cache.Count > 512) s_cache.Clear();
                    s_cache[__instance] = e;
                }
                if (!e.Lava) return;
                float surface = e.Surface;
                if (p.y > surface + AboveSurface || p.y < surface - BelowSurface) return;
                float resist = 0f;
                try { resist = s_resist != null ? (float)s_resist.Invoke(__instance, null) : 0f; } catch { }
                s_proximity(__instance) = 1f;
                s_aboveTimer(__instance) = 0f;
                s_timer(__instance) = 0f;
                if (!__instance.m_tolerateFire)
                    s_heat(__instance) = Mathf.Clamp01(s_heat(__instance) + dt * __instance.m_heatBuildupBase * (1f - resist));
                s_ticks++;
                if (__instance == Player.m_localPlayer && FiresLogger.DebugEnabled && Time.unscaledTime >= s_logAt)
                {
                    s_logAt = Time.unscaledTime + 10f;
                    FiresLogger.LogDebug($"[WaterLava] player in FAT lava at ({p.x:0},{p.z:0}) surface {surface:0.00}: vanilla heat level {s_heat(__instance):0.00} (Burning + AshlandsLava damage via Character.UpdateHeatDamage); {s_ticks} lava tick(s) so far");
                }
            }
        }
    }
}
