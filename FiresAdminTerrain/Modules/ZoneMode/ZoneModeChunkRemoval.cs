using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chaos Hammer's remover: an open chunk taken back out of the world so a different one can be placed there. It
    // reuses the re-close pair the wrong-seed undo already had (Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md): the zone's objects
    // are set aside as <x>_<y>.override, which fires_zones_undo parks again, and ZoneModeOpening.Close makes it closed
    // sea. Then every peer forgets the zone's voxel chunks, because a destroyed anchor leaves its edited land standing.
    internal static class ZoneModeChunkRemoval
    {
        private const string RpcDropVoxels = "FiresAdminTerrain_ZoneVoxelsDropped";
        // Vanilla sends destroyed objects on its next update, after this frame's RPCs. A peer that dropped its chunks
        // before its anchors were gone would read the old edits straight back in.
        private const float DropDelaySeconds = 2f;

        private static ZRoutedRpc s_registeredOn;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcDropVoxels, RPC_DropVoxels);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk remover RPC register failed: {ex.Message}"); }
        }

        // Server only.
        internal static bool TryRemove(Vector2s zone, out string report)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                report = "Chunks are taken back by the server.";
                return false;
            }
            if (!ZoneModeRegistry.ModeActive)
            {
                report = "Only a one-zone-at-a-time world has chunks to take back.";
                return false;
            }
            if (ZNet.instance.IsSaving())
            {
                report = ZoneModeChunkBlueprint.WorldSavingMessage;
                return false;
            }
            if (FiresWorldLoadGuard.Refused)
            {
                report = ZoneModeChunkBlueprint.RefusedLoadMessage;
                return false;
            }
            if (!ZoneModeRegistry.IsOpen(zone))
            {
                report = $"({zone.x},{zone.y}) is already closed sea.";
                return false;
            }
            if (HoldsProtected(zone))
            {
                report = $"({zone.x},{zone.y}) holds the starter Chunk Generator or the first hammer's chest, which a chunk "
                    + "world cannot lose. Take back another chunk.";
                return false;
            }
            int worldBefore = ZDOMan.instance.NrOfObjects();
            int zoneBefore = ZoneObjectCount(zone);
            if (!ZoneModePark.TrySetAsideOpenZone(zone, out int removed, out string problem))
            {
                report = $"({zone.x},{zone.y}) was not taken back: {problem}.";
                return false;
            }
            FiresLogger.LogInfo($"[FiresZones] remover counts for ({zone.x},{zone.y}): world {worldBefore:N0} -> "
                + $"{ZDOMan.instance.NrOfObjects():N0} object(s), zone {zoneBefore:N0} -> {ZoneObjectCount(zone):N0}, "
                + $"{removed:N0} kept in .override.");
            ZoneModeOpening.Close(new[] { zone }, "taken back with the Chaos Hammer");
            var pkg = new ZPackage();
            pkg.Write(zone);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcDropVoxels, pkg);
            report = $"({zone.x},{zone.y}) is closed sea again: {removed} object(s) set aside, so a different chunk can be "
                + $"placed there. fires_zones_undo {zone.x} {zone.y} parks what stood there again.";
            return true;
        }

        private static int ZoneObjectCount(Vector2s zone)
        {
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            ZoneModePark.CollectZoneObjects(zone, objects, portals);
            return objects.Count + portals.Count;
        }

        private static bool HoldsProtected(Vector2s zone)
        {
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            ZoneModePark.CollectZoneObjects(zone, objects, portals);
            foreach (var zdo in objects)
                if (ZoneModeChunkMachines.IsProtected(zdo)) return true;
            return false;
        }

        // Everybody includes the sender, so the server drops its own copy through here as well.
        private static void RPC_DropVoxels(long sender, ZPackage pkg)
        {
            var zone = pkg.ReadVector2s();
            var host = FiresAdminTerrain.Instance;
            if (host == null) return;
            host.StartCoroutine(DropAfterAnchors(zone));
        }

        private static IEnumerator DropAfterAnchors(Vector2s zone, string what = "was taken back")
        {
            yield return new WaitForSeconds(DropDelaySeconds);
            int dropped = VoxelWorld.DropZoneChunks(zone);
            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) {what}: {dropped} voxel chunk(s) forgotten, so it "
                + "streams in again as closed sea.");
        }

        // 0.2.753 (Fire 10-06: "this chunk is supposed to be 'closed' but still visually looks open"): a CLOSE drops the zone's
        // voxel chunks on this peer too, as taking a chunk back always did. Closing dropped only its objects, so the old terrain
        // stayed on screen with the sea drawn over part of it until the peer next streamed the zone out.
        internal static void DropZoneVoxelsSoon(Vector2s zone)
        {
            var host = FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(DropAfterAnchors(zone, "was closed"));
        }
    }
}
