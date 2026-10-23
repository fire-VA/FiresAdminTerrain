using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    // Player-facing config: what a player sees in the config window is short and basic.
    //  * Shown without "Show Advanced": keybinds and the preset picker only (IsPlayerAdvanced, used by the one
    //    FiresConfigVisibility.MarkAdvanced call). The admin-only tuning gate keeps its own predicate (IsAdvancedTuning).
    //  * Hidden entirely: the built-in name lists the code reads (prefab / environment lists). They stay in the .cfg for anyone
    //    editing it by hand.
    //  * Every description is cut to its first sentence, at a word boundary, so it reads as one line.
    // Keys, types and defaults are unchanged (saved values and server sync).
    internal static class FatPlayerFacingConfig
    {
        private const int MaxLine = 120;

        private static readonly HashSet<string> Basics = new HashSet<string>
        {
            "00 - Presets/01 Preset",
        };

        private static readonly HashSet<string> Hidden = new HashSet<string>
        {
            "09 - World generation/Surface-only locations",
            "09 - World generation/Underground location environments",
            "09 - World generation/Relax to half quota",
            "Voxel Terrain - World/Vegetation Tree Names",
            "Voxel Terrain - World/Vegetation Water Sinker Names",
        };

        internal static bool IsPlayerAdvanced(ConfigFile config, ConfigDefinition definition)
        {
            if (ConfigManager.IsAdvancedTuning(definition)) return true;
            if (Basics.Contains(definition.Section + "/" + definition.Key)) return false;
            ConfigEntryBase entry = null;
            try { entry = config[definition]; } catch { }
            return entry == null || entry.SettingType != typeof(KeyboardShortcut);
        }

        internal static void Apply(ConfigFile config)
        {
            var descriptionField = AccessTools.Field(typeof(ConfigDescription), "<Description>k__BackingField");
            var tagsField = AccessTools.Field(typeof(ConfigDescription), "<Tags>k__BackingField");
            int shortened = 0, hidden = 0;
            foreach (var definition in config.Keys.ToList())
            {
                ConfigEntryBase entry;
                try { entry = config[definition]; } catch { continue; }
                var description = entry?.Description;
                if (description == null) continue;

                string line = OneLine(description.Description);
                if (line != description.Description && descriptionField != null)
                {
                    descriptionField.SetValue(description, line);
                    shortened++;
                }

                if (Hidden.Contains(definition.Section + "/" + definition.Key) && tagsField != null)
                {
                    var tags = description.Tags ?? new object[0];
                    if (!tags.OfType<BrowsableAttribute>().Any())
                    {
                        tagsField.SetValue(description, tags.Concat(new object[] { new BrowsableAttribute(false) }).ToArray());
                        hidden++;
                    }
                }
            }
            FiresLogger.LogInfo($"[Config] player-facing: {shortened} descriptions cut to one line, {hidden} built-in lists hidden.");
        }

        // The first sentence (or first line), cut at a word boundary when it is still long.
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = text.Replace("\r", "");
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            int dot = s.IndexOf(". ");
            if (dot > 0) s = s.Substring(0, dot + 1);
            s = s.Trim();
            if (s.Length > MaxLine)
            {
                int cut = s.LastIndexOf(' ', MaxLine - 3);
                s = (cut > 40 ? s.Substring(0, cut) : s.Substring(0, MaxLine - 3)).TrimEnd(',', ';', ':', ' ') + "...";
            }
            return s;
        }
    }
}
