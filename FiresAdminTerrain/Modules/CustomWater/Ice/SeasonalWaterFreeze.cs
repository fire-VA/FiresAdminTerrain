using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// 0.2.873 (Fire via [lead] 10-09; ported from [seasons]' Galaxies 0.1.39, which drops its copy in 0.1.40): Vedr's winter
    /// freezes FAT's custom ponds, rivers and springs (FAT owns all custom water when loaded), with no double handling.
    /// Vedr only ices vanilla WaterVolumes, so FAT freezes its own water: while Vedr says winter and its "Freeze lakes"
    /// is on, plain custom water turns to seasonal ice (never saved); outside winter, or without Vedr, that ice thaws.
    /// FAT also claims its water footprint through VedrApi.RegisterWaterOwner, so Vedr's lake ice never covers it.
    /// Every peer reads the same synced Vedr season, so no network traffic is needed. Vedr is used by reflection only.
    /// </summary>
    internal static class SeasonalWaterFreeze
    {
        // Same as Vedr's FrozenWater.NeverFrozen.
        private const Heightmap.Biome NeverFrozen = Heightmap.Biome.Ocean | Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth;
        private const float PollInterval = 2f;
        private const int MaxChangesPerPoll = 4;

        private static bool _resolved, _registered;
        private static Func<bool> _isWinter, _freezesLakes;
        private static float _nextPoll;
        private static bool _lastWinter;

        internal static void Tick()
        {
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + PollInterval;
            Resolve();

            bool winter = false;
            try { winter = _isWinter != null && _freezesLakes != null && _isWinter() && _freezesLakes(); } catch { }
            if (winter != _lastWinter)
            {
                _lastWinter = winter;
                FiresLogger.LogDebug($"[FiresWater] seasonal water: {(winter ? "Vedr winter, freezing custom water" : "not winter, thawing seasonal ice")}");
            }

            int changes = 0;
            foreach (WaterMeshCombiner c in WaterMeshCombiner.AllCombiners)
            {
                if (c == null) continue;
                bool want = winter && c.CustomLiquidType == CustomLiquidType.Water && Freezable(c);
                if (c.SeasonalIce) want = winter;
                if (want == c.SeasonalIce) continue;
                c.SetSeasonalIce(want);
                if (++changes >= MaxChangesPerPoll) { _nextPoll = Time.time + 0.25f; break; }
            }
        }

        private static bool Freezable(WaterMeshCombiner c)
        {
            if (WorldGenerator.instance == null) return false;
            Vector3 p = c.GetCombinedBounds().center;
            return (WorldGenerator.instance.GetBiome(p.x, p.z) & NeverFrozen) == 0;
        }

        /// <summary>Vedr's water-owner query: true where FAT custom water lies (XZ footprint).</summary>
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

        private static void Resolve()
        {
            if (_resolved) return;
            Type api = Type.GetType("Vedr.VedrApi, Norger.Vedr");
            if (api == null) return;   // Vedr absent or not loaded yet: look again next poll
            _resolved = true;
            try
            {
                MethodInfo w = api.GetProperty("IsWinter")?.GetGetMethod();
                MethodInfo f = api.GetProperty("FreezesLakes")?.GetGetMethod();
                if (w != null) _isWinter = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), w);
                if (f != null) _freezesLakes = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), f);
                MethodInfo reg = api.GetMethod("RegisterWaterOwner", new[] { typeof(Func<Vector3, bool>) });
                if (reg != null && !_registered)
                {
                    reg.Invoke(null, new object[] { new Func<Vector3, bool>(OwnsWater) });
                    _registered = true;
                }
            }
            catch (Exception e) { FiresLogger.LogWarning($"[FiresWater] seasonal water: Vedr API lookup failed: {e.Message}"); }
            FiresLogger.LogInfo($"[FiresWater] seasonal water: Vedr found; winter={( _isWinter != null)}, freezeLakes={(_freezesLakes != null)}, water owner registered={_registered}");
        }
    }
}
