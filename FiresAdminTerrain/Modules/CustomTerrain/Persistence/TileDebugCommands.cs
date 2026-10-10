using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Console commands for testing the tile-anchor pipeline end-to-end.
    //
    // Phase 8b commands (work today):
    //   va_tile_render <tileX> <tileZ> <height>
    //     Spawn a standalone TerrainTile + TileMeshRenderer at the given tile,
    //     fill it with a flat height pattern, verify the mesh appears in-game.
    //     No ZNetView, no ZDO — pure rendering smoke test.
    //
    //   va_tile_render_slope <tileX> <tileZ> <minH> <maxH>
    //     Same as render but ramps height linearly across the tile so you can
    //     visually verify the vertex layout.
    //
    //   va_tile_list
    //     List all registered TileAnchors (Phase 8c+ — empty in 8b).
    //
    //   va_tile_clear_test
    //     Destroy all GameObjects created by va_tile_render*.
    //
    // Phase 8c commands:
    //   va_tile_persist <tileX> <tileZ> <height>
    //     Same as va_tile_render but routed through TileAnchor + ZDO. Survives
    //     world save/load. Multiplayer-replicates via ZDO data revision.
    //
    //   va_tile_persist_slope <tileX> <tileZ> <minH> <maxH>
    //     Sloped variant of va_tile_persist.
    //
    //   va_tile_load <tileX> <tileZ>
    //     Find an existing TileAnchor in the registry and dump its decoded
    //     state (TileXZ, height/paint mod counts, disk count, data revision).
    //
    //   va_tile_destroy <tileX> <tileZ>
    //     Destroy the TileAnchor at the given index AND its ZDO. Use for
    //     iterating on tests without world-file pollution.
    public static class TileDebugCommands
    {
        private static bool _registered;
        private static readonly List<GameObject> _testObjects = new List<GameObject>();

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("va_tile_render",
                "[va_tile_render tileX tileZ height] Spawn a flat test tile at the given index and height. Pure rendering test, no ZDO.",
                (args) => Cmd_Render(args, slope: false),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_render_slope",
                "[va_tile_render_slope tileX tileZ minH maxH] Spawn a sloped test tile ramping from minH to maxH along +X.",
                (args) => Cmd_Render(args, slope: true),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_list",
                "List all registered TileAnchors (Phase 8c+).",
                (args) => Cmd_List(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_clear_test",
                "Destroy all test tile GameObjects spawned by va_tile_render*.",
                (args) => Cmd_ClearTest(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_persist",
                "[va_tile_persist tileX tileZ height] Spawn a TileAnchor at the given index via ZDO; fills with a flat height pattern. Survives reload.",
                (args) => Cmd_Persist(args, slope: false),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_persist_slope",
                "[va_tile_persist_slope tileX tileZ minH maxH] Sloped variant of va_tile_persist.",
                (args) => Cmd_Persist(args, slope: true),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_load",
                "[va_tile_load tileX tileZ] Dump state of an existing TileAnchor at the given index.",
                (args) => Cmd_Load(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_destroy",
                "[va_tile_destroy tileX tileZ] Destroy the TileAnchor at the given index and its ZDO.",
                (args) => Cmd_Destroy(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_prefab_ensure",
                "Force-register the VATile prefab now (normally happens on ZNetScene.Awake).",
                (args) => Cmd_PrefabEnsure(args),
                isCheat: true);
        }

        private static void Cmd_Render(Terminal.ConsoleEventArgs args, bool slope)
        {
            if (args.Length < (slope ? 5 : 4))
            {
                args.Context.AddString(slope
                    ? "Usage: va_tile_render_slope <tileX> <tileZ> <minH> <maxH>"
                    : "Usage: va_tile_render <tileX> <tileZ> <height>");
                return;
            }

            if (!int.TryParse(args[1], out int tileX) ||
                !int.TryParse(args[2], out int tileZ))
            {
                args.Context.AddString("tileX / tileZ must be integers.");
                return;
            }

            float h1, h2;
            if (slope)
            {
                if (!float.TryParse(args[3], out h1) || !float.TryParse(args[4], out h2))
                {
                    args.Context.AddString("Heights must be numbers.");
                    return;
                }
            }
            else
            {
                if (!float.TryParse(args[3], out h1))
                {
                    args.Context.AddString("Height must be a number.");
                    return;
                }
                h2 = h1;
            }

            var tileXZ = new Vector2i(tileX, tileZ);
            var go = new GameObject($"TestTile_{tileX}_{tileZ}");
            var tile = go.AddComponent<TerrainTile>();
            tile.InitializeEmpty(tileXZ);

            // Fill height grid.
            int size = TerrainTileCodec.VERTEX_GRID_SIZE;
            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float t = (size <= 1) ? 0f : (float)i / (size - 1);
                float h = Mathf.Lerp(h1, h2, t);
                int idx = j * size + i;
                tile.Heights[idx] = h;
                tile.HeightModified[idx] = true;
            }

            tile.NotifyMutated();
            _testObjects.Add(go);

            args.Context.AddString(
                $"Spawned test tile at index ({tileX}, {tileZ}), world center {tile.WorldCenter}, "
                + (slope ? $"slope {h1} -> {h2}." : $"flat height {h1}."));
        }

        private static void Cmd_List(Terminal.ConsoleEventArgs args)
        {
            args.Context.AddString($"TileAnchorRegistry: {TileAnchorRegistry.Count} anchors loaded.");
            int n = 0;
            var sb = new StringBuilder();
            foreach (var a in TileAnchorRegistry.All)
            {
                if (a == null) continue;
                sb.AppendLine($"  [{n++}] tile=({a.TileXZ.x}, {a.TileXZ.y}) "
                              + $"world={(a.transform != null ? a.transform.position.ToString() : "?")} "
                              + $"hasTile={(a.Tile != null)}");
                if (n >= 20)
                {
                    sb.AppendLine("  ... (truncated to 20)");
                    break;
                }
            }
            if (n > 0) args.Context.AddString(sb.ToString());
            args.Context.AddString($"Test GameObjects from va_tile_render*: {_testObjects.Count}");
        }

        private static void Cmd_ClearTest(Terminal.ConsoleEventArgs args)
        {
            int count = 0;
            for (int i = 0; i < _testObjects.Count; i++)
            {
                if (_testObjects[i] != null)
                {
                    Object.Destroy(_testObjects[i]);
                    count++;
                }
            }
            _testObjects.Clear();
            args.Context.AddString($"Destroyed {count} test tile GameObject(s).");
        }

        private static void Cmd_Persist(Terminal.ConsoleEventArgs args, bool slope)
        {
            if (args.Length < (slope ? 5 : 4))
            {
                args.Context.AddString(slope
                    ? "Usage: va_tile_persist_slope <tileX> <tileZ> <minH> <maxH>"
                    : "Usage: va_tile_persist <tileX> <tileZ> <height>");
                return;
            }

            if (!int.TryParse(args[1], out int tileX) || !int.TryParse(args[2], out int tileZ))
            {
                args.Context.AddString("tileX / tileZ must be integers.");
                return;
            }

            float h1, h2;
            if (slope)
            {
                if (!float.TryParse(args[3], out h1) || !float.TryParse(args[4], out h2))
                {
                    args.Context.AddString("Heights must be numbers.");
                    return;
                }
            }
            else
            {
                if (!float.TryParse(args[3], out h1))
                {
                    args.Context.AddString("Height must be a number.");
                    return;
                }
                h2 = h1;
            }

            var tileXZ = new Vector2i(tileX, tileZ);

            if (ZNetScene.instance == null)
            {
                args.Context.AddString("ZNetScene not available — must be in-world.");
                return;
            }

            var anchor = TileAnchor.Spawn(tileXZ);
            if (anchor == null)
            {
                args.Context.AddString($"Spawn failed for tile ({tileX}, {tileZ}). Check log for VATile prefab errors.");
                return;
            }

            anchor.ApplyOpAndSave(tile =>
            {
                int size = TerrainTileCodec.VERTEX_GRID_SIZE;
                for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    float t = (size <= 1) ? 0f : (float)i / (size - 1);
                    float h = Mathf.Lerp(h1, h2, t);
                    int idx = j * size + i;
                    tile.Heights[idx] = h;
                    tile.HeightModified[idx] = true;
                }
            });

            args.Context.AddString(
                $"Persisted tile ({tileX}, {tileZ}) "
                + (slope ? $"slope {h1} -> {h2}." : $"flat height {h1}.")
                + $" Anchor world={anchor.transform.position}");
        }

        private static void Cmd_Load(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                args.Context.AddString("Usage: va_tile_load <tileX> <tileZ>");
                return;
            }

            if (!int.TryParse(args[1], out int tileX) || !int.TryParse(args[2], out int tileZ))
            {
                args.Context.AddString("tileX / tileZ must be integers.");
                return;
            }

            var anchor = TileAnchorRegistry.GetAt(new Vector2i(tileX, tileZ));
            if (anchor == null)
            {
                args.Context.AddString($"No TileAnchor registered at ({tileX}, {tileZ}).");
                return;
            }

            if (anchor.Tile == null)
            {
                args.Context.AddString($"TileAnchor at ({tileX}, {tileZ}) has no decoded data yet (waiting for ZDO sync?).");
                return;
            }

            int heightMods = 0, paintMods = 0;
            for (int i = 0; i < TerrainTileCodec.VERTEX_COUNT; i++)
            {
                if (anchor.Tile.HeightModified[i]) heightMods++;
                if (anchor.Tile.PaintModified[i]) paintMods++;
            }

            args.Context.AddString(
                $"TileAnchor ({tileX}, {tileZ}) "
                + $"| world={anchor.transform.position} "
                + $"| heightMods={heightMods} paintMods={paintMods} disks={anchor.Tile.Disks.Count} "
                + $"| lastRev={anchor.Tile.LastSeenDataRevision}");
        }

        private static void Cmd_Destroy(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                args.Context.AddString("Usage: va_tile_destroy <tileX> <tileZ>");
                return;
            }

            if (!int.TryParse(args[1], out int tileX) || !int.TryParse(args[2], out int tileZ))
            {
                args.Context.AddString("tileX / tileZ must be integers.");
                return;
            }

            var anchor = TileAnchorRegistry.GetAt(new Vector2i(tileX, tileZ));
            if (anchor == null)
            {
                args.Context.AddString($"No TileAnchor registered at ({tileX}, {tileZ}).");
                return;
            }

            var nv = anchor.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                if (!nv.IsOwner()) nv.ClaimOwnership();
                nv.Destroy();
                args.Context.AddString($"Destroyed TileAnchor at ({tileX}, {tileZ}) via ZNetView.Destroy (ZDO removed).");
            }
            else
            {
                Object.Destroy(anchor.gameObject);
                args.Context.AddString($"Destroyed TileAnchor GameObject at ({tileX}, {tileZ}) (no ZDO to remove).");
            }
        }

        private static void Cmd_PrefabEnsure(Terminal.ConsoleEventArgs args)
        {
            bool ok = TileAnchorPrefab.Ensure();
            args.Context.AddString(ok
                ? $"VATile prefab registered. Hash={TileAnchorPrefab.PrefabName.GetStableHashCode()}"
                : "VATile prefab registration FAILED. Check log for details.");
        }

    }
}
