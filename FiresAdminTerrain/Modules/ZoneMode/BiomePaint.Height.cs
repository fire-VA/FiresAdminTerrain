using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1373 (Fire: "change height too based off the biomes data and blend it cleanly"): a custom biome's circle also turns the land's
    // shape, in one place: the end of WorldGenerator.GetBiomeHeight, so heightmaps, placement and every GetHeight read one answer.
    // The target is GetBiomeHeight asked for the painted biome: Expand World's own patch applies its yaml there (terrain,
    // altitudeMultiplier, minimumAltitude and the rest), so FAT holds no copy of those rules.
    //  - Layers, oldest circle first: height = lerp(height, target, weight). Each weight is 0 at its circle's rim, so the ground
    //    outside a circle never changes and any two neighbouring paints meet without a step. A Restore circle's target is the world's.
    //  - The sea stays sea (the lead + Fire's plan, v1): ground at or under the water line never moves, a shore band fades in above
    //    it, and a target can't sink land under the water.
    //  - Deterministic from the list, so every peer builds the same ground. Paint-free worlds and cells pay one check.
    // The look stays sharp at the rim as before (BiomePaint.Ground); only the shape blends.
    internal static partial class BiomePaint
    {
        private const float DefaultHeightBand = 32f;
        private const float ShoreFloor = 1f, ShoreFull = 5f;    // metres over the water: no change at or below the floor, all of it from full
        private const float DefaultWaterLevel = 30f;

        private static float s_heightBand = DefaultHeightBand;  // the server's key; clients take it from the server (RPC_HeightBand)
        [ThreadStatic] private static int t_heightScope;
        [ThreadStatic] private static int t_noPaint;   // 0.2.1378: the land with paint left out, a converted world's map step kept (the proof lines' "world")
        [ThreadStatic] private static float t_memoX, t_memoZ, t_memoHeight;
        [ThreadStatic] private static Heightmap.Biome t_memoGround;
        [ThreadStatic] private static Color t_memoMask;
        [ThreadStatic] private static bool t_memoSet;
        private static long s_heightCalls, s_heightTicks;
        private static bool s_heightSaid;

        // While true, GetBiomeHeight answers the painted biome's own shape: a converted world's map step and this layer stay out.
        internal static bool InHeightScope => t_heightScope > 0;

        private static float HeightWeight(in Circle c, float wx, float wz)
        {
            float band = Mathf.Min(c.BlendWidth >= 0f ? c.BlendWidth : s_heightBand, c.R);
            float dx = wx - c.X, dz = wz - c.Z;
            if (dx * dx + dz * dz > c.R * c.R) return 0f;
            float inside = c.Rim(wx, wz, band);
            if (inside <= 0f) return 0f;
            if (band <= 0f || inside >= band) return 1f;
            float t = inside / band;
            return t * t * (3f - 2f * t);
        }

        private static float Smooth(float from, float to, float v)
        {
            float t = Mathf.Clamp01((v - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static float TargetHeight(WorldGenerator generator, Heightmap.Biome ground, float wx, float wz, bool preGeneration, bool riverPreDN, out Color mask)
        {
            if (t_memoSet && t_memoGround == ground && t_memoX == wx && t_memoZ == wz) { mask = t_memoMask; return t_memoHeight; }
            float height = generator.GetBiomeHeight(ground, wx, wz, out mask, preGeneration, riverPreDN);
            t_memoSet = true; t_memoGround = ground; t_memoX = wx; t_memoZ = wz; t_memoHeight = height; t_memoMask = mask;
            return height;
        }

        // The end of GetBiomeHeight on generated ground (Patch_WorldGenerator_GetBiomeHeight_FiresGen). Any thread.
        internal static void ApplyPaintHeight(WorldGenerator generator, float wx, float wz, bool preGeneration, bool riverPreDN, ref Color mask, ref float height)
        {
            var snapshot = s_snapshot;
            if (!snapshot.AnyHeight || t_heightScope > 0 || t_noPaint > 0) return;
            if (!snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return;
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : DefaultWaterLevel;
            float world = height;
            float land = Smooth(water + ShoreFloor, water + ShoreFull, world);
            if (land <= 0f) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            // Newest first in the list. A full-weight layer hides everything older, so start there.
            int start = -1;
            for (int i = 0; i < candidates.Length; i++)
            {
                var c = snapshot.Circles[candidates[i]];
                if ((!c.Height && c.Brush != Brush.Restore) || (c.Brush == Brush.Custom && c.Ground == Heightmap.Biome.None)) continue;
                float w = HeightWeight(c, wx, wz);
                if (w <= 0f) continue;
                if (c.Brush != Brush.Restore) w *= HoldAllows(c, wx, wz);
                if (w <= 0f) continue;
                start = i;
                if (w >= 1f) break;
            }
            if (start < 0) return;
            Color worldMask = mask;
            float final = world;
            Color finalMask = worldMask;
            bool changed = false;
            t_heightScope++;
            try
            {
                for (int i = start; i >= 0; i--)
                {
                    var c = snapshot.Circles[candidates[i]];
                    if ((!c.Height && c.Brush != Brush.Restore) || (c.Brush == Brush.Custom && c.Ground == Heightmap.Biome.None)) continue;
                    float w = HeightWeight(c, wx, wz) * land;
                    if (c.Brush != Brush.Restore) w *= HoldAllows(c, wx, wz);
                    if (w <= 0f) continue;
                    float target = world;
                    Color targetMask = worldMask;
                    if (c.Brush != Brush.Restore)
                    {
                        target = TargetHeight(generator, c.Ground, wx, wz, preGeneration, riverPreDN, out targetMask);
                        target = Mathf.Max(target, water + ShoreFloor);
                    }
                    final = Mathf.Lerp(final, target, w);
                    finalMask = Color.Lerp(finalMask, targetMask, w);
                    changed = true;
                }
            }
            finally { t_heightScope--; }
            if (changed)
            {
                height = final;
                mask = finalMask;
            }
            Interlocked.Increment(ref s_heightCalls);
            Interlocked.Add(ref s_heightTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }

        // A heightmap's terrain request the builder finished before this paint would be handed back unchanged.
        private static void DropReadyTerrain()
        {
            var builder = HeightmapBuilder.instance;
            if (builder == null || builder.m_lock == null) return;
            int dropped;
            lock (builder.m_lock)
            {
                dropped = builder.m_ready.Count;
                builder.m_ready.Clear();
            }
            if (dropped > 0) FiresLogger.LogDebug($"[BiomePaint] height paint changed: {dropped} finished terrain request(s) dropped so they build again (0.2.1373)");
        }

        private static bool AnyHeightIn(List<Circle> circles)
        {
            foreach (var c in circles) if (c.Height) return true;
            return false;
        }

        // Proof for the client and dedi logs: the same list has to give the same numbers on every peer. Debug only.
        private static void NoteHeightProof(in Circle c)
        {
            if (!FiresLogger.DebugEnabled || !c.Height || c.Ground == Heightmap.Biome.None) return;
            var generator = WorldGenerator.instance;
            if (generator == null) return;
            var sb = new System.Text.StringBuilder($"[BiomePaint] height proof: {c.Blend} round ({c.X:0},{c.Z:0}) r {c.R:0}, band {Mathf.Min(c.BlendWidth >= 0f ? c.BlendWidth : s_heightBand, c.R):0}, target from the yaml / world / final:");
            float[] shares = { 0f, 0.5f, 0.95f, 1.05f };   // along +X from the centre; on a drawn outline these are only sample spots
            foreach (float share in shares)
            {
                float wx = c.X + c.R * share, wz = c.Z;
                float final = generator.GetHeight(wx, wz);
                TestHeights(generator, c.Ground, wx, wz, out float world, out float target);
                sb.Append($" at {share * 100f:0} % {target:0.00} / {world:0.00} / {final:0.00};");
            }
            sb.Append(" (0.2.1373)");
            FiresLogger.LogDebug(sb.ToString());
        }

        private static void NoteHeightCost()
        {
            long calls = Interlocked.Exchange(ref s_heightCalls, 0L);
            long ticks = Interlocked.Exchange(ref s_heightTicks, 0L);
            if (calls == 0) return;
            FiresLogger.LogDebug($"[BiomePaint] height paint last minute: {calls:N0} sample(s) under a height circle, {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms in all threads (0.2.1373)");
        }

        // ── The blend width travels with the paint ──────────────────────────────
        // It shapes the ground, so every peer must use the server's. Sent ahead of the list.
        private const string RpcHeightBand = "FiresAdminTerrain_BiomePaintHeightBand";

        private static void ReadServerHeightBand()
        {
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintBlendWidth;
            s_heightBand = key != null ? Mathf.Clamp(key.Value, 0f, 96f) : DefaultHeightBand;
        }

        private static void SendHeightBand(long target)
        {
            if (ZRoutedRpc.instance == null || !AnyHeightIn(s_circles)) return;
            ReadServerHeightBand();
            var pkg = new ZPackage();
            PackHeightData(pkg);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcHeightBand, pkg);
        }

        private static void RPC_HeightBand(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (!FatNetGuard.FromServer(sender, "biome paint blend width")) return;
            UnpackHeightData(pkg);
        }
    }
}
