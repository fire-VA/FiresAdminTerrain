using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // THE IH DIVORCE — the V-tools are OUR OWN hoe pieces with our own input:
    // no infinity_tools.yaml, no IH ruler, no console-command round-trip.
    //   * Pieces register straight into the hoe's piece table (after IH's,
    //     replacing any same-named yaml leftovers).
    //   * Selecting one never places anything (TryPlacePiece blocked); the
    //     VoxelBrushDrag pump owns every click.
    //   * Plain scroll = radius (camera zoom suppressed while selected),
    //     PgUp/PgDn = cylinder height. The ghost cages are the only visual.
    internal static class VoxelToolPieces
    {
        internal static readonly HashSet<string> Names = new HashSet<string>
        { "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere" };

        // Our own tool state (was IH ruler / console args before the divorce).
        public static float ToolRadius = 3f;
        public static float CylHeight = 0f;   // 0 = flat disc (2m default in the brush)

        public static bool OurToolSelected(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            return piece != null && Names.Contains(piece.m_name);
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class Patch_ObjectDB_Awake_RegisterVoxelTools
        {
            [HarmonyPostfix]
            private static void Postfix(ObjectDB __instance)
            {
                try { Register(__instance); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] V-tool piece registration threw: {ex.Message}"); }
            }
        }

        private static void Register(ObjectDB odb)
        {
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;
            var hoe = odb.GetItemPrefab("Hoe");
            if (hoe == null) return;
            var table = hoe.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
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
            Debug.Log("[FiresVoxel] V-tools registered as native hoe pieces (IH-free).");
        }

        private static void AddTool(PieceTable table, string name, string desc)
        {
            var go = new GameObject($"fires_vtool_{name}");
            Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            var piece = go.AddComponent<Piece>();
            piece.m_name = name;
            piece.m_description = desc;
            piece.m_icon = FindIcon(name);
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

        // Selecting a V-tool must never place/spend anything — the drag pump
        // owns the clicks.
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static class Patch_Player_TryPlacePiece_BlockVoxelTools
        {
            private static bool Prefix(Piece piece, ref bool __result)
            {
                if (piece == null || !Names.Contains(piece.m_name)) return true;
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
