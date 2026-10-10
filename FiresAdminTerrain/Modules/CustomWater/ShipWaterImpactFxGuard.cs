using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-86 — heal a null-prefab crash in Ship's water-impact effect.
    //
    // Vanilla Ship.UpdateWaterForce fires m_waterImpactEffect.Create on a hard
    // water impact (fast depth change). EffectList.Create Instantiates each
    // entry's m_prefab with NO null check (assembly_valheim/EffectList.cs), so a
    // ship whose m_waterImpactEffect has a null-prefab entry (a broken / modded
    // ship) throws "The Object you want to instantiate is null" on every impact,
    // spamming the log.
    //
    // Our FiresLiquid buoyancy integration (the Floating.GetWaterLevel postfix)
    // makes a ship sitting in/near a custom spring take these impacts as the
    // spring surface flows — which SURFACES the latent broken-effect crash (it
    // also fires on the open ocean in rough seas). Strip the null entries once
    // at Awake so Create never Instantiates null. Harmless on healthy ships
    // (no null entries → early-out, no change).
    [HarmonyPatch(typeof(Ship), "Awake")]
    internal static class ShipWaterImpactFxGuard
    {
        [HarmonyPostfix]
        private static void Postfix(Ship __instance)
        {
            var fx = __instance != null ? __instance.m_waterImpactEffect : null;
            var arr = fx != null ? fx.m_effectPrefabs : null;
            if (arr == null || arr.Length == 0) return;

            bool hasNull = false;
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i];
                if (e == null || e.m_prefab == null) { hasNull = true; break; }
            }
            if (!hasNull) return;

            var cleaned = new List<EffectList.EffectData>(arr.Length);
            foreach (var e in arr)
                if (e != null && e.m_prefab != null) cleaned.Add(e);
            fx.m_effectPrefabs = cleaned.ToArray();
            FiresLogger.LogInfo($"[FiresLiquidWater] stripped {arr.Length - cleaned.Count} null water-impact effect(s) from ship '{__instance.name}' (was crash-spamming EffectList.Create).");
        }
    }
}
