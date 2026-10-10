using System.Reflection;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Zones
{
    // ZDO.Save reads each object's data from ZDOExtraData's s_save* stores, which vanilla's PrepareSave fills by
    // DEEP-CLONING every live store, so a save can run on a worker thread while the game keeps changing the data. The park
    // writes on the main thread with nothing changing, so it points the save stores at the live ones instead. For a
    // 7.68 M-object world that skips millions of dictionary copies (measured 2026-09-27: 23.9 s of GC inside a 118 s park).
    // ClearSave (the park's finally) drops the references again. If vanilla ever renames a store, it falls back to
    // PrepareSave: slower, never wrong.
    internal static class ZoneModeParkSnapshot
    {
        private static readonly string[] SaveStores =
            { "s_saveFloats", "s_saveVec3s", "s_saveQuats", "s_saveInts", "s_saveLongs", "s_saveStrings", "s_saveByteArrays", "s_saveConnections" };

        private static readonly string[] LiveStores =
            { "s_floats", "s_vec3", "s_quats", "s_ints", "s_longs", "s_strings", "s_byteArrays", "s_connectionsHashData" };

        private static readonly MethodInfo RegenerateConnections = AccessTools.Method(typeof(ZDOExtraData), "RegenerateConnectionHashData");

        /// <summary>True when the save stores now view the live data; false when vanilla's cloning PrepareSave ran instead.</summary>
        internal static bool Begin()
        {
            var save = new FieldInfo[SaveStores.Length];
            var live = new FieldInfo[LiveStores.Length];
            for (int i = 0; i < SaveStores.Length; i++)
            {
                save[i] = AccessTools.Field(typeof(ZDOExtraData), SaveStores[i]);
                live[i] = AccessTools.Field(typeof(ZDOExtraData), LiveStores[i]);
                if (save[i] == null || live[i] == null || save[i].FieldType != live[i].FieldType || RegenerateConnections == null)
                {
                    ZDOExtraData.PrepareSave();
                    return false;
                }
            }
            RegenerateConnections.Invoke(null, null);
            for (int i = 0; i < save.Length; i++) save[i].SetValue(null, live[i].GetValue(null));
            return true;
        }
    }
}
