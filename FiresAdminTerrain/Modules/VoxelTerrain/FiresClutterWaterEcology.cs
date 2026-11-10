using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Clutter ecology around generated water ───────────────────────────
    // The vegetation port (FiresVegWaterEcology) fixed TREES and the placed
    // shore plants. Clutter — vass, reeds, waterlilies, the swamp grasses —
    // is a different system with the same defect, and its own constant:
    //
    //   float alt = point.y - this.m_waterLevel;        // m_waterLevel = 27
    //   if (alt >= clutter.m_minAlt && alt <= clutter.m_maxAlt) ...
    //   if (clutter.m_snapToWater) p.y = this.m_waterLevel;
    //
    // Every water-clutter band is written against that 27. At a generated
    // lake surface 60m up, `alt` is ~33 so only dry-land grass qualifies, and
    // a snapToWater lily would be placed 30m underground. Result: water over
    // ordinary meadow grass, no vass, no lilies.
    //
    // The fix is the mutate-and-restore pattern the vegetation density and
    // altitude patches already use: for each 8m patch that sits over our
    // water, point m_waterLevel at THAT water for the duration of the patch
    // build, preserving vanilla's own offset between the sea plane and this
    // constant, then put it back. Every band vanilla tuned then applies to
    // our lakes and rivers unchanged, and patches away from generated water
    // are bit-identical vanilla.
    // Apply is a prefix and Restore a finalizer of FAT's one ClutterSystem.GenerateVegPatch hook (FatVanillaHooks).
    //
    // Inland water grows water clutter at its shores and over its water (lilies and vass):
    //  * The spread builder (ClutterSpread, on by default) places a patch frames AFTER this hook has restored
    //    m_waterLevel, so it read 27 and the move never reached a single instance. It now takes WaterLevel and
    //    Surface from here when the patch is queued.
    //  * A patch whose centre is dry but which lies on a shore looks for the water around it (ShoreRings), so the
    //    bank grows vass as well as the water its lilies.
    //  * Surface is the water's own level (diagnostics). The ocean-depth bands keep vanilla's zone-level depth under the
    //    sea, which reads 0 inland (substituting the local depth refused every lily).
    //  * What grows where, from the runtime rules (m_waterLevel ~27 against the 30 m sea, so the move keeps
    //    the 3 m gap): instanced_waterlilies alt -1.5..-0.2 → a bed 3.2-4.5 m under the surface; instanced_vass alt
    //    -1..-0.1 → 3.1-4.0 m. Fish1 (SpawnSystem, against 30) wants a bed 1.5-3 m deep. A Vpool pond is shaped for both.
    internal static class ClutterSystem_GenerateVegPatch_WaterEcology
    {
        // 0.2.1086: ClutterSystem's m_waterLevel in vanilla's main scene is 30, the sea plane itself (1.0 rip, Scenes\main.unity
        // ClutterSystem block: "m_waterLevel: 30"; 27 is only the C# field default the scene overrides). The old 3 m gap put every
        // snap-to-water lily 3 m UNDER our surface (inside a 1.9 m river's bed: never seen) and moved every underwater band 3 m deep.
        internal const float VanillaClutterGap = 0f;
        private const float SeaSheetGate = 30.3f;
        private const float MinDepth = 0.15f;
        // Probe rings around a dry patch centre (metres) and the points on each.
        private static readonly float[] ShoreRings = { 6f, 12f };
        private const int RingPoints = 8;

        private static float s_saved;
        private static bool s_mutated;

        // 0.2.1072 ([seasons], Fire 10-26: "were not ... applying our water clutter like vass or lillies"; lilies in ONE pond only):
        // vanilla's underwater bands are fixed against the 3 m gap, so the BED must be 3.2-4.5 m under the surface for lilies
        // (alt -1.5..-0.2) and 3.1-4.0 m for vass (-1..-0.1). Our carves are shallower: river default 3.3 m - 0.3 freeboard =
        // ~3.0 m of water, pools 0.35 x radius (min 1.5 m), so only a big pool reached a band. While a patch sits on OUR water,
        // underwater bands (m_maxAlt < 0) accept any bed from MinWideDepth under the surface down to their own deepest (their
        // min alt is kept); the gap (lily snap height) and every dry-land band stay vanilla. The spread builder reads the same rule
        // through WideMaxAlt with the UnderwaterWide flag it takes when the patch is queued.
        // 0.2.1086: with the gap 0 the bands are vanilla's own against our surface: vass (-1..-0.1) grows where the bed is 0.1-1 m
        // deep = the river's edges; underwater bands keep their vanilla max. Snap-to-water bands (lilies float on the surface, so
        // the bed depth only says "water here") take any bed down to WideMinAlt, so the deep middle of a river grows lilies too.
        internal const float MinWideDepth = 0.4f;
        internal const float WideMinAltSurface = -8f;
        internal static bool UnderwaterWide { get; private set; }
        private static readonly System.Collections.Generic.List<(ClutterSystem.Clutter c, float max, float min)> s_widened = new System.Collections.Generic.List<(ClutterSystem.Clutter, float, float)>();
        private static int s_widePatches;
        private static float s_wideLogAt;

        /// <summary>The max alt an underwater band uses on our water (the vanilla value elsewhere / for dry bands).</summary>
        // 0.2.1345 (Fire: "pads should be in the deeeper waters and the vass along the shallows/shoreline"): lilies 1-8 m (the 1 m edge is vanilla vass's deepest), vass back to vanilla 0.1-1 m. The -8 floor caught vass too because it is also snap-to-water.
        internal static bool IsLily(ClutterSystem.Clutter c) => c != null && c.m_prefab != null && c.m_prefab.name == global::VerdantsAscent.Water.Core.FatWaterLife.LilyPrefab;
        internal static float WideMaxAlt(ClutterSystem.Clutter c) => IsLily(c) ? -1f : c != null && c.m_maxAlt < 0f ? Mathf.Max(c.m_maxAlt, VanillaClutterGap - MinWideDepth) : (c != null ? c.m_maxAlt : 0f);

        /// <summary>The min alt a band uses on our water: surface (snap-to-water) bands reach any depth down to WideMinAltSurface.</summary>
        internal static float WideMinAlt(ClutterSystem.Clutter c) => IsLily(c) ? Mathf.Min(c.m_minAlt, WideMinAltSurface) : (c != null ? c.m_minAlt : 0f);

        // 0.2.1090 (Fire, shot 528: "lillypads either need to rise and fall with the water or sit just tall enough that the water
        // rising and falling doesnt cover them"). The inland water lifts its own vertices in the shader (FiresInlandWater Vert:
        // wpos.y += SwellAt(..).x * amp * att, SwellAt h = sin(a1) + 0.5 sin(a2) so |h| <= 1.5; amp = _SwellAmp x max(1 + biome or
        // class swell multiplier - 1, 0); att <= 1). Lilies are static clutter batches and cannot follow it per frame, so they sit
        // that bound plus 3 cm above the surface. The big-water ocean wave (bw = big x (1 - flow)) is not in the bound: it is off on
        // flowing water and only grows on big, deep bodies, where a lily band 0.8 m up would float in the air.
        // 0.2.1094: the clearance is the cfg "Lily height offset (m)" (default 0.07; was a fixed 0.03 - Fire shot 543: pads still dipped).
        internal static float LilyClearance => global::VerdantsAscent.Water.Core.FiresInlandWaterConfig.LilyHeightOffset != null ? global::VerdantsAscent.Water.Core.FiresInlandWaterConfig.LilyHeightOffset.Value : 0.07f;
        private static float s_liftLogged = float.NaN;

        internal static float LilyLift()
        {
            var a = global::VerdantsAscent.Water.Core.FiresInlandWaterConfig.SwellAmp;
            float amp = a != null ? a.Value : 0.06f;
            float mult = global::VerdantsAscent.Water.Core.InlandLookConfig.MaxSwellMultiplier();
            float swell = 1.5f * amp * mult;
            float lift = swell + LilyClearance;
            if (!Mathf.Approximately(lift, s_liftLogged))
            {
                s_liftLogged = lift;
                FiresLogger.LogInfo($"[WaterLife] lily height = surface + {lift:0.000} m (swell amp {swell:0.000} m, from FiresInlandWater 'Swell amplitude (m)' {amp:0.000} x 1.5 crest x biome/class swell multiplier {mult:0.00}, + {LilyClearance:0.00} m clearance)");
            }
            return lift;
        }

        // The water this patch was moved to, or NaN; read by ClutterSpread while the patch is queued.
        internal static float Surface { get; private set; } = float.NaN;

        internal static void Apply(ClutterSystem __instance, Vector2Int patchID)
        {
            s_mutated = false;
            Surface = float.NaN;
            UnderwaterWide = false;
            // No hydrology needed: tool ponds count too, and Wet asks the generated field first.
            if (!FiresVegWaterEcology.Enabled) return;
            var c = __instance.GetVegPatchCenter(patchID);
            if (!PatchWaterMemo(patchID, c, out float lvl)) return;

            s_saved = __instance.m_waterLevel;
            // Keep vanilla's DESIGN gap between the sea plane and this constant (30 - 27 = 3 m), so a band that sits at
            // the shoreline in vanilla sits at ours, not the live value's gap. Galaxies' tide moves
            // m_waterLevel with the sea (it read 25.15, a 4.85 m gap), and an inland pond has no tide, so the lily band
            // drifted 1.85 m deeper than any pond is cut.
            __instance.m_waterLevel = lvl - VanillaClutterGap;
            Surface = lvl;
            s_mutated = true;
            UnderwaterWide = true;
            s_widened.Clear();
            if (__instance.m_clutter != null)
                foreach (var cl in __instance.m_clutter)
                    if (cl != null && cl.m_maxAlt < 0f) { s_widened.Add((cl, cl.m_maxAlt, cl.m_minAlt)); float mx = WideMaxAlt(cl), mn = WideMinAlt(cl); cl.m_maxAlt = mx; cl.m_minAlt = mn; }
            s_widePatches++;
            if (Time.unscaledTime >= s_wideLogAt)
            {
                s_wideLogAt = Time.unscaledTime + 30f;
                if (FatDiag.WaterClutter) FiresLogger.LogDebug($"[WaterLife] clutter: {s_widePatches} patch(es) on our water so far; {s_widened.Count} underwater band(s) (lilies, vass ...) take beds from {MinWideDepth:0.0} m deep (latest at ({c.x:0}, {c.z:0}), water y {lvl:0.0})");
            }
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogInfo($"[WaterLife] clutter: the grass patch at ({c.x:0}, {c.z:0}) grows against water at y {lvl:0.0} "
                + $"(clutter level {__instance.m_waterLevel:0.0}); first of this session.");
        }

        private static bool s_logged;

        internal static void Restore(ClutterSystem __instance)
        {
            if (!s_mutated) return;
            s_mutated = false;
            Surface = float.NaN;
            UnderwaterWide = false;
            foreach (var (cl, max, min) in s_widened) if (cl != null) { cl.m_maxAlt = max; cl.m_minAlt = min; }
            s_widened.Clear();
            __instance.m_waterLevel = s_saved;
        }

        // A client learns its generated water late (the hydrology solve runs on a worker
        // thread), and the grass already built around it keeps the sea-level bands until each patch happens to rebuild.
        // Called every frame from ClutterSpread's LateUpdate postfix: the first frame the water is queryable, the grass
        // around the camera is rebuilt once, so the shore and lake patches grow against it.
        private static bool s_waterWasKnown;

        internal static void RebuildWhenWaterKnown(ClutterSystem cs)
        {
            if (s_waterWasKnown || cs == null || !FiresVegWaterEcology.Enabled || !GenWaterField.SurfaceReady) return;
            s_waterWasKnown = true;
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 at = cam.transform.position;
            cs.ResetGrass(at, cs.m_distance + cs.m_grassPatchSize);
            FiresLogger.LogInfo($"[WaterLife] clutter: generated water became known; grass within {cs.m_distance + cs.m_grassPatchSize:0} m "
                + $"of ({at.x:0}, {at.z:0}) rebuilt so it grows against it.");
        }

        // A new world in the same session learns its water again.
        internal static void ForgetWater() => s_waterWasKnown = false;

        // The generated water under the patch centre, else the highest water on the shore rings around it.
        // 0.2.1089 (FDT, live 1088: the GenerateVegPatch hook ran 372 times a second): a dry patch asked TryWaterAt 17 times
        // (its centre, then 2 shore rings of 8), each a long downward raycast plus a surface probe, on every rebuild of that
        // patch. The answer per patch is kept for PatchMemoSeconds; a water edit resets the grass anyway, and a stale "dry"
        // lasts at most that long.
        private const float PatchMemoSeconds = 5f;
        private static readonly System.Collections.Generic.Dictionary<long, float> s_patchWater = new System.Collections.Generic.Dictionary<long, float>();   // NaN = dry
        private static float s_patchWaterUntil;
        private static int s_memoHits, s_memoComputed;

        // 0.2.1346: a river stroke closing: its water is real now, and a kept "dry" would plant its one grass pass on it.
        internal static void ForgetPatchWater() { s_patchWater.Clear(); s_patchWaterUntil = 0f; }

        private static bool PatchWaterMemo(Vector2Int patchID, Vector3 c, out float level)
        {
            float now = Time.time;
            if (now >= s_patchWaterUntil) { s_patchWater.Clear(); s_patchWaterUntil = now + PatchMemoSeconds; }
            long key = ((long)patchID.x << 32) ^ (uint)patchID.y;
            if (s_patchWater.TryGetValue(key, out level)) { s_memoHits++; return !float.IsNaN(level); }
            s_memoComputed++;
            bool wet = TryPatchWater(c, out level);
            s_patchWater[key] = wet ? level : float.NaN;
            return wet;
        }

        // For the [ClutterGate] hook line; resets the counts.
        internal static string TakeMemoStats()
        {
            string s = $"{s_memoHits} hit(s) / {s_memoComputed} computed (each computed dry patch = up to 17 water probes)";
            s_memoHits = s_memoComputed = 0;
            return s;
        }

        internal static bool TryPatchWater(Vector3 c, out float level)
        {
            if (Wet(c.x, c.z, out level)) return true;
            level = float.MinValue;
            foreach (float r in ShoreRings)
                for (int k = 0; k < RingPoints; k++)
                {
                    float a = k * (2f * Mathf.PI / RingPoints);
                    if (Wet(c.x + Mathf.Cos(a) * r, c.z + Mathf.Sin(a) * r, out float l) && l > level) level = l;
                }
            return level > float.MinValue;
        }

        // FatWaterChecks.TryWaterAt: the generated field, else tool ponds and other FAT water over solid ground.
        private static bool Wet(float x, float z, out float level)
            => VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x, z, MinDepth, false, out level, out _);
    }
}
