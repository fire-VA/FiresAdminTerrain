using System;
using System.Reflection;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1410 ([seasons] single-owner calls via [lead] 11-08, Vedr 0.8.184 VedrApi 18): Vedr's live flags that decide whether FAT draws
    // a look itself. Bound by member presence (never ApiVersion alone); the values are re-read on every call, so a Vedr config change or
    // Vedr's own stand-down takes effect at once. No Vedr / an older Vedr = the members are absent and FAT keeps its own look.
    //   RainbowsActive (bool): Vedr draws the sky rainbow -> FAT's sky bow stands down (its waterfall-spray bow stays).
    //   GroundSnowActive (bool) + SnowSparkle (float): Vedr owns the ground snow -> FAT's voxel snow sparkle follows Vedr's key.
    internal static class VedrLooks
    {
        private static bool s_bound;
        private static PropertyInfo s_rainbows, s_groundSnow, s_sparkle;
        private static Func<global::Heightmap, bool> s_digs;   // 0.2.1420: VedrApi.DigsSnowTrail (Vedr 0.8.202, VedrApi 23)

        private static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            try
            {
                var vedr = Type.GetType("Vedr.VedrApi, Norger.Vedr", false);
                if (vedr == null) return;
                const BindingFlags SF = BindingFlags.Public | BindingFlags.Static;
                s_rainbows = vedr.GetProperty("RainbowsActive", SF);
                s_groundSnow = vedr.GetProperty("GroundSnowActive", SF);
                s_sparkle = vedr.GetProperty("SnowSparkle", SF);
                if (s_rainbows != null && s_rainbows.PropertyType != typeof(bool)) s_rainbows = null;
                if (s_groundSnow != null && s_groundSnow.PropertyType != typeof(bool)) s_groundSnow = null;
                if (s_sparkle != null && s_sparkle.PropertyType != typeof(float)) s_sparkle = null;
                var dm = vedr.GetMethod("DigsSnowTrail", SF, null, new[] { typeof(global::Heightmap) }, null);
                if (dm != null && dm.ReturnType == typeof(bool)) s_digs = (Func<global::Heightmap, bool>)Delegate.CreateDelegate(typeof(Func<global::Heightmap, bool>), dm);
                FiresLogger.LogInfo(s_digs != null ? "[SnowTrail] Vedr's live per-chunk trail owner bound (VedrApi.DigsSnowTrail): FAT stands down on every step where Vedr digs (0.2.1420)" : "[SnowTrail] VedrApi.DigsSnowTrail not in this Vedr: FAT's trail split uses FatApi.IsHeightmapHidden + Vedr's GroundSnowActive (0.2.1419)");
                FiresLogger.LogDebug($"[VedrLooks] VedrApi bound: RainbowsActive {(s_rainbows != null ? "yes" : "no")}, GroundSnowActive {(s_groundSnow != null ? "yes" : "no")}, SnowSparkle {(s_sparkle != null ? "yes" : "no")} (0.2.1410)");
            }
            catch (Exception e) { FiresLogger.LogDebug($"[VedrLooks] VedrApi not bound ({e.Message}); FAT keeps its own rainbow and sparkle"); s_rainbows = s_groundSnow = s_sparkle = null; }
        }

        private static bool ReadBool(PropertyInfo p)
        {
            if (p == null) return false;
            try { return (bool)p.GetValue(null, null); } catch { return false; }
        }

        /// <summary>0.2.1419: true while Vedr owns the ground snow (VedrApi.GroundSnowActive, re-read every call; never ApiVersion).</summary>
        internal static bool GroundSnowActive { get { Bind(); return ReadBool(s_groundSnow); } }

        /// <summary>0.2.1420: Vedr's own per-step answer, live (asked every step, never cached): null = this Vedr has no DigsSnowTrail.</summary>
        internal static bool HasDigs { get { Bind(); return s_digs != null; } }

        internal static bool? DigsSnowTrail(global::Heightmap hm)
        {
            Bind();
            if (s_digs == null) return null;
            try { return s_digs(hm); } catch { return null; }
        }

        /// <summary>True while Vedr draws the sky rainbow (live).</summary>
        internal static bool RainbowsActive { get { Bind(); return ReadBool(s_rainbows); } }

        /// <summary>Vedr's snow sparkle while Vedr owns the ground snow (live); false = FAT's own key applies.</summary>
        internal static bool TryVedrSparkle(out float sparkle)
        {
            Bind();
            sparkle = 0f;
            if (s_sparkle == null || !ReadBool(s_groundSnow)) return false;
            try { sparkle = (float)s_sparkle.GetValue(null, null); return true; } catch { return false; }
        }
    }
}
