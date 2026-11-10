using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1373 (Fire: paint "our own custom biomes which would use the expand world yaml for the biome data but our own placement"):
    // the biomes Expand World Data has registered from its yamls, as brushes. Bound softly by name: no Expand World, or `Biome data`
    // off, means no custom biome and no brush.
    // A custom circle saves the biome's NAME (in Circle.Blend). Expand World gives a custom biome its bit by yaml order, so a saved
    // bit could turn into another biome when a yaml is added. The bit is read from the name whenever a list is installed; a name
    // nothing has registered leaves the circle saved and doing nothing, with one warning, until the name is back.
    internal static partial class BiomePaint
    {
        private const string CustomCode = "c";
        private const float CustomCheckSeconds = 5f;

        private static bool s_ewLooked;
        private static MethodInfo s_ewTryGetBiome;
        private static FieldInfo s_ewDisplayNames;
        private static readonly HashSet<string> s_customWarned = new HashSet<string>();
        private static float s_customCheckAt;

        private static void BindExpandWorld()
        {
            if (s_ewLooked) return;
            s_ewLooked = true;
            // 0.2.1389: Expand World Data 1.79 moved the class into ExpandWorldData.Biome, so this found nothing and no custom biome could be painted. The real run stopped on "RedThicket registered False (bit 0)".
            var type = FatSoftTypes.Find("ExpandWorldData.Biome.BiomeManager") ?? FatSoftTypes.Find("ExpandWorldData.BiomeManager");
            if (type == null)
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("expand_world_data")) FiresLogger.LogWarning("[BiomePaint] Expand World Data is installed but its BiomeManager was not found: custom biomes cannot be painted.");
                return;
            }
            s_ewTryGetBiome = AccessTools.Method(type, "TryGetBiome", new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() });
            s_ewDisplayNames = AccessTools.Field(type, "BiomeToDisplayName");
            FiresLogger.LogDebug($"[BiomePaint] Expand World's biome list bound: names {(s_ewDisplayNames != null ? "yes" : "no")}, lookup {(s_ewTryGetBiome != null ? "yes" : "no")} (0.2.1373)");
        }

        private static bool IsCustomBit(Heightmap.Biome biome) => biome != Heightmap.Biome.None && !Enum.IsDefined(typeof(Heightmap.Biome), biome);

        internal static bool TryResolveCustom(string name, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            BindExpandWorld();
            if (s_ewTryGetBiome == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                var args = new object[] { name, Heightmap.Biome.None };
                if (!(bool)s_ewTryGetBiome.Invoke(null, args)) return false;
                biome = (Heightmap.Biome)args[1];
                if (IsCustomBit(biome)) return true;
                biome = Heightmap.Biome.None;
                return false;
            }
            catch (Exception ex)
            {
                s_ewTryGetBiome = null;
                FiresLogger.LogWarning($"[BiomePaint] Expand World's biome lookup failed ({ex.GetType().Name}): custom biomes are off.");
                return false;
            }
        }

        // The registered custom biomes by name, sorted. Empty with no Expand World or `Biome data` off.
        internal static List<string> CustomBiomeNames()
        {
            var names = new List<string>();
            BindExpandWorld();
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintCustomBrushes;
            if (s_ewDisplayNames == null || (key != null && !key.Value)) return names;
            try
            {
                if (s_ewDisplayNames.GetValue(null) is IDictionary map)
                    foreach (DictionaryEntry pair in map)
                        if (pair.Key is Heightmap.Biome biome && IsCustomBit(biome) && pair.Value is string name && !string.IsNullOrEmpty(name))
                            names.Add(name);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] Expand World's biome names could not be read ({ex.GetType().Name})."); }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // Changes when the registered names or their bits change, so the hammer's list and the painted circles follow a yaml reload.
        internal static int CustomStamp()
        {
            if (Time.unscaledTime < s_stampAt) return s_stamp;   // the hammer asks every time it lists its rows
            s_stampAt = Time.unscaledTime + StampSeconds;
            int stamp = 17;
            foreach (string name in CustomBiomeNames())
            {
                stamp = stamp * 31 + name.GetStableHashCode();
                if (TryResolveCustom(name, out var biome)) stamp = stamp * 31 + (int)biome;
            }
            s_stamp = stamp;
            return stamp;
        }

        private const float StampSeconds = 2f;
        private static int s_stamp;
        private static float s_stampAt;

        private static Circle Resolved(Circle circle)
        {
            if (circle.Brush != Brush.Custom) return circle;
            if (!TryResolveCustom(circle.Blend, out circle.Ground) && s_customWarned.Add(circle.Blend))
                FiresLogger.LogWarning($"[BiomePaint] the painted biome '{circle.Blend}' is not registered on this PC: its paint stays saved and does nothing until Expand World has that biome again.");
            return circle;
        }

        // Returns true when any circle's bit changed.
        private static bool ResolveCustom(List<Circle> circles)
        {
            bool changed = false;
            for (int i = 0; i < circles.Count; i++)
            {
                if (circles[i].Brush != Brush.Custom) continue;
                var circle = Resolved(circles[i]);
                if (circle.Ground != circles[i].Ground) changed = true;
                circles[i] = circle;
            }
            return changed;
        }

        // Expand World can load its yamls after the paint, and reloads them live; it raises no event FAT could hook, so this looks every few seconds, and only while a custom circle exists.
        private static void TickCustomBiomes()
        {
            if (Time.unscaledTime < s_customCheckAt) return;
            s_customCheckAt = Time.unscaledTime + CustomCheckSeconds;
            bool any = false;
            foreach (var c in s_circles) if (c.Brush == Brush.Custom) { any = true; break; }
            if (!any || !ResolveCustom(s_circles)) return;
            s_sectors.Clear();
            FiresLogger.LogInfo("[BiomePaint] Expand World's biome list changed: the painted custom biomes follow it.");
            s_snapshot = Snapshot.Empty;   // every circle counts as changed, so the loaded ground rebuilds
            Install(new List<Circle>(s_circles));
        }

        // Height paint needs generated ground that follows GetBiomeHeight: vanilla and converted worlds. Voxel ground is saved chunks.
        private static bool HeightAllowed(in Circle circle, out string why)
        {
            why = string.Empty;
            if (circle.Brush != Brush.Custom) return false;
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintChangeHeight;
            if (key != null && !key.Value) { why = "The land keeps its shape (key off)."; return false; }
            if (VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.Active || VerdantsAscent.Terrain.Voxel.VoxelWorld.ChunkCount > 0)
            {
                why = "The land keeps its shape: this world's ground is voxel chunks.";
                return false;
            }
            return true;
        }

        // For the map tool (FatApi.TryGetBiomeBrushes): every brush, in the hammer's order.
        internal static void ListBrushes(List<string> keys, List<string> names, List<bool> custom)
        {
            BiomePaintEntries.ListVanilla(keys, names, custom);
            int restoreAt = keys.Count > 0 ? keys.Count - 1 : 0;
            foreach (string name in CustomBiomeNames())
            {
                keys.Insert(restoreAt, KeyOf(Brush.Custom, Heightmap.Biome.None, name));
                names.Insert(restoreAt, name);
                custom.Insert(restoreAt, true);
                restoreAt++;
            }
        }
    }
}
