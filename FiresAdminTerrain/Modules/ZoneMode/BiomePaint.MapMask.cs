using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using VerdantsAscent.Terrain.BcImport;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1378 (Fire: "paint over all the blacked out areas with red thicket custom biome and make sure the regen happens properly"): the black parts of a BetterContinents
    // biome map (index 0, "leave the biome here to whatever is underneath"). Each connected black area is one painted shape. Circles or polygons would leave
    // scallops, gaps or inner rims, and 26 areas up to 7 km wide don't fit either; here the map itself is the shape, so paint meets the map's other biomes exactly.
    // Saved as the seed pixel only: every peer holds the same map and rebuilds the area from it. Built on first use, then read-only from any thread.
    internal sealed class PaintMapMask
    {
        internal readonly int SeedCol, SeedRow;
        private readonly object m_lock = new object();
        private volatile bool m_ready;
        private bool m_failed;
        private BcBiomeMap m_map;
        private BcWorldSettings m_settings;
        private int m_col0, m_row0, m_w, m_h;
        private float m_pixel;
        private float[] m_rim;
        internal float Area, CentreX, CentreZ, BoundR;

        internal PaintMapMask(int col, int row) { SeedCol = col; SeedRow = row; }

        internal static bool TryMaps(out BcBiomeMap map, out BcWorldSettings settings)
        {
            var generator = BcMigratedWorld.Generator;
            map = generator?.Maps?.Biome;
            settings = generator?.Settings;
            return map != null && map.Biomes != null && settings != null;
        }

        // Every black area: seed column, seed row, pixels. Largest first.
        internal static List<int[]> FindAreas()
        {
            var areas = new List<int[]>();
            if (!TryMaps(out var map, out _)) return areas;
            int size = map.Size;
            var seen = new bool[map.Biomes.Length];
            var stack = new Stack<int>();
            for (int i = 0; i < map.Biomes.Length; i++)
            {
                if (seen[i] || map.Biomes[i] != Heightmap.Biome.None) continue;
                int pixels = 0;
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    pixels++;
                    int row = k / size, col = k - row * size;
                    if (col > 0) Visit(map, seen, stack, k - 1);
                    if (col < size - 1) Visit(map, seen, stack, k + 1);
                    if (row > 0) Visit(map, seen, stack, k - size);
                    if (row < size - 1) Visit(map, seen, stack, k + size);
                }
                areas.Add(new[] { i % size, i / size, pixels });
            }
            areas.Sort((a, b) => b[2].CompareTo(a[2]));
            return areas;
        }

        private static void Visit(BcBiomeMap map, bool[] seen, Stack<int> stack, int k)
        {
            if (seen[k] || map.Biomes[k] != Heightmap.Biome.None) return;
            seen[k] = true;
            stack.Push(k);
        }

        internal bool Ensure()
        {
            if (m_ready) return true;
            lock (m_lock)
            {
                if (m_ready) return true;
                if (m_failed || !TryMaps(out var map, out var settings)) return false;
                int size = map.Size, seed = SeedRow * size + SeedCol;
                if (SeedCol < 0 || SeedRow < 0 || SeedCol >= size || SeedRow >= size || map.Biomes[seed] != Heightmap.Biome.None)
                {
                    m_failed = true;
                    FiresLogger.LogWarning($"[BiomePaint] a saved black map area's seed ({SeedCol},{SeedRow}) is not black on this world's map: it does nothing.");
                    return false;
                }
                var member = new HashSet<int>();
                var stack = new Stack<int>();
                member.Add(seed);
                stack.Push(seed);
                int c0 = SeedCol, c1 = SeedCol, r0 = SeedRow, r1 = SeedRow;
                void Try(int k) { if (map.Biomes[k] == Heightmap.Biome.None && member.Add(k)) stack.Push(k); }
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    int row = k / size, col = k - row * size;
                    c0 = Mathf.Min(c0, col); c1 = Mathf.Max(c1, col); r0 = Mathf.Min(r0, row); r1 = Mathf.Max(r1, row);
                    if (col > 0) Try(k - 1);
                    if (col < size - 1) Try(k + 1);
                    if (row > 0) Try(k - size);
                    if (row < size - 1) Try(k + size);
                }
                float pixel = settings.TotalSize / (size - 1);
                int w = c1 - c0 + 3, h = r1 - r0 + 3;
                var rim = new float[w * h];
                float diagonal = pixel * 1.4142f, far = pixel * (w + h);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int col = c0 - 1 + x, row = r0 - 1 + y;
                        rim[y * w + x] = col >= 0 && row >= 0 && col < size && row < size && member.Contains(row * size + col) ? far : 0f;
                    }
                // Two-pass chamfer distance to the nearest pixel outside the area, in metres.
                for (int y = 1; y < h; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        rim[i] = Mathf.Min(rim[i], Mathf.Min(rim[i - 1] + pixel, Mathf.Min(rim[i - w] + pixel, Mathf.Min(rim[i - w - 1] + diagonal, rim[i - w + 1] + diagonal))));
                    }
                for (int y = h - 2; y >= 0; y--)
                    for (int x = w - 2; x >= 1; x--)
                    {
                        int i = y * w + x;
                        rim[i] = Mathf.Min(rim[i], Mathf.Min(rim[i + 1] + pixel, Mathf.Min(rim[i + w] + pixel, Mathf.Min(rim[i + w + 1] + diagonal, rim[i + w - 1] + diagonal))));
                    }
                m_map = map; m_settings = settings; m_col0 = c0 - 1; m_row0 = r0 - 1; m_w = w; m_h = h; m_pixel = pixel; m_rim = rim;
                Area = member.Count * pixel * pixel;
                CentreX = ((c0 + c1) * 0.5f / (size - 1) - 0.5f) * settings.TotalSize;
                CentreZ = ((r0 + r1) * 0.5f / (size - 1) - 0.5f) * settings.TotalSize;
                BoundR = Mathf.Ceil(Mathf.Sqrt((c1 - c0 + 2) * (c1 - c0 + 2) + (r1 - r0 + 2) * (r1 - r0 + 2)) * 0.5f * pixel) + 1f;
                m_ready = true;
                return true;
            }
        }

        private bool Cell(float wx, float wz, out float fx, out float fy)
        {
            int last = m_map.Size - 1;
            fx = m_settings.Normalize(wx) * last - m_col0;
            fy = m_settings.Normalize(wz) * last - m_row0;
            return fx >= 0f && fy >= 0f && fx < m_w - 1 && fy < m_h - 1;
        }

        // The map's own answer at the point, so the shape meets its neighbours exactly as the map draws them.
        internal bool Holds(float wx, float wz)
        {
            if (!Ensure() || !Cell(wx, wz, out _, out _)) return false;
            return m_map.GetValue(m_settings.Normalize(wx), m_settings.Normalize(wz)) == Heightmap.Biome.None;
        }

        internal float Rim(float wx, float wz)
        {
            if (!Holds(wx, wz) || !Cell(wx, wz, out float fx, out float fy)) return 0f;
            int ix = (int)fx, iy = (int)fy;
            float tx = fx - ix, ty = fy - iy;
            float a = Mathf.Lerp(m_rim[iy * m_w + ix], m_rim[iy * m_w + ix + 1], tx);
            float b = Mathf.Lerp(m_rim[(iy + 1) * m_w + ix], m_rim[(iy + 1) * m_w + ix + 1], tx);
            return Mathf.Max(Mathf.Lerp(a, b, ty), m_pixel * 0.25f);   // a thin neck still counts as inside
        }

        // The point furthest from the rim, for a test to stand on.
        internal Vector2 DeepestPoint()
        {
            Ensure();
            int best = 0;
            for (int i = 0; i < m_rim.Length; i++) if (m_rim[i] > m_rim[best]) best = i;
            int y = best / m_w, x = best - y * m_w, last = m_map.Size - 1;
            return new Vector2(((m_col0 + x) / (float)last - 0.5f) * m_settings.TotalSize, ((m_row0 + y) / (float)last - 0.5f) * m_settings.TotalSize);
        }
    }

    internal static partial class BiomePaint
    {
        internal const string BlackTestBiome = "RedThicket";

        // Server. One shape per black area of this world's map. Each commits on its own; growth runs a zone at a time (Regrow).
        internal static bool TryPaintMapBlack(string key, long asker, float blendWidth, out string report)
        {
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            var areas = PaintMapMask.FindAreas();
            if (areas.Count == 0) { report = "This world has no BetterContinents map with black areas."; return false; }
            // 0.2.1380 (Fire: "Just wipe and regenerate everything from within the red ticket"): first request counts and arms, a repeat within WipeArmSeconds wipes, then paints.
            if (!MapWipeGate(key, asker, areas, out report)) return false;
            string wipedReport = report;
            int painted = 0;
            string last = string.Empty;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var area in areas)
            {
                if (!TryPaint(key, Vector3.zero, 0f, asker, out _, out last, false, blendWidth, null, area)) { report = $"Stopped after {painted} of {areas.Count} black areas: {last}"; return false; }
                painted++;
            }
            report = $"{wipedReport} Painted all {painted} black map area(s) with {Describe(key)} in {(System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms; growth follows a zone at a time.";
            FiresLogger.LogInfo("[BiomePaint] " + report + " (0.2.1378)");
            return true;
        }

        // Held ground for a shape too big for a grid: discs (x, z, radius) kept in the same byte store, found through a 64 m index.
        private sealed class HoldDiscs
        {
            internal readonly float[] Values;
            internal readonly Dictionary<long, List<int>> Cells = new Dictionary<long, List<int>>();
            internal HoldDiscs(byte[] bytes)
            {
                Values = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, Values, 0, Values.Length * 4);
                for (int i = 0; i + 2 < Values.Length; i += 3)
                {
                    float reach = Values[i + 2] + HoldRamp;
                    for (int cz = Cell(Values[i + 1] - reach); cz <= Cell(Values[i + 1] + reach); cz++)
                        for (int cx = Cell(Values[i] - reach); cx <= Cell(Values[i] + reach); cx++)
                        {
                            long key = Key(cx, cz);
                            if (!Cells.TryGetValue(key, out var list)) Cells[key] = list = new List<int>();
                            list.Add(i);
                        }
                }
            }
        }

        private static readonly ConditionalWeakTable<byte[], HoldDiscs> s_holdDiscs = new ConditionalWeakTable<byte[], HoldDiscs>();

        private static float DiscsAllow(byte[] bytes, float wx, float wz)
        {
            var discs = s_holdDiscs.GetValue(bytes, b => new HoldDiscs(b));
            if (!discs.Cells.TryGetValue(CellKey(wx, wz), out var list)) return 1f;
            float allow = 1f;
            foreach (int i in list)
            {
                float dx = wx - discs.Values[i], dz = wz - discs.Values[i + 1], r = discs.Values[i + 2];
                allow = Mathf.Min(allow, Smooth(r, r + HoldRamp, Mathf.Sqrt(dx * dx + dz * dz)));
            }
            return allow;
        }

        private static void BuildMaskHolds(in Circle circle)
        {
            var map = new Dictionary<uint, byte[]>(s_holdMap);
            map.Remove(circle.Id);
            var values = new List<float>();
            var marked = new HashSet<long>();
            var zones = ZoneSystem.instance;
            int pieces = 0, locations = 0, scanned = 0;
            var found = new List<ZDO>();
            var one = new List<Circle> { circle };
            var generated = zones != null ? GeneratedZones(zones) : null;
            foreach (var zone in ZonesOf(circle))
            {
                if (generated != null && !generated.Contains(zone)) continue;
                scanned++;
                found.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (zdo == null || zdo.GetLong(ZDOVars.s_creator) == 0L) continue;
                    var at = zdo.GetPosition();
                    if (!circle.Holds(at.x, at.z)) continue;
                    pieces++;
                    if (!marked.Add(Key(Mathf.FloorToInt(at.x / 8f), Mathf.FloorToInt(at.z / 8f)))) continue;
                    values.Add(at.x); values.Add(at.z); values.Add(PieceHoldRadius + 4f);
                }
                if (zones != null && zones.m_locationInstances.TryGetValue(zone, out var instance) && instance.m_location != null && !LocationGoes(zone, instance, one)
                    && circle.Holds(instance.m_position.x, instance.m_position.z))
                {
                    locations++;
                    values.Add(instance.m_position.x); values.Add(instance.m_position.z);
                    values.Add(Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin);
                }
            }
            if (values.Count > 0)
            {
                var bytes = new byte[values.Count * 4];
                Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
                map[circle.Id] = bytes;
            }
            s_holdMap = map;
            FiresLogger.LogDebug($"[BiomePaint] ground held under black area {circle.Id}: {pieces} piece(s), {locations} location(s) that stay, {scanned} generated zone(s) read (0.2.1380)");
        }

        // ── For fat_black_paint_test ──────────────────────────────────────
        internal static int MaskShapes { get { int n = 0; foreach (var c in s_snapshot.Circles) if (c.Mask != null) n++; return n; } }
        internal static string CustomBlackKey() => KeyOf(Brush.Custom, Heightmap.Biome.None, BlackTestBiome);

        // The land with paint left out (the map step kept), and the painted biome's own target.
        internal static void TestHeights(WorldGenerator generator, Heightmap.Biome ground, float wx, float wz, out float world, out float target)
        {
            t_noPaint++;
            try { world = generator.GetBiomeHeight(OriginalBiome(generator, wx, wz), wx, wz, out Color _); }
            finally { t_noPaint--; }
            t_heightScope++;
            try { target = generator.GetBiomeHeight(ground, wx, wz, out Color _); }
            finally { t_heightScope--; }
        }

        internal static int GrowthWaiting => s_pending.Count;
    }
}
