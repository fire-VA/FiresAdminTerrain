using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Location swaps (paint build 2; Fire, 2026-09-27, by popup; Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md, "Location swaps").
    // A zone's location follows the paint over its centre, by one rule of the zone's OWN location (the world's) and the
    // newest circle there, so a paint, Undo, Redo, Restore and newer paint all settle the same way:
    //  - Protected places never swap (the chunk seeds' list, ZoneModeSwaps.MustStay): the start temple, FAT's progression
    //    list and every one-per-world place. Nor does a location with no circle over it, under a Restore circle, or at sea.
    //  - Under a blend with locations of its own, one of them stands wherever one fits ("the blend's own take over").
    //  - Otherwise the own location stays while vanilla's biome and blend rules still allow it there; when they do not,
    //    one of vanilla's locations for the new ground that fits the spot takes its place, and when none fits the zone
    //    loses its location.
    //  - "Fits" is vanilla's own placement test at the spot (ZoneSystem.GenerateLocations): biome and blend rules,
    //    altitude, slope, forest, vegetation, distance from the world's centre and from its like. A pick is seeded by the
    //    world, the zone and the brush, weighted by how common each location is, and a standing replacement is kept while
    //    it still qualifies, so a changed location list never re-rolls a placed one.
    // The server remembers each swapped zone's own location in the paint file. A zone nobody has built only changes its
    // plan. A built zone swaps once the server has it loaded and nobody is inside its dungeon; until then it waits and is
    // retried. The old location's own objects go through vanilla DestroyZDO, never a player's build, a tombstone or a tamed
    // creature: its chests' items drop at its centre, and a dungeon's tombstones, tamed creatures and loose items are lifted
    // there. A moment later vanilla places the new one, and the ground the old one cleared grows again.
    internal static partial class BiomePaint
    {
        // Dungeon interiors stand 5000 m over their entrance; nothing else a zone holds is this high.
        private const float InteriorFloorY = 2500f;
        private const float SameSpot = 0.5f;
        private const float SpillLift = 1f;
        private const float SpillScatter = 1.5f;
        private const int SpillColumns = 8;
        private const int SpillRows = 8;
        private const string SpillName = "fires_biomepaint_spill";
        private const float SettleSeconds = 2f;
        private const float FullVegetation = 1f;
        private const uint HashBasis = 2166136261u;
        private const uint HashPrime = 16777619u;
        private const int HashShift = 15;

        private static readonly int PlayerHash = "Player".GetStableHashCode();
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();

        // A swapped zone: its own location (the world's) and the one standing in its place (empty: none).
        private struct Swap
        {
            internal string Own;
            internal Vector3 OwnAt;
            internal string Standing;
            internal Vector3 StandingAt;
        }

        private struct Placement
        {
            internal Vector2s Zone;
            internal float Due;
        }

        // What a location's prefab puts in the world as objects of their own: its networked parts, its spawners' creatures
        // and its proxy; and whether it has a dungeon.
        private sealed class Parts
        {
            internal readonly HashSet<int> Prefabs = new HashSet<int>();
            internal bool Dungeon;
        }

        private sealed class SwapTally
        {
            internal int Objects, Items, Lifted, Waiting;
            internal bool Icons;
            internal readonly List<string> Lines = new List<string>();
        }

        private static readonly Dictionary<Vector2s, Swap> s_swaps = new Dictionary<Vector2s, Swap>();
        private static readonly HashSet<Vector2s> s_waiting = new HashSet<Vector2s>();
        private static readonly List<Placement> s_placements = new List<Placement>();
        private static readonly Dictionary<string, Parts> s_parts = new Dictionary<string, Parts>();
        private static readonly Dictionary<int, bool> s_itemByPrefab = new Dictionary<int, bool>();
        private static readonly Dictionary<string, ZoneSystem.ZoneLocation> s_locationsByName = new Dictionary<string, ZoneSystem.ZoneLocation>();
        private static readonly List<ZoneSystem.ZoneLocation> s_locationPool = new List<ZoneSystem.ZoneLocation>();
        private static readonly List<ZoneSystem.ZoneLocation> s_fitting = new List<ZoneSystem.ZoneLocation>();
        private static readonly List<Vector3> s_fittingAt = new List<Vector3>();
        private static ZoneSystem s_locationsFor;
        private static bool s_swapsChanged;
        private static bool s_settledWorld;
        private static float s_settleAt;
        private static MethodInfo s_placeLocations;
        private static MethodInfo s_sendLocationIcons;

        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        // ── After a paint (server) ──────────────────────────────────────────
        // Every zone the changed circle covers settles its location. A line for the painter's report.
        private static string SwapLocations(in Circle painted)
        {
            var zones = ZoneSystem.instance;
            if (zones == null || !ResolveLocations()) return string.Empty;
            var tally = new SwapTally();
            foreach (var zone in ZonesOf(painted)) Settle(zones, zone, tally, painted, true);
            return Finish(zones, tally);
        }

        // From Tick, on the server: once after the world loads, every zone a circle or a swap touches settles; then the
        // zones that waited to be loaded settle as they load.
        private static void SettleLocations()
        {
            if (Time.time < s_settleAt) return;
            s_settleAt = Time.time + SettleSeconds;
            var zones = ZoneSystem.instance;
            if (zones == null || !zones.LocationsGenerated || zones.m_locationInstances.Count == 0) return;
            if (!ZoneModeChunkBlueprint.ServerReady(out _) || !ResolveLocations() || !Resolve()) return;
            var tally = new SwapTally();
            if (!s_settledWorld)
            {
                s_settledWorld = true;
                var seen = new HashSet<Vector2s>();
                foreach (var circle in s_circles)
                    foreach (var zone in ZonesOf(circle))
                        if (seen.Add(zone)) Settle(zones, zone, tally, default, false);
                foreach (var zone in new List<Vector2s>(s_swaps.Keys))
                    if (seen.Add(zone)) Settle(zones, zone, tally, default, false);
            }
            else if (s_waiting.Count > 0)
            {
                foreach (var zone in new List<Vector2s>(s_waiting))
                    if (HeightmapOf(zones, zone) != null) Settle(zones, zone, tally, default, false);
            }
            else return;
            string line = Finish(zones, tally);
            if (line.Length > 0) FiresLogger.LogInfo("[BiomePaint]" + line);
        }

        private static void Settle(ZoneSystem zones, Vector2s zone, SwapTally tally, in Circle painted, bool painting)
        {
            var instances = zones.m_locationInstances;
            bool standing = instances.TryGetValue(zone, out var current) && current.m_location != null;
            bool remembered = s_swaps.TryGetValue(zone, out var swap);
            if (remembered && !StandsAsRemembered(swap, standing, current))
            {
                s_swaps.Remove(zone);
                s_swapsChanged = true;
                remembered = false;
            }
            ZoneSystem.ZoneLocation own;
            Vector3 ownAt;
            if (remembered)
            {
                if (!s_locationsByName.TryGetValue(swap.Own, out own))
                {
                    FiresLogger.LogWarning($"[BiomePaint] zone ({zone.x},{zone.y})'s own location '{swap.Own}' is not in this game; its swap is forgotten.");
                    s_swaps.Remove(zone);
                    s_swapsChanged = true;
                    return;
                }
                ownAt = swap.OwnAt;
            }
            else if (standing)
            {
                own = current.m_location;
                ownAt = current.m_position;
            }
            else return;

            var now = standing ? current.m_location : null;
            var nowAt = standing ? current.m_position : ownAt;
            var want = Want(zones, zone, own, ownAt, now, nowAt, out var wantAt);
            bool built = GeneratedZones(zones).Contains(zone);
            if (Same(want, now) && (want == null || Near(wantAt, nowAt)))
            {
                s_waiting.Remove(zone);
                if (remembered && standing && !current.m_placed && built) QueuePlacement(zones, zone);
                return;
            }
            bool placed = standing && current.m_placed;
            if (built && (placed || remembered))
            {
                if (HeightmapOf(zones, zone) == null || PlayerInside(zone))
                {
                    if (s_waiting.Add(zone)) tally.Waiting++;
                    return;
                }
                if (placed) RemovePlaced(zones, zone, current, tally, painted, painting);
            }
            s_waiting.Remove(zone);
            if (want == null) instances.Remove(zone);
            else instances[zone] = new ZoneSystem.LocationInstance { m_location = want, m_position = wantAt, m_placed = false };
            if (Same(want, own) && Near(wantAt, ownAt)) s_swaps.Remove(zone);
            else s_swaps[zone] = new Swap
            {
                Own = LocationName(own), OwnAt = ownAt, Standing = want != null ? LocationName(want) : string.Empty, StandingAt = wantAt,
            };
            s_swapsChanged = true;
            // A built zone gets the new location only where one stood (or a swap of ours was cut short): vanilla never
            // placed an unplaced plan in a built zone, and neither does this.
            bool placeNow = built && want != null && (placed || remembered);
            if (placeNow)
            {
                ClearFootprint(zones, zone, want, wantAt);
                QueuePlacement(zones, zone);
            }
            tally.Icons |= HasIcon(now) || HasIcon(want);
            string was = now != null ? LocationName(now) : "no location";
            string line = want == null ? $"{was} at ({nowAt.x:0},{nowAt.z:0}) is removed: nothing of the new ground fits there"
                : Same(want, own) ? $"{LocationName(own)} comes back at ({wantAt.x:0},{wantAt.z:0}) in place of {was}"
                : $"{was} at ({nowAt.x:0},{nowAt.z:0}) becomes {LocationName(want)}";
            tally.Lines.Add(line);
            FiresLogger.LogInfo($"[BiomePaint] zone ({zone.x},{zone.y}): {line}"
                + (placeNow ? " (built: placed in a moment)." : built ? " (built, never placed: only its plan changes)." : " (planned: placed when the zone is built)."));
        }

        private static bool StandsAsRemembered(in Swap swap, bool standing, in ZoneSystem.LocationInstance current)
            => string.IsNullOrEmpty(swap.Standing)
                ? !standing
                : standing && LocationName(current.m_location) == swap.Standing && Near(current.m_position, swap.StandingAt);

        private static string Finish(ZoneSystem zones, SwapTally tally)
        {
            if (s_swapsChanged)
            {
                s_swapsChanged = false;
                Save();
            }
            if (tally.Icons) SendIcons(zones);
            if (tally.Lines.Count == 0 && tally.Waiting == 0) return string.Empty;
            string report = tally.Lines.Count > 0 ? $" Locations: {string.Join("; ", tally.Lines)}." : string.Empty;
            if (tally.Objects > 0) report += $" {tally.Objects} of their objects went, {tally.Items} chest item(s) lie at their centres"
                + (tally.Lifted > 0 ? $", {tally.Lifted} thing(s) came up out of a dungeon" : "") + ".";
            if (tally.Waiting > 0) report += $" {tally.Waiting} built zone(s) not loaded here (or with someone in its dungeon) swap their location when they are.";
            return report;
        }

        // ── The rule ────────────────────────────────────────────────────────
        private static ZoneSystem.ZoneLocation Want(ZoneSystem zones, Vector2s zone, ZoneSystem.ZoneLocation own, Vector3 ownAt,
            ZoneSystem.ZoneLocation now, Vector3 nowAt, out Vector3 at)
        {
            at = ownAt;
            var generator = WorldGenerator.instance;
            if (generator == null || ZoneModeSwaps.MustStay(own) || !TryNewest(ownAt.x, ownAt.z, out var circle, out var sector)
                || circle.Brush == Brush.Restore || generator.GetBiome(ownAt) == Heightmap.Biome.Ocean) return own;
            var blend = circle.Brush == Brush.Blend ? FindBlend(circle.Blend) : null;
            if (blend?.m_addLocations != null && blend.m_addLocations.Count > 0)
            {
                if (now != null && IsOneOf(blend.m_addLocations, now))
                {
                    at = nowAt;
                    return now;
                }
                if (IsOneOf(blend.m_addLocations, own)) return own;
                if (TryPick(zones, blend.m_addLocations, zone, ownAt, circle, sector, out var pick, out at)) return pick;
            }
            at = ownAt;
            if (Belongs(own, circle.Ground, sector)) return own;
            if (now != null && !Same(now, own) && Belongs(now, circle.Ground, sector))
            {
                at = nowAt;
                return now;
            }
            return TryPick(zones, s_locationPool, zone, ownAt, circle, sector, out var any, out at) ? any : null;
        }

        // The newest circle over a point, Restore included (its sector is null), skipping a brush whose blend is missing.
        private static bool TryNewest(float wx, float wz, out Circle circle, out BiomeSector sector)
        {
            circle = default;
            sector = null;
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0 || !snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return false;
            foreach (int index in candidates)
            {
                var c = snapshot.Circles[index];
                if (!c.Holds(wx, wz) || (c.Brush != Brush.Restore && snapshot.Sectors[index] == null)) continue;
                circle = c;
                sector = snapshot.Sectors[index];
                return true;
            }
            return false;
        }

        // Vanilla's own three checks for a location in a sector: its biome, the blend it needs, and no blend blocking it.
        private static bool Belongs(ZoneSystem.ZoneLocation location, Heightmap.Biome ground, BiomeSector sector)
        {
            if ((location.m_biome & ground) == Heightmap.Biome.None) return false;
            if (location.AltBiomeParent != null && !Carries(sector, location.AltBiomeParent)) return false;
            return !Blocks(sector, location.m_name);
        }

        private static bool Carries(BiomeSector sector, string blend)
        {
            if (sector?.AltBiomes == null) return false;
            foreach (var alt in sector.AltBiomes)
                if (alt != null && alt.m_name == blend) return true;
            return false;
        }

        private static bool Blocks(BiomeSector sector, string location)
        {
            if (sector?.AltBiomes == null) return false;
            foreach (var alt in sector.AltBiomes)
                if (alt?.m_blockLocationNames != null && alt.m_blockLocationNames.Contains(location)) return true;
            return false;
        }

        // Never a one-per-world place, a protected one, the start or one vanilla itself could never place.
        private static bool Eligible(ZoneSystem.ZoneLocation location)
            => location != null && location.m_enable && location.m_quantity > 0 && location.m_prefab.IsValid
               && location.m_biomeArea != (Heightmap.BiomeArea)0 && !location.m_unique && !location.m_centerFirst
               && !ZoneModeSwaps.MustStay(location);

        private static bool TryPick(ZoneSystem zones, List<ZoneSystem.ZoneLocation> from, Vector2s zone, Vector3 spot, in Circle circle,
            BiomeSector sector, out ZoneSystem.ZoneLocation pick, out Vector3 at)
        {
            pick = null;
            at = spot;
            s_fitting.Clear();
            s_fittingAt.Clear();
            long total = 0;
            foreach (var location in from)
            {
                if (!Eligible(location) || !Fits(zones, location, zone, spot, circle.Ground, sector, out var fitAt)) continue;
                s_fitting.Add(location);
                s_fittingAt.Add(fitAt);
                total += location.m_quantity;
            }
            if (total == 0) return false;
            long roll = Roll(zone, circle) % total;
            for (int i = 0; i < s_fitting.Count; i++)
            {
                roll -= s_fitting[i].m_quantity;
                if (roll >= 0) continue;
                pick = s_fitting[i];
                at = s_fittingAt[i];
                return true;
            }
            return false;
        }

        // Vanilla's GenerateLocations test at one spot, the spot moved in until the location's radius fits its zone.
        // Surround-check vegetation and the biome-area test are left out: they rank or measure the world's own map.
        private static bool Fits(ZoneSystem zones, ZoneSystem.ZoneLocation location, Vector2s zone, Vector3 spot,
            Heightmap.Biome ground, BiomeSector sector, out Vector3 at)
        {
            at = InZone(zone, spot, Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius));
            if (!Belongs(location, ground, sector)) return false;
            float fromCentre = new Vector2(at.x, at.z).magnitude;
            if (location.m_minDistance != 0f && fromCentre < location.m_minDistance) return false;
            if (location.m_maxDistance != 0f && fromCentre > location.m_maxDistance) return false;
            if (location.m_minDistanceFromCenter > 0f && fromCentre < location.m_minDistanceFromCenter) return false;
            if (location.m_maxDistanceFromCenter > 0f && fromCentre > location.m_maxDistanceFromCenter) return false;
            var generator = WorldGenerator.instance;
            at.y = generator.GetHeight(at.x, at.z, out Color mask);
            float altitude = at.y - zones.m_waterLevel;
            if (altitude < location.m_minAltitude || altitude > location.m_maxAltitude) return false;
            if (location.m_inForest)
            {
                float forest = WorldGenerator.GetForestFactor(at);
                if (forest < location.m_forestTresholdMin || forest > location.m_forestTresholdMax) return false;
            }
            generator.GetTerrainDelta(at, location.m_exteriorRadius, out float delta, out _);
            if (delta > location.m_maxTerrainDelta || delta < location.m_minTerrainDelta) return false;
            if (location.m_minimumVegetation > 0f && mask.a <= location.m_minimumVegetation) return false;
            if (location.m_maximumVegetation < FullVegetation && mask.a >= location.m_maximumVegetation) return false;
            if (location.m_minDistanceFromSimilar > 0f && NearSimilar(zones, location, zone, at, location.m_minDistanceFromSimilar, false)) return false;
            return !(location.m_maxDistanceFromSimilar > 0f) || NearSimilar(zones, location, zone, at, location.m_maxDistanceFromSimilar, true);
        }

        // Vanilla's HaveLocationInRange over the world's instances, this zone's own left out.
        private static bool NearSimilar(ZoneSystem zones, ZoneSystem.ZoneLocation location, Vector2s zone, Vector3 at, float radius, bool maxGroup)
        {
            string group = maxGroup ? location.m_groupMax : location.m_group;
            string name = LocationName(location);
            float reach = radius * radius;
            foreach (var pair in zones.m_locationInstances)
            {
                var other = pair.Value.m_location;
                if (other == null || (pair.Key.x == zone.x && pair.Key.y == zone.y)) continue;
                bool alike = LocationName(other) == name
                             || (!string.IsNullOrEmpty(group) && (maxGroup ? other.m_groupMax : other.m_group) == group);
                if (alike && ZoneModeStartSpots.FlatDistanceSquared(pair.Value.m_position, at) <= reach) return true;
            }
            return false;
        }

        private static Vector3 InZone(Vector2s zone, Vector3 spot, float radius)
        {
            var centre = ZoneSystem.GetZonePos(zone);
            float room = Mathf.Max(0f, ZoneSystem.c_ZoneSizeHalf - radius);
            return new Vector3(Mathf.Clamp(spot.x, centre.x - room, centre.x + room), spot.y, Mathf.Clamp(spot.z, centre.z - room, centre.z + room));
        }

        private static uint Roll(Vector2s zone, in Circle circle)
        {
            uint h = Mix(HashBasis, (uint)(WorldGenerator.instance?.GetSeed() ?? 0));
            h = Mix(Mix(h, (uint)zone.x), (uint)zone.y);
            return Mix(h, (uint)KeyOf(circle.Brush, circle.Ground, circle.Blend).GetStableHashCode());
        }

        private static uint Mix(uint h, uint value)
        {
            h = (h ^ value) * HashPrime;
            return h ^ (h >> HashShift);
        }

        private static bool IsOneOf(List<ZoneSystem.ZoneLocation> list, ZoneSystem.ZoneLocation location)
        {
            foreach (var entry in list)
                if (Same(entry, location)) return true;
            return false;
        }

        private static bool Same(ZoneSystem.ZoneLocation a, ZoneSystem.ZoneLocation b)
            => ReferenceEquals(a, b) || (a != null && b != null && LocationName(a) == LocationName(b));

        private static bool Near(Vector3 a, Vector3 b) => ZoneModeStartSpots.FlatDistanceSquared(a, b) <= SameSpot * SameSpot;

        private static string LocationName(ZoneSystem.ZoneLocation location) => location.m_prefab.Name;

        private static bool HasIcon(ZoneSystem.ZoneLocation location) => location != null && (location.m_iconPlaced || location.m_iconAlways);

        // ── Taking a built location away ────────────────────────────────────
        private static void RemovePlaced(ZoneSystem zones, Vector2s zone, ZoneSystem.LocationInstance instance, SwapTally tally,
            in Circle painted, bool painting)
        {
            var parts = PartsOf(zones, instance.m_location);
            var centre = instance.m_position;
            float reach = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin;
            var spillAt = SpillPoint(zones, centre);
            int before = ZDOMan.instance.NrOfObjects();
            long session = ZDOMan.GetSessionID();
            var found = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
            int removed = 0, items = 0, lifted = 0;
            foreach (var zdo in found)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                int prefab = zdo.GetPrefab();
                var at = zdo.GetPosition();
                bool inside = parts.Dungeon && at.y > InteriorFloorY;
                if (prefab == PlayerHash || zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                if (!inside && (!parts.Prefabs.Contains(prefab) || ZoneModeStartSpots.FlatDistanceSquared(at, centre) > reach * reach)) continue;
                if (ZoneModeChunkMachines.IsProtected(zdo) || FiresSkyIslands.TrySkylandAt(at, out _)) continue;
                if (prefab == TombstoneHash || zdo.GetBool(ZDOVars.s_tamed) || (inside && IsItem(prefab)))
                {
                    if (!inside) continue;
                    zdo.SetOwner(session);
                    Lift(zdo, Scatter(spillAt));
                    lifted++;
                    continue;
                }
                zdo.SetOwner(session);
                items += Spill(zdo, spillAt);
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            tally.Objects += removed;
            tally.Items += items;
            tally.Lifted += lifted;
            FiresLogger.LogInfo($"[BiomePaint] {LocationName(instance.m_location)} in zone ({zone.x},{zone.y}) taken away: {removed} of its object(s) "
                + $"go through DestroyZDO ({before} objects in the world before), {items} chest item(s) dropped at its centre, "
                + $"{lifted} thing(s) lifted out of its dungeon.");
            if (!instance.m_location.m_clearArea) return;
            s_pending.Add(new PendingGrowth
            {
                Circle = new Circle { X = centre.x, Z = centre.z, R = instance.m_location.m_exteriorRadius },
                Zones = new List<Vector2s> { zone },
                Due = Time.time + GrowDelaySeconds,
                Label = $"the ground {LocationName(instance.m_location)} held",
                HasExcept = painting,
                Except = painted,
            });
        }

        private static Parts PartsOf(ZoneSystem zones, ZoneSystem.ZoneLocation location)
        {
            string name = LocationName(location);
            if (s_parts.TryGetValue(name, out var parts)) return parts;
            parts = new Parts();
            if (zones.m_locationProxyPrefab != null) parts.Prefabs.Add(Utils.GetPrefabName(zones.m_locationProxyPrefab).GetStableHashCode());
            location.m_prefab.Load();
            try
            {
                var asset = location.m_prefab.Asset;
                if (asset != null)
                {
                    foreach (var view in asset.GetComponentsInChildren<ZNetView>(true))
                        parts.Prefabs.Add(Utils.GetPrefabName(view.gameObject).GetStableHashCode());
                    foreach (var spawner in asset.GetComponentsInChildren<CreatureSpawner>(true))
                        if (spawner.m_creaturePrefab != null) parts.Prefabs.Add(Utils.GetPrefabName(spawner.m_creaturePrefab).GetStableHashCode());
                    foreach (var area in asset.GetComponentsInChildren<SpawnArea>(true))
                        foreach (var spawn in area.m_prefabs)
                            if (spawn?.m_prefab != null) parts.Prefabs.Add(Utils.GetPrefabName(spawn.m_prefab).GetStableHashCode());
                    parts.Dungeon = asset.GetComponentInChildren<DungeonGenerator>(true) != null;
                }
            }
            finally { location.m_prefab.Release(); }
            s_parts[name] = parts;
            return parts;
        }

        private static bool IsItem(int prefabHash)
        {
            if (s_itemByPrefab.TryGetValue(prefabHash, out bool item)) return item;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            item = prefab != null && prefab.GetComponent<ItemDrop>() != null;
            s_itemByPrefab[prefabHash] = item;
            return item;
        }

        private static Vector3 SpillPoint(ZoneSystem zones, Vector3 centre)
        {
            var at = centre;
            if (zones.GetGroundHeight(centre, out float ground)) at.y = ground;
            at.y += SpillLift;
            return at;
        }

        private static Vector3 Scatter(Vector3 at)
            => at + new Vector3(UnityEngine.Random.Range(-SpillScatter, SpillScatter), 0f, UnityEngine.Random.Range(-SpillScatter, SpillScatter));

        // A chest's items (vanilla keeps them in the ZDO) dropped on the ground, as tearing the chest down would.
        private static int Spill(ZDO zdo, Vector3 at)
        {
            var data = zdo.GetByteArray(ZDOVars.s_items);
            if (data == null || data.Length == 0) return 0;
            var inventory = new Inventory(SpillName, null, SpillColumns, SpillRows);
            try { inventory.Load(new ZPackage(data)); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BiomePaint] a chest's items at ({at.x:0},{at.z:0}) could not be read ({ex.Message}); they go with it.");
                return 0;
            }
            int dropped = 0;
            foreach (var item in inventory.GetAllItems())
            {
                if (item?.m_dropPrefab == null) continue;
                ItemDrop.DropItem(item, item.m_stack, Scatter(at), Quaternion.identity);
                dropped++;
            }
            return dropped;
        }

        private static void Lift(ZDO zdo, Vector3 at)
        {
            var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (view != null)
            {
                view.transform.position = at;
                var body = view.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = at;
                    body.velocity = Vector3.zero;
                }
            }
            zdo.SetPosition(at);
        }

        private static bool PlayerInside(Vector2s zone)
        {
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                var at = player.transform.position;
                var theirs = ZoneSystem.GetZone(at);
                if (at.y > InteriorFloorY && theirs.x == zone.x && theirs.y == zone.y) return true;
            }
            return false;
        }

        // ── Placing the new one ─────────────────────────────────────────────
        // The new location's clear ground is cleared of growth now, and vanilla places it a moment later, once the old
        // objects' colliders are gone and its prefab has loaded.
        private static void ClearFootprint(ZoneSystem zones, Vector2s zone, ZoneSystem.ZoneLocation location, Vector3 at)
        {
            if (!location.m_clearArea) return;
            s_keepOut.Clear();
            ClearGrowth(new Circle { X = at.x, Z = at.z, R = location.m_exteriorRadius }, new List<Vector2s> { zone });
        }

        private static void QueuePlacement(ZoneSystem zones, Vector2s zone)
        {
            if (HeightmapOf(zones, zone) == null)
            {
                s_waiting.Add(zone);
                return;
            }
            foreach (var placement in s_placements)
                if (placement.Zone.x == zone.x && placement.Zone.y == zone.y) return;
            s_placements.Add(new Placement { Zone = zone, Due = Time.time + GrowDelaySeconds });
        }

        // From Tick, on the server, before the growth.
        private static void PlacePending()
        {
            if (s_placements.Count == 0) return;
            var zones = ZoneSystem.instance;
            if (zones == null || ZDOMan.instance == null || !ResolveLocations() || !Resolve()) return;
            for (int i = s_placements.Count - 1; i >= 0; i--)
            {
                var placement = s_placements[i];
                if (Time.time < placement.Due) continue;
                if (!zones.m_locationInstances.TryGetValue(placement.Zone, out var instance) || instance.m_location == null || instance.m_placed)
                {
                    s_placements.RemoveAt(i);
                    continue;
                }
                var hmap = HeightmapOf(zones, placement.Zone);
                if (hmap == null)
                {
                    s_placements.RemoveAt(i);
                    s_waiting.Add(placement.Zone);
                    continue;
                }
                if (zones.ShouldDelayProxyLocationSpawning(instance.m_location.Hash)) continue;
                s_placements.RemoveAt(i);
                Place(zones, placement.Zone, hmap, instance);
            }
        }

        private static void Place(ZoneSystem zones, Vector2s zone, Heightmap hmap, ZoneSystem.LocationInstance instance)
        {
            var holder = new GameObject("fires_biomepaint_location");
            int before = ZDOMan.instance.NrOfObjects();
            try
            {
                var clearAreas = Activator.CreateInstance(typeof(List<>).MakeGenericType(s_clearAreaType));
                s_placeLocations.Invoke(zones, new object[]
                {
                    zone, ZoneSystem.GetZonePos(zone), holder.transform, hmap, clearAreas, ZoneSystem.SpawnMode.Full, new List<GameObject>(),
                });
                var at = instance.m_position;
                FiresLogger.LogInfo($"[BiomePaint] {LocationName(instance.m_location)} placed in zone ({zone.x},{zone.y}) at ({at.x:0},{at.z:0}): "
                    + $"{ZDOMan.instance.NrOfObjects() - before} object(s) ({before} before).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BiomePaint] {LocationName(instance.m_location)} could not be placed in zone ({zone.x},{zone.y}): "
                    + $"{ex.InnerException?.Message ?? ex.Message}");
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        private static void SendIcons(ZoneSystem zones)
        {
            if (s_sendLocationIcons == null) return;
            try { s_sendLocationIcons.Invoke(zones, new object[] { ZRoutedRpc.Everybody }); }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] the map's location icons could not be sent again: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        // ── Lookups ─────────────────────────────────────────────────────────
        private static bool ResolveLocations()
        {
            s_placeLocations ??= AccessTools.Method(typeof(ZoneSystem), "PlaceLocations");
            s_sendLocationIcons ??= AccessTools.Method(typeof(ZoneSystem), "SendLocationIcons");
            EnsureLocations();
            return s_placeLocations != null;
        }

        private static void EnsureLocations()
        {
            var zones = ZoneSystem.instance;
            if (ReferenceEquals(zones, s_locationsFor)) return;
            s_locationsFor = zones;
            s_locationsByName.Clear();
            s_locationPool.Clear();
            s_parts.Clear();
            if (zones?.m_locations == null) return;
            foreach (var location in zones.m_locations)
            {
                if (location == null || !location.m_enable || !location.m_prefab.IsValid) continue;
                string name = LocationName(location);
                if (!s_locationsByName.ContainsKey(name)) s_locationsByName[name] = location;
                s_locationPool.Add(location);
            }
        }

        // ── Beside the world (the paint file, format 2) ─────────────────────
        private static void WriteSwaps(BinaryWriter writer)
        {
            writer.Write(s_swaps.Count);
            foreach (var pair in s_swaps)
            {
                writer.Write((int)pair.Key.x);
                writer.Write((int)pair.Key.y);
                writer.Write(pair.Value.Own ?? string.Empty);
                WritePoint(writer, pair.Value.OwnAt);
                writer.Write(pair.Value.Standing ?? string.Empty);
                WritePoint(writer, pair.Value.StandingAt);
            }
        }

        private static void ReadSwaps(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var zone = new Vector2s(reader.ReadInt32(), reader.ReadInt32());
                s_swaps[zone] = new Swap { Own = reader.ReadString(), OwnAt = ReadPoint(reader), Standing = reader.ReadString(), StandingAt = ReadPoint(reader) };
            }
        }

        private static void WritePoint(BinaryWriter writer, Vector3 point)
        {
            writer.Write(point.x);
            writer.Write(point.y);
            writer.Write(point.z);
        }

        private static Vector3 ReadPoint(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static void ResetLocations()
        {
            s_swaps.Clear();
            s_waiting.Clear();
            s_placements.Clear();
            s_swapsChanged = false;
            s_settledWorld = false;
            s_settleAt = 0f;
            s_locationsFor = null;
        }
    }
}
