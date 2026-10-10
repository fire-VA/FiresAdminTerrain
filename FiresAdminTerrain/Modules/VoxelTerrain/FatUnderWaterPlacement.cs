using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Nothing grows or stands under FAT water but stumps, logs and rocks (0.2.686) ─────────────────────────────────────
    // Fire 10-01: "Our custom water should prevent things like trees and locations from spawning in it, only stumps logs
    // and rocks for vegetation should be able to sit under the water"; and, watching the tests, "a few small beech trees
    // and maybe some saplings in the water". One rule, by component first and name second, for three users:
    //   * generation: ZoneSystem.PlaceVegetation refuses a refused kind whose spot is under FAT water (Filter, spliced in
    //     right after vanilla's per-spot InsideClearArea test, so the spot is skipped exactly as a clear area skips it);
    //   * locations: WorldGenerator.GetTerrainDelta, vanilla's location suitability test, reports an impossible delta for
    //     an area overlapping FAT water, so no camp, house or dolmen is sited in a lake or pond;
    //   * the drowned sweep (VoxelZoneStreamer.DrownedVegTick) and waterlife_test step 4 clear / count the same kinds.
    // Kept under water: rocks (MineRock, MineRock5, rock/stone/cliff names), logs (TreeLog, *log*), stumps (*stub*,
    // *stump*) and water plants (lily, vass, reed, cattail, seaweed). Refused: TreeBase, a Destructible of the Tree type
    // (small beeches and birches are Destructible-only), Plant (saplings), Pickable, and bush/shrub/sapling names.
    // Anything else (spawners, pieces, drops, characters) is not vegetation and is left alone.
    // FAT water = the generated hydrology field above the sea sheet + tool water (WaterMeshCombiner disks).
    internal static class FatUnderWaterPlacement
    {
        internal const float SurfaceTolerance = 0.05f;
        private const float SeaSheetGate = 30.3f;
        private static readonly string[] KeepWords = { "rock", "stone", "cliff", "mine", "log", "stub", "stump", "lily", "lilies", "vass", "reed", "cattail", "seaweed" };
        private static readonly string[] RefuseWords = { "bush", "shrub", "sapling" };
        private static readonly Dictionary<int, bool> s_byPrefab = new Dictionary<int, bool>();

        // A prefab's verdict, cached by instance id (prefabs live for the session).
        internal static bool IsRefusedKind(GameObject prefab)
        {
            if (prefab == null) return false;
            int id = prefab.GetInstanceID();
            if (s_byPrefab.TryGetValue(id, out bool known)) return known;
            bool refused = IsRefusedInstance(prefab);
            if (s_byPrefab.Count > 8192) s_byPrefab.Clear();
            s_byPrefab[id] = refused;
            return refused;
        }

        // True for a kind that must not stand under water. Clones are judged by their name with "(Clone)" stripped.
        internal static bool IsRefusedInstance(GameObject go)
        {
            if (go == null) return false;
            if (go.GetComponent<Piece>() != null || go.GetComponent<Character>() != null
                || go.GetComponent<ItemDrop>() != null || go.GetComponent<Container>() != null) return false;
            if (go.GetComponent<MineRock>() != null || go.GetComponent<MineRock5>() != null || go.GetComponent<TreeLog>() != null) return false;
            string name = go.name.Replace("(Clone)", "").ToLowerInvariant();
            for (int i = 0; i < KeepWords.Length; i++)
                if (name.Contains(KeepWords[i])) return false;
            if (go.GetComponent<TreeBase>() != null || go.GetComponent<Plant>() != null || go.GetComponent<Pickable>() != null) return true;
            var d = go.GetComponent<Destructible>();
            if (d != null && d.m_destructibleType == DestructibleType.Tree) return true;
            for (int i = 0; i < RefuseWords.Length; i++)
                if (name.Contains(RefuseWords[i])) return true;
            return false;
        }

        // True for the kinds Fire allows under water (rocks, logs, stumps, water plants): step 4 lists them apart.
        internal static bool IsAllowedUnderWater(GameObject go)
        {
            if (go == null || IsRefusedInstance(go)) return false;
            if (go.GetComponent<MineRock>() != null || go.GetComponent<MineRock5>() != null || go.GetComponent<TreeLog>() != null) return true;
            string name = go.name.Replace("(Clone)", "").ToLowerInvariant();
            for (int i = 0; i < KeepWords.Length; i++)
                if (name.Contains(KeepWords[i])) return true;
            return false;
        }

        // The FAT water surface over ground point p when p is under it by more than tolerance: the generated field, then
        // tool water. Water at or below the sea sheet is vanilla's own, which its altitude rules already handle.
        internal static bool UnderFatWater(Vector3 p, float tolerance, out float surface)
        {
            surface = float.MinValue;
            if (GenWaterField.Ready && GenWaterField.TryWaterSurfaceSmooth(p.x, p.z, out float lvl, out float dep)
                && dep > 0f && lvl > SeaSheetGate) surface = lvl;
            float tool = ToolSurface(p);
            if (tool > surface) surface = tool;
            return surface > SeaSheetGate && surface > p.y + tolerance;
        }

        // Tool water: the combiners near the zone being placed (BeginZone), else every combiner.
        private static readonly List<WaterMeshCombiner> s_near = new List<WaterMeshCombiner>();
        private static bool s_nearSet;

        private static float ToolSurface(Vector3 p)
        {
            float best = float.MinValue;
            IReadOnlyList<WaterMeshCombiner> list = s_nearSet ? (IReadOnlyList<WaterMeshCombiner>)s_near : WaterMeshCombiner.AllCombiners;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null || c.CustomLiquidType.IsSolid()) continue;
                float s = c.GetWaterSurfaceAt(p);
                if (s > best) best = s;
            }
            return best;
        }

        // ── Generation ─────────────────────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, int> s_zoneRefused = new Dictionary<string, int>();
        private static readonly Dictionary<Vector2s, int> s_locRefusedByZone = new Dictionary<Vector2s, int>();
        private const float ZoneHalf = 32f, NearMargin = 16f;

        internal static void BeginZone(Vector3 zoneCenter)
        {
            s_zoneRefused.Clear();
            s_near.Clear();
            foreach (var c in WaterMeshCombiner.AllCombiners)
            {
                if (c == null) continue;
                var b = c.GetCombinedBounds();
                if (b.max.x < zoneCenter.x - ZoneHalf - NearMargin || b.min.x > zoneCenter.x + ZoneHalf + NearMargin
                    || b.max.z < zoneCenter.z - ZoneHalf - NearMargin || b.min.z > zoneCenter.z + ZoneHalf + NearMargin) continue;
                s_near.Add(c);
            }
            s_nearSet = true;
        }

        // Spliced after vanilla's `InsideClearArea(clearAreas, p)`: true skips the spot exactly as a clear area does.
        public static bool Filter(bool insideClearArea, ZoneSystem.ZoneVegetation veg, Vector3 p)
        {
            if (insideClearArea || veg == null || veg.m_prefab == null || !FiresVegWaterEcology.Enabled) return insideClearArea;
            if (!IsRefusedKind(veg.m_prefab)) return false;
            if (!UnderFatWater(p, SurfaceTolerance, out _)) return false;
            s_zoneRefused.TryGetValue(veg.m_prefab.name, out int n);
            s_zoneRefused[veg.m_prefab.name] = n + 1;
            return true;
        }

        // Once per zone, and only when something was refused: "vegetation refused under water: N (prefab×count); locations refused: M".
        internal static void EndZone(Vector2s zoneID)
        {
            s_nearSet = false;
            s_near.Clear();
            int veg = 0;
            foreach (var kv in s_zoneRefused) veg += kv.Value;
            s_locRefusedByZone.TryGetValue(zoneID, out int loc);
            s_locRefusedByZone.Remove(zoneID);
            if (veg == 0 && loc == 0) return;
            var parts = new List<string>();
            foreach (var kv in s_zoneRefused) parts.Add($"{kv.Key}×{kv.Value}");
            FiresLogger.LogDebug($"[FiresWater] zone ({zoneID.x},{zoneID.y}): vegetation refused under water: {veg}"
                + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "") + $"; locations refused: {loc}");
            s_zoneRefused.Clear();
        }

        // ── IL: after `call ZoneSystem.InsideClearArea`, load the loop's ZoneVegetation and p and call Filter ─────────
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var inside = AccessTools.Method(typeof(ZoneSystem), "InsideClearArea");
            var filter = AccessTools.Method(typeof(FatUnderWaterPlacement), nameof(Filter));
            // The loop variable `veg` lives in a closure (vanilla's lambdas read it): take the first load of a
            // ZoneVegetation field and the local that owns it.
            CodeInstruction vegOwner = null, vegField = null;
            for (int i = 1; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Ldfld && code[i].operand is FieldInfo f && f.FieldType == typeof(ZoneSystem.ZoneVegetation)
                    && IsLdLoc(code[i - 1]))
                { vegOwner = code[i - 1]; vegField = code[i]; break; }
            int spliced = 0;
            if (inside != null && filter != null && vegOwner != null)
                for (int i = 1; i < code.Count; i++)
                {
                    if (!(code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) || !Equals(code[i].operand, inside)) continue;
                    var loadP = code[i - 1];
                    if (!IsLdLoc(loadP)) continue;
                    code.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(vegOwner.opcode, vegOwner.operand),
                        new CodeInstruction(vegField.opcode, vegField.operand),
                        new CodeInstruction(loadP.opcode, loadP.operand),
                        new CodeInstruction(OpCodes.Call, filter),
                    });
                    spliced++;
                    i += 4;
                }
            if (spliced != s_loggedSplices)
            {
                s_loggedSplices = spliced;
                if (spliced > 0)
                    FiresLogger.LogInfo($"[FiresWater] PlaceVegetation under-water filter spliced after {spliced} InsideClearArea test(s).");
                else
                    FiresLogger.LogWarning("[FiresWater] PlaceVegetation under-water filter NOT spliced (vanilla layout changed): "
                        + $"InsideClearArea {(inside != null ? "found" : "missing")}, vegetation closure {(vegOwner != null ? "found" : "missing")}. "
                        + "Trees may generate under FAT water; the drowned sweep still clears them.");
            }
            return code;
        }

        private static int s_loggedSplices = -1;

        private static bool IsLdLoc(CodeInstruction ci)
            => ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc || ci.opcode == OpCodes.Ldloc_0
            || ci.opcode == OpCodes.Ldloc_1 || ci.opcode == OpCodes.Ldloc_2 || ci.opcode == OpCodes.Ldloc_3;

        // ── Locations: WorldGenerator.GetTerrainDelta postfix ─────────────────────────────────────────────────────
        // Vanilla (ZoneSystem.cs:1271) and FAT's BiomePaint.Locations call it once per candidate site with the location's
        // exterior radius. An area whose centre or any of 16 ring points (at the radius and half of it) stands under FAT
        // water at least LocationDepth deep fails the location's own max-terrain-delta test.
        private const float LocationDepth = 0.3f;
        private static int s_locRefusedTotal;

        internal static void TerrainDeltaPostfix(Vector3 center, float radius, ref float delta)
        {
            if (!FiresVegWaterEcology.Enabled || radius <= 0f) return;
            if (!GenWaterField.Ready && WaterMeshCombiner.AllCombiners.Count == 0) return;
            if (!AreaWet(center, radius)) return;
            delta = float.MaxValue;
            var z = ZoneSystem.GetZone(center);
            s_locRefusedByZone.TryGetValue(z, out int n);
            s_locRefusedByZone[z] = n + 1;
            s_locRefusedTotal++;
            if (s_locRefusedTotal == 1 || s_locRefusedTotal % 100 == 0)
                FiresLogger.LogDebug($"[FiresWater] location site refused: area r {radius:0} at ({center.x:0},{center.z:0}) overlaps FAT water "
                    + $"({s_locRefusedTotal} refused this session).");
        }

        private static bool AreaWet(Vector3 c, float r) => AreaWetCore(c, r, true);

        // 0.2.788 (the location accelerator, Fire 10-06: multithreaded from the start): the same refusal for a worker
        // thread. Generated water only (GenWaterField: on Fires worlds the frozen generator field); tool water is
        // player-made, never present while a world's locations are generated, and its combiners are Unity objects
        // (Time, EnvMan), so it is left out. No tallies: the caller counts its own refusals.
        internal static bool RefusesSite(Vector3 center, float radius)
            => FiresVegWaterEcology.Enabled && radius > 0f && GenWaterField.Ready && AreaWetCore(center, radius, false);

        private static bool AreaWetCore(Vector3 c, float r, bool toolWater)
        {
            if (WetAt(c.x, c.z, toolWater)) return true;
            for (int k = 0; k < 8; k++)
            {
                float a = k * (Mathf.PI / 4f);
                float dx = Mathf.Cos(a), dz = Mathf.Sin(a);
                if (WetAt(c.x + dx * r, c.z + dz * r, toolWater) || WetAt(c.x + dx * r * 0.5f, c.z + dz * r * 0.5f, toolWater)) return true;
            }
            return false;
        }

        private static bool WetAt(float x, float z, bool toolWater)
        {
            float ground = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;
            if (toolWater) return UnderFatWater(new Vector3(x, ground, z), LocationDepth, out _);
            return GenWaterField.TryWaterSurfaceSmooth(x, z, out float lvl, out float dep)
                && dep > 0f && lvl > SeaSheetGate && lvl > ground + LocationDepth;
        }
    }
}
