using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Voxel
{
    // THE IH DIVORCE — the V-tools are OUR OWN pieces with our own input:
    // no infinity_tools.yaml, no IH ruler, no console-command round-trip.
    //   * They live on the Chaos Hammer's Voxel tab, admin-gated, with every other admin terrain tool (Fire,
    //     2026-09-27, Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md); ZoneModeChunkTools puts them in its table. The vanilla
    //     Hammer no longer carries them. The admin seam is Core (AdminSyncing), never FAP.
    //   * Selecting one never places anything (TryPlacePiece blocked); the
    //     VoxelBrushDrag pump owns every click.
    //   * Plain scroll = radius (camera zoom suppressed while selected),
    //     PgUp/PgDn = cylinder height / level offset. The ghost cages are the
    //     only visual.
    internal static class VoxelToolPieces
    {
        private const string OldHostItemName = "Hammer";
        private const string ToolObjectPrefix = "fires_vtool_";

        private static readonly List<GameObject> s_tools = new List<GameObject>();

        // The tool pieces, in menu order, for the Chaos Hammer's Voxel tab.
        internal static IReadOnlyList<GameObject> Tools => s_tools;

        internal static readonly HashSet<string> Names = new HashSet<string>
        { "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere", "Vlevel", "Vskin", "Vriver", "Vpour", "Vpool", "Vpond", "Vfall", "Vmop" };

        // Tools the VoxelBrushDrag pump actually drives. Only these take the
        // scroll wheel off the camera — a registered-but-unwired tool must not
        // swallow input and read as broken.
        internal static readonly HashSet<string> PumpedNames = new HashSet<string>
        { "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere", "Vlevel", "Vskin", "Vriver", "Vpour", "Vpool", "Vpond", "Vfall", "Vmop" };

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
            ClearFromVanillaHammer(odb);
            s_tools.RemoveAll(tool => tool == null);
            if (s_tools.Count > 0) return;

            // 0.2.751: the texts say what the 0.2.748 stroke tools do (click and drag at the draw speed, PgUp/PgDn offset).
            AddTool("Vcarve", "Carve voxel terrain — click and DRAG to sculpt a trench at a constant depth under the ground "
                + "(it rides the terrain as it was before the stroke). Scroll = width, PgUp/PgDn = depth (Shift = fine, hold = faster).");
            AddTool("Vfill", "Fill voxel terrain — click and DRAG to raise the ground by a constant height along the stroke. "
                + "Scroll = width, PgUp/PgDn = height (Shift = fine, hold = faster).");
            AddTool("Vdelete", "Restore voxel terrain to its generated state — click and DRAG over the edits to undo them. "
                + "Scroll = width, PgUp/PgDn = how far above and below the surface it restores.");
            AddTool("Vcube", "3D CUBE voxel brush — click and DRAG to lay runs of blocks. Scroll = size, PgUp/PgDn = lift "
                + "off the ground. Hold LeftAlt = CARVE (lines-only ghost).");
            AddTool("Vsphere", "3D SPHERE voxel brush — click and DRAG for smooth tubes and mounds. Scroll = size, "
                + "PgUp/PgDn = lift off the ground. Hold LeftAlt = CARVE (lines-only ghost).");
            AddTool("Vlevel", "Level voxel ground — click and DRAG: everything under the disc is cut or filled to the height "
                + "you started at plus the PgUp/PgDn offset (no limit; the disc shows it). LeftAlt + drag = smooth instead. Scroll = size.");
            AddTool("Vskin", "Skin over holes — click and DRAG across a cave opening or a broken lattice: a thin smooth shell is "
                + "laid exactly at the surrounding ground's height (roads stay flat), only where there is a hole; the cave below "
                + "stays hollow and intact ground is never raised. Scroll = size, PgUp/PgDn = shell thickness.");

            AddTool("Vriver", "Carve a river bed — hold LMB and DRAG the spine you want. Sweeps a "
                + "channel between samples and clears vegetation across the wet width, so vanilla's shore "
                + "sand and water ecology take over. Scroll = width, PgUp/PgDn = depth below the cursor.");
            AddTool("Vpour", "Pour a water ribbon — hold LMB and DRAG to lay a smoothed water "
                + "surface along the path. Scroll = width, PgUp/PgDn = surface offset from the terrain hit.");
            AddTool("Vpool", "Pool shaper (all-in-one) — click to level, carve, fill and seed a pool, or click existing water "
                + "to use it. Click again (a new spot or another water) to auto-carve and fill a river between the two, routed "
                + "along the low ground; click the higher one first. LeftAlt + click = fill the dip under the cursor to its rim "
                + "(nothing carved). Scroll = size, PgUp/PgDn = water level.");
            AddTool("Vpond", "Pond — a flat circle of water at the height you set: PgUp/PgDn nudge it over the ground under the "
                + "crosshair (Shift = fine), scroll = radius. Click fills the dip under the circle with water flat at that height. "
                + "Red with posts = it would spill there (lower it or shrink it); nothing is laid until it holds all round.");
            AddTool("Vfall", "Waterfall — click the ground just before an edge (a cliff, a sky island's rim): a "
                + "spillway at the lip, a curtain with mist down to the ground or water below, and a pool where it "
                + "lands. Scroll = width. LeftAlt + click = remove the falls in the ring.");
            AddTool("Vmop", "Water mop — hold LMB and DRAG to remove water in the ring for good: generated rivers, "
                + "lakes and ponds (sky islands too) AND water you drew. Water outside the ring stays. Redraw with "
                + "Vpour/Vpool/Vriver. Scroll = size. LeftAlt = restore what was mopped there.");

            FiresLogger.LogInfo($"[FiresVoxel] {s_tools.Count} V-tools built for the Chaos Hammer's Voxel tab (IH-free, admin-gated).");
        }

        // A world saved with an older FAT, or IH yaml leftovers, may still list same-named tools on the vanilla
        // Hammer; the Chaos Hammer is their only home now.
        private static void ClearFromVanillaHammer(ObjectDB odb)
        {
            var hammer = odb.GetItemPrefab(OldHostItemName);
            var table = hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            if (table == null) return;
            int removed = table.m_pieces.RemoveAll(p =>
            {
                var pc = p != null ? p.GetComponent<Piece>() : null;
                return pc != null && Names.Contains(pc.m_name);
            });
            if (removed > 0) FiresLogger.LogInfo($"[FiresVoxel] {removed} V-tool(s) taken off the vanilla {OldHostItemName}.");
        }

        private static void AddTool(string name, string desc)
        {
            var go = new GameObject(ToolObjectPrefix + name);
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
            s_tools.Add(go);
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
        // the tool radius. Our own reads (FatInput.WheelNotches, 0.2.791) pass ReadingRaw and see the wheel.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
        private static class Patch_ZInput_Scroll_VoxelTools
        {
            private static void Postfix(ref float __result)
            {
                if (__result == 0f || FatInput.ReadingRaw) return;
                var player = Player.m_localPlayer;
                if (OurToolSelected(player) || Zones.ZoneModeAreaTool.WantsWheel(player)) __result = 0f;
            }
        }
    }
}
