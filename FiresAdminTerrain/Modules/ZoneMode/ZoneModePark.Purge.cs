using System.Collections.Generic;

namespace VerdantsAscent.Terrain.Zones
{
    internal static partial class ZoneModePark
    {
        // Server, at world load before any peer joins: takes exactly these ZDOs out of the live world AND out of the save.
        // DestroyZDO alone is not enough when it empties a save chunk: vanilla 1.0 writes only chunks that still hold objects
        // and never drops an emptied chunk from its mapping, so the old chunk file loads them all again next time (the 81
        // off-map _ZoneCtrl in 00_00__0_1.chunk, back every session since 09-28). The park's chunk-store reset makes the next
        // save write only what is live; the old files are orphans then, and vanilla's next load removes them.
        internal static int PurgeFromSave(List<ZDO> zdos)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || zdos == null || zdos.Count == 0 || ZNet.instance == null || !ZNet.instance.IsServer()) return 0;
            var bySector = ObjectsBySectorRef(zdoMan);
            var portals = PortalObjectsRef(zdoMan);
            var byId = ObjectsByIdRef(zdoMan);
            int purged = 0;
            foreach (var zdo in zdos)
            {
                if (zdo == null || !byId.ContainsKey(zdo.m_uid)) continue;
                zdoMan.m_onZDODestroyed?.Invoke(zdo);
                // An off-map ZDO may sit in its own sector's list or in sector zero (vanilla's place for outside positions).
                var sector = zdo.GetSectorIndex();
                bySector[(int)sector.Sector]?.Remove(zdo);
                bySector[(int)ZoneSystem.SectorZero.Sector]?.Remove(zdo);
                if (portals.TryGetValue(sector, out var inPortals))
                {
                    inPortals.Remove(zdo);
                    if (inPortals.Count == 0) portals.Remove(sector);
                }
                byId.Remove(zdo.m_uid);
                ZDOPool.Release(zdo);
                purged++;
            }
            if (purged > 0) ResetChunkStore(zdoMan);
            return purged;
        }
    }
}
