using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chaos Hammer's Paint entries (Fire's popup, 2026-09-27). For each land biome: a Plain brush (the bare biome),
    // then every enabled vanilla blend that belongs to it. A blend of several biomes gets one entry per biome
    // ("Lantern (Swamp)"), because a paint always names the ground it is for. Then one Restore brush. Each is a copy of
    // the Chaos Hammer's saving tool, like the save-area tool, wearing a disc in its ground's colour: a ring marks a
    // blend, grey marks Restore. The list is rebuilt when the game's alt-biome list changes (ZoneSystem fills it after
    // ObjectDB wakes).
    internal static class BiomePaintEntries
    {
        internal struct Entry
        {
            internal string Key;
            internal Heightmap.Biome Ground;
        }

        private const string ObjectPrefix = "fires_paint_";
        private const string RestoreName = "Restore the world's blends";
        private const string PlainPrefix = "Plain ";
        private const int IconSize = 64;
        private const float DiscShare = 0.42f;
        private const float RingInner = 0.30f;
        private const float RingOuter = 0.36f;

        private static readonly (Heightmap.Biome Biome, string Label, Color Colour)[] Grounds =
        {
            (Heightmap.Biome.Meadows, "$biome_meadows", new Color(0.45f, 0.75f, 0.30f)),
            (Heightmap.Biome.BlackForest, "$biome_blackforest", new Color(0.15f, 0.35f, 0.18f)),
            (Heightmap.Biome.Swamp, "$biome_swamp", new Color(0.40f, 0.33f, 0.20f)),
            (Heightmap.Biome.Mountain, "$biome_mountain", new Color(0.85f, 0.90f, 0.95f)),
            (Heightmap.Biome.Plains, "$biome_plains", new Color(0.85f, 0.75f, 0.35f)),
            (Heightmap.Biome.Mistlands, "$biome_mistlands", new Color(0.45f, 0.35f, 0.55f)),
            (Heightmap.Biome.AshLands, "$biome_ashlands", new Color(0.70f, 0.20f, 0.12f)),
            (Heightmap.Biome.DeepNorth, "$biome_deepnorth", new Color(0.60f, 0.80f, 0.95f)),
        };

        private static readonly Color RestoreColour = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color RingColour = new Color(1f, 1f, 1f, 0.95f);

        private static readonly List<GameObject> s_pieces = new List<GameObject>();
        private static readonly Dictionary<Piece, Entry> s_entries = new Dictionary<Piece, Entry>();
        private static int s_builtFrom = -1;

        internal static bool IsStale => s_builtFrom != BlendCount();

        internal static bool TryGet(Piece piece, out Entry entry)
        {
            entry = default;
            return piece != null && s_entries.TryGetValue(piece, out entry);
        }

        internal static Heightmap.Biome GroundOf(GameObject go)
        {
            var piece = go != null ? go.GetComponent<Piece>() : null;
            return TryGet(piece, out var entry) ? entry.Ground : Heightmap.Biome.None;
        }

        // The entries, built from `template` when the blend list has changed since last time.
        internal static IReadOnlyList<GameObject> Ensure(GameObject template)
        {
            if (template == null || !IsStale) return s_pieces;
            foreach (var go in s_pieces)
            {
                if (go == null) continue;
                var icon = go.GetComponent<Piece>()?.m_icon;
                if (icon != null)
                {
                    if (icon.texture != null) Object.Destroy(icon.texture);
                    Object.Destroy(icon);
                }
                Object.Destroy(go);
            }
            s_pieces.Clear();
            s_entries.Clear();
            s_builtFrom = BlendCount();
            foreach (var (biome, label, colour) in Grounds)
            {
                Add(template, BiomePaint.KeyOf(BiomePaint.Brush.Plain, biome, null), biome, PlainPrefix + label,
                    $"Turns a circle to bare {label} ground: its look, what grows and what spawns there, with no blend.", colour, false);
                foreach (var blend in BlendsOf(biome))
                {
                    string name = CountGrounds(blend.m_biome) > 1 ? $"{blend.m_name} ({label})" : blend.m_name;
                    Add(template, BiomePaint.KeyOf(BiomePaint.Brush.Blend, biome, blend.m_name), biome, name,
                        $"Turns a circle to {label} ground with the {blend.m_name} blend; what grows there grows again as "
                        + $"{blend.m_name}.", colour, true);
                }
            }
            Add(template, BiomePaint.KeyOf(BiomePaint.Brush.Restore, Heightmap.Biome.None, null), Heightmap.Biome.None, RestoreName,
                "Paints the world's own blends back over a circle, undoing any paint there; what grows there grows again as it "
                + "was.", RestoreColour, true);
            FiresLogger.LogInfo($"[BiomePaint] {s_pieces.Count} paint entries on the Chaos Hammer ({s_builtFrom} blend(s) in this game).");
            return s_pieces;
        }

        private static void Add(GameObject template, string key, Heightmap.Biome ground, string name, string description, Color colour, bool ring)
        {
            var go = Object.Instantiate(template);
            go.name = ObjectPrefix + key.Replace('|', '_').Replace(' ', '_');
            go.SetActive(false);
            Object.DontDestroyOnLoad(go);
            var piece = go.GetComponent<Piece>();
            if (piece == null)
            {
                Object.Destroy(go);
                return;
            }
            // 0.2.764 (Fire 10-06: "get this giant seed off our tool ... it blocks the cursor"): the entry is a copy of the save
            // tool, whose crystal model became the placement ghost at the crosshair. A paint entry shows only the ground ring
            // (ZoneModeAreaTool draws it with its own LineRenderers), so the copy's renderers go.
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) Object.DestroyImmediate(r);
            piece.m_name = name;
            piece.m_description = description + " Wheel: radius. Swing to paint; Undo takes it back.";
            piece.m_icon = Icon(colour, ring);
            s_pieces.Add(go);
            s_entries[piece] = new Entry { Key = key, Ground = ground };
        }

        private static IEnumerable<AltBiome> BlendsOf(Heightmap.Biome biome)
        {
            var blends = new List<AltBiome>();
            if (AltBiomeList.m_altBiomes == null) return blends;
            foreach (var alt in AltBiomeList.m_altBiomes)
                if (alt != null && alt.m_enabled && !string.IsNullOrEmpty(alt.m_name) && (alt.m_biome & biome) != 0 && !blends.Exists(b => b.m_name == alt.m_name))
                    blends.Add(alt);
            blends.Sort((a, b) => string.CompareOrdinal(a.m_name, b.m_name));
            return blends;
        }

        private static int CountGrounds(Heightmap.Biome mask)
        {
            int count = 0;
            foreach (var (biome, _, _) in Grounds) if ((mask & biome) != 0) count++;
            return count;
        }

        private static int BlendCount() => AltBiomeList.m_altBiomes?.Count ?? 0;

        private static Sprite Icon(Color colour, bool ring)
        {
            var texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false) { name = "fires_paint_icon" };
            float half = IconSize * 0.5f;
            for (int y = 0; y < IconSize; y++)
                for (int x = 0; x < IconSize; x++)
                {
                    float d = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / IconSize;
                    Color c = d <= DiscShare ? colour : Color.clear;
                    if (ring && d >= RingInner && d <= RingOuter) c = RingColour;
                    texture.SetPixel(x, y, c);
                }
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, IconSize, IconSize), new Vector2(0.5f, 0.5f));
        }
    }
}
