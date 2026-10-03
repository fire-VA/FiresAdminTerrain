using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Loading a pre-1.0 save, vanilla's ZDOMan.RemoveObsoleteData strips retired keys from every object's data. For each
    // store and each of its strip lists it walks every object and binary-searches every listed hash, about 240 searches
    // an object, and on a 7.7 M object save that was one 107 s freeze (Va, 2026-09-27). This makes the same removals in
    // one walk per store: each object's few keys are looked up in one set of that store's strip hashes. A removed key is
    // counted under the first vanilla list that names it, so the log line reads exactly as vanilla's would. The anim_speed
    // pass stays vanilla's own. If a store cannot be found or anything throws, vanilla runs; its removals repeat safely.
    internal static class ZoneModeOldSaveStrip
    {
        private const string AnimSpeedKey = "anim_speed";

        private enum Kind { Floats, Vec3s, Quats, Ints, Longs, Strings, ByteArrays, Misc, AnimSalt }

        private static readonly int KindCount = Enum.GetValues(typeof(Kind)).Length;

        [HarmonyPatch(typeof(ZDOMan), "RemoveObsoleteData")]
        private static class OnePass
        {
            private static bool Prefix(ZDOMan __instance)
            {
                try { return !Strip(__instance); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresZones] the one-pass obsolete-data strip failed ({ex.GetType().Name}: {ex.Message}); "
                        + "vanilla's own pass runs instead.");
                    return true;
                }
            }
        }

        private static bool Strip(ZDOMan zdoMan)
        {
            var floats = Store<float>("s_floats");
            var vecs = Store<Vector3>("s_vec3");
            var quats = Store<Quaternion>("s_quats");
            var ints = Store<int>("s_ints");
            var longs = Store<long>("s_longs");
            var strings = Store<string>("s_strings");
            var byteArrays = Store<byte[]>("s_byteArrays");
            if (floats == null || vecs == null || quats == null || ints == null || longs == null || strings == null || byteArrays == null)
                return false;

            var clock = Stopwatch.StartNew();
            var removed = new int[KindCount];
            var found = new List<int>();
            removed[(int)Kind.Floats] += RemoveAnimSpeed(zdoMan);
            StripStore(floats, KindsFor(Of(Kind.Floats, ZDOHelper.s_stripOldFloats), Misc(), Of(Kind.AnimSalt, ZDOHelper.s_stripAnimationFloatData)), removed, found);
            StripStore(vecs, KindsFor(Of(Kind.Vec3s, ZDOHelper.s_stripOldVecs), Misc()), removed, found);
            StripStore(quats, KindsFor(Of(Kind.Quats, ZDOHelper.s_stripOldQuats), Misc()), removed, found);
            StripStore(ints, KindsFor(Of(Kind.Ints, ZDOHelper.s_stripOldInts), Misc(), Of(Kind.AnimSalt, ZDOHelper.s_stripAnimationIntData)), removed, found);
            StripStore(longs, KindsFor(Of(Kind.Longs, ZDOHelper.s_stripOldLongData), Of(Kind.Longs, AsInts(ZDOHelper.s_stripOldUnknownLongData)), Misc()), removed, found);
            StripStore(strings, KindsFor(Of(Kind.Strings, ZDOHelper.s_stripOldStringData), Misc()), removed, found);
            StripStore(byteArrays, KindsFor(Of(Kind.ByteArrays, ZDOHelper.s_stripOldDataByteArray), Misc()), removed, found);

            int total = 0;
            foreach (int count in removed) total += count;
            if (total > 0)
                UnityEngine.Debug.Log("Found obsolete data => Removed a total of " + total + " values. Breakdown:\n\tFloats:     "
                    + removed[(int)Kind.Floats] + "\n\tVec3s:      " + removed[(int)Kind.Vec3s] + "\n\tQuats:      " + removed[(int)Kind.Quats]
                    + "\n\tInts:       " + removed[(int)Kind.Ints] + "\n\tLongs:      " + removed[(int)Kind.Longs]
                    + "\n\tByteArrays: " + removed[(int)Kind.ByteArrays] + "\n\tStrings:    " + removed[(int)Kind.Strings]
                    + "\n\tAnimSalt:   " + removed[(int)Kind.AnimSalt] + "\n\tMisc:       " + removed[(int)Kind.Misc]);
            FiresLogger.LogInfo($"[FiresZones] old-save upgrade: obsolete data stripped in one pass per store, {total:N0} value(s) "
                + $"in {clock.ElapsedMilliseconds} ms.");
            return true;
        }

        private static int RemoveAnimSpeed(ZDOMan zdoMan)
        {
            int hash = Animator.StringToHash(AnimSpeedKey);
            int count = 0;
            foreach (ZDOID id in ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Float, hash))
            {
                ZDO zdo = zdoMan.GetZDO(id);
                if (zdo != null && zdo.RemoveFloat(hash)) count++;
            }
            return count;
        }

        private static Dictionary<ZDOID, BinarySearchDictionary<int, T>> Store<T>(string field)
            => AccessTools.Field(typeof(ZDOExtraData), field)?.GetValue(null) as Dictionary<ZDOID, BinarySearchDictionary<int, T>>;

        private static KeyValuePair<Kind, IEnumerable<int>> Of(Kind kind, IEnumerable<int> hashes)
            => new KeyValuePair<Kind, IEnumerable<int>>(kind, hashes);

        private static KeyValuePair<Kind, IEnumerable<int>> Misc() => Of(Kind.Misc, ZDOHelper.s_stripOldData);

        private static IEnumerable<int> AsInts(List<uint> hashes)
        {
            foreach (uint hash in hashes) yield return (int)hash;
        }

        // Vanilla strips a store's lists in this order, so a hash in two lists is removed, and counted, by the first.
        private static Dictionary<int, Kind> KindsFor(params KeyValuePair<Kind, IEnumerable<int>>[] listsInVanillaOrder)
        {
            var kinds = new Dictionary<int, Kind>();
            foreach (var list in listsInVanillaOrder)
                foreach (int hash in list.Value)
                    if (!kinds.ContainsKey(hash)) kinds[hash] = list.Key;
            return kinds;
        }

        private static void StripStore<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> store, Dictionary<int, Kind> kinds,
            int[] removed, List<int> found)
        {
            if (kinds.Count == 0) return;
            foreach (var data in store.Values)
            {
                int count = data.Count;
                if (count == 0) continue;
                found.Clear();
                if (data.Keys is int[] keys)
                {
                    for (int at = 0; at < count; at++)
                        if (kinds.ContainsKey(keys[at])) found.Add(keys[at]);
                }
                else
                {
                    foreach (var pair in data)
                        if (kinds.ContainsKey(pair.Key)) found.Add(pair.Key);
                }
                foreach (int hash in found)
                    if (data.Remove(hash)) removed[(int)kinds[hash]]++;
            }
        }
    }
}
