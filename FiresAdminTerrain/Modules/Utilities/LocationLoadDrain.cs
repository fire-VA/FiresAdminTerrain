using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Utilities
{
    // At logout there were ~25 pairs of "AssetBundle.Unload was called while the asset bundle had an async
    // load operation in progress" + "AssetBundle.Unload could not complete". Vanilla's ZoneSystem keeps one LocationPrefabLoadData per
    // location it is about to place; each starts an ASYNC prefab load. FAT's ghost-zone accelerator places many more locations per frame
    // than vanilla, so at logout many of those loads are still in flight when ZoneSystem.OnDestroy force-releases them, and Unity refuses
    // to unload a bundle mid-load. Before that release, every still-loading entry is finished SYNCHRONOUSLY (one extra Load, then its
    // Release: the reference count is unchanged), so the release that follows unloads loaded bundles cleanly.
    [HarmonyPatch(typeof(ZoneSystem), "OnDestroy")]
    internal static class LocationLoadDrain
    {
        private static FieldInfo s_list, s_prefab, s_rooms;
        private static PropertyInfo s_isLoaded;

        [HarmonyPriority(Priority.First)]
        private static void Prefix(ZoneSystem __instance)
        {
            try
            {
                if (s_list == null) s_list = AccessTools.Field(typeof(ZoneSystem), "m_locationPrefabs");
                if (!(s_list?.GetValue(__instance) is IList list) || list.Count == 0) return;
                int pending = 0, total = list.Count;
                foreach (var data in list)
                {
                    if (data == null) continue;
                    var t = data.GetType();
                    s_isLoaded ??= AccessTools.Property(t, "IsLoaded");
                    s_prefab ??= AccessTools.Field(t, "m_prefab");
                    s_rooms ??= AccessTools.Field(t, "m_possibleRooms");
                    if (s_isLoaded != null && (bool)s_isLoaded.GetValue(data)) continue;
                    pending++;
                    Finish(s_prefab?.GetValue(data));
                    if (s_rooms?.GetValue(data) is IEnumerable rooms)
                        foreach (var room in rooms) Finish(room);
                }
                FiresLogger.LogDebug($"[LocationLoadDrain] logout: {pending} of {total} location prefab loads still in flight at ZoneSystem teardown, finished synchronously before release");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[LocationLoadDrain] skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // A sync Load completes the in-flight async load of the same asset; the matching Release keeps the count where it was.
        private static void Finish(object softRef)
        {
            if (softRef == null) return;
            var t = softRef.GetType();
            var valid = AccessTools.Property(t, "IsValid");
            if (valid != null && !(bool)valid.GetValue(softRef)) return;
            var load = AccessTools.Method(t, "Load", System.Type.EmptyTypes);
            var release = AccessTools.Method(t, "Release", System.Type.EmptyTypes);
            if (load == null || release == null) return;
            load.Invoke(softRef, null);
            release.Invoke(softRef, null);
        }
    }
}
