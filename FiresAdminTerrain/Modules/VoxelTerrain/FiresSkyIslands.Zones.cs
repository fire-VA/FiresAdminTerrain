using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // SKYLANDS — the one-zone-at-a-time world's floating islands. A fixed lattice of candidate sites (one
    // roll per SkylandCell square) comes alive as open zones reach within SkylandReach, so more of them
    // appear on the horizon as the world grows. Each is a small classic island: a grassy dome on top, a
    // ribbed stone spire underneath, and often a pond whose rill runs to the rim and spills a short fall
    // that thins out in mid-air. Activated sites join the frozen island registry like any other island,
    // so they persist and reach clients through the same sync. New skylands stand on shapes rather than discs,
    // and some rise together as archipelagos (FiresSkyIslands.Shape.cs).
    internal static partial class FiresSkyIslands
    {
        private const byte SkylandKind = 4;
        private const float SkylandCell = 288f;
        private const float SkylandChance = 0.22f;
        private const float SkylandReach = 520f;
        private const float SkylandMaxRadius = 64f;
        private const float SkylandDeepRadius = 30f;
        private const float SkylandBigDepthShare = 0.6f;
        private const float SkylandMinBase = 110f;
        private const float SkylandBaseSpan = 110f;
        private const float SkylandGroundClearance = 30f;
        private const float SkylandRimReach = 0.97f;
        private const int SkylandSalt = 0x5C1A;
        private const int RibSalt = 0x5C43;
        private const int RoundnessSalt = 0x5C44;
        private const int FallDropSalt = 0x5C45;
        private const float IslandContactMargin = 2f;

        internal static bool ZoneSkylandsReady => !s_awaitingServer;

        private static float SpireRibs(in Island isl) => 5f + Mathf.Floor(Hash(0, 0, isl.Variety, RibSalt) * 5f);

        // 0 = a sharp spire, 1 = a thick round bowl.
        private static float Roundness(in Island isl) => Hash(0, 0, isl.Variety, RoundnessSalt);

        // The belly under a skyland: a superellipse profile that ends anywhere from a round bowl to a needle tip, broken
        // up by lumps, knobs and ribs. Its brim follows the rim's style (cut back under a lip, rolling like a hillside, or
        // sheer), and a shaped skyland's keel thins with its width.
        private static float SpireDepth(in Island isl, float wx, float wz, float rr)
        {
            float round = Roundness(in isl);
            float centre = Mathf.Lerp(0.75f, 2f, round);
            float rim = Lerp3(BrimCutout, BrimSlope, BrimCliff, EdgeStyle(in isl, wx, wz));
            float profile = Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(rr, centre)), 1f / rim);
            float angle = Mathf.Atan2(wz - isl.Z, wx - isl.X);
            float twist = N(in isl, wx * 0.05f, isl.Seed + 3f, wz * 0.05f) * 3f;
            float ribs = 0.5f + 0.5f * Mathf.Sin(angle * SpireRibs(in isl) + twist);
            float knobs = N(in isl, wx * 0.11f + 5f, isl.Seed + 29f, wz * 0.11f);
            float lumps = N(in isl, wx * 0.045f + 11f, isl.Seed + 47f, wz * 0.045f);
            float shape = 0.70f + 0.14f * ribs * (1f - 0.6f * round) + 0.12f * knobs + 0.24f * (lumps - 0.5f);
            float thin = isl.Shape != null ? Mathf.Clamp01(isl.Shape.Width(wx, wz) / isl.Shape.HeartWidth) : 1f;
            return Mathf.Max(RimThickness, isl.MaxDepth * Mathf.Lerp(1f, 0.55f, round) * profile * shape * thin);
        }

        private static float SkylandBottomAt(in Island isl, float wx, float wz)
            => Mathf.Min(SkylandBaseTopAt(in isl, wx, wz, out float rr) - SpireDepth(in isl, wx, wz, rr), TopAt(in isl, wx, wz) - RimThickness);

        // The lowest island underside over this column, when an island covers it.
        internal static bool TryBottomAt(float wx, float wz, out float bottom)
        {
            bottom = float.MaxValue;
            if (!Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) { bottom = float.NaN; return false; }
            bool any = false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = wx - isl.OffX, sz = wz - isl.OffZ;
                if (!Covers(in isl, sx, sz)) continue;
                float b = (IsSkyland(isl.Kind) ? SkylandBottomAt(in isl, sx, sz) : isl.Bot) + isl.OffY;
                if (b < bottom) bottom = b;
                any = true;
            }
            // 0.2.737: a miss leaves NaN, never float.MaxValue (Run C read "underside 3.4e38" as a height).
            if (!any) bottom = float.NaN;
            return any;
        }

        // 0.2.738: the underside the VOXELS are built with. SkylandColumn takes it from the CARVED top (ponds, rills and island
        // water dug in), so under a carved bed the body reaches lower than SkylandBottomAt (the uncarved top) says: Run C's
        // ops stood on a bed at 204.33 under a recorded underside of 207.67. Dearer than TryBottomAt (it carves the column),
        // so only the terrain-edit path uses it; per-frame probes keep TryBottomAt.
        internal static bool TryExactBottomAt(float wx, float wz, out float bottom)
        {
            bottom = float.NaN;
            if (!Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            float low = float.MaxValue;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = wx - isl.OffX, sz = wz - isl.OffZ;
                if (!Covers(in isl, sx, sz)) continue;
                float b;
                if (!IsSkyland(isl.Kind)) b = isl.Bot;
                else if (SkylandColumn(in isl, sx, sz, out _, out float column, out _)) b = Mathf.Min(column, SkylandBottomAt(in isl, sx, sz));
                else b = SkylandBottomAt(in isl, sx, sz);
                low = Mathf.Min(low, b + isl.OffY);
            }
            if (low == float.MaxValue) return false;
            bottom = low;
            return true;
        }

        internal static bool IsOnIslandExact(Vector3 point)
            => TryExactBottomAt(point.x, point.z, out float bottom) && point.y > bottom - IslandContactMargin;

        // Is this point on (or in) an island rather than down on the ground below it?
        internal static bool IsOnIsland(Vector3 point)
            => TryBottomAt(point.x, point.z, out float bottom) && point.y > bottom - IslandContactMargin;

        internal static bool IsOnZoneSkyland(Vector3 point) => Zones.ZoneModeRegistry.ModeActive && IsOnIsland(point);

        // The top of the zone skyland a point stands on or over, from the island's shape rather than its colliders.
        internal static bool TrySkylandTopAt(Vector3 point, out float top)
        {
            top = 0f;
            if (!Zones.ZoneModeRegistry.ModeActive || !Enabled || !s_index.TryGetValue(CellKey(point.x, point.z), out var list))
                return false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = point.x - isl.OffX, sz = point.z - isl.OffZ;
                if (!IsSkyland(isl.Kind) || !Covers(in isl, sx, sz)) continue;
                if (point.y - isl.OffY <= SkylandBottomAt(in isl, sx, sz) - IslandContactMargin) continue;
                top = FarTopAt(in isl, sx, sz) + isl.OffY;   // 0.2.763: the density surface the voxels draw (carved, rim-rounded)
                return true;
            }
            return false;
        }

        // The lowest island underside within `range` of a point (horizontally), when any island is that close.
        internal static bool TryLowestBottomNear(Vector3 point, float range, out float bottom)
        {
            bottom = float.MaxValue;
            if (!Enabled) return false;
            foreach (var isl in s_islands)
            {
                float dx = WorldX(in isl) - point.x, dz = WorldZ(in isl) - point.z;
                float reach = range + isl.R;
                if (dx * dx + dz * dz > reach * reach) continue;
                if (isl.Bot + isl.OffY < bottom) bottom = isl.Bot + isl.OffY;
            }
            return bottom < float.MaxValue;
        }

        // ── Activation (server) ─────────────────────────────────────────────────────────────────────
        // Every lattice site within reach of an open zone that isn't in the set yet: the members of any archipelago
        // first, then the single skylands, which never rise inside an archipelago's reach. `groundAt` answers the REAL
        // ground (the zone gate would read closed land as sea floor), so an island clears the land it floats over even
        // before that land opens. Returns the indices of the islands it added.
        internal static void ActivateZoneSkylands(IEnumerable<Vector2s> openZones, int seed,
            System.Func<float, float, float> groundAt, System.Func<float, float, bool> wetAt, List<int> added)
        {
            added.Clear();
            // Keyed where each island stands: a placed copy keeps its source's fields, and keying it by them would mark the
            // source's lattice site as risen before it ever has.
            var present = new HashSet<long>();
            foreach (var isl in s_islands) present.Add(SiteKey(WorldX(in isl), WorldZ(in isl)));
            var zones = new List<Vector2s>(openZones);
            var cells = new HashSet<long>();
            var formations = new HashSet<long>();
            foreach (var zone in zones)
            {
                GatherCells(cells, zone, SkylandReach, SkylandCell);
                GatherCells(formations, zone, SkylandReach + ArchipelagoExtent, ArchipelagoCell);
            }
            foreach (long cell in formations)
            {
                int ax = (int)(cell >> 32), az = (int)(uint)cell;
                if (!TryArchipelagoCentre(ax, az, seed, out float fx, out float fz) || !WithinReach(fx, fz, zones, SkylandReach + ArchipelagoExtent))
                    continue;
                var formation = FormationAt(ax, az, seed, fx, fz, groundAt, wetAt);
                foreach (var member in formation.Members)
                {
                    long key = SiteKey(member.X, member.Z);
                    if (present.Contains(key) || s_refused.Contains(key) || !WithinReach(member.X, member.Z, zones, SkylandReach)) continue;
                    if (OverlapsLive(in member, formation.Keys))
                    {
                        s_refused.Add(key);
                        continue;
                    }
                    AddIsland(in member);
                    present.Add(key);
                    added.Add(s_islands.Count - 1);
                }
            }
            foreach (long cell in cells)
            {
                int cx = (int)(cell >> 32), cz = (int)(uint)cell;
                if (!TrySkylandCentre(cx, cz, seed, out float x, out float z, out float r)) continue;
                long key = SiteKey(x, z);
                if (present.Contains(key) || s_refused.Contains(key) || !WithinReach(x, z, zones, SkylandReach)) continue;
                var isl = SingleSkyland(cx, cz, seed, x, z, r, groundAt, wetAt);
                if (OverlapsLive(in isl, null))
                {
                    s_refused.Add(key);
                    continue;
                }
                AddIsland(in isl);
                present.Add(key);
                added.Add(s_islands.Count - 1);
            }
            if (added.Count == 0) return;
            s_any = true;
            s_seeded = true;
        }

        private sealed class Formation
        {
            internal readonly List<Island> Members = new List<Island>();
            internal readonly HashSet<long> Keys = new HashSet<long>();
        }

        private const float SkylandGap = 12f;
        private static readonly Dictionary<long, Formation> s_formations = new Dictionary<long, Formation>();
        private static readonly HashSet<long> s_refused = new HashSet<long>();

        private static void ClearFormations()
        {
            s_formations.Clear();
            s_refused.Clear();
        }

        // An archipelago's members, laid out once per session: the layout is pure lattice math over the real ground.
        private static Formation FormationAt(int ax, int az, int seed, float x, float z, System.Func<float, float, float> groundAt,
            System.Func<float, float, bool> wetAt)
        {
            long cell = ((long)ax << 32) ^ (uint)az;
            if (s_formations.TryGetValue(cell, out var formation)) return formation;
            formation = new Formation();
            LayOutArchipelago(ax, az, seed, x, z, groundAt, wetAt, formation.Members);
            foreach (var member in formation.Members) formation.Keys.Add(SiteKey(member.X, member.Z));
            s_formations[cell] = formation;
            return formation;
        }

        private static void GatherCells(HashSet<long> cells, Vector2s zone, float reach, float size)
        {
            float zx = zone.x * 64f, zz = zone.y * 64f;
            int cx0 = Mathf.FloorToInt((zx - reach) / size), cx1 = Mathf.FloorToInt((zx + reach) / size);
            int cz0 = Mathf.FloorToInt((zz - reach) / size), cz1 = Mathf.FloorToInt((zz + reach) / size);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                    cells.Add(((long)cx << 32) ^ (uint)cz);
        }

        private static long SiteKey(float x, float z) => ((long)Mathf.RoundToInt(x) << 32) ^ (uint)Mathf.RoundToInt(z);

        private static bool WithinReach(float x, float z, List<Vector2s> zones, float reach)
        {
            foreach (var zone in zones)
            {
                float dx = x - zone.x * 64f, dz = z - zone.y * 64f;
                if (dx * dx + dz * dz <= reach * reach) return true;
            }
            return false;
        }

        // Would this skyland crowd one already in the sky? Members of its own archipelago are laid out against each other.
        // Both are measured where they stand, so a placed copy is tested at its new spot.
        private static bool OverlapsLive(in Island candidate, HashSet<long> formation)
        {
            float cx = WorldX(in candidate), cz = WorldZ(in candidate);
            foreach (var other in s_islands)
            {
                float ox = WorldX(in other), oz = WorldZ(in other);
                if (formation != null && formation.Contains(SiteKey(ox, oz))) continue;
                float dx = cx - ox, dz = cz - oz;
                float reach = candidate.R + other.R + SkylandGap;
                if (dx * dx + dz * dz < reach * reach) return true;
            }
            return false;
        }

        // Where a single skyland's lattice cell puts it, and its bounding radius, when the cell holds one and no bigger
        // neighbour claims the spot.
        private static bool TrySkylandCentre(int cx, int cz, int seed, out float x, out float z, out float r)
        {
            SkylandCandidate(cx, cz, seed, out x, out z, out r);
            float rim = FatWorldSize.Radius - 700f;   // 0.2.721: 9300 at vanilla size
            return Hash(cx, cz, seed, SkylandSalt) < SkylandChance && x * x + z * z <= rim * rim
                   && !InArchipelago(x, z, r + SkylandGap, seed) && !Outranked(cx, cz, seed, x, z, r);
        }

        // A cell's site and size: most skylands are small to large, one in twelve or so a great one of four to six zones.
        private static void SkylandCandidate(int cx, int cz, int seed, out float x, out float z, out float r)
        {
            x = (cx + 0.2f + 0.6f * Hash(cx, cz, seed, SkylandSalt + 1)) * SkylandCell;
            z = (cz + 0.2f + 0.6f * Hash(cx, cz, seed, SkylandSalt + 2)) * SkylandCell;
            float h = Hash(cx, cz, seed, SkylandSalt + 3);
            r = Hash(cx, cz, seed, SkylandSalt + 9) < GreatChance
                ? GreatMinRadius + h * (GreatMaxRadius - GreatMinRadius)
                : SingleMinRadius + h * h * (SingleMaxRadius - SingleMinRadius);
        }

        // Would a bigger neighbouring site crowd this one? The bigger skyland takes the spot, so a great skyland never loses
        // its place to a small one that happened to rise first.
        private static bool Outranked(int cx, int cz, int seed, float x, float z, float r)
        {
            int reach = Mathf.CeilToInt((GreatMaxRadius + r + SkylandGap) / SkylandCell) + 1;
            float rim = FatWorldSize.Radius - 700f;   // 0.2.721: 9300 at vanilla size
            for (int nz = cz - reach; nz <= cz + reach; nz++)
                for (int nx = cx - reach; nx <= cx + reach; nx++)
                {
                    if ((nx == cx && nz == cz) || Hash(nx, nz, seed, SkylandSalt) >= SkylandChance) continue;
                    SkylandCandidate(nx, nz, seed, out float ox, out float oz, out float otherR);
                    if (otherR < r || (otherR == r && (nx > cx || (nx == cx && nz > cz)))) continue;
                    float dx = ox - x, dz = oz - z, gap = r + otherR + SkylandGap;
                    if (dx * dx + dz * dz >= gap * gap || ox * ox + oz * oz > rim * rim) continue;
                    if (!InArchipelago(ox, oz, otherR + SkylandGap, seed)) return true;
                }
            return false;
        }

        // A single skyland: a family shape fitted in its site's radius, hung at the site's altitude unless the ground under
        // it stands too high for that.
        private static Island SingleSkyland(int cx, int cz, int seed, float x, float z, float r, System.Func<float, float, float> groundAt,
            System.Func<float, float, bool> wetAt)
        {
            var isl = new Island
            {
                X = x, Z = z, R = r, Kind = ShapedKind, RillAng = float.NaN,
                Seed = seed * 0.61803f + cx * 13.37f + cz * 31.1f,
            };
            isl.Shape = FamilyShape(ShapeKey(in isl), x, z, r, r >= GreatMinRadius, out _);
            float altitude = SkylandMinBase + Hash(cx, cz, seed, SkylandSalt + 4) * SkylandBaseSpan;
            return Hang(isl, Hash(cx, cz, seed, SkylandSalt + 5), altitude, GroundUnder(isl.Shape, groundAt),
                Hash(cx, cz, seed, SkylandSalt + 6), Hash(cx, cz, seed, SkylandSalt + 8), wetAt);
        }

        // ── Queries for the skyland module (by island identity, stable across set reloads) ───────────
        internal static void SkylandsNear(Vector3 point, float range, List<uint> ids, List<int> indices)
        {
            ids.Clear();
            indices.Clear();
            if (!Enabled) return;
            for (int i = 0; i < s_islands.Count; i++)
            {
                var isl = s_islands[i];
                if (!IsSkyland(isl.Kind)) continue;
                float dx = WorldX(in isl) - point.x, dz = WorldZ(in isl) - point.z;
                if (dx * dx + dz * dz > range * range) continue;
                ids.Add(IslandHash(in isl));
                indices.Add(i);
            }
        }

        internal static uint IslandId(int index) => IslandHash(s_islands[index]);

        // 0.2.740 (skyland_check): the far view's top at a world column of a skyland (what its far mesh is built from), false
        // when the column is past its coast; and its location pad (centre, flat radius) when it holds one.
        internal static bool TryFarTop(int index, float wx, float wz, out float y)
        {
            var isl = s_islands[index];
            float sx = wx - isl.OffX, sz = wz - isl.OffZ;
            y = float.NaN;
            if (!SkylandColumn(in isl, sx, sz, out _, out _, out _)) return false;
            y = FarTopAt(in isl, sx, sz) + isl.OffY;
            return true;
        }

        internal static bool TryGetSitePad(int index, out Vector3 centre, out float flat)
        {
            var isl = s_islands[index];
            centre = new Vector3(isl.SiteX + isl.OffX, isl.SiteY + isl.OffY, isl.SiteZ + isl.OffZ);
            flat = isl.SiteFlat;
            return isl.Site > 0 && isl.SiteFlat > 0f;
        }

        internal static float SkylandRadius(int index) => s_islands[index].R;

        // 0.2.756 (FiresTerrainLayers): the island whose body a point is in or on, with that column's solid span (the voxels'
        // own: carved top, rounded rim) and the island's id. A point counts from Margin under the underside to Margin over the
        // top. False when no island holds the column there.
        internal static bool TryIslandAt(Vector3 p, float margin, out uint id, out float bottom, out float top)
        {
            id = 0;
            bottom = top = float.NaN;
            if (!Enabled || !s_index.TryGetValue(CellKey(p.x, p.z), out var list)) return false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = p.x - isl.OffX, sz = p.z - isl.OffZ;
                if (!Covers(in isl, sx, sz)) continue;
                float low, high;
                if (IsSkyland(isl.Kind))
                {
                    if (!SkylandColumn(in isl, sx, sz, out float t, out float b, out float side) || !SolidSpan(t, b, side, out low, out high)) continue;
                }
                else
                {
                    low = isl.Bot;
                    high = TopAt(in isl, sx, sz);
                }
                low += isl.OffY;
                high += isl.OffY;
                if (p.y < low - margin || p.y > high + margin) continue;
                id = IslandHash(in isl);
                bottom = low;
                top = high;
                return true;
            }
            return false;
        }

        internal static Vector2 SkylandCentre(int index) { var isl = s_islands[index]; return new Vector2(WorldX(in isl), WorldZ(in isl)); }

        // A skyland's disc in the world, widened by the skin its rim colour and dressing can reach past the coast.
        internal static void SkylandDisc(int index, out float x, out float z, out float radius)
        {
            var isl = s_islands[index];
            x = WorldX(in isl);
            z = WorldZ(in isl);
            radius = isl.R + SkylandSkinReach;
        }

        internal static Vector3 SkylandSummit(int index)
        {
            var isl = s_islands[index];
            var heart = isl.Shape != null ? isl.Shape.Heart : new Vector2(isl.X, isl.Z);
            return new Vector3(heart.x + isl.OffX, TopAt(in isl, heart.x, heart.y) + isl.OffY, heart.y + isl.OffZ);
        }

        internal static bool SkylandHasFall(int index)
        {
            var isl = s_islands[index];
            return IsSkyland(isl.Kind) && ((!float.IsNaN(isl.RillAng) && isl.PondLvl > 0f) || (isl.Waters != null && isl.Waters.Rivers.Count > 0));
        }

        internal static void IslandFootprint(int index, out Vector3 center, out float radius)
        {
            var isl = s_islands[index];
            center = new Vector3(WorldX(in isl), isl.BaseY + isl.OffY, WorldZ(in isl));
            radius = isl.R;
        }

        // Dressing for one skyland from its biome's palette, in layers so a skyland dressed before a layer existed
        // still gets it: base (plants and trees on the dome, rocks around the rim), vines (curtains hanging from the
        // rim's cliff band), belly (rocks and roots on the underside) and cliff (rocks layered into the steep face
        // below the rim). Pure island math, deterministic.
        internal const string ScaledRootPrefix = "FiresSkyRoot_";
        internal const string ScaledVinesPrefab = "FiresSkyVines";
        internal const int DressBase = 1;
        internal const int DressVines = 2;
        internal const int DressBelly = 4;
        internal const int DressCliff = 8;
        internal const int DressAll = DressBase | DressVines | DressBelly | DressCliff;
        internal const int DressSiteLayout = 16;
        internal const int DressLocation = 32;
        internal const int DressRolledRims = 64;
        internal const int DressGround = 128;
        internal const int DressNames = 256;
        internal const int DressSurface = 512;   // 0.2.763: every item on the density surface the voxels draw (belly, rim, vines too)
        private const float VineSlotSpacing = 3.2f;
        private const float VineTopAbovePivot = 1.26f;
        private const float FallClearance = 5f;
        private const float CliffSlotSpacing = 6f;
        private const float CliffBareShare = 0.15f;
        private const float SlopeStep = 0.5f;
        private const float CliffDepthJitter = 0.4f;
        private static readonly float[] CliffTierDepths = { 3f, 9f, 16f, 24f, 33f };
        private const float CliffSearchInner = 0.3f;
        private const float CliffSearchOuter = 0.999f;
        private const int CliffSearchSteps = 14;
        private const float NearRimShare = 0.7f;
        private const float RimBareShare = 0.45f;
        private const float RootStretch = 1.2f;
        internal const float DressingOverhang = 1f;
        private const float DressingHeadroom = 6f;
        private const float DressingBelowBottom = 5f;

        // Fire 10-05 ("a lot of floating vegetation"): the dressing was placed on TopAt, the island's top BEFORE its ponds and
        // rivers are dug in (SkylandColumn → CarveWaters runs only when the chunks generate), so a river bank cut away under a
        // plant left it floating and a pond rim raised over one buried it. Dressing now stands on the CARVED top, the same
        // column the voxel shape is built from: a spot the water carve dug more than DressCarveTolerance into is skipped, a
        // raised bank lifts the item onto it. Counted per island for the dressing line.
        private const float DressCarveTolerance = 0.3f;
        private static int s_dressSkipped, s_dressLifted;
        private static float s_dressWorstCut;

        // Where the PREVIOUS rule (FAT up to 0.2.706: the uncarved top, nothing skipped) put this skyland's dressing, every
        // layer: the re-dress command recognises untagged dressing from that rule by these exact spots.
        private static bool s_legacyTops;

        internal static void CollectLegacySkylandDressing(int index, List<Vector3> pos, List<Quaternion> rot, List<string> name, List<Vector3> scale)
        {
            s_legacyTops = true;
            try { CollectSkylandDressing(index, DressAll, pos, rot, name, scale); }
            finally { s_legacyTops = false; }
        }

        // The ground a dressing item stands on: the carved column top. false = the water carve dug this spot out (skip it).
        private static bool DressingTop(in Island isl, float px, float pz, out float top)
        {
            float natural = TopAt(in isl, px, pz);
            top = natural;
            if (s_legacyTops) return true;
            // 0.2.740 (Fire 10-05: rocks and trees hanging off the edge, a cluster falling toward the sea): a spot past the
            // coast has no island under it, so a top item there is skipped, never set on the shape's top in mid-air.
            if (!SkylandColumn(in isl, px, pz, out float carved, out float bottom, out float side)) { s_dressSkipped++; return false; }
            float cut = natural - carved;
            if (cut > DressCarveTolerance)
            {
                s_dressSkipped++;
                if (cut > s_dressWorstCut) s_dressWorstCut = cut;
                return false;
            }
            if (-cut > DressCarveTolerance) s_dressLifted++;
            // 0.2.738 (Run C 10-05: 17 of 133 re-dressed items floated, worst 2.95 m): the voxel surface is where the column's
            // density crosses zero, and its soft rounding (EdgeSoftness) lowers it below the carved top near the coast and
            // where the island is thin. Stand on that crossing; a column the rounding leaves no ground in is skipped.
            float surface = DensitySurface(carved, bottom, side);
            if (float.IsNaN(surface)) { s_dressSkipped++; return false; }
            if (carved - surface > s_dressWorstSink) s_dressWorstSink = carved - surface;
            top = surface;
            return true;
        }

        private static float s_dressWorstSink;

        // The highest y in a column where BodyDensity (the voxels' own body, without cave carving, which is sealed near the
        // surface) turns solid, between the carved top and the underside: bisection to 2 cm. NaN when the column holds none.
        private static float DensitySurface(float top, float bottom, float side)
        {
            if (top <= bottom) return float.NaN;
            float solidY = float.NaN;
            for (float y = top; y >= bottom; y -= 0.5f)
                if (BodyDensity(y, top, bottom, side) > 0f) { solidY = y; break; }
            if (float.IsNaN(solidY)) return float.NaN;
            float lo = solidY, hi = Mathf.Min(top, solidY + 0.5f);
            if (BodyDensity(hi, top, bottom, side) > 0f) return hi;
            for (int i = 0; i < 6; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (BodyDensity(mid, top, bottom, side) > 0f) lo = mid; else hi = mid;
            }
            return lo;
        }

        // The lowest y in a column where the body turns solid (its underside as the voxels build it). NaN when none.
        private static float DensityUnderside(float top, float bottom, float side)
        {
            if (top <= bottom) return float.NaN;
            float solidY = float.NaN;
            for (float y = bottom; y <= top; y += 0.5f)
                if (BodyDensity(y, top, bottom, side) > 0f) { solidY = y; break; }
            if (float.IsNaN(solidY)) return float.NaN;
            float hi = solidY, lo = Mathf.Max(bottom, solidY - 0.5f);
            if (BodyDensity(lo, top, bottom, side) > 0f) return lo;
            for (int i = 0; i < 6; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (BodyDensity(mid, top, bottom, side) > 0f) hi = mid; else lo = mid;
            }
            return hi;
        }

        // 0.2.763 (Fire 10-06: dressing must match "the actual shown visual terrain"): every dressing item goes on the surface the
        // voxels build, the density's crossing, never the analytic shape. A belly item hangs from (or stands on) the column's lowest
        // solid crossing; false where the column holds no body (the item is skipped, not hung in the air).
        private static bool DressingUnderside(in Island isl, float px, float pz, out float y)
        {
            y = SkylandBottomAt(in isl, px, pz);
            if (s_legacyTops) return true;
            if (!SkylandColumn(in isl, px, pz, out float top, out float bottom, out float side)) return false;
            float underside = DensityUnderside(top, bottom, side);
            if (float.IsNaN(underside)) return false;
            y = underside;
            return true;
        }

        // A vine's or rim item's anchor: the density surface at the rim, found stepping inward (inX, inZ: unit, toward the
        // island) up to RimSearch when the rim's rounding leaves the outermost column without ground. false = no ground near.
        private const float RimSearch = 2f, RimSearchStep = 0.25f;

        private static bool RimTop(in Island isl, float px, float pz, float inX, float inZ, out float top)
        {
            top = AnchorTop(in isl, px, pz);
            if (s_legacyTops) return true;
            for (float d = 0f; d <= RimSearch; d += RimSearchStep)
            {
                float x = px + inX * d, z = pz + inZ * d;
                if (!SkylandColumn(in isl, x, z, out float carved, out float bottom, out float side)) continue;
                float surface = DensitySurface(carved, bottom, side);
                if (float.IsNaN(surface)) continue;
                top = surface;
                return true;
            }
            return false;
        }

        // The carved top for a vine or rim anchor (never skipped; it hangs off whatever ground is there).
        private static float AnchorTop(in Island isl, float px, float pz)
            => !s_legacyTops && SkylandColumn(in isl, px, pz, out float carved, out _, out _) ? carved : TopAt(in isl, px, pz);

        internal static void CollectSkylandDressing(int index, int layers, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            pos.Clear(); rot.Clear(); name.Clear(); scale.Clear();
            var isl = s_islands[index];
            if (!IsSkyland(isl.Kind)) return;
            var palette = PaletteOf(isl.Biome);
            s_dressSkipped = s_dressLifted = 0;
            s_dressWorstCut = s_dressWorstSink = 0f;
            if (isl.Shape != null) CollectShapedDressing(in isl, palette, layers, pos, rot, name, scale);
            else
            {
                if ((layers & DressBase) != 0) CollectBaseDressing(in isl, palette, pos, rot, name, scale);
                if ((layers & DressVines) != 0) CollectRimVines(in isl, palette, pos, rot, name, scale);
                if ((layers & DressBelly) != 0) CollectBellyDressing(in isl, palette, pos, rot, name, scale);
                if ((layers & DressCliff) != 0) CollectCliffDressing(in isl, palette, pos, rot, name, scale);
            }
            ShiftOut(in isl, pos, 0);
            if (!s_legacyTops) FiresLogger.LogDebug($"[FiresSkyIslands] dressing skyland {index} ({isl.Biome}): {pos.Count} item(s) on the carved top; "
                + $"{s_dressSkipped} skipped where the water carve dug in (worst {s_dressWorstCut:0.0} m below the uncarved top), "
                + $"{s_dressLifted} lifted onto a raised bank or rim; set on the voxel surface (up to {s_dressWorstSink:0.00} m under the carved top)");
        }

        // Anywhere this skyland's dressing can stand: on the dome, around the rim, hanging off it, or under it.
        internal static bool InSkylandDressing(int index, Vector3 point)
        {
            var isl = s_islands[index];
            point -= new Vector3(isl.OffX, isl.OffY, isl.OffZ);
            float dx = point.x - isl.X, dz = point.z - isl.Z;
            float reach = isl.R + DressingOverhang;
            if (dx * dx + dz * dz > reach * reach) return false;
            if (isl.Shape != null && isl.Shape.Sample(point.x, point.z, out float width) > 1f + 2f * DressingOverhang / Mathf.Max(1f, width))
                return false;
            return point.y < TopAt(in isl, point.x, point.z) + DressingHeadroom && point.y > isl.Bot - DressingBelowBottom;
        }

        private static void CollectRimVines(in Island isl, SkylandPalette palette, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            if (palette.Vines == null) return;
            int hseed = isl.Variety;
            int slots = Mathf.Max(8, Mathf.CeilToInt(isl.R * 2f * Mathf.PI / VineSlotSpacing));
            for (int k = 0; k < slots; k++)
            {
                if (Hash(k, 83, hseed, 191) < palette.BareVineShare) continue;
                float a = (k + Hash(k, 89, hseed, 193) * 0.6f) * (Mathf.PI * 2f / slots);
                if (NearFall(in isl, a)) continue;
                float size = 1.2f + Hash(k, 97, hseed, 197) * 0.9f;
                float stretch = 1.1f + Hash(k, 101, hseed, 199) * 0.9f;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                if (!RimTop(in isl, isl.X + cx * isl.R * 0.995f, isl.Z + cz * isl.R * 0.995f, -cx, -cz, out float rimTop)) continue;
                pos.Add(new Vector3(isl.X + cx * (isl.R + 0.15f), rimTop - 0.15f - VineTopAbovePivot * size * stretch, isl.Z + cz * (isl.R + 0.15f)));
                rot.Add(Quaternion.LookRotation(new Vector3(-cx, 0f, -cz)) * Quaternion.Euler(0f, (Hash(k, 103, hseed, 211) - 0.5f) * 16f, 0f));
                name.Add(palette.Vines);
                scale.Add(new Vector3(size, size * stretch, size));
            }
        }

        private static void CollectBaseDressing(in Island isl, SkylandPalette palette, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            int hseed = isl.Variety;
            float area = Mathf.PI * isl.R * isl.R;

            int topCount = Mathf.CeilToInt(area / 55f);
            for (int k = 0; k < topCount; k++)
            {
                float a = Hash(k, 3, hseed, 101) * Mathf.PI * 2f;
                float rr = Mathf.Sqrt(Hash(k, 5, hseed, 103)) * 0.86f;
                float px = isl.X + Mathf.Cos(a) * rr * isl.R, pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                if (InPondOrRill(in isl, px, pz) || InSiteClearing(in isl, px, pz, SiteDressingGap)) continue;
                if (!DressingTop(in isl, px, pz, out float top)) continue;
                if (CaveHollow(in isl, px, top - FootingDepth, pz)) continue;
                var pick = Choose(palette.Top, Hash(k, 7, hseed, 107));
                if (pick.Prefab == null) continue;
                if (rr > NearRimShare && pick.Tall) pick = palette.NearRim;
                if (pick.Tall && InSiteClearing(in isl, px, pz, SiteTreeGap)) pick = palette.NearRim;
                if (Steep(in isl, px, pz)) pick = Choose(palette.Rim, Hash(k, 9, hseed, 108));
                pos.Add(new Vector3(px, top + pick.Lift, pz));
                rot.Add(Quaternion.AngleAxis(Hash(k, 11, hseed, 109) * 360f, Vector3.up));
                name.Add(pick.Prefab);
                scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 211, hseed, 227)));
            }

            int rimCount = Mathf.Max(6, Mathf.CeilToInt(isl.R * 2f * Mathf.PI / 11f));
            for (int k = 0; k < rimCount; k++)
            {
                float a = (k + Hash(k, 13, hseed, 113) * 0.7f) * (Mathf.PI * 2f / rimCount);
                float rr = 0.90f + Hash(k, 17, hseed, 117) * 0.06f;
                float px = isl.X + Mathf.Cos(a) * rr * isl.R, pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                if (InPondOrRill(in isl, px, pz)) continue;
                if (Hash(k, 19, hseed, 119) < RimBareShare) continue;
                if (!DressingTop(in isl, px, pz, out float top)) continue;
                if (CaveHollow(in isl, px, top - FootingDepth, pz)) continue;
                var pick = Choose(palette.Rim, Hash(k, 23, hseed, 121));
                pos.Add(new Vector3(px, top + pick.Lift, pz));
                rot.Add(Quaternion.Euler(Hash(k, 29, hseed, 123) * 30f, Hash(k, 31, hseed, 127) * 360f, Hash(k, 37, hseed, 131) * 30f));
                name.Add(pick.Prefab);
                scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 223, hseed, 229)));
            }

            if (palette.Centerpiece == null || !DressingTop(in isl, isl.X, isl.Z, out float centreTop)) return;
            pos.Add(new Vector3(isl.X, centreTop, isl.Z));
            rot.Add(Quaternion.identity);
            name.Add(palette.Centerpiece);
            scale.Add(Vector3.one);
        }

        private static void CollectBellyDressing(in Island isl, SkylandPalette palette, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            int hseed = isl.Variety;
            float area = Mathf.PI * isl.R * isl.R;
            int bellyCount = Mathf.Max(6, Mathf.CeilToInt(area / 140f));
            for (int k = 0; k < bellyCount; k++)
            {
                float a = Hash(k, 41, hseed, 137) * Mathf.PI * 2f;
                float rr = 0.35f + Hash(k, 43, hseed, 139) * 0.57f;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                float px = isl.X + cx * rr * isl.R, pz = isl.Z + cz * rr * isl.R;
                var pick = Choose(palette.Belly, Hash(k, 47, hseed, 149));
                if (!DressingUnderside(in isl, px, pz, out float underY)) continue;
                var surface = new Vector3(px, underY, pz);
                if (CaveHollow(in isl, px, surface.y + FootingDepth, pz)) continue;
                name.Add(pick.Prefab);
                if (!pick.Hangs)
                {
                    var normal = UndersideNormal(in isl, cx, cz, rr);
                    pos.Add(surface + normal * pick.Lift);
                    rot.Add(Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(Hash(k, 59, hseed, 157) * 360f, Vector3.up));
                    scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 71, hseed, 173)));
                    continue;
                }
                pos.Add(surface + Vector3.up * pick.Lift);
                var outward = Quaternion.AngleAxis(-a * Mathf.Rad2Deg + 90f, Vector3.up);
                rot.Add(outward * Quaternion.AngleAxis(150f + Hash(k, 67, hseed, 167) * 50f, Vector3.right));
                float grow = pick.ScaleAt(Hash(k, 73, hseed, 179));
                scale.Add(new Vector3(grow, grow * RootStretch, grow));
            }
        }

        // Rocks set into the steep face below the rim in tiers, so the cliff reads as layered stone instead of raw
        // mesh. A tier stops where the underside no longer reaches that deep.
        private static void CollectCliffDressing(in Island isl, SkylandPalette palette, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            int hseed = isl.Variety;
            int slots = Mathf.Max(8, Mathf.CeilToInt(isl.R * 2f * Mathf.PI / CliffSlotSpacing));
            for (int k = 0; k < slots; k++)
            {
                float a = (k + Hash(k, 151, hseed, 233) * 0.8f) * (Mathf.PI * 2f / slots);
                if (NearFall(in isl, a)) continue;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                for (int tier = 0; tier < CliffTierDepths.Length; tier++)
                {
                    if (Hash(k, 157 + tier, hseed, 239) < CliffBareShare) continue;
                    float depth = CliffTierDepths[tier] * (1f + CliffDepthJitter * (Hash(k, 163 + tier, hseed, 241) - 0.5f));
                    if (!TryCliffPoint(in isl, cx, cz, depth, out var face, out float rr)) break;
                    var pick = Choose(palette.Cliff, Hash(k, 167 + tier, hseed, 251));
                    var normal = UndersideNormal(in isl, cx, cz, rr);
                    var pivot = face + normal * pick.Lift;
                    if (CaveHollow(in isl, face.x - cx * FootingDepth, face.y, face.z - cz * FootingDepth)
                        || CaveHollow(in isl, pivot.x - cx * FootingDepth, pivot.y, pivot.z - cz * FootingDepth)) continue;
                    pos.Add(pivot);
                    rot.Add(Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(Hash(k, 181 + tier, hseed, 269) * 360f, Vector3.up));
                    name.Add(pick.Prefab);
                    scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 173 + tier, hseed, 257)));
                }
            }
        }

        // The point of the face `depth` below the top along a bearing. The underside thins toward the rim, so the
        // radius where it is that deep is found by bisection between the inner belly and the rim.
        private static bool TryCliffPoint(in Island isl, float cx, float cz, float depth, out Vector3 face, out float rr)
        {
            face = default;
            rr = CliffSearchInner;
            float outer = CliffSearchOuter;
            if (ThicknessAt(in isl, cx, cz, rr) < depth) return false;
            for (int step = 0; step < CliffSearchSteps; step++)
            {
                float mid = (rr + outer) * 0.5f;
                if (ThicknessAt(in isl, cx, cz, mid) >= depth) rr = mid;
                else outer = mid;
            }
            float px = isl.X + cx * rr * isl.R, pz = isl.Z + cz * rr * isl.R;
            DressingUnderside(in isl, px, pz, out float faceY);   // 0.2.763: the voxels' underside where it has one
            face = new Vector3(px, faceY, pz);
            return true;
        }

        private static float ThicknessAt(in Island isl, float cx, float cz, float rr)
        {
            float px = isl.X + cx * rr * isl.R, pz = isl.Z + cz * rr * isl.R;
            return TopAt(in isl, px, pz) - SkylandBottomAt(in isl, px, pz);
        }

        // Outward normal of the underside along a bearing, from how fast the bottom rises toward the rim: near the rim
        // the face is steep and the normal points out, toward the tip it points down. Rocks stand on it like on ground.
        private static Vector3 UndersideNormal(in Island isl, float cx, float cz, float rr)
        {
            float step = SlopeStep / isl.R;
            float inner = SkylandBottomAt(in isl, isl.X + cx * (rr - step) * isl.R, isl.Z + cz * (rr - step) * isl.R);
            float outer = SkylandBottomAt(in isl, isl.X + cx * (rr + step) * isl.R, isl.Z + cz * (rr + step) * isl.R);
            float rise = (outer - inner) / (2f * SlopeStep);
            return new Vector3(cx * rise, -1f, cz * rise).normalized;
        }

        // Is this bearing close enough to the skyland's fall that rim dressing would hang in the water?
        private static bool NearFall(in Island isl, float bearing)
        {
            if (float.IsNaN(isl.RillAng) || isl.PondLvl <= 0f) return false;
            float lip = Mathf.Atan2(isl.PondZ + Mathf.Sin(isl.RillAng) * isl.RillLen - isl.Z, isl.PondX + Mathf.Cos(isl.RillAng) * isl.RillLen - isl.X);
            return Mathf.Abs(Mathf.DeltaAngle(bearing * Mathf.Rad2Deg, lip * Mathf.Rad2Deg)) * Mathf.Deg2Rad * isl.R < FallClearance;
        }

        // A shaped skyland's dressing at a round one's densities, in the same layers: the top and belly slots scattered over
        // its footprint, the rim, vine and cliff slots spaced evenly along its traced coast.
        private const int ShapedSlotTries = 6;

        private static void CollectShapedDressing(in Island isl, SkylandPalette palette, int layers, List<Vector3> pos, List<Quaternion> rot,
            List<string> name, List<Vector3> scale)
        {
            var shape = isl.Shape;
            int hseed = isl.Variety;
            if ((layers & DressBase) != 0)
            {
                int topWanted = Mathf.CeilToInt(shape.Area / 55f);
                for (int k = 0, found = 0; found < topWanted && k < topWanted * ShapedSlotTries; k++)
                {
                    float a = Hash(k, 3, hseed, 101) * Mathf.PI * 2f;
                    float d = Mathf.Sqrt(Hash(k, 5, hseed, 103)) * isl.R;
                    float px = isl.X + Mathf.Cos(a) * d, pz = isl.Z + Mathf.Sin(a) * d;
                    float rr = shape.Interior(px, pz);
                    if (rr >= 0.86f) continue;
                    found++;
                    if (InPondOrRill(in isl, px, pz) || InSiteClearing(in isl, px, pz, SiteDressingGap)) continue;
                    if (!DressingTop(in isl, px, pz, out float top)) continue;
                    if (CaveHollow(in isl, px, top - FootingDepth, pz)) continue;
                    var pick = Choose(palette.Top, Hash(k, 7, hseed, 107));
                    if (pick.Prefab == null) continue;
                    if (rr > NearRimShare && pick.Tall) pick = palette.NearRim;
                    if (pick.Tall && InSiteClearing(in isl, px, pz, SiteTreeGap)) pick = palette.NearRim;
                    if (Steep(in isl, px, pz)) pick = Choose(palette.Rim, Hash(k, 9, hseed, 108));
                    pos.Add(new Vector3(px, top + pick.Lift, pz));
                    rot.Add(Quaternion.AngleAxis(Hash(k, 11, hseed, 109) * 360f, Vector3.up));
                    name.Add(pick.Prefab);
                    scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 211, hseed, 227)));
                }

                int rimCount = Mathf.Max(6, Mathf.CeilToInt(shape.Perimeter / 11f));
                for (int k = 0; k < rimCount; k++)
                {
                    var coast = shape.CoastAt((k + Hash(k, 13, hseed, 113) * 0.7f) / rimCount, out var outward);
                    float inset = (0.04f + Hash(k, 17, hseed, 117) * 0.06f) * shape.Width(coast.x, coast.y);
                    float px = coast.x - outward.x * inset, pz = coast.y - outward.y * inset;
                    if (InPondOrRill(in isl, px, pz)) continue;
                    if (Hash(k, 19, hseed, 119) < RimBareShare) continue;
                    if (!DressingTop(in isl, px, pz, out float top)) continue;
                    if (CaveHollow(in isl, px, top - FootingDepth, pz)) continue;
                    var pick = Choose(palette.Rim, Hash(k, 23, hseed, 121));
                    pos.Add(new Vector3(px, top + pick.Lift, pz));
                    rot.Add(Quaternion.Euler(Hash(k, 29, hseed, 123) * 30f, Hash(k, 31, hseed, 127) * 360f, Hash(k, 37, hseed, 131) * 30f));
                    name.Add(pick.Prefab);
                    scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 223, hseed, 229)));
                }

                if (palette.Centerpiece != null && DressingTop(in isl, shape.Heart.x, shape.Heart.y, out float heartTop))
                {
                    pos.Add(new Vector3(shape.Heart.x, heartTop, shape.Heart.y));
                    rot.Add(Quaternion.identity);
                    name.Add(palette.Centerpiece);
                    scale.Add(Vector3.one);
                }
            }

            if ((layers & DressVines) != 0 && palette.Vines != null)
            {
                int slots = Mathf.Max(8, Mathf.CeilToInt(shape.Perimeter / VineSlotSpacing));
                for (int k = 0; k < slots; k++)
                {
                    if (Hash(k, 83, hseed, 191) < palette.BareVineShare) continue;
                    var coast = shape.CoastAt((k + Hash(k, 89, hseed, 193) * 0.6f) / slots, out var outward);
                    if (NearAnyFall(in isl, coast)) continue;
                    float size = 1.2f + Hash(k, 97, hseed, 197) * 0.9f;
                    float stretch = 1.1f + Hash(k, 101, hseed, 199) * 0.9f;
                    if (!RimTop(in isl, coast.x - outward.x * 0.1f, coast.y - outward.y * 0.1f, -outward.x, -outward.y, out float rimTop)) continue;
                    pos.Add(new Vector3(coast.x + outward.x * 0.15f, rimTop - 0.15f - VineTopAbovePivot * size * stretch, coast.y + outward.y * 0.15f));
                    rot.Add(Quaternion.LookRotation(new Vector3(-outward.x, 0f, -outward.y)) * Quaternion.Euler(0f, (Hash(k, 103, hseed, 211) - 0.5f) * 16f, 0f));
                    name.Add(palette.Vines);
                    scale.Add(new Vector3(size, size * stretch, size));
                }
            }

            if ((layers & DressBelly) != 0)
            {
                int bellyWanted = Mathf.Max(6, Mathf.CeilToInt(shape.Area / 140f));
                for (int k = 0, found = 0; found < bellyWanted && k < bellyWanted * ShapedSlotTries; k++)
                {
                    float a = Hash(k, 41, hseed, 137) * Mathf.PI * 2f;
                    float d = Mathf.Sqrt(Hash(k, 43, hseed, 139)) * isl.R;
                    float px = isl.X + Mathf.Cos(a) * d, pz = isl.Z + Mathf.Sin(a) * d;
                    float rr = shape.Interior(px, pz);
                    if (rr < 0.35f || rr > 0.92f) continue;
                    found++;
                    var pick = Choose(palette.Belly, Hash(k, 47, hseed, 149));
                    if (!DressingUnderside(in isl, px, pz, out float underY)) continue;
                    var surface = new Vector3(px, underY, pz);
                    if (CaveHollow(in isl, px, surface.y + FootingDepth, pz)) continue;
                    name.Add(pick.Prefab);
                    if (!pick.Hangs)
                    {
                        var normal = UndersideNormalAt(in isl, px, pz);
                        pos.Add(surface + normal * pick.Lift);
                        rot.Add(Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(Hash(k, 59, hseed, 157) * 360f, Vector3.up));
                        scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 71, hseed, 173)));
                        continue;
                    }
                    pos.Add(surface + Vector3.up * pick.Lift);
                    rot.Add(Quaternion.AngleAxis(-a * Mathf.Rad2Deg + 90f, Vector3.up) * Quaternion.AngleAxis(150f + Hash(k, 67, hseed, 167) * 50f, Vector3.right));
                    float grow = pick.ScaleAt(Hash(k, 73, hseed, 179));
                    scale.Add(new Vector3(grow, grow * RootStretch, grow));
                }
            }

            if ((layers & DressCliff) == 0) return;
            int cliffSlots = Mathf.Max(8, Mathf.CeilToInt(shape.Perimeter / CliffSlotSpacing));
            for (int k = 0; k < cliffSlots; k++)
            {
                var coast = shape.CoastAt((k + Hash(k, 151, hseed, 233) * 0.8f) / cliffSlots, out var outward);
                if (NearAnyFall(in isl, coast)) continue;
                for (int tier = 0; tier < CliffTierDepths.Length; tier++)
                {
                    if (Hash(k, 157 + tier, hseed, 239) < CliffBareShare) continue;
                    float depth = CliffTierDepths[tier] * (1f + CliffDepthJitter * (Hash(k, 163 + tier, hseed, 241) - 0.5f));
                    if (!TryShapedCliffPoint(in isl, coast, outward, depth, out var face)) break;
                    var pick = Choose(palette.Cliff, Hash(k, 167 + tier, hseed, 251));
                    var normal = UndersideNormalAt(in isl, face.x, face.z);
                    var pivot = face + normal * pick.Lift;
                    if (CaveHollow(in isl, face.x - outward.x * FootingDepth, face.y, face.z - outward.y * FootingDepth)
                        || CaveHollow(in isl, pivot.x - outward.x * FootingDepth, pivot.y, pivot.z - outward.y * FootingDepth)) continue;
                    pos.Add(pivot);
                    rot.Add(Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(Hash(k, 181 + tier, hseed, 269) * 360f, Vector3.up));
                    name.Add(pick.Prefab);
                    scale.Add(Vector3.one * pick.ScaleAt(Hash(k, 173 + tier, hseed, 257)));
                }
            }
        }

        // The point of the face `depth` below the top, in from a coast point: the underside thins towards the coast, so the
        // spot that deep is found by bisection along the inward normal.
        private static bool TryShapedCliffPoint(in Island isl, Vector2 coast, Vector2 outward, float depth, out Vector3 face)
        {
            face = default;
            float width = isl.Shape.Width(coast.x, coast.y);
            float thin = width * (1f - CliffSearchOuter), thick = width * (1f - CliffSearchInner);
            if (ThicknessAt(in isl, coast - outward * thick) < depth) return false;
            for (int step = 0; step < CliffSearchSteps; step++)
            {
                float mid = (thin + thick) * 0.5f;
                if (ThicknessAt(in isl, coast - outward * mid) >= depth) thick = mid;
                else thin = mid;
            }
            var p = coast - outward * thick;
            DressingUnderside(in isl, p.x, p.y, out float faceY);   // 0.2.763: the voxels' underside where it has one
            face = new Vector3(p.x, faceY, p.y);
            return true;
        }

        private static float ThicknessAt(in Island isl, Vector2 p)
            => TopAt(in isl, p.x, p.y) - SkylandBottomAt(in isl, p.x, p.y);

        // The underside's outward normal from its slope: it stands rocks on the belly the way ground stands them.
        private static Vector3 UndersideNormalAt(in Island isl, float wx, float wz)
        {
            float dx = SkylandBottomAt(in isl, wx + SlopeStep, wz) - SkylandBottomAt(in isl, wx - SlopeStep, wz);
            float dz = SkylandBottomAt(in isl, wx, wz + SlopeStep) - SkylandBottomAt(in isl, wx, wz - SlopeStep);
            return new Vector3(dx / (2f * SlopeStep), -1f, dz / (2f * SlopeStep)).normalized;
        }

        // Inside the skyland's rock but hollowed out by one of its caves: dressing never stands on such a footing.
        private const float FootingDepth = 0.6f;

        private static bool CaveHollow(in Island isl, float wx, float wy, float wz)
        {
            if (!HoldsCaves(in isl) || !FiresVoxelConfig.GeneratesCaves || !SkylandColumn(in isl, wx, wz, out float top, out float bottom, out float side))
                return false;
            return BodyDensity(wy, top, bottom, side) > 0f && SkylandDensity(in isl, wx, wy, wz, top, bottom, side) <= 0f;
        }

        // Is this coast point close enough to the skyland's fall that rim dressing would hang in the water?
        private static bool NearFallAt(in Island isl, Vector2 coast)
        {
            if (float.IsNaN(isl.RillAng) || isl.PondLvl <= 0f) return false;
            float dx = coast.x - (isl.PondX + Mathf.Cos(isl.RillAng) * isl.RillLen);
            float dz = coast.y - (isl.PondZ + Mathf.Sin(isl.RillAng) * isl.RillLen);
            return dx * dx + dz * dz < FallClearance * FallClearance;
        }

        // ── Far view ────────────────────────────────────────────────────────────────────────────────
        // Voxel chunks only exist near the player, so each skyland also has a closed mesh of its top and underside for
        // the horizon, shown until its own voxel chunks are in: polar rings for a round skyland, a grid cut to the coast
        // for a shaped one.
        private const int FarRings = 10;
        private const int FarSegments = 32;
        private const float FarCellShare = 0.25f;
        private const float FarMinCell = 1.5f;
        private const float FarMaxCell = 4f;
        private const int FarMaxCells = 3000;
        private const float FarWaterUvPerMetre = 1f / 64f;

        // Rings crowd toward the rim, where the belly drops away steeply.
        private static float FarRingRadius(int ring) => 1f - Mathf.Pow(1f - (float)ring / FarRings, 1.6f);

        internal static void BuildSkylandFarMesh(int index, Mesh mesh, Color32 topColor)
        {
            var isl = s_islands[index];
            var verts = new List<Vector3>(FarRings * FarSegments * 2 + 2);
            var tris = new List<int>(FarRings * FarSegments * 12);
            if (isl.Shape != null) ShapedFarBody(in isl, verts, tris);
            else RoundFarBody(in isl, verts, tris);
            ShiftOut(in isl, verts, 0);
            var cols = new List<Color32>(verts.Count);
            var uvs = new List<Vector2>(verts.Count);
            foreach (var v in verts)
            {
                cols.Add(topColor);
                uvs.Add(new Vector2(v.x / 32f, v.z / 32f));
            }
            mesh.Clear(false);
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, new List<Vector2>(new Vector2[verts.Count]));
            mesh.SetTriangles(tris, 0, calculateBounds: true);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
        }

        // The far view's top is the column's own, with ponds, rill and rivers dug in as the voxels dig them; at the coast,
        // where columns end, it is the plain top.
        // 0.2.740 (Fire 10-05: "skylands show one shape from far away and then reshape/recarve themselves when we get close"):
        // the far view is the voxels' OWN surface, the density crossings of the same column the near chunks are built from
        // (rounded at the coast and where the island is thin), not the carved top and the uncarved underside. A column the
        // rounding leaves no solid in closes to the middle of its top and underside, as the near rim does.
        private static float FarTopAt(in Island isl, float x, float z)
        {
            if (!SkylandColumn(in isl, x, z, out float top, out float bottom, out float side)) return TopAt(in isl, x, z);
            float surface = DensitySurface(top, bottom, side);
            return float.IsNaN(surface) ? (top + bottom) * 0.5f : surface;
        }

        private static float FarBottomAt(in Island isl, float x, float z)
        {
            if (!SkylandColumn(in isl, x, z, out float top, out float bottom, out float side)) return SkylandBottomAt(in isl, x, z);
            float underside = DensityUnderside(top, bottom, side);
            return float.IsNaN(underside) ? (top + bottom) * 0.5f : underside;
        }

        // The far view's cell size, and the local bounds it covers: the coast's, or the disc's for a round skyland.
        private static float FarGrid(in Island isl, out float x0, out float z0, out float x1, out float z1)
        {
            if (isl.Shape == null)
            {
                x0 = isl.X - isl.R; x1 = isl.X + isl.R;
                z0 = isl.Z - isl.R; z1 = isl.Z + isl.R;
                return FarMaxCell;
            }
            x0 = float.MaxValue; x1 = float.MinValue; z0 = float.MaxValue; z1 = float.MinValue;
            foreach (var p in isl.Shape.Coast)
            {
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                z0 = Mathf.Min(z0, p.y); z1 = Mathf.Max(z1, p.y);
            }
            float step = Mathf.Clamp(isl.Shape.HeartWidth * FarCellShare, FarMinCell, FarMaxCell);
            return Mathf.Max(step, Mathf.Sqrt((x1 - x0) * (z1 - z0) / FarMaxCells));
        }

        // The far view's water: this skyland's own ponds, rill and rivers as quads at the levels the chunk water sheet
        // stores, on the far body's grid, so from afar the water already stands where it will close up. False when the
        // skyland holds none.
        internal static bool BuildSkylandFarWater(int index, Mesh mesh)
        {
            var isl = s_islands[index];
            if (isl.Waters == null && isl.PondLvl <= 0f) return false;
            float step = FarGrid(in isl, out float x0, out float z0, out float x1, out float z1);
            int nx = Mathf.CeilToInt((x1 - x0) / step), nz = Mathf.CeilToInt((z1 - z0) / step);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float wx = x0 + i * step + isl.OffX, wz = z0 + j * step + isl.OffZ;
                    if (!IslandWaterAt(in isl, wx + step * 0.5f, wz + step * 0.5f, out float centre)) continue;
                    int b = verts.Count;
                    AddFarWaterCorner(in isl, verts, uvs, wx, wz, centre);
                    AddFarWaterCorner(in isl, verts, uvs, wx + step, wz, centre);
                    AddFarWaterCorner(in isl, verts, uvs, wx, wz + step, centre);
                    AddFarWaterCorner(in isl, verts, uvs, wx + step, wz + step, centre);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
            if (tris.Count == 0) return false;
            var normals = new Vector3[verts.Count];
            var tangents = new Vector4[verts.Count];
            var colours = new Color32[verts.Count];
            var tangent = new Vector4(1f, 0f, 0f, -1f);
            for (int n = 0; n < verts.Count; n++)
            {
                normals[n] = Vector3.up;
                tangents[n] = tangent;
                colours[n] = Color.white;
            }
            mesh.Clear(false);
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetColors(colours);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, new List<Vector2>(new Vector2[verts.Count]));
            mesh.SetTriangles(tris, 0, calculateBounds: true);
            return true;
        }

        // A water corner stands at its own level where it has water, else at its cell's.
        private static void AddFarWaterCorner(in Island isl, List<Vector3> verts, List<Vector2> uvs, float wx, float wz, float cellLevel)
        {
            float level = IslandWaterAt(in isl, wx, wz, out float own) ? own : cellLevel;
            verts.Add(new Vector3(wx, level, wz));
            uvs.Add(new Vector2(wx * FarWaterUvPerMetre, wz * FarWaterUvPerMetre));
        }

        private static void RoundFarBody(in Island isl, List<Vector3> verts, List<int> tris)
        {
            int topCenter = verts.Count;
            verts.Add(new Vector3(isl.X, FarTopAt(in isl, isl.X, isl.Z), isl.Z));
            for (int ring = 1; ring <= FarRings; ring++)
            {
                float rr = FarRingRadius(ring);
                for (int s = 0; s < FarSegments; s++)
                {
                    float a = s * Mathf.PI * 2f / FarSegments;
                    float px = isl.X + Mathf.Cos(a) * rr * isl.R, pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                    float top = FarTopAt(in isl, px, pz);
                    float y = ring == FarRings ? top - 1.25f : top;
                    verts.Add(new Vector3(px, y, pz));
                }
            }
            int bottomCenter = verts.Count;
            verts.Add(new Vector3(isl.X, FarBottomAt(in isl, isl.X, isl.Z), isl.Z));
            for (int ring = 1; ring < FarRings; ring++)
            {
                float rr = FarRingRadius(ring);
                for (int s = 0; s < FarSegments; s++)
                {
                    float a = s * Mathf.PI * 2f / FarSegments;
                    float px = isl.X + Mathf.Cos(a) * rr * isl.R, pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                    verts.Add(new Vector3(px, FarBottomAt(in isl, px, pz), pz));
                }
            }

            int TopVert(int ring, int s) => topCenter + 1 + (ring - 1) * FarSegments + ((s % FarSegments + FarSegments) % FarSegments);
            int BottomVert(int ring, int s)
                => ring == FarRings ? TopVert(FarRings, s) : bottomCenter + 1 + (ring - 1) * FarSegments + ((s % FarSegments + FarSegments) % FarSegments);

            for (int s = 0; s < FarSegments; s++)
            {
                tris.Add(topCenter); tris.Add(TopVert(1, s + 1)); tris.Add(TopVert(1, s));
                tris.Add(bottomCenter); tris.Add(BottomVert(1, s)); tris.Add(BottomVert(1, s + 1));
                for (int ring = 1; ring < FarRings; ring++)
                {
                    int a0 = TopVert(ring, s), a1 = TopVert(ring, s + 1), b0 = TopVert(ring + 1, s), b1 = TopVert(ring + 1, s + 1);
                    tris.Add(a0); tris.Add(a1); tris.Add(b1);
                    tris.Add(a0); tris.Add(b1); tris.Add(b0);
                    int c0 = BottomVert(ring, s), c1 = BottomVert(ring, s + 1), d0 = BottomVert(ring + 1, s), d1 = BottomVert(ring + 1, s + 1);
                    tris.Add(c0); tris.Add(d1); tris.Add(c1);
                    tris.Add(c0); tris.Add(d0); tris.Add(d1);
                }
            }
        }

        // A grid over the skyland's bounds with the coast cut through it by marching squares: the top and the underside
        // over the same cells, and a wall round the coast between them.
        private static void ShapedFarBody(in Island isl, List<Vector3> verts, List<int> tris)
        {
            var shape = isl.Shape;
            float step = FarGrid(in isl, out float x0, out float z0, out float x1, out float z1);
            x0 -= step * 2f; z0 -= step * 2f; x1 += step * 2f; z1 += step * 2f;
            int nx = Mathf.CeilToInt((x1 - x0) / step) + 1, nz = Mathf.CeilToInt((z1 - z0) / step) + 1;
            var field = new float[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                    field[j * nx + i] = shape.Interior(x0 + i * step, z0 + j * step) - 1f;
            var upper = new Dictionary<int, int>();
            var lower = new Dictionary<int, int>();
            var local = isl;
            Vector2 Point(int key)
            {
                int kind = key & 3, cell = key >> 2, i = cell % nx, j = cell / nx;
                if (kind == 0) return new Vector2(x0 + i * step, z0 + j * step);
                int i2 = kind == 1 ? i + 1 : i, j2 = kind == 1 ? j : j + 1;
                float a = field[j * nx + i], b = field[j2 * nx + i2];
                float t = Mathf.Clamp01(a / (a - b));
                return new Vector2(x0 + (i + (i2 - i) * t) * step, z0 + (j + (j2 - j) * t) * step);
            }
            int Vertex(Dictionary<int, int> cache, int key, bool high)
            {
                if (cache.TryGetValue(key, out int index)) return index;
                var p = Point(key);
                verts.Add(new Vector3(p.x, high ? FarTopAt(in local, p.x, p.y) : FarBottomAt(in local, p.x, p.y), p.y));
                return cache[key] = verts.Count - 1;
            }
            var polygon = new List<int>(8);
            var corners = new int[4];
            var edges = new int[4];
            for (int j = 0; j < nz - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    corners[0] = (j * nx + i) << 2; corners[1] = (j * nx + i + 1) << 2;
                    corners[2] = ((j + 1) * nx + i + 1) << 2; corners[3] = ((j + 1) * nx + i) << 2;
                    edges[0] = corners[0] | 1; edges[1] = corners[1] | 2; edges[2] = corners[3] | 1; edges[3] = corners[0] | 2;
                    polygon.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        bool inside = field[corners[k] >> 2] < 0f, nextInside = field[corners[(k + 1) % 4] >> 2] < 0f;
                        if (inside) polygon.Add(corners[k]);
                        if (inside != nextInside) polygon.Add(edges[k]);
                    }
                    if (polygon.Count < 3) continue;
                    for (int t = 1; t < polygon.Count - 1; t++)
                    {
                        tris.Add(Vertex(upper, polygon[0], true)); tris.Add(Vertex(upper, polygon[t + 1], true)); tris.Add(Vertex(upper, polygon[t], true));
                        tris.Add(Vertex(lower, polygon[0], false)); tris.Add(Vertex(lower, polygon[t], false)); tris.Add(Vertex(lower, polygon[t + 1], false));
                    }
                    for (int m = 0; m < polygon.Count; m++)
                    {
                        int a = polygon[m], b = polygon[(m + 1) % polygon.Count];
                        if ((a & 3) == 0 || (b & 3) == 0) continue;
                        var pa = Point(a);
                        var pb = Point(b);
                        int wall = verts.Count;
                        verts.Add(new Vector3(pa.x, FarTopAt(in isl, pa.x, pa.y), pa.y));
                        verts.Add(new Vector3(pb.x, FarTopAt(in isl, pb.x, pb.y), pb.y));
                        verts.Add(new Vector3(pb.x, FarBottomAt(in isl, pb.x, pb.y), pb.y));
                        verts.Add(new Vector3(pa.x, FarBottomAt(in isl, pa.x, pa.y), pa.y));
                        tris.Add(wall); tris.Add(wall + 1); tris.Add(wall + 2);
                        tris.Add(wall); tris.Add(wall + 2); tris.Add(wall + 3);
                    }
                }
        }

        // The zones a skyland's footprint touches (its voxel chunks live in these), widened by `margin` beyond its rim.
        internal static void SkylandZones(int index, List<Vector2s> zones, float margin = 0f)
        {
            zones.Clear();
            var isl = s_islands[index];
            float reach = isl.R + margin;
            float x = WorldX(in isl), z = WorldZ(in isl);
            var lo = ZoneSystem.GetZone(new Vector3(x - reach, 0f, z - reach));
            var hi = ZoneSystem.GetZone(new Vector3(x + reach, 0f, z + reach));
            for (int zy = lo.y; zy <= hi.y; zy++)
                for (int zx = lo.x; zx <= hi.x; zx++)
                    zones.Add(new Vector2s(zx, zy));
        }

        // Server: save the grown set and send it to every client. `before` is a SnapshotSet() token.
        internal static void PublishSkylands(object before, List<string> lines)
        {
            var world = ZNet.World;
            if (world == null || !(before is List<Island> previous)) return;
            SaveAndPublish(world, previous, lines);
        }

        internal static object SnapshotSet() => new List<Island>(s_islands);
    }
}
