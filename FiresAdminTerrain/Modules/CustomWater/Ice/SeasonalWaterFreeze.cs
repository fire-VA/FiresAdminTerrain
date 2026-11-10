using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Freezes and thaws FAT's placed fresh water (ponds, rivers, springs: every WaterMeshCombiner of plain water).
    /// 0.2.1095 (Fire: "water in the deep north should freeze over and become slippery"): the rule is VanillaIce.ShouldFreeze
    /// ([seasons]: Deep North, painted or snow-base, is ALWAYS frozen; other biomes only in Vedr's winter while it freezes lakes,
    /// read through Core's FiresCore.Bridge.SeasonBridge). A frozen combiner turns to seasonal ice (FrozenWaterController: the ice
    /// look plus a walkable collider that opts into vanilla's slippery ice, VanillaIce.OptIn); seasonal ice is never saved, so it
    /// thaws back to the water it was when the rule stops (a biome repaint, the thaw season). FAT also claims its water footprint
    /// through SeasonBridge.RegisterWaterOwner, so Vedr's lake ice never covers it. Every peer reads the same synced biome paint and
    /// season, so no network traffic is needed.
    /// </summary>
    internal static class SeasonalWaterFreeze
    {
        // 0.2.1097: the per-body freeze/lava switching that lived here (Decide, the per-combiner states, SetSeasonalIce / SetBiomeLava) is
        // REMOVED: ice / lava / crust are per 1 m column in WaterStateField. What stays: the Core water-owner registration and the tick.
        private static bool _registered;

        internal static void Tick()
        {
            Register();
            WaterStateField.Tick();
            PlacedWaterSolids.Tick();
            WaterSteam.Tick();
        }

        private static Func<int> s_paintVersion;
        private static bool s_paintBound;
        private static int PaintVersion()
        {
            if (!s_paintBound)
            {
                s_paintBound = true;
                var p = AccessTools.TypeByName("VerdantsAscent.Terrain.Zones.BiomePaint")?.GetProperty("BiomePaintVersion", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
                if (p != null && p.ReturnType == typeof(int)) s_paintVersion = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), p);
            }
            try { return s_paintVersion != null ? s_paintVersion() : 0; } catch { return 0; }
        }

        /// <summary>The water-owner query: true where FAT custom water lies (XZ footprint).</summary>
        private static bool OwnsWater(Vector3 point)
        {
            var all = WaterMeshCombiner.AllCombiners;
            for (int i = 0; i < all.Count; i++)
            {
                WaterMeshCombiner c = all[i];
                if (c == null) continue;
                Bounds b = c.GetCombinedBounds();
                if (point.x < b.min.x || point.x > b.max.x || point.z < b.min.z || point.z > b.max.z) continue;
                if (c.IsWithinAnyDiskRadius(point)) return true;
            }
            return false;
        }

        // Once: SeasonBridge.RegisterWaterOwner (Core queues it until Vedr binds).
        private static void Register()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                var sb = AccessTools.TypeByName("FiresCore.Bridge.SeasonBridge");
                var reg = sb?.GetMethod("RegisterWaterOwner", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Func<Vector3, bool>) }, null);
                if (reg != null) reg.Invoke(null, new object[] { new Func<Vector3, bool>(OwnsWater) });
                FiresLogger.LogInfo($"[WaterIce] water owner {(reg != null ? "registered with Core SeasonBridge" : "NOT registered (SeasonBridge.RegisterWaterOwner missing)")}");
            }
            catch (Exception e) { FiresLogger.LogWarning($"[WaterIce] RegisterWaterOwner failed: {e.Message}"); }
        }
    }
}
