using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1127 (Fire 10-27): a fire melts a BULLSEYE in the voxel snow - "about a 2 m ring around the campfire of dry dirt... another
    // roughly 1 m ring of wet dirt... another 1 m ring of slush sloping up into the deep snow" with a carved wall at the edge ("All
    // rings scale"). The shader (FiresVoxelHeightmap.shader FiresHeatBands) reads FTS's heat sources (_FiresHeat xyz + w = the Heat
    // EffectArea's horizontal extent, FiresPieceSnowDriver.LateUpdate) and scales each fire by w / _FiresHeatCampfireR, so a
    // campfire's outer edge is 4 m. FAT sets _FiresHeatCampfireR from fire_pit's Heat EffectArea with FTS's own measure; Bands()
    // is the C# replica the fire test logs.
    internal static class FatFireBullseye
    {
        private static readonly int s_campfireRId = Shader.PropertyToID("_FiresHeatCampfireR");
        internal static float CampfireR = 4f;
        // 0.2.1377 (Fire: "fire ring needs to blend better and not be perfect circles"): replaced by shares of the outer edge, matching FiresHeatBands; a campfire's blend 2 x 0.64 m, wall 2 x 0.24 m
        internal const float Wall = 0.25f;   // the mesh refinement's snap rings either side of the wall's middle (VoxelGTessellation)
        internal const float BlendShare = 0.16f, WallShare = 0.06f, WobbleShare = 0.30f;

        // The C# copy of the shader's FiresRingHash + FiresRingNoise: world position only, add + multiply + frac, so it gives the wave the GPU draws.
        private static float Frac(float v) => v - Mathf.Floor(v);
        private static float RingHash(float x, float z)
        {
            float qx = Frac(x * 0.1031f), qy = Frac(z * 0.1031f), qz = Frac(x * 0.1031f);
            float dot = qx * (qy + 33.33f) + qy * (qz + 33.33f) + qz * (qx + 33.33f);
            qx += dot; qy += dot; qz += dot;
            return Frac((qx + qy) * qz);
        }
        private static float RingNoise(float x, float z)
        {
            float ix = Mathf.Floor(x), iz = Mathf.Floor(z), fx = x - ix, fz = z - iz;
            fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
            return Mathf.Lerp(Mathf.Lerp(RingHash(ix, iz), RingHash(ix + 1f, iz), fx), Mathf.Lerp(RingHash(ix, iz + 1f), RingHash(ix + 1f, iz + 1f), fx), fz);
        }

        // The distance every ring band reads: true distance moved by the noise, up to +-0.15 x outer.
        internal static float RingDistance(float x, float z, Vector4 h, float outer)
        {
            float wobble = RingNoise(x * 0.45f, z * 0.45f) * 0.67f + RingNoise(x * 1.1f, z * 1.1f) * 0.33f - 0.5f;
            return new Vector2(x - h.x, z - h.z).magnitude + wobble * WobbleShare * outer;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Patch_SceneAwake
        {
            private static void Postfix() { try { Bind(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[FireMelt] bail: campfire radius at ZNetScene.Awake threw: {ex.Message}"); } }
        }

        // FTS's measure (FiresPieceSnowDriver: max(collider.bounds.extents.x, .z), min 0.5, default 3) on the prefab's Heat area.
        internal static float HeatRadius(string prefabName, out string why)
        {
            why = "prefab not found";
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (go == null) return -1f;
            foreach (var a in go.GetComponentsInChildren<EffectArea>(true))
            {
                if ((a.m_type & EffectArea.Type.Heat) == 0) continue;
                var col = a.GetComponent<Collider>();
                float r = 3f;
                Vector3 s = a.transform.lossyScale;
                if (col is SphereCollider sc) r = sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                else if (col is BoxCollider bc) r = Mathf.Max(bc.size.x * Mathf.Abs(s.x), bc.size.z * Mathf.Abs(s.z)) * 0.5f;
                else if (col is CapsuleCollider cc) r = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                else if (col != null) r = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);
                why = col != null ? col.GetType().Name : "no collider (FTS default 3)";
                return Mathf.Max(0.5f, r);
            }
            why = "no Heat EffectArea";
            return -1f;
        }

        // 0.2.1128 (agreed with [seasons]): FTS 0.3.255+ publishes _FiresHeatCampfireR itself (one source of truth, the same
        // collider-shape measure for every live heat source). FAT only READS it when FTS is loaded; it writes the same value only as a
        // fallback when FTS is absent (FAT ships to players without FTS and the shader still needs a campfire R).
        private const string FtsGuid = "com.Fire.FiresTossinShade";
        internal static bool FtsLoaded => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FtsGuid);

        private static void Bind()
        {
            float r = HeatRadius("fire_pit", out string why);
            if (r > 0.01f) CampfireR = r;
            // 0.2.1405: FAT is the one writer of the heat globals (FatHeatSources), with or without FTS.
            Shader.SetGlobalFloat(s_campfireRId, CampfireR);
            FiresLogger.LogInfo($"[FireMelt] campfire R set by FAT = {CampfireR:0.00} m{(FtsLoaded ? " (FTS reads FAT's value)" : "")}");
            float hearth = HeatRadius("hearth", out _), bonfire = HeatRadius("bonfire", out _);
            FiresLogger.LogInfo($"[FireMelt] bullseye: campfire heat R {CampfireR:0.00} m (fire_pit Heat area, {why}) = 4 m outer edge; "
                + $"hearth R {hearth:0.00} -> outer {Outer(hearth):0.0} m, bonfire R {bonfire:0.00} -> outer {Outer(bonfire):0.0} m (-1 = not found)");
        }

        // 0.2.1128 (round #26 top-down shot: a dark, stepped halo round the bullseye, ~1 m outside the carved edge, offset toward the
        // sun-away side): the bullseye height is a VERTEX displacement, and with no trail paint there (G 0.00 out to 12 m in the round's
        // readings) the 0.25 m refinement never ran, so the "0.25 m wall" was stretched across the 1 m base triangles - a jagged slope
        // stepping on the 1 m grid, up to ~1.4 m outside the edge, lit and shadowed as a slope. Ground edges within WallBand of a
        // heat source's outer edge now refine too (VoxelGTessellation), and chunks round a heat source re-mesh when the source set changes.
        internal const float WallBand = 1.5f;
        private static Vector4[] s_heat = new Vector4[0];
        private static int s_heatN;
        private static readonly System.Collections.Generic.List<Vector4> s_lastKey = new System.Collections.Generic.List<Vector4>();
        private static float s_tickAt;
        private static int s_remeshed;

        internal static bool HasHeat => s_heatN > 0;

        // 0.2.1197: the heat snapshot itself (Tick replaces the array whole, never writes into it), for the off-thread G refinement.
        internal static Vector4[] HeatSnapshot(out int n) { n = s_heatN; return s_heat; }

        // 0.2.1131: d - outer of the heat source whose wall is nearest (signed: < 0 inside), from the same snapshot as NearWall, for the
        // refinement wall snap. float.MaxValue when there's no source.
        internal static float WallField(float x, float z) => WallField(s_heat, s_heatN, x, z);
        internal static float WallField(Vector4[] heat, int heatN, float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 0; i < heatN; i++)
            {
                Vector4 h = heat[i];
                float outer = Outer(h.w);
                if (outer <= 0f) continue;
                float f = RingDistance(x, z, h, outer) - outer;
                if (Mathf.Abs(f) < Mathf.Abs(best)) best = f;
            }
            return best;
        }

        // 0.2.1132 (Fire: the white wispy clutter drawn over the rings): true inside the dry + wet bands (d <= 0.75 x outer) of any
        // heat source in the snapshot. FAT's clutter generator (ClutterSpreadPatches) refuses grass there; slush and snow keep theirs.
        internal static bool InMeltCore(Vector3 p)
        {
            for (int i = 0; i < s_heatN; i++)
            {
                Vector4 h = s_heat[i];
                float outer = Outer(h.w);
                if (outer <= 0f) continue;
                if (RingDistance(p.x, p.z, h, outer) <= 0.75f * outer) return true;
            }
            return false;
        }
        internal static int ClutterRefused;

        // Same inputs for every chunk built in a frame (the snapshot is taken by Tick), so two chunks sharing an edge agree.
        internal static bool NearWall(Vector3 v) => NearWall(s_heat, s_heatN, v);
        internal static bool NearWall(Vector4[] heat, int heatN, Vector3 v)
        {
            for (int i = 0; i < heatN; i++)
            {
                Vector4 h = heat[i];
                float outer = Outer(h.w);
                if (outer <= 0f) continue;
                if (Mathf.Abs(RingDistance(v.x, v.z, h, outer) - outer) <= WallBand) return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_HeatTick
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || Time.unscaledTime < s_tickAt) return;
                s_tickAt = Time.unscaledTime + 1f;
                try { Tick(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[FireMelt] bail: heat tick threw: {ex.Message}"); }
            }
        }

        private static void Tick()
        {
            int n = Mathf.Min(16, Mathf.RoundToInt(Shader.GetGlobalFloat("_FiresHeatCount")));
            var arr = n > 0 ? Shader.GetGlobalVectorArray("_FiresHeat") : null;
            var now = new System.Collections.Generic.List<Vector4>();
            for (int i = 0; arr != null && i < n && i < arr.Length; i++)
                if (arr[i].w > 0f) now.Add(new Vector4(Mathf.Round(arr[i].x * 4f) / 4f, Mathf.Round(arr[i].y * 4f) / 4f, Mathf.Round(arr[i].z * 4f) / 4f, Mathf.Round(arr[i].w * 10f) / 10f));
            bool same = now.Count == s_lastKey.Count;
            for (int i = 0; same && i < now.Count; i++) same = s_lastKey.Contains(now[i]);
            if (same) return;
            // re-mesh every loaded chunk touched by a source that appeared or went (old + new rings)
            var changed = new System.Collections.Generic.List<Vector4>();
            foreach (var h in now) if (!s_lastKey.Contains(h)) changed.Add(h);
            foreach (var h in s_lastKey) if (!now.Contains(h)) changed.Add(h);
            s_lastKey.Clear(); s_lastKey.AddRange(now);
            s_heat = now.ToArray(); s_heatN = s_heat.Length;
            int queued = 0;
            foreach (var chunk in VoxelWorld.AllChunks)
            {
                if (chunk == null) continue;
                float x0 = chunk.Key.x * VoxelChunk.CELLS, z0 = chunk.Key.z * VoxelChunk.CELLS, x1 = x0 + VoxelChunk.CELLS, z1 = z0 + VoxelChunk.CELLS;
                foreach (var h in changed)
                {
                    float reach = Outer(h.w) * (1f + WobbleShare * 0.5f) + WallBand + 1f;
                    float cx = Mathf.Clamp(h.x, x0, x1), cz = Mathf.Clamp(h.z, z0, z1);
                    if ((cx - h.x) * (cx - h.x) + (cz - h.z) * (cz - h.z) <= reach * reach) { VoxelMeshPipeline.EnqueueRemesh(chunk); queued++; break; }
                }
            }
            s_remeshed += queued;
            // 0.2.1132: re-roll the grass round every source that came or went, so the melt core clears (and regrows when it goes out).
            var cs = ClutterSystem.instance;
            if (cs != null)
            {
                foreach (var h in changed) cs.ResetGrass(new Vector3(h.x, 0f, h.z), Outer(h.w) * (1f + WobbleShare * 0.5f) + 2f);
                FiresLogger.LogDebug($"[FireMelt] clutter cleared round {s_heatN} heat source(s) (re-rolled round {changed.Count} changed; {ClutterRefused} grass point(s) refused in melt cores so far)");
            }
            FiresLogger.LogDebug($"[FireMelt] heat sources changed ({changed.Count} source(s) came or went, {s_heatN} live): {queued} chunk(s) re-meshed so the bullseye wall is on the 0.25 m refined mesh ({s_remeshed} this session)");
        }

        // Albedo source per band (the 0.2.1128 shader colours), for the test readings.
        internal static string AlbedoSource(string band)
            => band == "dry" ? "dirt (L3 dug dirt)" : band == "wet" ? "wet dirt (L3 x0.6)" : band == "slush" ? "slush (0.5 x L6.r grey snow, ~10% wet dirt, 30% cool blue tint, sheen 0.7, cool emissive lift)" : band == "wall" ? "snow wall" : "snow";

        // 0.2.1134: the shader's slush/ring albedo formula (FiresVoxelHeightmap.shader rev 1134) on NOMINAL texel values - the
        // textures can't be sampled from C#, so L6.r = 1.0 (snow) and the L3 dug dirt = (0.45, 0.33, 0.22); the readings show the
        // band mix lands snow-grey/blue in the slush, not warm. Before lighting (the fire's point light comes on top).
        private static readonly Vector3 NomDirt = new Vector3(0.45f, 0.33f, 0.22f);
        internal static Vector3 NominalAlbedo(float dry, float wet, float slush)
        {
            Vector3 snowA = Vector3.one * 0.5f, wetA = NomDirt * 0.6f;
            Vector3 slushA = Vector3.Lerp(snowA, wetA, 0.10f * 0.75f);   // the drift noise taken at its mean (0.5 + 0.25)
            float lum = slushA.x * 0.299f + slushA.y * 0.587f + slushA.z * 0.114f;
            slushA = Vector3.Lerp(slushA, new Vector3(0.78f, 0.92f, 1.15f) * lum, 0.30f);
            float sum = dry + wet + slush, wIn = Mathf.Clamp01(sum), wOut = 1f - wIn, inv = sum > 1e-4f ? wIn / sum : 0f;
            return snowA * wOut + (NomDirt * dry + wetA * wet + slushA * slush) * inv;
        }

        // 0.2.1131: the weighted mix at a point (the shader's partition), for the readings: "dirt a wet b slush c snow d".
        internal static string Mix(Vector3 p)
        {
            Weights(p, out float dry, out float wet, out float slush, out _, out _);
            float snow = Mathf.Max(0f, 1f - dry - wet - slush);
            Vector3 a = NominalAlbedo(dry, wet, slush);
            return $"dirt {dry:0.00} wet {wet:0.00} slush {slush:0.00} snow {snow:0.00}; albedo (nominal texels, before light) RGB {a.x:0.00},{a.y:0.00},{a.z:0.00}";
        }

        internal static float Outer(float heatR) => heatR > 0f ? 4f * heatR / Mathf.Max(CampfireR, 0.01f) : -1f;

        // Replica of the shader's FiresHeatBands (same sources, same edges): band weights + the cover and height multipliers.
        internal static string Bands(Vector3 p, out float cover, out float hgt, out float nearestOuter)
        {
            nearestOuter = Weights(p, out float dry, out float wet, out float slush, out cover, out hgt);
            // 0.2.1131: the band = the LARGEST weight (was thresholds at 0.5, which named every blend midpoint "wall"); "wall" only
            // where the carved outer wall is mid-rise.
            float snow = Mathf.Max(0f, 1f - dry - wet - slush);
            if (hgt > 0.36f && hgt < 0.99f && slush < 0.5f) return "wall";
            if (dry >= wet && dry >= slush && dry >= snow) return "dry";
            if (wet >= slush && wet >= snow) return "wet";
            if (slush >= snow) return "slush";
            return "snow";
        }

        // Replica of the shader's FiresHeatBands outputs; returns the nearest source's outer edge.
        internal static float Weights(Vector3 p, out float dry, out float wet, out float slush, out float cover, out float hgt)
        {
            dry = 0f; wet = 0f; slush = 0f; cover = 1f; hgt = 1f; float nearestOuter = 0f;
            try
            {
                int n = Mathf.Min(16, Mathf.RoundToInt(Shader.GetGlobalFloat("_FiresHeatCount")));
                var arr = n > 0 ? Shader.GetGlobalVectorArray("_FiresHeat") : null;
                float best = float.MaxValue;
                for (int i = 0; arr != null && i < n && i < arr.Length; i++)
                {
                    Vector4 h = arr[i];
                    if (h.w <= 0f) continue;
                    float outer = Outer(h.w), e1 = 0.5f * outer, e2 = 0.75f * outer;
                    float d = RingDistance(p.x, p.z, h, outer);
                    if (d < best) { best = d; nearestOuter = outer; }
                    float blend = BlendShare * outer, wall = WallShare * outer;
                    if (d > outer + wall) continue;
                    float pastE1 = S(e1 - blend, e1 + blend, d), pastE2 = S(e2 - blend, e2 + blend, d);
                    float wallT = S(outer - wall, outer + wall, d);
                    dry = Mathf.Max(dry, 1f - pastE1);
                    wet = Mathf.Max(wet, pastE1 * (1f - pastE2));
                    slush = Mathf.Max(slush, pastE2 * (1f - wallT));
                    cover = Mathf.Min(cover, pastE2);
                    float slushH = 0.35f * Mathf.Clamp01((d - e2) / Mathf.Max(outer - e2, 0.01f));
                    hgt = Mathf.Min(hgt, Mathf.Lerp(slushH, 1f, wallT));
                }
            }
            catch { }
            return nearestOuter;
        }

        private static float S(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }
    }
}
