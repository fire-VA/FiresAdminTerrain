using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Parking hands every removed object back to vanilla's pools, and those pools never shrink: ZDOPool keeps each ZDO shell
    // on its free stack, and ZDOHelper.Release clears each ZDO's per-type data dictionaries and pushes them onto the static
    // Pool<BinarySearchDictionary<int, T>> stacks with their grown arrays. After a 7.65 M object park that held the mono
    // heap at 7-8 GB with 37.5 k objects live (2026-09-27, [perf]), and every full GC stopped the world ~0.5 s. The
    // dictionaries vanilla sized for the whole old save stay that size too. Run once, right after a park, while the game
    // is still loading: empty the data pools, cap the ZDO pool, trim what was sized for the old save, and collect.
    internal static class ZoneModeParkMemory
    {
        private const int ZdoPoolKept = 65536;
        private const double BytesPerMegabyte = 1024d * 1024d;
        private const string PoolTypeName = "Pool`1";
        private const string SearchDictionaryTypeName = "BinarySearchDictionary`2";
        private const string PoolStackField = "s_available";

        private static readonly Type[] PooledValueTypes =
            { typeof(float), typeof(Vector3), typeof(Quaternion), typeof(int), typeof(long), typeof(string), typeof(byte[]) };

        private static readonly string[] ExtraDataDictionaries =
        {
            "s_floats", "s_vec3", "s_quats", "s_ints", "s_longs", "s_strings", "s_byteArrays",
            "s_connectionsHashData", "s_connections", "s_owner",
        };

        internal static void ReleaseAfterPark(ZDOMan zdoMan)
        {
            long before = GC.GetTotalMemory(false);
            int pooledData = EmptyDataPools();
            int pooledZdos = CapZdoPool();
            int trimmed = TrimDictionaries(zdoMan);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long after = GC.GetTotalMemory(true);
            FiresLogger.LogInfo($"[FiresZones] park memory released: {pooledData:N0} pooled data dictionaries and {pooledZdos:N0} pooled "
                + $"objects dropped, {trimmed} store(s) trimmed; managed heap {before / BytesPerMegabyte:N0} -> {after / BytesPerMegabyte:N0} MB.");
        }

        // The types come from vanilla's own field, never a by-name search: another mod may define its own Pool<T>.
        private static int EmptyDataPools()
        {
            Type floats = AccessTools.Field(typeof(ZDOExtraData), ExtraDataDictionaries[0])?.FieldType;
            Type searchDictionary = floats != null && floats.IsGenericType ? floats.GetGenericArguments()[1].GetGenericTypeDefinition() : null;
            if (searchDictionary == null || searchDictionary.Name != SearchDictionaryTypeName) return 0;
            Type pool = searchDictionary.Assembly.GetType(PoolTypeName);
            if (pool == null) return 0;
            int dropped = 0;
            foreach (Type value in PooledValueTypes)
            {
                Type pooled = searchDictionary.MakeGenericType(typeof(int), value);
                FieldInfo field = AccessTools.Field(pool.MakeGenericType(pooled), PoolStackField);
                if (!(field?.GetValue(null) is ICollection stack)) continue;
                dropped += stack.Count;
                field.FieldType.GetMethod(nameof(Stack<object>.Clear))?.Invoke(stack, null);
                field.FieldType.GetMethod(nameof(Stack<object>.TrimExcess))?.Invoke(stack, null);
            }
            return dropped;
        }

        private static int CapZdoPool()
        {
            if (!(AccessTools.Field(typeof(ZDOPool), "s_free")?.GetValue(null) is Stack<ZDO> free)) return 0;
            int dropped = 0;
            while (free.Count > ZdoPoolKept)
            {
                free.Pop();
                dropped++;
            }
            free.TrimExcess();
            return dropped;
        }

        private static int TrimDictionaries(ZDOMan zdoMan)
        {
            int trimmed = 0;
            if (zdoMan != null)
            {
                if (TrimExcess(AccessTools.Field(typeof(ZDOMan), "m_objectsByID")?.GetValue(zdoMan))) trimmed++;
                if (AccessTools.Field(typeof(ZDOMan), "m_objectsBySector")?.GetValue(zdoMan) is List<ZDO>[] sectors)
                    foreach (List<ZDO> sector in sectors) sector?.TrimExcess();
            }
            foreach (string name in ExtraDataDictionaries)
                if (TrimExcess(AccessTools.Field(typeof(ZDOExtraData), name)?.GetValue(null))) trimmed++;
            return trimmed;
        }

        // Dictionary.TrimExcess exists in the game's runtime but not in the net48 reference assemblies FAT builds against.
        private static bool TrimExcess(object store)
        {
            MethodInfo trim = store?.GetType().GetMethod(nameof(List<object>.TrimExcess), Type.EmptyTypes);
            if (trim == null) return false;
            trim.Invoke(store, null);
            return true;
        }
    }
}
