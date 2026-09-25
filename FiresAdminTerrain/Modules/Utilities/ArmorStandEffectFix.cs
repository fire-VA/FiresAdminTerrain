using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    // Putting any cape on an armor stand crashed every client that came near it, and the stand is not at fault:
    //
    //   EffectList.Create: for each entry -> checks m_enabled and m_variant, then Instantiate(effectData.m_prefab)
    //
    // There is NO null check on m_prefab. A 1.0 armor stand ships an effect entry whose prefab did not survive,
    // so ArmorStand.UpdateAttach's m_effects.Create throws ArgumentException "The Object you want to instantiate
    // is null" and the attach dies partway - and anyone loading the zone hits it again.
    //
    // This is a PREFAB fault, not world data. The circulating workaround clears the stand's ShoulderItem, which
    // only avoids the trigger and costs the item; scrubbing the dead entry fixes every stand in every world,
    // including worlds this install has never converted, and leaves what people put on them alone.
    //
    // Every ArmorStand in the scene's prefab list is swept, not just the two vanilla ones, so a modded stand with
    // the same broken reference is covered too.
    // Driven from FatVanillaHooks' single ZNetScene.Awake hook rather than patching it a second time.
    internal static class ArmorStandEffectFix
    {
        internal static void OnZNetSceneAwake()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;

            int stands = 0;
            int dropped = 0;
            var names = new List<string>();

            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                foreach (ArmorStand stand in prefab.GetComponentsInChildren<ArmorStand>(true))
                {
                    if (stand == null) continue;
                    int before = dropped;
                    dropped += Scrub(stand.m_effects);
                    dropped += Scrub(stand.m_destroyEffects);
                    if (dropped == before) continue;
                    stands++;
                    if (!names.Contains(prefab.name)) names.Add(prefab.name);
                }
            }

            if (dropped == 0) return;
            FiresLogger.LogInfo($"[ArmorStand] removed {dropped} dead effect reference(s) from {stands} armor stand "
                + $"component(s) on: {string.Join(", ", names.ToArray())}. Those are what made EffectList.Create "
                + "instantiate null and crash every client near a stand wearing a cape.");
        }

        /// <summary>Drops entries with no prefab. An entry that names nothing can only throw.</summary>
        private static int Scrub(EffectList effects)
        {
            if (effects == null || effects.m_effectPrefabs == null) return 0;

            var kept = new List<EffectList.EffectData>(effects.m_effectPrefabs.Length);
            foreach (EffectList.EffectData entry in effects.m_effectPrefabs)
                if (entry != null && entry.m_prefab != null) kept.Add(entry);

            int dropped = effects.m_effectPrefabs.Length - kept.Count;
            if (dropped > 0) effects.m_effectPrefabs = kept.ToArray();
            return dropped;
        }
    }
}
