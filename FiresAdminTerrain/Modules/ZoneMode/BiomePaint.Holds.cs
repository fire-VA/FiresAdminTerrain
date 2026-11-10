using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1374 (Fire: "builds within the zone would be either preserved or wiped based off a toggle on the brush tool").
    // PRESERVE, the default: pieces are never moved, so the ground under them keeps the height it had. When a height circle is painted the
    // server works out once what stands in it (player pieces; the locations that stay) and keeps a small grid of how much of the new
    // height each spot may take: 0 at a build, 1 away from one, a smooth ramp between. The grid belongs to that circle only, so a
    // build made later on the new ground is not disturbed by this circle, and every peer builds the same ground from the same bytes.
    // WIPE, per stroke, off by default: the first request only counts and arms; a repeat within WipeArmSeconds wipes. Only player
    // pieces inside the circle go. Tombstones, portals, ships, carts and anything alive stay; chest items drop as tearing down would.
    internal static partial class BiomePaint
    {
        private const float MinHoldStep = 4f;
        private const float PieceHoldRadius = 6f, HoldRamp = 12f;
        private const int MaxHoldSide = 128;   // 0.2.1375: a drawn area can be 1 km across: its grid steps wider, up to 8 m
        private const float WipeArmSeconds = 30f;
        private const byte HoldsFileFormat = 3;
        private const int MaxHeldBytes = 1 << 22;   // 0.2.1378: a black map area's held discs share this store

        private static volatile Dictionary<uint, byte[]> s_holdMap = new Dictionary<uint, byte[]>();
        private static long s_wipeArmedBy;
        private static string s_wipeArmedKey;
        private static float s_wipeArmedUntil;

        private static float HoldStepFor(float radius) => Mathf.Max(MinHoldStep, radius * 2f / (MaxHoldSide - 1));

        private static int HoldSide(float radius) => Mathf.Clamp(Mathf.CeilToInt(radius * 2f / HoldStepFor(radius)) + 1, 2, MaxHoldSide);

        // How much of the new height this spot may take: 1 with no build near.
        private static float HoldAllows(in Circle c, float wx, float wz)
        {
            if (!s_holdMap.TryGetValue(c.Id, out var grid)) return 1f;
            if (c.Mask != null) return DiscsAllow(grid, wx, wz);
            int side = HoldSide(c.R);
            if (grid.Length != side * side) return 1f;
            float step = HoldStepFor(c.R);
            float fx = Mathf.Clamp((wx - (c.X - c.R)) / step, 0f, side - 1.001f), fz = Mathf.Clamp((wz - (c.Z - c.R)) / step, 0f, side - 1.001f);
            int ix = (int)fx, iz = (int)fz;
            float tx = fx - ix, tz = fz - iz;
            float a = Mathf.Lerp(grid[iz * side + ix], grid[iz * side + ix + 1], tx);
            float b = Mathf.Lerp(grid[(iz + 1) * side + ix], grid[(iz + 1) * side + ix + 1], tx);
            return Mathf.Lerp(a, b, tz) / 255f;
        }

        private static void MarkHold(byte[] grid, int side, in Circle c, float x, float z, float radius)
        {
            float step = HoldStepFor(c.R);
            radius += (step - MinHoldStep) * 0.5f;   // a wider grid still covers the build
            float reach = radius + HoldRamp;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((x - reach - (c.X - c.R)) / step)), x1 = Mathf.Min(side - 1, Mathf.CeilToInt((x + reach - (c.X - c.R)) / step));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((z - reach - (c.Z - c.R)) / step)), z1 = Mathf.Min(side - 1, Mathf.CeilToInt((z + reach - (c.Z - c.R)) / step));
            for (int iz = z0; iz <= z1; iz++)
                for (int ix = x0; ix <= x1; ix++)
                {
                    float dx = c.X - c.R + ix * step - x, dz = c.Z - c.R + iz * step - z;
                    float allow = Smooth(radius, reach, Mathf.Sqrt(dx * dx + dz * dz));
                    byte value = (byte)Mathf.RoundToInt(allow * 255f);
                    if (value < grid[iz * side + ix]) grid[iz * side + ix] = value;
                }
        }

        // Would this location go when the stroke ends? The same answer the swap uses (SwapOutLocations).
        private static bool LocationGoes(Vector2s zone, ZoneSystem.LocationInstance instance, List<Circle> circles)
        {
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintSwapLocations;
            if ((key != null && !key.Value) || instance.m_location == null || VanillaNeeds(instance.m_location, out _)) return false;
            float reach = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin;
            return InsideStroke(circles, instance.m_position, reach) && !BuildNear(zone, instance.m_position, reach) && !PlayerInside(zone);
        }

        // Server, once, when a height circle is painted.
        private static void BuildHolds(in Circle circle, out int pieces, out int locations)
        {
            pieces = 0; locations = 0;
            var map = new Dictionary<uint, byte[]>(s_holdMap);
            map.Remove(circle.Id);
            int side = HoldSide(circle.R);
            byte[] grid = null;
            var marked = new HashSet<long>();
            var found = new List<ZDO>();
            var one = new List<Circle> { circle };
            foreach (var zone in ZonesOf(circle))
            {
                found.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (zdo == null || zdo.GetLong(ZDOVars.s_creator) == 0L) continue;
                    var at = zdo.GetPosition();
                    float dx = at.x - circle.X, dz = at.z - circle.Z, far = circle.R + PieceHoldRadius + HoldRamp;
                    if (dx * dx + dz * dz > far * far) continue;
                    pieces++;
                    if (!marked.Add(Key(Mathf.FloorToInt(at.x / HoldStepFor(circle.R)), Mathf.FloorToInt(at.z / HoldStepFor(circle.R))))) continue;
                    if (grid == null) grid = NewHoldGrid(side);
                    MarkHold(grid, side, circle, at.x, at.z, PieceHoldRadius);
                }
            }
            var zones = ZoneSystem.instance;
            if (zones != null)
            {
                var low = ZoneSystem.GetZone(new Vector3(circle.X - circle.R, 0f, circle.Z - circle.R));
                var high = ZoneSystem.GetZone(new Vector3(circle.X + circle.R, 0f, circle.Z + circle.R));
                for (int z = low.y - 1; z <= high.y + 1; z++)
                    for (int x = low.x - 1; x <= high.x + 1; x++)
                    {
                        var zone = new Vector2s(x, z);
                        if (!zones.m_locationInstances.TryGetValue(zone, out var instance) || instance.m_location == null || LocationGoes(zone, instance, one)) continue;
                        float reach = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin;
                        float dx = instance.m_position.x - circle.X, dz = instance.m_position.z - circle.Z, far = circle.R + reach + HoldRamp;
                        if (dx * dx + dz * dz > far * far) continue;
                        locations++;
                        if (grid == null) grid = NewHoldGrid(side);
                        MarkHold(grid, side, circle, instance.m_position.x, instance.m_position.z, reach);
                    }
            }
            if (grid != null) map[circle.Id] = grid;
            s_holdMap = map;
            FiresLogger.LogDebug($"[BiomePaint] ground held under circle {circle.Id}: {pieces} piece(s), {locations} location(s) that stay ({(grid != null ? side * side : 0)} grid bytes) (0.2.1374)");
        }

        private static byte[] NewHoldGrid(int side)
        {
            var grid = new byte[side * side];
            for (int i = 0; i < grid.Length; i++) grid[i] = 255;
            return grid;
        }

        private static void DropHolds(uint id)
        {
            if (!s_holdMap.ContainsKey(id)) return;
            var map = new Dictionary<uint, byte[]>(s_holdMap);
            map.Remove(id);
            s_holdMap = map;
        }

        private static void WriteHolds(BinaryWriter writer)
        {
            var map = s_holdMap;
            writer.Write(map.Count);
            foreach (var pair in map)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value.Length);
                writer.Write(pair.Value);
            }
        }

        private static Dictionary<uint, byte[]> ReadHolds(BinaryReader reader)
        {
            var map = new Dictionary<uint, byte[]>();
            int count = FatNetGuard.Count(reader.ReadInt32(), MaxCircles, "biome paint held ground");
            for (int i = 0; i < count; i++)
            {
                uint id = reader.ReadUInt32();
                int length = FatNetGuard.Count(reader.ReadInt32(), MaxHeldBytes, "biome paint held grid");
                map[id] = reader.ReadBytes(length);
            }
            return map;
        }

        // The band and the held ground travel ahead of the list.
        private static void PackHeightData(ZPackage pkg)
        {
            pkg.Write(s_heightBand);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHolds(writer);
                writer.Flush();
                pkg.Write(stream.ToArray());
            }
        }

        private static void UnpackHeightData(ZPackage pkg)
        {
            float band = pkg.ReadSingle();
            if (!float.IsNaN(band) && band >= 0f && band <= 96f) s_heightBand = band;
            using (var reader = new BinaryReader(new MemoryStream(pkg.ReadByteArray()))) s_holdMap = ReadHolds(reader);
        }

        // ── Wipe ──────────────────────────────────────────────────
        private static bool WipeTakes(ZDO zdo, out GameObject prefab)
        {
            prefab = null;
            if (zdo == null || !zdo.IsValid() || zdo.GetLong(ZDOVars.s_creator) == 0L || zdo.GetPrefab() == PlayerHash || zdo.GetPrefab() == TombstoneHash) return false;
            if (ZoneModeChunkMachines.IsProtected(zdo) || zdo.GetBool(ZDOVars.s_tamed)) return false;
            prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab == null || prefab.GetComponent<Piece>() == null) return false;
            return prefab.GetComponent<TombStone>() == null && prefab.GetComponent<TeleportWorld>() == null && prefab.GetComponent<Ship>() == null
                && prefab.GetComponent<Vagon>() == null && prefab.GetComponent<Character>() == null;
        }

        private static int WipeRun(in Circle circle, bool apply, out int items)
        {
            int count = 0;
            items = 0;
            var zones = ZoneSystem.instance;
            var spillAt = zones != null ? SpillPoint(zones, new Vector3(circle.X, 0f, circle.Z)) : new Vector3(circle.X, 0f, circle.Z);
            long session = ZDOMan.GetSessionID();
            var found = new List<ZDO>();
            foreach (var zone in ZonesOf(circle))
            {
                found.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (!WipeTakes(zdo, out _)) continue;
                    var at = zdo.GetPosition();
                    if (!circle.Holds(at.x, at.z)) continue;
                    count++;
                    if (!apply) continue;
                    zdo.SetOwner(session);
                    DisarmRespawn(zdo);
                    items += Spill(zdo, spillAt);
                    ZDOMan.instance.DestroyZDO(zdo);
                }
            }
            return count;
        }

        // ── The black map wipe ─────────────────────────────────────
        // 0.2.1380 (Fire: "Just wipe and regenerate everything from within the red ticket"): every saved object inside the black shapes goes, builds, tombstones, portals, ships,
        // carts, tames and chests with their contents. Stays: players, what the world machinery needs to work, a location's own objects (a location that doesn't stay goes in the
        // stroke's swap; boss sites and one-per-world places stay, VanillaNeeds), and a terrain record whose zone is only partly black, since it holds the edits outside too.
        private static readonly HashSet<int> s_mapWipeKeeps = new HashSet<int>
        {
            "Player".GetStableHashCode(), "_ZoneCtrl".GetStableHashCode(), "VATile".GetStableHashCode(), "VAVoxel".GetStableHashCode(), "CustomRiver".GetStableHashCode(), "VALandIce".GetStableHashCode(),
            "VALandSnow".GetStableHashCode(), "FiresLiquidWater".GetStableHashCode(), "FiresZonesRegistry".GetStableHashCode(),
        };
        private static readonly int s_terrainRecordHash = "_TerrainCompiler".GetStableHashCode();
        // 0.2.1387 (Fire: "wipe everything"): Expand World Prefabs' two destroy rows (expand_prefabs_FAP.yaml, expand_prefabs_FAP_chests.yaml) respawn anything destroyed
        // with one of these set, whoever destroyed it, so a wipe would bring those objects back. Clear the marker first, as the owner, right before DestroyZDO.
        private static readonly int s_respawnPersist = "FAP_persist".GetStableHashCode(), s_respawnChest = "va_chest".GetStableHashCode();
        private static int s_mapWipeMarked;
        internal static bool HasRespawnMark(ZDO zdo) => zdo.GetInt(s_respawnPersist) == 1 || zdo.GetInt(s_respawnChest) == 1;
        internal static void DisarmRespawn(ZDO zdo)
        {
            if (zdo.GetInt(s_respawnPersist) == 1) zdo.Set(s_respawnPersist, 0);
            if (zdo.GetInt(s_respawnChest) == 1) zdo.Set(s_respawnChest, 0);
        }

        private static bool ZoneWhollyIn(in Circle circle, Vector2s zone)
        {
            var c = ZoneSystem.GetZonePos(zone);
            const float half = 32f;
            return circle.Holds(c.x, c.z) && circle.Holds(c.x - half, c.z - half) && circle.Holds(c.x + half, c.z - half) && circle.Holds(c.x - half, c.z + half) && circle.Holds(c.x + half, c.z + half);
        }

        private static void MapWipeRun(in Circle circle, bool apply, ref int built, ref int tombstones, ref int other, ref int keptLocation, ref int keptTerrain)
        {
            var zones = ZoneSystem.instance;
            if (zones == null) return;
            CollectKeepOut(zones, circle);
            long session = ZDOMan.GetSessionID();
            var found = new List<ZDO>();
            foreach (var zone in ZonesOf(circle))
            {
                found.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (zdo == null || !zdo.IsValid() || s_mapWipeKeeps.Contains(zdo.GetPrefab())) continue;
                    var at = zdo.GetPosition();
                    if (!circle.Holds(at.x, at.z) || ZoneModeChunkMachines.IsProtected(zdo) || VerdantsAscent.Terrain.Voxel.FiresSkyIslands.TrySkylandAt(at, out _)) continue;
                    bool creator = zdo.GetLong(ZDOVars.s_creator) != 0L;
                    if (!creator && InKeepOut(at)) { keptLocation++; continue; }
                    if (zdo.GetPrefab() == s_terrainRecordHash && !ZoneWhollyIn(circle, zone)) { keptTerrain++; continue; }
                    if (zdo.GetPrefab() == TombstoneHash) tombstones++;
                    else if (creator) built++;
                    else other++;
                    if (HasRespawnMark(zdo)) s_mapWipeMarked++;
                    if (!apply) continue;
                    zdo.SetOwner(session);
                    DisarmRespawn(zdo);
                    ZDOMan.instance.DestroyZDO(zdo);
                }
            }
        }

        // True = wiped, go on and paint. False = this request only counted and armed, or a shape could not be read (report says so).
        private static bool MapWipeGate(string key, long asker, List<int[]> areas, out string report)
        {
            float now = Time.unscaledTime;
            bool apply = asker == s_wipeArmedBy && key == s_wipeArmedKey && now < s_wipeArmedUntil;
            int built = 0, tombstones = 0, other = 0, keptLocation = 0, keptTerrain = 0;
            s_mapWipeMarked = 0;
            foreach (var area in areas)
            {
                var mask = new PaintMapMask(area[0], area[1]);
                if (!mask.Ensure()) { report = "A black map area could not be read: nothing wiped from it, nothing painted."; return false; }
                var shape = new Circle { Mask = mask, X = mask.CentreX, Z = mask.CentreZ, R = mask.BoundR };
                MapWipeRun(shape, apply, ref built, ref tombstones, ref other, ref keptLocation, ref keptTerrain);
            }
            string counts = $"{built} built object(s), {tombstones} tombstone(s), {other} other object(s), {s_mapWipeMarked} of them marked to respawn and stopped from it; kept {keptLocation} location object(s), {keptTerrain} terrain record(s) on the edge";
            if (!apply)
            {
                ZNet.instance.Save(true);   // a save on disk from before the wipe
                s_wipeArmedBy = asker; s_wipeArmedKey = key; s_wipeArmedUntil = Time.unscaledTime + WipeArmSeconds;   // after the scan and the save, which take time on a big world
                report = $"Black map wipe check, nothing changed yet: {counts}. This cannot be undone. The world was saved. Paint again within {WipeArmSeconds:0} s to wipe.";
                FiresLogger.LogInfo("[BiomePaint] " + report + " (0.2.1380)");
                return false;
            }
            s_wipeArmedUntil = 0f;
            report = $"Wiped {counts}.";
            FiresLogger.LogInfo("[BiomePaint] black map " + report + " (0.2.1380)");
            return true;
        }

        // True = go on and paint. False = this request only counted and armed (report says so).
        private static bool WipeGate(string key, in Circle circle, long asker, out string report)
        {
            float now = Time.unscaledTime;
            if (asker != s_wipeArmedBy || key != s_wipeArmedKey || now >= s_wipeArmedUntil)
            {
                int would = WipeRun(circle, false, out _);
                s_wipeArmedBy = asker; s_wipeArmedKey = key; s_wipeArmedUntil = now + WipeArmSeconds;
                ZNet.instance.Save(true);   // a save on disk from before the wipe. wsr needs the server stopped, so a snapshot is taken ahead of the session.
                report = $"Wipe check, nothing changed yet: {would} piece(s) in this circle would go; tombstones, portals, ships, carts and tames stay, and this cannot be undone. The world was saved. Paint again within {WipeArmSeconds:0} s to wipe.";
                FiresLogger.LogInfo("[BiomePaint] " + report);
                return false;
            }
            s_wipeArmedUntil = now + WipeArmSeconds;
            int gone = WipeRun(circle, true, out int dropped);
            report = $"Wiped {gone} piece(s), {dropped} chest item(s) lie at the centre.";
            FiresLogger.LogInfo($"[BiomePaint] wipe round ({circle.X:0},{circle.Z:0}) r {circle.R:0}: {report} (0.2.1374)");
            return true;
        }

        // ── The stroke's location swap ─────────────────────────────────────
        // Fire: a new biome resets "veg/location/spawner etc" on the area. A location wholly inside one of the stroke's circles, not one vanilla needs and
        // with no build or player at it, goes; the stroke's own job then adds the new ground's locations on the new heights.
        // Nests are vegetation and go with it; the zone's spawner reads the painted biome already.
        private static int SwapOutLocations(ZoneSystem zones, List<Circle> circles, List<Vector2s> stroke)
        {
            var tally = new SwapTally();
            var generated = GeneratedZones(zones);
            int gone = 0;
            foreach (var zone in stroke)
            {
                if (!zones.m_locationInstances.TryGetValue(zone, out var old) || !LocationGoes(zone, old, circles)) continue;
                if (old.m_placed && generated.Contains(zone)) RemovePlaced(zones, zone, old, tally, default, false);
                zones.m_locationInstances.Remove(zone);
                s_swaps.Remove(zone);
                s_swapsChanged = true;
                tally.Icons |= HasIcon(old.m_location);
                gone++;
            }
            if (gone > 0) Finish(zones, tally);
            return gone;
        }
    }
}
