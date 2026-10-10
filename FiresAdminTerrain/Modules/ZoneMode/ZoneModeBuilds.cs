using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // What counts as built: a Piece prefab someone placed (its ZDO has a creator). Pieces a location or the world
    // spawned have no creator and are not builds.
    //
    // A converted world's town comes over with no creators at all (SanctuaryAshlands, 2026-09-27: "0 built piece(s)" in
    // its whole start area), so what the start objects stand on, avoid or replace is any placed piece (IsPlaced).
    internal static class ZoneModeBuilds
    {
        private static readonly Dictionary<int, bool> s_pieceByPrefab = new Dictionary<int, bool>();

        internal static bool IsBuilt(ZDO zdo) => zdo != null && zdo.GetLong(ZDOVars.s_creator) != 0L && IsPiecePrefab(zdo.GetPrefab());

        internal static bool IsPlaced(ZDO zdo) => zdo != null && IsPiecePrefab(zdo.GetPrefab());

        // A prefab not registered yet is asked about again next time.
        internal static bool IsPiecePrefab(int prefabHash)
        {
            if (s_pieceByPrefab.TryGetValue(prefabHash, out bool isPiece)) return isPiece;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            if (prefab == null) return false;
            isPiece = prefab.GetComponent<Piece>() != null;
            s_pieceByPrefab[prefabHash] = isPiece;
            return isPiece;
        }
    }
}
