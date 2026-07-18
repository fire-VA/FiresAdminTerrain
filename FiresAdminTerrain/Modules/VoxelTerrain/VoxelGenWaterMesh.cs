using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Generated water v4 — ONE pure function decides everything.
    // WetAt/SurfaceAt sample the SMOOTH (bilinear) gen water field against
    // the real heightmap: wet where smoothed depth and clearance over real
    // ground both hold. The mesh renders that function per 1m vertex; the
    // swim surface reports THE SAME function at the swimmer's position — the
    // trigger IS the mesh, nothing else. Pure function of world position →
    // adjacent zones compute identical border values (level steps at zone
    // crossings are impossible), bilinear taper → the surface dives under
    // the banks instead of holding channel level over the edge (both were
    // shipped 0.2.142 defects, screenshot-proven).
    // Zones whose heightmaps aren't in yet queue and retry (the wrap can
    // beat the vanilla heightmap spawn; caching an empty build forever was
    // the sheets-ending-mid-air / water-with-no-swim bug).
    internal static class VoxelGenWaterMesh
    {
        private const float STEP = 1f;
        private const int INTERIOR = 64;           // 64m zone, 65 verts
        private const int APRON = 8;               // covers EDGE_PASSES so border zones agree
        private const int G = INTERIOR + 1 + APRON * 2;
        private const int EDGE_PASSES = 8;         // bank-erosion reach (m)
        private const float EDGE_DROP = 0.6f;      // water edge may stand ≤ this over dry ground
        private const float SEA_GATE = 30.25f;     // ≤ this: the ocean owns it — river mouths settle to ~30 and BLEND into the sea surface. Mouth verts ride the tide in-shader (Custom/FiresLiquid weights _FiresTideOffset by vert height: full ≤30.55, 0 ≥31.5).
        private const float MIN_DEPTH = 0.25f;     // smoothed-field depth to count as water
        private const float MIN_CLEAR = 0.05f;     // level must stand above real ground
        private const int EXT_RINGS = 2;           // flat shoreline overshoot: the sheet holds channel level INTO the bank (clips through) — flooded-bed look, no tucked seam

        private static readonly Dictionary<Vector2i, GameObject> s_zones = new Dictionary<Vector2i, GameObject>();
        private static readonly HashSet<Vector2i> s_pending = new HashSet<Vector2i>();
        private static readonly List<Vector2i> s_retryScratch = new List<Vector2i>();
        private static Transform s_root;
        private static float s_nextRetry;

        private static readonly float[] s_out = new float[G * G];
        private static readonly float[] s_gnd = new float[G * G];
        private static readonly bool[] s_wet = new bool[G * G];
        private static readonly bool[] s_preWet = new bool[G * G];
        private static readonly bool[] s_kill = new bool[G * G];
        private static readonly byte[] s_ring = new byte[G * G];

        // THE function. Wet + surface level at a world position, or dry.
        // Everything — mesh verts, swim level, future FX — must go through
        // this so they can never disagree.
        public static bool WetAt(float wx, float wz, out float surface)
        {
            surface = 0f;
            if (!FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)) return false;
            if (depth < MIN_DEPTH || lvl <= SEA_GATE) return false;
            if (!Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float gnd)) return false;
            if (lvl < gnd + MIN_CLEAR) return false;
            surface = lvl;
            return true;
        }

        // Swim query — the mesh function at the swimmer's position. −10000 =
        // not in this water (vanilla's "no liquid" sentinel). Positions well
        // below the real terrain surface are caves UNDER the river: dry.
        public static float SurfaceAt(Vector3 pos)
        {
            if (!WetAt(pos.x, pos.z, out float surface)) return -10000f;
            if (Heightmap.GetHeight(new Vector3(pos.x + 0.013f, 0f, pos.z + 0.013f), out float gnd)
                && pos.y < gnd - 1f) return -10000f;
            return surface;
        }

        public static void BuildForZone(Vector2i zone)
        {
            if (s_zones.ContainsKey(zone)) return;
            if (!FiresWorldGenBase.Active) return;
            if (!TryBuild(zone)) s_pending.Add(zone);
        }

        public static void DropZone(Vector2i zone)
        {
            s_pending.Remove(zone);
            if (!s_zones.TryGetValue(zone, out var go)) return;
            s_zones.Remove(zone);
            if (go != null) Object.Destroy(go);
        }

        public static void Tick()
        {
            if (s_pending.Count == 0 || Time.time < s_nextRetry) return;
            s_nextRetry = Time.time + 1.5f;
            s_retryScratch.Clear();
            s_retryScratch.AddRange(s_pending);
            foreach (var zone in s_retryScratch)
            {
                if (s_zones.ContainsKey(zone)) { s_pending.Remove(zone); continue; }
                if (TryBuild(zone)) s_pending.Remove(zone);
            }
        }

        private static bool TryBuild(Vector2i zone)
        {
            float ox = zone.x * 64f - 32f - APRON * STEP;
            float oz = zone.y * 64f - 32f - APRON * STEP;

            var outY = s_out; var wet = s_wet;
            bool any = false, groundMissing = false;
            float lvMin = float.MaxValue, lvMax = float.MinValue, gndMin = float.MaxValue;
            for (int z = 0; z < G; z++)
            {
                for (int x = 0; x < G; x++)
                {
                    int i = z * G + x;
                    float wx = ox + x * STEP, wz = oz + z * STEP;
                    wet[i] = false; outY[i] = 0f; s_kill[i] = false;
                    s_gnd[i] = Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float gndHere)
                        ? gndHere : float.MaxValue;
                    if (!FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)) continue;
                    if (depth < MIN_DEPTH || lvl <= SEA_GATE) continue;
                    if (s_gnd[i] == float.MaxValue) { groundMissing = true; continue; }
                    if (lvl < s_gnd[i] + MIN_CLEAR) continue;
                    wet[i] = true;
                    outY[i] = lvl;
                    any = true;
                    if (lvl < lvMin) lvMin = lvl;
                    if (lvl > lvMax) lvMax = lvl;
                    if (s_gnd[i] < gndMin) gndMin = s_gnd[i];
                }
            }
            if (groundMissing) return false;             // heightmap not in — retry from Tick
            if (!any) { s_zones[zone] = null; return true; }

            // SLOPE GATE — a water SURFACE steeper than ~20° is the bilinear
            // ramp between two pools draped on a hillside (screenshot-proven
            // water climbing a 45° slope), not water. Settled pools are
            // near-flat; small cascade steps (≤0.35m per meter) survive.
            for (int z = 0; z < G; z++)
            {
                for (int x = 0; x < G; x++)
                {
                    int i = z * G + x;
                    if (!wet[i]) continue;
                    bool steep = (x + 1 < G && wet[i + 1] && Mathf.Abs(outY[i + 1] - outY[i]) > 0.35f)
                              || (z + 1 < G && wet[i + G] && Mathf.Abs(outY[i + G] - outY[i]) > 0.35f);
                    if (steep) s_kill[i] = true;
                }
            }
            for (int i = 0; i < G * G; i++)
                if (s_kill[i]) { wet[i] = false; outY[i] = 0f; s_kill[i] = false; }

            // BANK EROSION — fine-scale enforcement of never-above-banks.
            // A wet vert whose NEVER-WET dry neighbor's ground sits more than
            // EDGE_DROP below the surface is a standing water wall (perched
            // rims, step faces); retreat until the edge lands on holding
            // ground. Only never-wet neighbors count — an eroded cell's
            // ground is the water body's own bed, and counting it cascaded
            // the erosion straight through entire rivers. Two-phase per pass,
            // bounded by EDGE_PASSES = APRON for zone-identical borders.
            for (int i = 0; i < G * G; i++) s_preWet[i] = wet[i];
            for (int pass = 0; pass < EDGE_PASSES; pass++)
            {
                bool changed = false;
                for (int z = 0; z < G; z++)
                {
                    for (int x = 0; x < G; x++)
                    {
                        int i = z * G + x;
                        if (!wet[i]) continue;
                        bool wall = false;
                        for (int dz = -1; dz <= 1 && !wall; dz++)
                            for (int dx = -1; dx <= 1 && !wall; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                                int j = nz * G + nx;
                                if (wet[j] || s_preWet[j] || s_gnd[j] == float.MaxValue) continue;
                                if (s_gnd[j] < outY[i] - EDGE_DROP) wall = true;
                            }
                        if (wall) { s_kill[i] = true; changed = true; }
                    }
                }
                if (!changed) break;
                // s_preWet stays TRUE on eroded cells — they were water, and
                // treating them as fresh cliff would restart the cascade.
                for (int i = 0; i < G * G; i++)
                    if (s_kill[i]) { wet[i] = false; outY[i] = 0f; s_kill[i] = false; }
            }
            any = false;
            for (int i = 0; i < G * G && !any; i++) if (wet[i]) any = true;
            if (!any) { s_zones[zone] = null; return true; }

            // Shoreline: FLAT overshoot. Dry verts near wet hold the neighbor
            // water LEVEL and the sheet clips straight through the rising
            // bank — a flooded bed that visually fills its channel edge to
            // edge. (The old tuck bent the rim under its own bank, which read
            // as water stopping short of the banks.) Two rings so the clip
            // plane buries deep enough that no camera angle sees the rim.
            for (int i = 0; i < G * G; i++) s_ring[i] = 0;
            for (int pass = 1; pass <= EXT_RINGS; pass++)
            {
                for (int z = 0; z < G; z++)
                {
                    for (int x = 0; x < G; x++)
                    {
                        int i = z * G + x;
                        if (wet[i] || s_ring[i] != 0) continue;
                        float lvl = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                                int j = nz * G + nx;
                                bool src = pass == 1 ? wet[j] : s_ring[j] == pass - 1;
                                if (src && outY[j] > lvl) lvl = outY[j];
                            }
                        if (lvl > 0f) { s_kill[i] = true; outY[i] = lvl; }
                    }
                }
                for (int i = 0; i < G * G; i++)
                    if (s_kill[i]) { s_ring[i] = (byte)pass; s_kill[i] = false; }
            }

            var go = new GameObject($"FiresGenWater_{zone.x}_{zone.y}");
            if (s_root == null)
            {
                var rgo = new GameObject("FiresGenWaterRoot");
                Object.DontDestroyOnLoad(rgo);
                s_root = rgo.transform;
            }
            go.transform.SetParent(s_root, false);
            go.transform.position = new Vector3(zone.x * 64f - 32f, 0f, zone.y * 64f - 32f);

            var verts = new List<Vector3>(1024);
            var tris = new List<int>(2048);
            var map = new int[G * G];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            int V(int x, int z)
            {
                int i = z * G + x;
                if (map[i] >= 0) return map[i];
                map[i] = verts.Count;
                verts.Add(new Vector3((x - APRON) * STEP, outY[i], (z - APRON) * STEP));
                return map[i];
            }
            // Float detector — a wet vert whose neighbor ground sits well
            // below the surface renders as a sheet hanging over a hole (the
            // clip-through overshoot is INTO banks; over drops it's visible
            // underside). Counted per zone; worst offender logs world coords.
            int floats = 0;
            float worstF = 0f, worstWx = 0f, worstWz = 0f;
            for (int z = APRON; z <= APRON + INTERIOR; z++)
            {
                for (int x = APRON; x <= APRON + INTERIOR; x++)
                {
                    int i = z * G + x;
                    if (!wet[i]) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                            int j = nz * G + nx;
                            if (wet[j] || s_ring[j] == 0 || s_gnd[j] == float.MaxValue) continue;
                            float gap = outY[i] - s_gnd[j];
                            if (gap > 1.5f)
                            {
                                floats++;
                                if (gap > worstF)
                                { worstF = gap; worstWx = ox + x * STEP; worstWz = oz + z * STEP; }
                            }
                        }
                }
            }

            int wetCells = 0;
            for (int z = APRON; z < APRON + INTERIOR; z++)
            {
                for (int x = APRON; x < APRON + INTERIOR; x++)
                {
                    int i00 = z * G + x, i10 = i00 + 1, i01 = i00 + G, i11 = i01 + 1;
                    // Emit through the first extension ring so ring-1↔ring-2
                    // quads exist — the flat overshoot spans clear into banks.
                    bool core = wet[i00] || wet[i10] || wet[i01] || wet[i11]
                             || s_ring[i00] == 1 || s_ring[i10] == 1 || s_ring[i01] == 1 || s_ring[i11] == 1;
                    if (!core) continue;
                    if (outY[i00] <= 0f || outY[i10] <= 0f || outY[i01] <= 0f || outY[i11] <= 0f) continue;
                    int a = V(x, z), b = V(x + 1, z), c = V(x, z + 1), d = V(x + 1, z + 1);
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                    wetCells++;
                }
            }
            if (tris.Count == 0) { Object.Destroy(go); s_zones[zone] = null; return true; }

            var mesh = new Mesh { name = go.name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var norms = new Vector3[verts.Count];
            for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;
            mesh.normals = norms;
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mat = Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
            if (mat != null) mr.sharedMaterial = mat;

            // Swim: one trigger spanning the zone's wet band; the level each
            // swimmer receives comes from SurfaceAt — the mesh function.
            var sv = new GameObject("surface");
            sv.transform.SetParent(go.transform, false);
            sv.transform.position = new Vector3(zone.x * 64f, lvMax, zone.y * 64f);
            int waterLayer = LayerMask.NameToLayer("Water");
            sv.layer = waterLayer >= 0 ? waterLayer : 4;
            var box = sv.AddComponent<BoxCollider>();
            box.isTrigger = true;
            float below = Mathf.Max(6f, lvMax - gndMin + 2f);
            box.size = new Vector3(64f, below + 2f, 64f);
            box.center = new Vector3(0f, 1f - (below + 2f) * 0.5f, 0f);
            sv.AddComponent<FiresGenWaterSurface>();

            s_zones[zone] = go;
            Debug.Log($"[FiresVoxel] zone ({zone.x},{zone.y}) gen water: {wetCells} cells @1m, " +
                      $"lvl {lvMin:F1}-{lvMax:F1}, floats={floats}" +
                      (floats > 0 ? $" worst Δ{worstF:0.0}m at ({worstWx:0},{worstWz:0})" : "") +
                      ", swim=mesh function.");
            return true;
        }
    }

    // LiquidSurface-style swim driver: characters entering the trigger get
    // their liquid level from VoxelGenWaterMesh.SurfaceAt every FixedUpdate —
    // the exact function the visible mesh renders. Standing on a dry bank
    // inside the box reads −10000 (dry); wading where the mesh is reads the
    // mesh height. Swim and visuals cannot disagree.
    // Anti-flap: at the 1m shoreline lattice a wading character straddles
    // wet/dry cells every step, and raw −10000 flips bounced them between
    // walk and swim (the hop). Dry reads HOLD the last wet surface for
    // DRY_HOLD ticks before reporting dry. Players also get hip-depth swim
    // (m_swimDepth 2→1.5: vanilla enters swim at depth > m_swimDepth−0.4,
    // so ~1.1m — the hips — instead of chest/head).
    internal sealed class FiresGenWaterSurface : MonoBehaviour
    {
        private const int DRY_HOLD = 12;

        private sealed class Entry
        {
            public IWaterInteractable Wi;
            public float LastSurface = -10000f;
            public int DryTicks;
        }

        private readonly List<Entry> m_inWater = new List<Entry>();

        private void OnTriggerEnter(Collider c)
        {
            var rb = c.attachedRigidbody;
            if (rb == null) return;
            var wi = rb.GetComponent<IWaterInteractable>();
            if (wi == null) return;
            wi.Increment(LiquidType.Water);
            for (int i = 0; i < m_inWater.Count; i++)
                if (ReferenceEquals(m_inWater[i].Wi, wi)) return;
            m_inWater.Add(new Entry { Wi = wi });
        }

        private void OnTriggerExit(Collider c)
        {
            var rb = c.attachedRigidbody;
            if (rb == null) return;
            var wi = rb.GetComponent<IWaterInteractable>();
            if (wi == null) return;
            if (wi.Decrement(LiquidType.Water) == 0)
                wi.SetLiquidLevel(-10000f, LiquidType.Water, this);
            for (int i = m_inWater.Count - 1; i >= 0; i--)
                if (ReferenceEquals(m_inWater[i].Wi, wi)) m_inWater.RemoveAt(i);
        }

        private bool m_playerWasInMeshWater;

        private void FixedUpdate()
        {
            for (int i = m_inWater.Count - 1; i >= 0; i--)
            {
                var e = m_inWater[i];
                var wi = e.Wi;
                if (wi == null) { m_inWater.RemoveAt(i); continue; }
                var t = wi.GetTransform();
                if (t == null) { m_inWater.RemoveAt(i); continue; }
                float surface = VoxelGenWaterMesh.SurfaceAt(t.position);
                if (surface > -9999f)
                {
                    e.LastSurface = surface;
                    e.DryTicks = 0;
                }
                else if (e.LastSurface > -9999f && ++e.DryTicks <= DRY_HOLD)
                {
                    surface = e.LastSurface;
                }
                else
                {
                    e.LastSurface = -10000f;
                }
                wi.SetLiquidLevel(surface, LiquidType.Water, this);
                if (wi is Player pl && pl.m_swimDepth > 1.5f) pl.m_swimDepth = 1.5f;

                // Swim receipt: state CHANGES for the local player log the
                // surface the mesh function reported at their feet — the log
                // proves swim==mesh (or names the exact spot where it lied).
                if (Player.m_localPlayer != null && t == Player.m_localPlayer.transform)
                {
                    bool inWater = surface > -9999f && surface > t.position.y - 2f;
                    if (inWater != m_playerWasInMeshWater)
                    {
                        m_playerWasInMeshWater = inWater;
                        Vector3 p = t.position;
                        Debug.Log($"[FiresVoxel] swim {(inWater ? "ENTER" : "EXIT")} gen water at ({p.x:0},{p.z:0}): " +
                                  $"surface={(surface > -9999f ? surface.ToString("F1") : "dry")} feet={p.y:F1}");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var e in m_inWater)
                if (e.Wi != null && e.Wi.Decrement(LiquidType.Water) == 0)
                    e.Wi.SetLiquidLevel(-10000f, LiquidType.Water, this);
            m_inWater.Clear();
        }
    }
}
