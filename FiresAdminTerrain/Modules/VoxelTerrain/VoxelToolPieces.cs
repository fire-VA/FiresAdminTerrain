using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Voxel
{
    // THE IH DIVORCE — the V-tools are OUR OWN pieces with our own input:
    // no infinity_tools.yaml, no IH ruler, no console-command round-trip.
    //   * Pieces register straight into the HAMMER's piece table, admin-gated,
    //     so FAP's build-menu tabs can carry them and its vanilla-friendly
    //     toggle can hide them. The seam is Core (AdminSyncing), never FAP —
    //     FAT takes no dependency on it and gates itself when it is absent.
    //   * Selecting one never places anything (TryPlacePiece blocked); the
    //     VoxelBrushDrag pump owns every click.
    //   * Plain scroll = radius (camera zoom suppressed while selected),
    //     PgUp/PgDn = cylinder height / level offset. The ghost cages are the
    //     only visual.
    internal static class VoxelToolPieces
    {
        private const string HostItemName = "Hammer";

        internal static readonly HashSet<string> Names = new HashSet<string>
        { "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere", "Vriver", "Vpour", "Vpool" };

        // Tools the VoxelBrushDrag pump actually drives. Only these take the
        // scroll wheel off the camera — a registered-but-unwired tool must not
        // swallow input and read as broken.
        internal static readonly HashSet<string> PumpedNames = new HashSet<string>
        { "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere", "Vriver", "Vpour", "Vpool" };

        // Our own tool state (was IH ruler / console args before the divorce).
        public static float ToolRadius = 3f;
        public static float CylHeight = 0f;   // 0 = flat disc (2m default in the brush)

        public static bool OurToolSelected(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            return piece != null && PumpedNames.Contains(piece.m_name);
        }

        // From FAT's one ObjectDB.Awake hook (FatVanillaHooks), ahead of the chunk machines and the tool icons.
        internal static void OnObjectDBAwake(ObjectDB odb)
        {
            try { Register(odb); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] V-tool piece registration threw: {ex.Message}"); }
        }

        private static void Register(ObjectDB odb)
        {
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;
            var host = odb.GetItemPrefab(HostItemName);
            if (host == null) return;
            var table = host.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null) return;

            // Drop any same-named entries (IH yaml leftovers) so ours are the
            // single source of the tool.
            table.m_pieces.RemoveAll(p =>
            {
                var pc = p != null ? p.GetComponent<Piece>() : null;
                return pc != null && Names.Contains(pc.m_name);
            });

            AddTool(table, "Vcarve", "Carve TRUE 3D terrain — sphere dig at the cursor: what the ghost "
                + "shows is exactly what gets removed. Hold LMB and DRAG to paint trenches/tunnels. "
                + "Scroll = size, PgUp/PgDn = cylinder mode (explicit height).");
            AddTool(table, "Vfill", "Fill TRUE 3D terrain — sphere fill at the cursor: what the ghost "
                + "shows is exactly what gets placed. Hold LMB and DRAG to raise runs. "
                + "Scroll = size, PgUp/PgDn = cylinder mode (explicit height).");
            AddTool(table, "Vdelete", "Restore voxel terrain to its generated state in the brush. "
                + "Scroll = radius.");
            AddTool(table, "Vcube", "3D CUBE voxel brush — click to stamp, hold LMB + drag to paint runs "
                + "of blocks. Scroll = size. Hold LeftAlt = CARVE (lines-only ghost).");
            AddTool(table, "Vsphere", "3D SPHERE voxel brush — click to stamp, drag for smooth tubes. "
                + "Scroll = size. Hold LeftAlt = CARVE (lines-only ghost).");

            AddTool(table, "Vriver", "Carve a river bed — hold LMB and DRAG the spine you want. Sweeps a "
                + "channel between samples and clears vegetation across the wet width, so vanilla's shore "
                + "sand and water ecology take over. Scroll = width, PgUp/PgDn = depth below the cursor.");
            AddTool(table, "Vpour", "Pour a water ribbon — hold LMB and DRAG to lay a smoothed water "
                + "surface along the path. Scroll = width, PgUp/PgDn = surface offset from the terrain hit.");
            AddTool(table, "Vpool", "Pool shaper (all-in-one) — click to level, carve, fill and seed a pool. "
                + "Click again downstream to auto-connect the two with a graded channel. "
                + "Scroll = size, PgUp/PgDn = water level.");

            FiresLogger.LogInfo($"[FiresVoxel] {Names.Count} V-tools registered as native {HostItemName} pieces (IH-free, admin-gated).");
        }

        private static void AddTool(PieceTable table, string name, string desc)
        {
            var go = new GameObject($"fires_vtool_{name}");
            Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            var piece = go.AddComponent<Piece>();
            piece.m_name = name;
            piece.m_description = desc;
            piece.m_icon = VAFatToolIcons.SpriteFor(name) ?? FindIcon(name);
            piece.m_category = Piece.PieceCategory.Misc;
            piece.m_repairPiece = false;
            piece.m_groundPiece = false;
            piece.m_canBeRemoved = false;
            table.m_pieces.Add(go);
        }

        // Borrow readable vanilla icons: dig-style for carve/delete,
        // raise-style for fill, stone for the 3D stamps.
        private static Sprite FindIcon(string name)
        {
            string source =
                name == "Vfill" ? "raise" :
                name == "Vcube" ? "paved_road_v2" :
                name == "Vsphere" ? "mud_road_v2" : "cultivate_v2";
            var hoe = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hoe") : null;
            var table = hoe != null ? hoe.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            if (table == null) return null;
            Sprite fallback = null;
            foreach (var p in table.m_pieces)
            {
                var pc = p != null ? p.GetComponent<Piece>() : null;
                if (pc == null || pc.m_icon == null) continue;
                if (fallback == null) fallback = pc.m_icon;
                if (p.name.ToLowerInvariant().Contains(source)) return pc.m_icon;
            }
            return fallback;
        }

        // The tools are admin-only, and the rule is asked at MENU time rather than
        // stored on the piece: 1.0 adds everything that passes the gates to the flat
        // m_availablePieces set BEFORE it reads m_category, so a piece parked in a
        // hidden category still leaks into Show All, Materials, Recent, Favorites
        // and search. Pruning has to hit every list together.
        //
        // Admins get the tools forced IN as well as left in: the pieces carry no
        // recipe, and vanilla drops anything missing from knownRecipies unless
        // no-placement-cost happens to be on, so "does the tool show up" would
        // otherwise ride on whether nocost was toggled.
        //
        // Runs AHEAD of FAP's VABuildMenuVisibility (LowerThanNormal) so that when
        // FAP is installed its pass is still the final word and its vanilla-friendly
        // rules can hide these too. FAT never references FAP — the seam is Core.
        // From the Priority.High postfix of FAT's one PieceTable.UpdateAvailable hook (FatVanillaHooks).
        internal static void ApplyToolVisibility(PieceTable table) => ToolVisibilityGate.Run(table);

        private static class ToolVisibilityGate
        {
            private static readonly AccessTools.FieldRef<PieceTable, List<List<Piece>>> ByCategory
                = AccessTools.FieldRefAccess<PieceTable, List<List<Piece>>>("m_availablePiecesByCategory");

            internal static void Run(PieceTable __instance)
            {
                try { Apply(__instance); }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresVoxel] tool visibility gate threw: {ex.Message}");
                }
            }

            private static void Apply(PieceTable table)
            {
                if (table == null || table.m_pieces == null) return;

                bool show = FiresCore.Sync.AdminSyncing.IsLocalAdmin()
                    && (FiresVoxelConfig.ShowToolsInBuildMenu == null
                        || FiresVoxelConfig.ShowToolsInBuildMenu.Value);

                var byCategory = ByCategory(table);
                foreach (var go in table.m_pieces)
                {
                    var piece = go != null ? go.GetComponent<Piece>() : null;
                    if (piece == null || !Names.Contains(piece.m_name)) continue;

                    if (!show)
                    {
                        table.m_availablePieces.Remove(piece);
                        table.m_enabledPieces.Remove(piece);
                        if (byCategory != null)
                            foreach (var list in byCategory) list.Remove(piece);
                        continue;
                    }

                    table.m_enabledPieces.Add(piece);
                    if (!table.m_availablePieces.Add(piece)) continue;
                    if (byCategory == null) continue;
                    int category = (int)piece.m_category;
                    if (category >= 0 && category < byCategory.Count)
                        byCategory[category].Add(piece);
                }
            }
        }

        // FAT's one TryPlacePiece hook. Selecting a V-tool must never place or spend anything (the drag pump owns the
        // clicks), and nothing is built over a closed zone in a one-zone-at-a-time world.
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static class Patch_Player_TryPlacePiece
        {
            private static bool Prefix(Player __instance, Piece piece, ref bool __result)
            {
                // A Chaos Hammer tool runs here instead of building: its piece is the tool, and the click is the swing.
                if (Zones.ZoneModeChunkTools.IsOurPiece(piece))
                {
                    Zones.ZoneModeChunkTools.OnPlace(__instance, piece);
                    __result = false;
                    return false;
                }
                if ((piece == null || !Names.Contains(piece.m_name)) && !Zones.ZoneModeRefusals.RefusesPlacement(__instance)) return true;
                __result = false;
                return false;
            }
        }

        // Camera zoom stands down while a V-tool is selected — the wheel is
        // the tool radius. Our own reads use raw Input.GetAxis, unaffected.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
        private static class Patch_ZInput_Scroll_VoxelTools
        {
            private static void Postfix(ref float __result)
            {
                if (__result == 0f) return;
                if (OurToolSelected(Player.m_localPlayer)) __result = 0f;
            }
        }
    }
}
