using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Terrain.Performance;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1413 FarLands #2 stage 2 (Tools\SPEC_FAT_FarForestPlacement.md §2): vanilla's ZoneSystem.PlaceVegetation (ZoneSystem.cs:746,
    // assembly_valheimREF1.0.15) replayed for the far KINDS only. Vanilla seeds every m_vegetation entry on its own
    // (seed + zone.x*4271 + zone.y*9187 + prefab hash), so replaying only our entries is exact.
    //
    // MAIN-THREAD PATH (this file): the real UnityEngine.Random (state saved and restored, as vanilla does) and the REAL ZoneSystem /
    // Heightmap calls vanilla makes (GetGroundData, IsBlocked, GetStaticSolidHeight, GetTerrainDelta, InsideClearArea, HaveBiome,
    // GetVegetationMask, GetOceanDepth), so every FAT Harmony patch on them applies here exactly as in the real pass, plus FAT's two
    // transpiler helpers called at the same step (FiresVegWaterEcology.EffectiveWaterY, FatUnderWaterPlacement.FilterDecision) and
    // FAT's rule prefixes (VegetationDensity / WaterEcology Apply + Restore, ZoneModeStartCave.KeepMouthClear). One implementation.
    // It needs the zone's Heightmap (a loaded zone). Far zones (no heightmap, no collider) get their ground in the next stage; the
    // worker path (FarRng) after that. Each placed instance consumes the census's Awake words (FarForest.AwakeWords).
    internal static class FarReplay
    {
        internal struct Tree
        {
            public int Kind;           // index into FarForest.Kinds
            public Vector3 Pos;
            public Quaternion Rot;
            public float Scale;
            public bool GroundTilt;
        }

        internal sealed class Stats { public int Entries, Attempts, Placed, NoHmap, AwakeUnknown; }

        private static Func<ZoneSystem, Vector3, float, object[]> s_terrainDelta;
        private static MethodInfo s_terrainDeltaMi, s_insideClearMi;
        private static Type s_clearAreaType;
        private static ConstructorInfo s_clearAreaCtor;
        private static FieldInfo s_clearCenter, s_clearRadius;
        private static bool s_bound, s_bindFailed;

        private static bool Bind()
        {
            if (s_bound) return true;
            if (s_bindFailed) return false;
            s_terrainDeltaMi = AccessTools.Method(typeof(ZoneSystem), "GetTerrainDelta", new[] { typeof(Vector3), typeof(float), typeof(float).MakeByRefType(), typeof(Vector3).MakeByRefType() });
            s_clearAreaType = AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
            s_insideClearMi = s_clearAreaType == null ? null : AccessTools.Method(typeof(ZoneSystem), "InsideClearArea");
            s_clearAreaCtor = s_clearAreaType == null ? null : AccessTools.Constructor(s_clearAreaType, new[] { typeof(Vector3), typeof(float) });
            if (s_terrainDeltaMi == null || s_insideClearMi == null || s_clearAreaCtor == null)
            {
                s_bindFailed = true;
                FiresLogger.LogInfo($"[FarForest] replay stands down: vanilla members missing (GetTerrainDelta {(s_terrainDeltaMi != null)}, InsideClearArea {(s_insideClearMi != null)}, ClearArea {(s_clearAreaCtor != null)}).");
                return false;
            }
            s_bound = true;
            return true;
        }

        // The zone's clear areas as vanilla's PlaceLocations + FAT's prefix build them: the zone's location (when m_clearArea), its
        // position snapped by the real GetGroundData, radius = m_exteriorRadius; then ZoneModeStartCave.KeepMouthClear.
        private static IList BuildClearAreas(ZoneSystem zs, Vector2s zone, Vector3 zoneCenter)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(s_clearAreaType));
            if (zs.m_locationInstances.TryGetValue(zone, out var li) && li.m_location != null && li.m_location.m_clearArea)
            {
                Vector3 pos = li.m_position;
                zs.GetGroundData(ref pos, out _, out _, out _, out _);
                if (li.m_location.m_snapToWater) pos.y = 30f;
                list.Add(s_clearAreaCtor.Invoke(new object[] { pos, li.m_location.m_exteriorRadius }));
            }
            Zones.ZoneModeStartCave.KeepMouthClear(zoneCenter, list);
            return list;
        }

        private static unsafe void WriteState(FarRng r)
        {
            UnityEngine.Random.State st = default;
            int* p = (int*)&st;
            p[0] = unchecked((int)r.X); p[1] = unchecked((int)r.Y); p[2] = unchecked((int)r.Z); p[3] = unchecked((int)r.W);
            UnityEngine.Random.state = st;
        }

        // Steps UnityEngine.Random by n words (FarRng's transition is proven by the self-test's end-state checks).
        private static void SkipWords(int n)
        {
            if (n <= 0) return;
            FarRngSelfTest.ReadState(out int a, out int b, out int c, out int d);
            var r = FarRng.FromState(a, b, c, d);
            for (int i = 0; i < n; i++) r.NextU32();
            WriteState(r);
        }

        // Main thread. hmap = the zone's own Heightmap (the one vanilla passes). Appends the zone's far-kind trees to `into`.
        internal static bool ReplayZoneMain(Vector2s zone, Heightmap hmap, List<Tree> into, Stats st)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || hmap == null || WorldGenerator.instance == null || !Bind()) return false;
            if (FatSuperFlat.On) return false;   // super flat places only island items (FatSuperFlat.NoVegetation); not replayed yet
            var kindOf = FarForest.KindIndexByHash;
            if (kindOf.Count == 0) return false;
            Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
            var saved = UnityEngine.Random.state;
            ZoneSystem_PlaceVegetation_Density.Apply(zs);
            ZoneSystem_PlaceVegetation_WaterEcology.Apply(zs);
            try
            {
                IList clear = BuildClearAreas(zs, zone, zoneCenter);
                int seed = WorldGenerator.instance.GetSeed();
                var tempVeg = new List<float>();
                // First m_vegetation index of every enabled prefab: a vegetation object blocks a later entry's IsBlocked (it stood there
                // during generation), never an earlier one's.
                s_firstEntry.Clear();
                for (int i = 0; i < zs.m_vegetation.Count; i++)
                {
                    var v = zs.m_vegetation[i];
                    if (v == null || v.m_prefab == null || !v.m_enable) continue;
                    int h = v.m_prefab.name.GetStableHashCode();
                    if (!s_firstEntry.ContainsKey(h)) s_firstEntry[h] = i;
                }
                for (int i = 0; i < zs.m_vegetation.Count; i++)
                {
                    var veg = zs.m_vegetation[i];
                    if (veg == null || veg.m_prefab == null) continue;
                    if (!kindOf.TryGetValue(veg.m_prefab.name.GetStableHashCode(), out int kind)) continue;
                    if (!veg.m_enable || !hmap.HaveBiome(veg.m_biome)) continue;
                    st.Entries++;
                    int before = into.Count;
                    ReplayEntry(zs, veg, i, kind, zone, zoneCenter, seed, clear, tempVeg, into, before, st);
                }
            }
            finally
            {
                ZoneSystem_PlaceVegetation_Density.Restore(zs);
                ZoneSystem_PlaceVegetation_WaterEcology.Restore(zs);
                UnityEngine.Random.state = saved;
            }
            return true;
        }

        private static readonly Dictionary<int, int> s_firstEntry = new Dictionary<int, int>();
        private static readonly RaycastHit[] s_hits = new RaycastHit[64];

        // Vanilla's IsBlocked (a ray from p.y + 2000 down 10000 m on m_blockRayMask) as it was DURING generation, asked of a zone that
        // is already generated: every hit counts except vegetation that did not stand yet, i.e. a vegetation prefab whose first entry
        // comes after this one, or this entry's own prefab unless the replay already placed that instance (within 0.1 m).
        // Locations and anything that is not vegetation block, as they did.
        private static bool IsBlockedAtGeneration(ZoneSystem zs, Vector3 p, int entryIndex, int entryHash, List<Tree> into, int entryStart)
        {
            Vector3 o = p; o.y += 2000f;
            int n = Physics.RaycastNonAlloc(o, Vector3.down, s_hits, 10000f, zs.m_blockRayMask);
            for (int i = 0; i < n; i++)
            {
                var col = s_hits[i].collider;
                if (col == null) continue;
                var nv = col.GetComponentInParent<ZNetView>();
                var zdo = nv != null ? nv.GetZDO() : null;
                if (zdo == null) return true;   // not a networked object: a location part / static: stood there
                int h = zdo.GetPrefab();
                if (!s_firstEntry.TryGetValue(h, out int first)) return true;   // not vegetation (location piece etc.)
                if (first < entryIndex) return true;
                if (h != entryHash) continue;   // a later entry's vegetation: not there yet
                Vector3 zp = zdo.GetPosition();
                for (int t = entryStart; t < into.Count; t++)
                    if ((into[t].Pos - zp).sqrMagnitude <= 0.01f) return true;   // placed earlier in this entry's loop
            }
            return false;
        }

        private static void ReplayEntry(ZoneSystem zs, ZoneSystem.ZoneVegetation veg, int entryIndex, int kind, Vector2s zoneID, Vector3 zoneCenterPos, int seed,
                                        IList clearAreas, List<float> tempVeg, List<Tree> into, int entryStart, Stats st)
        {
            UnityEngine.Random.InitState(seed + zoneID.x * 4271 + zoneID.y * 9187 + veg.m_prefab.name.GetStableHashCode());
            int num2 = 1;
            if (veg.m_max < 1f)
            {
                if (UnityEngine.Random.value > veg.m_max) return;
            }
            else
                num2 = UnityEngine.Random.Range((int)veg.m_min, (int)veg.m_max + 1);
            bool flag1 = veg.m_prefab.GetComponent<ZNetView>() != null;
            float num3 = Mathf.Cos((float)Math.PI / 180f * veg.m_maxTilt);
            float num4 = Mathf.Cos((float)Math.PI / 180f * veg.m_minTilt);
            float num5 = 32f - veg.m_groupRadius;
            tempVeg.Clear();
            int num6 = veg.m_forcePlacement ? num2 * 50 : num2;
            int num7 = 0;
            int hash = veg.m_prefab.name.GetStableHashCode();
            int awake = 0;
            if (flag1 && FarForest.AwakeWords.TryGetValue(hash, out int w)) awake = Math.Max(0, w); else if (flag1) st.AwakeUnknown++;
            for (int index1 = 0; index1 < num6; ++index1)
            {
                st.Attempts++;
                Vector3 vector3 = new Vector3(UnityEngine.Random.Range(zoneCenterPos.x - num5, zoneCenterPos.x + num5), 0.0f, UnityEngine.Random.Range(zoneCenterPos.z - num5, zoneCenterPos.z + num5));
                BiomeSector biomeSector = WorldGenerator.instance.GetBiomeSector(vector3);
                if ((veg.AltBiomeParent == null || biomeSector.AltBiomes.Any(x => x.m_name == veg.AltBiomeParent)) && !biomeSector.AltBiomes.Any(x => x.m_blockVegetationNames.Contains(veg.m_name)))
                {
                    int num8 = UnityEngine.Random.Range(veg.m_groupSizeMin, veg.m_groupSizeMax + 1);
                    bool flag2 = false;
                    for (int index2 = 0; index2 < num8; ++index2)
                    {
                        Vector3 p = index2 == 0 ? vector3 : RandomPointInRadius(vector3, veg.m_groupRadius);
                        float y = UnityEngine.Random.Range(0, 360);
                        float num9 = UnityEngine.Random.Range(veg.m_scaleMin, veg.m_scaleMax);
                        float x = UnityEngine.Random.Range(-veg.m_randTilt, veg.m_randTilt);
                        float z = UnityEngine.Random.Range(-veg.m_randTilt, veg.m_randTilt);
                        if (veg.m_blockCheck && IsBlockedAtGeneration(zs, p, entryIndex, hash, into, entryStart)) continue;
                        zs.GetGroundData(ref p, out Vector3 normal1, out Heightmap.Biome biome, out Heightmap.BiomeArea biomeArea, out Heightmap hmap1);
                        if ((veg.m_biome & biome) == Heightmap.Biome.None || (veg.m_biomeArea & biomeArea) == 0) continue;
                        if (veg.m_snapToStaticSolid && zs.GetStaticSolidHeight(p, out float height, out Vector3 normal2)) { p.y = height; normal1 = normal2; }
                        float num10 = p.y - FiresVegWaterEcology.EffectiveWaterY(ref p);   // FAT's altitude port (vanilla: p.y - 30)
                        if (num10 < veg.m_minAltitude || num10 > veg.m_maxAltitude) continue;
                        if (!veg.m_minVegetation.Equals(veg.m_maxVegetation))
                        {
                            if (hmap1 == null) { st.NoHmap++; continue; }
                            float vm = hmap1.GetVegetationMask(p);
                            if (vm > veg.m_maxVegetation || vm < veg.m_minVegetation) continue;
                        }
                        if (!veg.m_minOceanDepth.Equals(veg.m_maxOceanDepth))
                        {
                            if (hmap1 == null) { st.NoHmap++; continue; }
                            float od = hmap1.GetOceanDepth(p);
                            if (od < veg.m_minOceanDepth || od > veg.m_maxOceanDepth) continue;
                        }
                        if (normal1.y < num3 || normal1.y > num4) continue;
                        if (veg.m_terrainDeltaRadius > 0f)
                        {
                            var args = new object[] { p, veg.m_terrainDeltaRadius, 0f, Vector3.zero };
                            s_terrainDeltaMi.Invoke(zs, args);
                            float delta = (float)args[2];
                            if (delta > veg.m_maxTerrainDelta || delta < veg.m_minTerrainDelta) continue;
                        }
                        if (veg.m_minDistanceFromCenter > 0f || veg.m_maxDistanceFromCenter > 0f)
                        {
                            float num11 = Utils.LengthXZ(p);
                            if (veg.m_minDistanceFromCenter > 0f && num11 < veg.m_minDistanceFromCenter || veg.m_maxDistanceFromCenter > 0f && num11 > veg.m_maxDistanceFromCenter) continue;
                        }
                        if (veg.m_inForest)
                        {
                            float ff = WorldGenerator.GetForestFactor(p);
                            if (ff < veg.m_forestTresholdMin || ff > veg.m_forestTresholdMax) continue;
                        }
                        if (veg.m_surroundCheckVegetation)
                        {
                            if (hmap1 == null) { st.NoHmap++; continue; }
                            float num12 = 0f;
                            for (int index3 = 0; index3 < veg.m_surroundCheckLayers; ++index3)
                            {
                                float num13 = (float)(index3 + 1) / veg.m_surroundCheckLayers * veg.m_surroundCheckDistance;
                                for (int index4 = 0; index4 < 6; ++index4)
                                {
                                    float f = (float)(index4 / 6.0 * 3.1415927410125732 * 2.0);
                                    float vm = hmap1.GetVegetationMask(p + new Vector3(Mathf.Sin(f) * num13, 0f, Mathf.Cos(f) * num13));
                                    float num14 = (float)((1.0 - num13) / (veg.m_surroundCheckDistance * 2.0));
                                    num12 += vm * num14;
                                }
                            }
                            tempVeg.Add(num12);
                            if (tempVeg.Count >= 10)
                            {
                                float num15 = tempVeg.Max();
                                float num16 = tempVeg.Average();
                                float num17 = num16 + (num15 - num16) * veg.m_surroundBetterThanAverage;
                                if (num12 < num17) continue;
                            }
                            else continue;
                        }
                        bool inside = (bool)s_insideClearMi.Invoke(zs, new object[] { clearAreas, p });
                        if (FatUnderWaterPlacement.FilterDecision(inside, veg, p)) continue;   // FAT's transpiled filter, same step
                        if (veg.m_snapToWater) p.y = FiresVegWaterEcology.EffectiveWaterY(ref p);
                        p.y += veg.m_groundOffset;
                        Quaternion rotation;
                        bool groundTilt = false;
                        if (veg.m_chanceToUseGroundTilt > 0f && UnityEngine.Random.value <= veg.m_chanceToUseGroundTilt)
                        {
                            Quaternion q = Quaternion.Euler(0f, y, 0f);
                            rotation = Quaternion.LookRotation(Vector3.Cross(normal1, q * Vector3.forward), normal1);
                            groundTilt = true;
                        }
                        else
                            rotation = Quaternion.Euler(x, y, z);
                        into.Add(new Tree { Kind = kind, Pos = p, Rot = rotation, Scale = num9, GroundTilt = groundTilt });
                        st.Placed++;
                        if (flag1) SkipWords(awake);   // the prefab's Awake draws inside the real Instantiate (census)
                        flag2 = true;
                    }
                    if (flag2) ++num7;
                    if (num7 >= num2) break;
                }
            }
        }

        // Vanilla's private ZoneSystem.GetRandomPointInRadius, same draws (ZoneSystem.cs:739).
        private static Vector3 RandomPointInRadius(Vector3 center, float radius)
        {
            float f = (float)(UnityEngine.Random.value * 3.1415927410125732 * 2.0);
            float num = UnityEngine.Random.Range(0f, radius);
            return center + new Vector3(Mathf.Sin(f) * num, 0f, Mathf.Cos(f) * num);
        }
    }
}
