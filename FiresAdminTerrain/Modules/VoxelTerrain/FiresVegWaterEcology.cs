using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Vegetation ecology around generated water ────────────────────────
    // Every altitude rule in vanilla vegetation placement is written against
    // SEA level (p.y − 30): trees have positive min altitudes so they stay
    // off the shore, vass and waterlilies have negative bands so they stand
    // in the shallows, swamp trees straddle zero. Our rivers sit ABOVE 30,
    // so none of that ecology applies to them — trees plant right at the
    // waterline and the shore plants never appear.
    //
    // The port: replace the 30 in the altitude test with the LOCAL water
    // surface (reconciled field) wherever one exists. The whole vanilla
    // shoreline ecology — trees back off, vass/lilies hug the edge, swamp
    // trees wade — transfers to rivers, lakes and swamps for free, using the
    // per-prefab altitude bands vanilla already tuned. Far from generated
    // water the helper returns 30 and behaviour is bit-identical vanilla.
    //
    // On top of that, two config-driven nudges (prefix/finalizer around the
    // same call, VegetationDensity's proven mutate-and-restore pattern):
    //  * tree-classified prefabs get extra min-altitude clearance so trees
    //    rarely stand at the water's very edge;
    //  * sinker-classified prefabs (stubs, logs, rocks) get their min
    //    altitude lowered so they can seed into shallow beds.
    internal static class FiresVegWaterEcology
    {
        internal static bool Enabled =>
            FiresVoxelConfig.VegWaterEcology == null || FiresVoxelConfig.VegWaterEcology.Value;

        // Called from transpiled IL in ZoneSystem.PlaceVegetation.
        // A chunk raised with other land (ZoneModeLift) judges altitude against its lifted sea, so it grows as its source.
        public static float EffectiveWaterY(ref Vector3 p)
        {
            float sea = ZoneSystem.c_WaterLevel + Zones.ZoneModeGate.LiftAt(p.x, p.z);
            if (!Enabled || !GenWaterField.Ready) return sea;
            if (GenWaterField.TryWaterSurfaceSmooth(p.x, p.z, out float lvl, out _)
                && lvl > sea + SeaSheetMargin) return lvl;
            return sea;
        }

        private const float SeaSheetMargin = 0.3f;

        private static string s_treeCsvCache, s_sinkCsvCache;
        private static string[] s_trees = System.Array.Empty<string>();
        private static string[] s_sinkers = System.Array.Empty<string>();

        internal static void RefreshNameLists()
        {
            string tc = FiresVoxelConfig.VegTreeNames != null ? FiresVoxelConfig.VegTreeNames.Value : "";
            string sc = FiresVoxelConfig.VegWaterSinkerNames != null ? FiresVoxelConfig.VegWaterSinkerNames.Value : "";
            if (tc != s_treeCsvCache) { s_treeCsvCache = tc; s_trees = Split(tc); }
            if (sc != s_sinkCsvCache) { s_sinkCsvCache = sc; s_sinkers = Split(sc); }
        }

        private static string[] Split(string csv)
        {
            var list = new List<string>();
            foreach (var part in csv.Split(','))
            {
                var t = part.Trim().ToLowerInvariant();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        internal static bool NameMatches(string prefabLower, string[] set)
        {
            for (int i = 0; i < set.Length; i++)
                if (prefabLower.Contains(set[i])) return true;
            return false;
        }

        internal static string[] Trees => s_trees;
        internal static string[] Sinkers => s_sinkers;
    }

    // Transpile, Apply and Restore are the transpiler, a prefix and a finalizer of FAT's one
    // ZoneSystem.PlaceVegetation hook (FatVanillaHooks).
    internal static class ZoneSystem_PlaceVegetation_WaterEcology
    {
        // ── IL port: altitude vs LOCAL water ─────────────────────────────
        // Two byte patterns, both anchored on the literal 30f so a game
        // update that reshuffles locals still matches:
        //  A) [ldloc(a) p][ldfld Vector3.y][ldc.r4 30] − altitude test
        //  B) [ldloca p][ldc.r4 30][stfld Vector3.y]   − m_snapToWater
        // The constant is replaced with `ldloca p; call EffectiveWaterY`.
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var fY = AccessTools.Field(typeof(Vector3), "y");
            var helper = AccessTools.Method(typeof(FiresVegWaterEcology),
                nameof(FiresVegWaterEcology.EffectiveWaterY));
            int a = 0, b = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var ci = code[i];
                if (ci.opcode != OpCodes.Ldc_R4 || !(ci.operand is float f) || f != 30f) continue;
                if (i >= 2 && code[i - 1].opcode == OpCodes.Ldfld && Equals(code[i - 1].operand, fY)
                    && IsLdLoc(code[i - 2], out object opA))
                {
                    code[i] = new CodeInstruction(OpCodes.Ldloca_S, opA);
                    code.Insert(i + 1, new CodeInstruction(OpCodes.Call, helper));
                    a++; i++;
                    continue;
                }
                if (i + 1 < code.Count && code[i + 1].opcode == OpCodes.Stfld && Equals(code[i + 1].operand, fY)
                    && i >= 1 && IsLdLoc(code[i - 1], out object opB))
                {
                    code[i] = new CodeInstruction(OpCodes.Ldloca_S, opB);
                    code.Insert(i + 1, new CodeInstruction(OpCodes.Call, helper));
                    b++; i++;
                }
            }
            // Harmony re-runs transpilers whenever another mod patches or
            // unpatches the method; report a result only when it changes.
            if (a != s_loggedAltitudePorts || b != s_loggedSnapPorts)
            {
                s_loggedAltitudePorts = a;
                s_loggedSnapPorts = b;
                FiresLogger.LogInfo($"[FiresVegWater] PlaceVegetation altitude ports applied: {a} altitude test(s), {b} snap-to-water.");
                if (a == 0)
                    FiresLogger.LogWarning("[FiresVegWater] altitude pattern NOT found — vanilla layout changed; " +
                                     "water ecology inactive, vegetation altitude runs pure vanilla.");
            }
            return code;
        }

        private static int s_loggedAltitudePorts = -1;
        private static int s_loggedSnapPorts = -1;

        private static bool IsLdLoc(CodeInstruction ci, out object operand)
        {
            operand = ci.operand;
            return ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloca_S
                || ci.opcode == OpCodes.Ldloc || ci.opcode == OpCodes.Ldloca;
        }

        // ── Per-class altitude nudges (mutate → place → restore) ─────────
        private static readonly List<float> s_altBackup = new List<float>(256);
        private static bool s_adjusted;

        internal static void Apply(ZoneSystem __instance)
        {
            s_adjusted = false;
            if (!FiresVegWaterEcology.Enabled || !GenWaterField.Ready) return;
            var veg = __instance.m_vegetation;
            if (veg == null || veg.Count == 0) return;
            float clear = FiresVoxelConfig.VegTreeWaterClearance != null
                ? FiresVoxelConfig.VegTreeWaterClearance.Value : 1f;
            FiresVegWaterEcology.RefreshNameLists();
            var trees = FiresVegWaterEcology.Trees;
            var sinkers = FiresVegWaterEcology.Sinkers;
            if ((clear <= 0f || trees.Length == 0) && sinkers.Length == 0) return;

            s_altBackup.Clear();
            for (int i = 0; i < veg.Count; i++)
            {
                var item = veg[i];
                s_altBackup.Add(item != null ? item.m_minAltitude : 0f);
                if (item == null || item.m_prefab == null) continue;
                string name = item.m_prefab.name.ToLowerInvariant();
                if (clear > 0f && FiresVegWaterEcology.NameMatches(name, trees))
                    item.m_minAltitude += clear;
                else if (FiresVegWaterEcology.NameMatches(name, sinkers) && item.m_minAltitude > -0.8f)
                    item.m_minAltitude = -0.8f;
            }
            s_adjusted = true;
        }

        internal static void Restore(ZoneSystem __instance)
        {
            if (!s_adjusted) return;
            s_adjusted = false;
            var veg = __instance.m_vegetation;
            if (veg == null) return;
            int n = Mathf.Min(veg.Count, s_altBackup.Count);
            for (int i = 0; i < n; i++)
                if (veg[i] != null) veg[i].m_minAltitude = s_altBackup[i];
        }
    }

    // Vanilla 'ice1' floes: with placement altitude now local-water-relative
    // they qualify at EVERY mountain pond — and spawn at vanilla-lake counts
    // and vanilla-lake SIZE (the oversized armada report). Ghost-spawn
    // post-pass: keep at most 2 per zone, only over OUR still water, and
    // scale each survivor to the body it floats in.
    // A postfix of FAT's one ZoneSystem.PlaceVegetation hook (FatVanillaHooks), before the skyland props are added.
    internal static class ZoneSystem_PlaceVegetation_IceFloes
    {
        internal static void Cull(ZoneSystem.SpawnMode mode, System.Collections.Generic.List<GameObject> spawnedObjects)
        {
            if (!GenWaterField.Ready || mode != ZoneSystem.SpawnMode.Ghost || spawnedObjects == null) return;
            int kept = 0;
            for (int idx = 0; idx < spawnedObjects.Count; idx++)
            {
                var go = spawnedObjects[idx];
                if (go == null || !go.name.StartsWith("ice1")) continue;
                var znv = go.GetComponent<ZNetView>();
                if (znv == null || znv.GetZDO() == null) continue;
                var p = go.transform.position;
                byte wc = GenWaterField.WaterClassAt(p.x, p.z);
                // No floes on HOT springs — the one still water that melts ice.
                bool still = wc == FiresWorldGenBase.WC_FROZEN
                          || wc == FiresWorldGenBase.WC_LAKE || wc == FiresWorldGenBase.WC_POND
                          || wc == FiresWorldGenBase.WC_SWAMP;
                float half = still ? LocalWaterHalfWidth(p) : 0f;
                if (!still || kept >= 2 || half < 3f)
                {
                    // Core's network-safe teardown — ownership claim, ZDO
                    // destroy and instance deregistration in one path. Never
                    // hand-roll ZDO destruction on ZNetView objects.
                    FiresCore.Net.NetworkObjectHelper.SafeDestroy(go);
                    continue;
                }
                kept++;
                float s = Mathf.Clamp(half / 9f, 0.3f, 1f);
                if (s < 0.99f) znv.SetLocalScale(new Vector3(s, s, s));
            }
        }

        // Local pond half-width: probe outward 4 ways until the water field
        // goes dry; the floe scales to the SMALLEST reach so it always fits.
        private static float LocalWaterHalfWidth(Vector3 p)
        {
            float best = 24f;
            for (int d = 0; d < 4; d++)
            {
                float dx = d == 0 ? 1f : d == 1 ? -1f : 0f;
                float dz = d == 2 ? 1f : d == 3 ? -1f : 0f;
                float r = 24f;
                for (float step = 2f; step <= 24f; step += 2f)
                {
                    if (!GenWaterField.TryWaterSurfaceSmooth(p.x + dx * step, p.z + dz * step, out _, out float dep)
                        || dep < 0.1f) { r = step - 2f; break; }
                }
                if (r < best) best = r;
            }
            return best;
        }
    }
}
