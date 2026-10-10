using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Every chunk world's guaranteed first cave. The start area is low Meadows a few metres above the y30 water table,
    // where no dry cave can form, so when the server seeds the start area it raises a rocky hill beside the StartTemple
    // and carves a cavern into it, reached by a walk-in passage whose mouth faces the temple.
    // The hill is added to WorldGenerator.GetBiomeHeight for live terrain only (never for planning passes or a swapped
    // zone's copy), so heightmaps, vegetation, the minimap and the voxel ground all carry it. The cavern is part of the
    // cave carve. The site is saved in the zone registry and sent in the handshake, so every peer builds the same hill.
    internal static class ZoneModeStartCave
    {
        private const float HillRadius = 26f;
        private const float HillHeight = 16f;
        private const float RimSpread = 1.1f;
        private const float ChamberRadius = 9f;
        private const float ChamberHalfHeight = 3.4f;
        private const float ChamberRise = 2.2f;
        private const float FloorLift = 1f;
        private const float PassageStart = 3f;
        private const float PassageHalfWidth = 1.9f;
        private const float PassageHeight = 3.6f;
        private const float EdgeMargin = 8f;
        private const float LocationMargin = 6f;
        private const float PreferredDistance = 58f;
        private const int SiteAngles = 16;
        private static readonly float[] SiteDistances = { 58f, 50f, 66f, 74f };

        internal const int SerializedBytes = 7 * 4 + 4;

        internal sealed class Site
        {
            internal readonly float X, Z, Radius, Height, FloorY, MouthAngle, MouthFloorY;
            internal readonly int Seed;
            internal readonly float DirX, DirZ, Reach, MinY, MaxY, PassageLength;

            internal Site(float x, float z, float radius, float height, float floorY, float mouthAngle, float mouthFloorY, int seed)
            {
                X = x;
                Z = z;
                Radius = radius;
                Height = height;
                FloorY = floorY;
                MouthAngle = mouthAngle;
                MouthFloorY = mouthFloorY;
                Seed = seed;
                DirX = Mathf.Cos(mouthAngle);
                DirZ = Mathf.Sin(mouthAngle);
                PassageLength = radius * 1.2f + 2f;
                Reach = Mathf.Max(radius * RimSpread, PassageLength + 1f) + 2f;
                MinY = Mathf.Min(floorY, mouthFloorY) - 2f;
                MaxY = Mathf.Max(floorY + ChamberRise + ChamberHalfHeight, mouthFloorY + PassageHeight) + 2f;
            }

            internal Vector3 Mouth => new Vector3(X + DirX * Radius, MouthFloorY, Z + DirZ * Radius);
        }

        private static Site s_site;
        private static ConstructorInfo s_clearAreaCtor;
        private static bool s_clearAreaMissing;

        internal static Site Current => Volatile.Read(ref s_site);
        internal static bool Active => Volatile.Read(ref s_site) != null;

        internal static void Set(Site site) => Volatile.Write(ref s_site, site);

        internal static Vector2s ChamberZone
        {
            get
            {
                var s = Current;
                return s == null ? default : ZoneSystem.GetZone(new Vector3(s.X, 0f, s.Z));
            }
        }

        // ── Terrain ─────────────────────────────────────────────────────────
        // GetBiomeHeight postfix: the hill rises from whatever the ground is there.
        internal static void AddHill(float wx, float wz, ref float height)
        {
            var s = Volatile.Read(ref s_site);
            if (s == null) return;
            float dx = wx - s.X, dz = wz - s.Z;
            if (dx * dx + dz * dz >= s.Reach * s.Reach) return;
            if (!ZoneModeGate.ReadsLiveTerrain) return;
            height += s.Height * HillShape(s, wx, wz, dx, dz);
        }

        // A crown flat enough to walk on, steep sides whose edge wanders round the hill, and rocky bumps that fade out
        // at the foot.
        private static float HillShape(Site s, float wx, float wz, float dx, float dz)
        {
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float angle = Mathf.Atan2(dz, dx);
            float rim = s.Radius * (0.9f + 0.2f * Noise(s.Seed, Mathf.Cos(angle) * 1.6f + 4.1f, Mathf.Sin(angle) * 1.6f + 9.7f));
            float t = d / rim;
            if (t >= 1f) return 0f;
            float rock = (Noise(s.Seed + 1, wx * 0.11f, wz * 0.11f) - 0.5f) * 0.14f;
            return Mathf.Max(0f, 1f - Step(0.3f, 1f, t) + rock * Mathf.Clamp01((1f - t) * 3f));
        }

        // Metres inside the cavern or its passage (> 0 is open), unioned into the cave carve.
        internal static float Carve(float wx, float wy, float wz)
        {
            var s = Volatile.Read(ref s_site);
            if (s == null || wy < s.MinY || wy > s.MaxY) return -10f;
            float dx = wx - s.X, dz = wz - s.Z;
            if (dx * dx + dz * dz >= s.Reach * s.Reach) return -10f;
            return Mathf.Max(Chamber(s, dx, wy, dz), Passage(s, dx, wy, dz));
        }

        // A domed chamber with a flat floor at FloorY and walls that wander a little.
        private static float Chamber(Site s, float dx, float wy, float dz)
        {
            float angle = Mathf.Atan2(dz, dx);
            float r = ChamberRadius * (0.88f + 0.24f * Noise(s.Seed + 2, Mathf.Cos(angle) * 1.3f + 2.2f, Mathf.Sin(angle) * 1.3f + wy * 0.2f));
            float ex = dx / r, ey = (wy - (s.FloorY + ChamberRise)) / ChamberHalfHeight, ez = dz / r;
            float inside = (1f - Mathf.Sqrt(ex * ex + ey * ey + ez * ez)) * ChamberHalfHeight;
            return Mathf.Min(inside, wy - s.FloorY);
        }

        // An arched passage from the chamber out past the foot of the hill, its floor running from the chamber floor to
        // the ground at the mouth.
        private static float Passage(Site s, float dx, float wy, float dz)
        {
            float along = dx * s.DirX + dz * s.DirZ;
            if (along < PassageStart - 2f || along > s.PassageLength + 1f) return -10f;
            float lateral = Mathf.Abs(dx * s.DirZ - dz * s.DirX);
            float u = Mathf.Clamp01((along - PassageStart) / (s.PassageLength - PassageStart));
            float up = wy - Mathf.Lerp(s.FloorY, s.MouthFloorY, u);
            float side = lateral / PassageHalfWidth;
            float roof = PassageHeight * (1f - 0.35f * side * side);
            float carve = Mathf.Min(PassageHalfWidth - lateral, Mathf.Min(up, roof - up));
            carve = Mathf.Min(carve, along - (PassageStart - 2f));
            return Mathf.Min(carve, s.PassageLength + 1f - along);
        }

        // PlaceVegetation: nothing grows in the cave mouth, where the passage cuts the hillside open.
        internal static void KeepMouthClear(Vector3 zoneCenter, object clearAreas)
        {
            var s = Volatile.Read(ref s_site);
            if (s == null || !(clearAreas is IList list) || s_clearAreaMissing) return;
            float along = s.Radius * 0.95f, radius = s.Radius * 0.4f;
            var mouth = new Vector3(s.X + s.DirX * along, 0f, s.Z + s.DirZ * along);
            float half = ZoneOpenSet.ZoneSize * 0.5f + radius;
            if (Mathf.Abs(mouth.x - zoneCenter.x) > half || Mathf.Abs(mouth.z - zoneCenter.z) > half) return;
            if (s_clearAreaCtor == null)
            {
                var type = AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
                s_clearAreaCtor = type != null ? AccessTools.Constructor(type, new[] { typeof(Vector3), typeof(float) }) : null;
                if (s_clearAreaCtor == null)
                {
                    s_clearAreaMissing = true;
                    FiresLogger.LogWarning("[FiresZones] ZoneSystem.ClearArea(Vector3, float) not found: vegetation may grow in the start cave's mouth.");
                    return;
                }
            }
            list.Add(s_clearAreaCtor.Invoke(new object[] { mouth, radius }));
        }

        // ── Contents ────────────────────────────────────────────────────────
        // Floor points in the cavern for the first Chunk Hammer's chest, the back wall first.
        internal static IEnumerable<Vector3> ChestSpots()
        {
            var s = Current;
            if (s == null) yield break;
            float px = -s.DirZ, pz = s.DirX;
            yield return new Vector3(s.X - s.DirX * 4f, s.FloorY, s.Z - s.DirZ * 4f);
            yield return new Vector3(s.X - s.DirX * 3f + px * 3f, s.FloorY, s.Z - s.DirZ * 3f + pz * 3f);
            yield return new Vector3(s.X - s.DirX * 3f - px * 3f, s.FloorY, s.Z - s.DirZ * 3f - pz * 3f);
            yield return new Vector3(s.X + px * 4f, s.FloorY, s.Z + pz * 4f);
            yield return new Vector3(s.X - px * 4f, s.FloorY, s.Z - pz * 4f);
            yield return new Vector3(s.X, s.FloorY, s.Z);
        }

        // A floor point inside the cavern or its passage.
        internal static bool Contains(Vector3 floor) => Carve(floor.x, floor.y + 1f, floor.z) > 0f;

        // True when a point lies on the hill, give or take the margin.
        internal static bool Covers(Vector3 point, float margin)
        {
            var s = Current;
            if (s == null) return false;
            float dx = point.x - s.X, dz = point.z - s.Z, reach = s.Radius * RimSpread + margin;
            return dx * dx + dz * dz < reach * reach;
        }

        // ── Server: choosing the site as the start area is seeded ───────────
        // Seeding runs inside location placement's planning scope, so every height here is the real world's. The site
        // is the flattest dry spot 50-74 m from the temple whose hill fits inside the start area and clears every
        // planned location. The rules relax level by level rather than give up, so every chunk world gets its cave. A
        // location is never buried; at the last levels one may stand on the hill's lower skirt.
        private struct Rules
        {
            public float Dry, MaxRelief, LocationRoom;
            public bool AvoidWater;
        }

        private static readonly Rules[] Levels =
        {
            new Rules { Dry = 33.5f, MaxRelief = 4f, AvoidWater = true, LocationRoom = 1f },
            new Rules { Dry = 32f, MaxRelief = 6f, AvoidWater = true, LocationRoom = 1f },
            new Rules { Dry = 30.8f, MaxRelief = 8f, AvoidWater = true, LocationRoom = 1f },
            new Rules { Dry = 30.8f, MaxRelief = 8f, AvoidWater = false, LocationRoom = 0.6f },
            new Rules { Dry = float.MinValue, MaxRelief = float.MaxValue, AvoidWater = false, LocationRoom = 0.6f },
        };

        internal static Site Choose(ZoneSystem zoneSystem, Vector3 temple, Vector2s center, int radius, int worldSeed)
        {
            var generator = WorldGenerator.instance;
            if (generator == null || zoneSystem == null) return null;
            float half = ZoneOpenSet.ZoneSize * 0.5f;
            float minX = (center.x - radius) * ZoneOpenSet.ZoneSize - half + EdgeMargin;
            float maxX = (center.x + radius) * ZoneOpenSet.ZoneSize + half - EdgeMargin;
            float minZ = (center.y - radius) * ZoneOpenSet.ZoneSize - half + EdgeMargin;
            float maxZ = (center.y + radius) * ZoneOpenSet.ZoneSize + half - EdgeMargin;
            float hill = HillRadius * RimSpread;
            float passage = HillRadius * 1.2f + 2f;

            foreach (var rules in Levels)
            {
                float bestScore = float.MaxValue, bestGround = 0f;
                Vector3 best = default;
                foreach (float distance in SiteDistances)
                {
                    for (int k = 0; k < SiteAngles; k++)
                    {
                        float angle = k * Mathf.PI * 2f / SiteAngles;
                        float x = temple.x + Mathf.Cos(angle) * distance, z = temple.z + Mathf.Sin(angle) * distance;
                        if (x - hill < minX || x + hill > maxX || z - hill < minZ || z + hill > maxZ) continue;
                        float mouthAngle = Mathf.Atan2(temple.z - z, temple.x - x);
                        float mx = x + Mathf.Cos(mouthAngle) * passage, mz = z + Mathf.Sin(mouthAngle) * passage;
                        if (mx < minX || mx > maxX || mz < minZ || mz > maxZ) continue;
                        if (!ClearOfLocations(zoneSystem, x, z, hill, rules.LocationRoom)) continue;
                        if (!Ground(generator, x, z, rules, out float ground, out float relief)) continue;
                        if (generator.GetHeight(mx, mz) < rules.Dry) continue;
                        float score = relief + Mathf.Abs(distance - PreferredDistance) * 0.05f;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        bestGround = ground;
                        best = new Vector3(x, 0f, z);
                    }
                }
                if (bestScore < float.MaxValue) return Build(generator, temple, best, bestGround, worldSeed);
            }
            return null;
        }

        private static bool ClearOfLocations(ZoneSystem zoneSystem, float x, float z, float hill, float room)
        {
            foreach (var instance in zoneSystem.m_locationInstances.Values)
            {
                float exterior = instance.m_location != null ? instance.m_location.m_exteriorRadius : 10f;
                float reach = (hill + LocationMargin) * room + exterior;
                float dx = instance.m_position.x - x, dz = instance.m_position.z - z;
                if (dx * dx + dz * dz < reach * reach) return false;
            }
            return true;
        }

        // The real ground under the hill's footprint: its height at the centre, whether all of it stands above the
        // level's dry line and clear of generated water, and how uneven it is.
        private static bool Ground(WorldGenerator generator, float x, float z, Rules rules, out float center, out float relief)
        {
            center = generator.GetHeight(x, z);
            relief = 0f;
            if (center < rules.Dry || Wet(rules, x, z)) return false;
            float min = center, max = center;
            for (int ring = 1; ring <= 3; ring++)
            {
                float r = HillRadius * 0.4f * ring;
                for (int k = 0; k < 12; k++)
                {
                    float a = k * Mathf.PI / 6f;
                    float px = x + Mathf.Cos(a) * r, pz = z + Mathf.Sin(a) * r;
                    float h = generator.GetHeight(px, pz);
                    if (h < rules.Dry || Wet(rules, px, pz)) return false;
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            }
            relief = max - min;
            return relief <= rules.MaxRelief;
        }

        private static bool Wet(Rules rules, float x, float z)
            => rules.AvoidWater && FiresWorldGenBase.Active && FiresWorldGenBase.IsWetAt(x, z);

        private static Site Build(WorldGenerator generator, Vector3 temple, Vector3 at, float ground, int worldSeed)
        {
            float angle = Mathf.Atan2(temple.z - at.z, temple.x - at.x);
            float length = HillRadius * 1.2f + 2f;
            float mouthGround = generator.GetHeight(at.x + Mathf.Cos(angle) * length, at.z + Mathf.Sin(angle) * length);
            return new Site(at.x, at.z, HillRadius, HillHeight, Mathf.Max(ground + FloorLift, 32.5f), angle,
                Mathf.Max(mouthGround + 0.1f, 31f), unchecked(worldSeed * 31 + 0x5CA4E));
        }

        internal static string Describe(Site s)
        {
            var mouth = s.Mouth;
            return $"a {s.Height:0} m hill at ({s.X:0},{s.Z:0}), cavern floor y={s.FloorY:0.0}, mouth at ({mouth.x:0},{mouth.z:0}) "
                + "facing the StartTemple";
        }

        // ── Registry and handshake ──────────────────────────────────────────
        internal static void Write(BinaryWriter writer, Site s)
        {
            writer.Write(s.X);
            writer.Write(s.Z);
            writer.Write(s.Radius);
            writer.Write(s.Height);
            writer.Write(s.FloorY);
            writer.Write(s.MouthAngle);
            writer.Write(s.MouthFloorY);
            writer.Write(s.Seed);
        }

        internal static Site Read(BinaryReader reader)
            => Validate(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32());

        internal static void Write(ZPackage pkg, Site s)
        {
            pkg.Write(s.X);
            pkg.Write(s.Z);
            pkg.Write(s.Radius);
            pkg.Write(s.Height);
            pkg.Write(s.FloorY);
            pkg.Write(s.MouthAngle);
            pkg.Write(s.MouthFloorY);
            pkg.Write(s.Seed);
        }

        internal static Site Read(ZPackage pkg)
            => Validate(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(),
                pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadInt());

        private static Site Validate(float x, float z, float radius, float height, float floorY, float mouthAngle, float mouthFloorY, int seed)
        {
            if (!Finite(x) || !Finite(z) || !Finite(floorY) || !Finite(mouthAngle) || !Finite(mouthFloorY)
                || radius < 8f || radius > 64f || height < 4f || height > 48f)
                throw new InvalidDataException($"start cave site ({x},{z}) r={radius} h={height}");
            return new Site(x, z, radius, height, floorY, mouthAngle, mouthFloorY, seed);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Mathf.Abs(value) < 20000f;

        // ── Noise: the hill's own, seeded by its site, so it never depends on when the cave noise gets its seed ────
        private static float Step(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float Noise(int seed, float x, float y)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(seed, x0, y0), b = Hash(seed, x0 + 1, y0);
            float c = Hash(seed, x0, y0 + 1), d = Hash(seed, x0 + 1, y0 + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Hash(int seed, int x, int y)
        {
            unchecked
            {
                int h = seed * 374761393 + x * 668265263 + y * 1274126177;
                h = (h ^ (h >> 13)) * 1103515245;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
