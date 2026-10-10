using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Round-57 — tints the Infinity Hammer ruler ring per tool family:
    //   GREEN for our terrain tools, BLUE for our water/liquid tools,
    //   untouched (vanilla white) for everything else.
    //
    // How the ruler renders (verified against InfinityHammer.dll IL):
    //   * `InfinityTools.Ruler.Projector` is a GameObject following the
    //     placement ghost; per-shape children (Circle/Ring/...) carry a
    //     BaseRuler subclass that instantiates SEGMENTS from the vanilla
    //     workbench `CircleProjector.m_prefab` (the white dashed bits) —
    //     plus a second OffsetSegments ring at the height offset.
    //   * Segments are Instantiate()d copies — tinting their INSTANCE
    //     materials (renderer.material, never sharedMaterial) can't leak
    //     into world workbench circles.
    //   * Segments get destroyed/recreated when the radius changes, so
    //     the tint must be re-applied continuously; fresh segments spawn
    //     with the default color and pick up the tint on the next tick.
    //
    // Attached to the plugin root by TileAnchorPrefab.Ensure (same
    // pattern as TileBrushDrag / WaterBrushDrag).
    public sealed class RulerColorDriver : MonoBehaviour
    {
        private const float CHECK_INTERVAL = 0.15f;

        private static readonly Color TERRAIN_GREEN = new Color(0.30f, 0.95f, 0.35f, 1f);
        private static readonly Color WATER_BLUE = new Color(0.25f, 0.55f, 1.00f, 1f);

        // Our terrain-family tool names (YAML `name:` values). Green ring.
        private static readonly HashSet<string> TERRAIN_TOOLS = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Floor", "Raise Terrain", "Raise (Sharp)", "Lower Terrain",
            "Floor (Floating Island)", "Raise Terrain (Floating Island)", "Lower Terrain (Floating Island)",
            "Smooth Terrain", "Grab Terrain", "Noise Terrain",
            "Repair Seams",
            "Paint Dirt", "Paint Cultivated", "Paint Paved", "Paint Grass",
            "Clear Brushed Area", "Precision Hole Punch", "Patch Hole", "Delete Tiles",
            "Biome Meadows", "Biome BlackForest", "Biome Plains", "Biome Swamp",
            "Biome Mountain", "Biome Mistlands", "Biome Ashlands (Lava)", "Biome DeepNorth",
            "Clear Biome",
        };

        // Our water/liquid-family tool names. Blue ring. Ice + snow are
        // part of the water family (they route through the water/land-ice
        // pipelines).
        private static readonly HashSet<string> WATER_TOOLS = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Water", "Waterfall", "Water Volume", "Spring",
            "Lava", "Swimmable Lava", "Tar", "Boiling Water", "Freezing Water",
            "Ice", "Snow", "Delete Water",
            "Place Ice (Auto)", "Place Ice (Land Force)", "Place Ice (Water Force)",
            "Ice Raise (Land)", "Ice Lower (Land)", "Ice Level (Land)", "Ice Smooth (Land)",
            "Delete Land Ice", "Break Ice Hole", "Delete Ice (Any)",
            "Place Land Snow",
            "Snow Raise (Land)", "Snow Lower (Land)", "Snow Level (Land)", "Snow Smooth (Land)",
            "Delete Land Snow",
        };

        private float _nextCheckTime;
        private bool _tinted;
        private bool _originalCaptured;
        private Color _originalColor = Color.white;

        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null) return;
            if (root.GetComponent<RulerColorDriver>() != null) return;
            root.AddComponent<RulerColorDriver>();
            FiresLogger.LogInfo("[RulerColorDriver] attached to plugin root (green=terrain, blue=water).");
        }

        private void Update()
        {
            if (Time.time < _nextCheckTime) return;
            _nextCheckTime = Time.time + CHECK_INTERVAL;

            var player = Player.m_localPlayer;
            if (player == null) return;

            string pieceName = player.m_buildPieces != null
                ? player.m_buildPieces.GetSelectedPiece()?.m_name
                : null;

            Color? target = null;
            if (!string.IsNullOrEmpty(pieceName))
            {
                if (TERRAIN_TOOLS.Contains(pieceName)) target = TERRAIN_GREEN;
                else if (WATER_TOOLS.Contains(pieceName)) target = WATER_BLUE;
            }

            if (target.HasValue)
            {
                ApplyTint(target.Value);
            }
            else if (_tinted)
            {
                // Left our tools — put the default color back on whatever
                // segments still exist. Newly created segments already
                // spawn with the prefab default, so this only needs to fix
                // the instances we touched.
                if (_originalCaptured) ApplyTint(_originalColor);
                _tinted = false;
            }
        }

        // Tint every renderer under the ruler projector via INSTANCE
        // materials. Segment count is small (ring ≈ 4·radius segments),
        // and repeated .material access returns the same instance, so the
        // per-tick cost is a few dozen color writes.
        private void ApplyTint(Color color)
        {
            if (!InfinityHammerRulerProbe.TryGetProjector(out var projector)) return;

            var renderers = projector.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;

                var mat = r.material; // instance — never sharedMaterial
                if (mat == null) continue;

                // Capture the segment prefab's original color once so we
                // can restore it when the player switches to a non-Fires
                // tool while the same segments are still alive.
                if (!_originalCaptured && mat.HasProperty("_Color"))
                {
                    _originalColor = mat.color;
                    _originalCaptured = true;
                }

                if (mat.HasProperty("_Color")) mat.color = color;
                // The workbench segment material glows via emission on
                // some shader variants — keep it in step when present so
                // the ring doesn't tint pale-white-with-colored-fringe.
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", color * 0.6f);
            }

            _tinted = true;
        }
    }
}
