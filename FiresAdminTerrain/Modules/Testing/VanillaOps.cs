using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Testing
{
    // The terrain-op pieces the vanilla tools place, found on the tools themselves (the hoe's and the cultivator's build
    // pieces, the pickaxe's hit-terrain spawn) by what their TerrainOp does, not by prefab name, so a renamed or modded
    // piece is still found. Placing one is what the tool does: instantiate it where the ghost stands; its TerrainOp.Awake
    // applies the edit and destroys the object.
    internal static class VanillaOps
    {
        private const string HoeItem = "Hoe";
        private const string CultivatorItem = "Cultivator";
        private const string PickaxeItem = "PickaxeAntler";

        // 1.0's Hoe pieces by their tokens first: Level Ground (mud_road_v2) no longer levels at all - it is m_level 0 /
        // m_smooth 1 - so the old settings-only match found nothing ([visual], R67 2026-09-29). The settings stay as the
        // fallback for a renamed or modded piece.
        internal static GameObject HoeRaise() => Piece(HoeItem, "$piece_raise", s => s.m_raise && s.m_raiseDelta > 0f);

        internal static GameObject HoeLevel() => Piece(HoeItem, "$piece_levelground", s => (s.m_level || s.m_smooth) && !s.m_raise
                                                                                        && s.m_paintType != TerrainModifier.PaintType.Paved);

        internal static GameObject HoePaved() => Piece(HoeItem, "$piece_pavedroad", s => s.m_paintCleared && s.m_paintType == TerrainModifier.PaintType.Paved);

        internal static GameObject HoePath() => Piece(HoeItem, "$piece_path", s => !s.m_level && !s.m_raise && !s.m_smooth && s.m_paintCleared
                                                                                && s.m_paintType == TerrainModifier.PaintType.Dirt);

        // How far Raise Ground lifts its centre.
        internal static float HoeRaiseDelta()
        {
            var op = HoeRaise()?.GetComponent<TerrainOp>();
            return op != null ? op.m_settings.m_raiseDelta : 0f;
        }

        internal static GameObject Cultivate() => Piece(CultivatorItem, s => s.m_paintCleared && s.m_paintType == TerrainModifier.PaintType.Cultivate);

        internal static GameObject Replant() => Piece(CultivatorItem, s => s.m_paintCleared && s.m_paintType == TerrainModifier.PaintType.Reset);

        internal static GameObject PickaxeDig()
        {
            var shared = Shared(PickaxeItem);
            return shared?.m_spawnOnHitTerrain;
        }

        internal static void Place(GameObject prefab, Vector3 at)
        {
            if (prefab == null) throw new InvalidOperationException("the vanilla tool's terrain-op piece was not found");
            Object.Instantiate(prefab, at, Quaternion.identity);
        }

        private static GameObject Piece(string item, Func<TerrainOp.Settings, bool> does) => Piece(item, null, does);

        private static GameObject Piece(string item, string token, Func<TerrainOp.Settings, bool> does)
        {
            var pieces = Shared(item)?.m_buildPieces?.m_pieces;
            if (pieces == null) return null;
            if (token != null)
                foreach (var piece in pieces)
                    if (piece != null && piece.GetComponent<TerrainOp>() != null && piece.GetComponent<Piece>()?.m_name == token) return piece;
            foreach (var piece in pieces)
            {
                var op = piece != null ? piece.GetComponent<TerrainOp>() : null;
                if (op != null && does(op.m_settings)) return piece;
            }
            return null;
        }

        private static ItemDrop.ItemData.SharedData Shared(string item)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop?.m_itemData?.m_shared;
        }
    }
}
