using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace VerdantsAscent
{
    // A config should be accurate and easy to read: keys an older FAT wrote
    // stay in the .cfg as orphans (BepInEx keeps unbound entries and writes them back), where they read like working settings -
    // "Chunk undo key = Keypad7" looks like a live key, and FAT has had no such key since ZoneModeChunkKeys. This removes exactly the
    // keys named below from the file, and carries a moved key's value to its new place first. Only NAMED keys go: a key a module
    // binds later in the session is also an orphan for a while, and clearing every orphan would lose its value.
    internal static class FatConfigRetire
    {
        // Whole sections no build binds any more.
        private static readonly string[] RetiredSections =
        {
            "FiresInlandWater Look",            // superseded by the numbered FiresInlandWater 1..9 sections
        };

        // Single keys no build binds any more: section, key.
        private static readonly string[,] RetiredKeys =
        {
            { "Zone Mode", "Chunk undo key" },  // ZoneModeChunkKeys: the undo/redo keys are the vanilla build keys now
            { "Zone Mode", "Chunk redo key" },
            { "Zone Mode", "Location island margin" },
            { "VoxelTerrain World", "Cave Floor Prefabs" },     // moved to Cave Content (VoxelCaveClutter layers)
            { "VoxelTerrain World", "Cave Ceiling Prefabs" },
            { "VoxelTerrain World", "Cave Cavern Prefabs" },
            { "VoxelTerrain World", "Cave Bone Prefabs" },
            { "VoxelTerrain World", "Cave Mushroom Chance" },
            { "VoxelTerrain World", "Cave Bone Chance" },
            { "World Gen Studio", "Executable" },
        };

        // A key that moved: its old place's value, if the file still has it, becomes the new entry's value, and the old place goes.
        internal static void Move(ConfigFile cfg, string oldSection, string oldKey, ConfigEntryBase moved)
        {
            var orphans = Orphans(cfg);
            if (orphans == null || moved == null) return;
            var old = new ConfigDefinition(oldSection, oldKey);
            if (!orphans.Contains(old)) return;
            string value = orphans[old] as string;
            orphans.Remove(old);
            if (value != null)
            {
                try { moved.SetSerializedValue(value); }
                catch { }
            }
            FiresLogger.LogInfo($"[Config] moved '[{oldSection}] {oldKey}' to '[{moved.Definition.Section}] {moved.Definition.Key}' (value {value ?? "default"})");
            cfg.Save();
        }

        // No duplicate or confusing sections: the VoxelTerrain sections now read "Voxel Terrain - ...".
        // Old section -> new section; a prefix entry (ending in a space) maps per-biome sections too ("... Cave Content Meadows").
        private static readonly string[,] SectionMoves =
        {
            { "VoxelTerrain World", "Voxel Terrain - World" },
            { "VoxelTerrain Performance", "Voxel Terrain - Performance" },
            { "VoxelTerrain Tools", "Voxel Terrain - Tools" },
            { "VoxelTerrain Cave Clutter", "Voxel Terrain - Cave clutter" },
            { "VoxelTerrain Cave Content", "Voxel Terrain - Cave content" },
            { "VoxelTerrain Cave Content ", "Voxel Terrain - Cave content " },
            { "VoxelTerrain Tree Seeds", "Voxel Terrain - Tree seeds" },
        };

        internal static string OldSectionOf(string newSection)
        {
            for (int i = 0; i < SectionMoves.GetLength(0); i++)
            {
                string from = SectionMoves[i, 0], to = SectionMoves[i, 1];
                if (to.EndsWith(" ") ? newSection.StartsWith(to) && newSection.Length > to.Length : newSection == to)
                    return from + newSection.Substring(to.Length);
            }
            return null;
        }

        private static bool IsMovedOldSection(string section)
        {
            for (int i = 0; i < SectionMoves.GetLength(0); i++)
            {
                string from = SectionMoves[i, 0];
                if (from.EndsWith(" ") ? section.StartsWith(from) && section.Length > from.Length : section == from) return true;
            }
            return false;
        }

        // Once, after ALL binds (every renamed section binds at startup through FiresVoxelConfig.Bind): each bound key in a renamed
        // section takes the value its old section still holds, then the old line goes. Old-section lines no key binds any more
        // (dead keys) go too. One Save. Logs how many moved, so a copy-cfg diff can be checked against it.
        internal static void MoveSections(ConfigFile cfg)
        {
            var orphans = Orphans(cfg);
            if (orphans == null) return;
            int moved = 0, dropped = 0;
            foreach (var def in new List<ConfigDefinition>(cfg.Keys))
            {
                string oldSection = OldSectionOf(def.Section);
                if (oldSection == null) continue;
                var old = new ConfigDefinition(oldSection, def.Key);
                if (!orphans.Contains(old)) continue;
                string value = orphans[old] as string;
                orphans.Remove(old);
                if (value != null)
                {
                    try { cfg[def].SetSerializedValue(value); moved++; }
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[Config] could not carry '[{oldSection}] {def.Key}' = '{value}': {ex.Message}"); }
                }
            }
            var leftovers = new List<ConfigDefinition>();
            foreach (var key in orphans.Keys)
                if (key is ConfigDefinition d && IsMovedOldSection(d.Section)) leftovers.Add(d);
            foreach (var d in leftovers) { orphans.Remove(d); dropped++; }
            if (moved == 0 && dropped == 0) return;
            cfg.Save();
            FiresLogger.LogInfo($"[Config] section rename 0.2.974: {moved} value(s) carried to the 'Voxel Terrain - ...' sections, {dropped} dead line(s) from the old sections removed");
        }

        // Once, after the plugin's own binds: the named dead keys leave the file.
        internal static void Retire(ConfigFile cfg)
        {
            var orphans = Orphans(cfg);
            if (orphans == null) return;
            var gone = new List<ConfigDefinition>();
            foreach (var key in orphans.Keys)
            {
                if (!(key is ConfigDefinition def)) continue;
                bool retired = System.Array.IndexOf(RetiredSections, def.Section) >= 0;
                for (int i = 0; !retired && i < RetiredKeys.GetLength(0); i++)
                    retired = RetiredKeys[i, 0] == def.Section && RetiredKeys[i, 1] == def.Key;
                if (retired) gone.Add(def);
            }
            if (gone.Count == 0) return;
            foreach (var def in gone) orphans.Remove(def);
            cfg.Save();
            FiresLogger.LogInfo($"[Config] removed {gone.Count} retired key(s) no FAT build reads any more from {System.IO.Path.GetFileName(cfg.ConfigFilePath)}");
        }

        // ConfigFile.OrphanedEntries (Dictionary<ConfigDefinition, string>): a protected property on BepInEx 5.4.2x.
        private static IDictionary Orphans(ConfigFile cfg)
        {
            if (cfg == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return (typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(cfg, null)
                    ?? typeof(ConfigFile).GetField("OrphanedEntries", flags)?.GetValue(cfg)
                    ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(cfg)) as IDictionary;
        }
    }
}
