using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING CLUTTER (0.2.1041, step 3 of the ring biome plan; Fire 10-24: biome patches with "paint, grass/clutter, trees,
    // the full design"). Vanilla's ClutterSystem lays its grass down a heightfield (rays straight down, world-up meshes), which
    // can never reach a ring wall. On a ring-only world this draws vanilla's OWN clutter entries instead, on the ring:
    //   * the band near the camera is cut into PatchSize-metre patches in the ring's unrolled (arc, axis) coordinates;
    //   * each patch takes the vanilla ClutterSystem entries of the ring biome under it (FatHaloBiome) at vanilla's density
    //     (m_amount per m_grassPatchSize square), sizes (m_scaleMin..Max) and random spin;
    //   * every piece is seated by a short ray onto the floor along the ring's down and turned to the ring's up;
    //   * pieces are drawn instanced (Graphics.RenderMeshInstanced) from the entry's InstanceRenderer mesh + material, or its
    //     prefab's mesh renderers. Visual only, per machine, deterministic from the world seed, nothing collides.
    // A patch whose floor is not built yet (no hit) is retried on the next pass.
    internal static class FatHaloClutter
    {
        private const float PatchSize = 16f, Range = 48f, MaintainSeconds = 0.5f, RayLift = 2f, RayReach = 5f;
        private const int MaxPerDraw = 1023, MaxBuildsPerPass = 6;

        private struct Piece { public Mesh Mesh; public Material Material; public int SubMesh; public Matrix4x4 FromRoot; public ShadowCastingMode Shadows; }

        private sealed class Patch
        {
            public bool Built;
            public int Tries;
            public readonly Dictionary<(Mesh, int, Material), List<Matrix4x4>> Batches = new Dictionary<(Mesh, int, Material), List<Matrix4x4>>();
            public readonly Dictionary<(Mesh, int, Material), ShadowCastingMode> Shadows = new Dictionary<(Mesh, int, Material), ShadowCastingMode>();
        }

        private static readonly Dictionary<(int, int), Patch> s_patches = new Dictionary<(int, int), Patch>();
        private static readonly Dictionary<GameObject, Piece[]> s_pieces = new Dictionary<GameObject, Piece[]>();
        private static readonly List<(int, int)> s_drop = new List<(int, int)>();
        private static float s_nextMaintain;
        private static int s_mask;
        private static bool s_logged, s_failed;

        internal static void Tick()
        {
            if (s_failed || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            if (!FatHaloArc.RingOnly || !FatHaloArc.Active || ClutterSystem.instance == null) { if (s_patches.Count > 0) s_patches.Clear(); return; }
            var cam = Camera.main;
            if (cam == null || !FatHaloArc.Axis(out Vector3 o, out Vector3 d, out float r)) return;
            try
            {
                if (Time.unscaledTime >= s_nextMaintain) { s_nextMaintain = Time.unscaledTime + MaintainSeconds; Maintain(cam.transform.position, o, d, r); }
                Draw(cam);
            }
            catch (System.Exception ex) { s_failed = true; FiresLogger.LogWarning($"[FatHalo] ring clutter off: {ex.Message}"); }
        }

        private static void Frame(Vector3 o, Vector3 d, out Vector3 ref0, out Vector3 ref1)
        {
            ref0 = Mathf.Abs(d.y) > 0.999f ? Vector3.right : Vector3.ProjectOnPlane(Vector3.down, d).normalized;
            ref1 = Vector3.Cross(d, ref0);
        }

        private static void Maintain(Vector3 camPos, Vector3 o, Vector3 d, float r)
        {
            Frame(o, d, out Vector3 ref0, out Vector3 ref1);
            Vector3 rel = camPos - o;
            float v = Vector3.Dot(rel, d);
            Vector3 radial = rel - v * d;
            if (radial.magnitude < 1f) return;
            float ang = Mathf.Atan2(Vector3.Dot(radial, ref1), Vector3.Dot(radial, ref0));
            float circ = 2f * Mathf.PI * r;
            int around = Mathf.Max(1, Mathf.RoundToInt(circ / PatchSize));
            float u = (ang < 0f ? ang + 2f * Mathf.PI : ang) * r;
            int pu = Mathf.FloorToInt(u / PatchSize), pv = Mathf.FloorToInt(v / PatchSize), reach = Mathf.CeilToInt(Range / PatchSize);

            s_drop.Clear();
            foreach (var key in s_patches.Keys)
            {
                int du = Mathf.Abs(key.Item1 - ((pu % around) + around) % around); du = Mathf.Min(du, around - du);
                if (du > reach + 1 || Mathf.Abs(key.Item2 - pv) > reach + 1) s_drop.Add(key);
            }
            foreach (var key in s_drop) s_patches.Remove(key);

            int built = 0;
            for (int dv = -reach; dv <= reach; dv++)
                for (int du = -reach; du <= reach; du++)
                {
                    var key = ((((pu + du) % around) + around) % around, pv + dv);
                    if (!s_patches.TryGetValue(key, out var patch)) { patch = new Patch(); s_patches[key] = patch; }
                    if (patch.Built || patch.Tries > 20 || built >= MaxBuildsPerPass) continue;
                    patch.Tries++;
                    if (Build(patch, key.Item1, key.Item2, o, d, r, ref0, ref1, around)) built++;
                }
        }

        private static bool Build(Patch patch, int pu, int pv, Vector3 o, Vector3 d, float r, Vector3 ref0, Vector3 ref1, int around)
        {
            var cs = ClutterSystem.instance;
            if (s_mask == 0) s_mask = LayerMask.GetMask("terrain");
            float circ = around * PatchSize;
            Vector3 PointAt(float uu, float vv, out Vector3 outward)
            {
                float a = uu / circ * 2f * Mathf.PI;
                outward = ref0 * Mathf.Cos(a) + ref1 * Mathf.Sin(a);
                return o + d * vv + outward * r;
            }
            Vector3 centre = PointAt((pu + 0.5f) * PatchSize, (pv + 0.5f) * PatchSize, out Vector3 outC);
            if (!Physics.Raycast(centre - outC * RayLift, outC, RayReach, s_mask, QueryTriggerInteraction.Ignore)) return false;   // floor not built yet
            if (!FatHaloBiome.TryBiomeAt(centre, out var biome)) { patch.Built = true; return true; }

            var rng = new System.Random(unchecked(pu * 73856093 ^ pv * 19349663 ^ (ZNet.World != null ? ZNet.World.m_seed : 0)));
            float vanillaArea = cs.m_grassPatchSize * cs.m_grassPatchSize, area = PatchSize * PatchSize;
            int placed = 0;
            foreach (var c in cs.m_clutter)
            {
                if (c == null || !c.m_enabled || c.m_prefab == null || (c.m_biome & biome) == 0) continue;
                var pieces = PiecesOf(c.m_prefab);
                if (pieces.Length == 0) continue;
                int count = Mathf.RoundToInt(c.m_amount * area / vanillaArea);
                for (int i = 0; i < count; i++)
                {
                    float uu = (pu + (float)rng.NextDouble()) * PatchSize, vv = (pv + (float)rng.NextDouble()) * PatchSize;
                    float spin = (float)rng.NextDouble() * 360f, scale = Mathf.Lerp(c.m_scaleMin, c.m_scaleMax, (float)rng.NextDouble());
                    Vector3 p = PointAt(uu, vv, out Vector3 outward);
                    if (!Physics.Raycast(p - outward * RayLift, outward, out RaycastHit hit, RayReach, s_mask, QueryTriggerInteraction.Ignore)) continue;
                    Vector3 up = -outward;
                    var rot = Quaternion.AngleAxis(spin, up) * Quaternion.FromToRotation(Vector3.up, up);
                    var root = Matrix4x4.TRS(hit.point, rot, Vector3.one * scale);
                    foreach (var piece in pieces)
                    {
                        var k = (piece.Mesh, piece.SubMesh, piece.Material);
                        if (!patch.Batches.TryGetValue(k, out var list)) { list = new List<Matrix4x4>(); patch.Batches[k] = list; patch.Shadows[k] = piece.Shadows; }
                        list.Add(root * piece.FromRoot);
                    }
                    placed++;
                }
            }
            patch.Built = true;
            if (!s_logged) { s_logged = true; FiresLogger.LogDebug($"[FatHalo] ring clutter: first patch ({pu},{pv}) {biome}: {placed} piece(s) from vanilla's ClutterSystem entries"); }
            return true;
        }

        private static Piece[] PiecesOf(GameObject prefab)
        {
            if (s_pieces.TryGetValue(prefab, out var cached)) return cached;
            var list = new List<Piece>();
            var inst = prefab.GetComponentInChildren<InstanceRenderer>(true);
            if (inst != null && inst.m_mesh != null && inst.m_material != null)
                list.Add(new Piece { Mesh = inst.m_mesh, Material = inst.m_material, SubMesh = 0, FromRoot = Matrix4x4.Scale(inst.m_scale), Shadows = inst.m_shadowCasting });
            else
            {
                var rootInv = prefab.transform.worldToLocalMatrix;
                foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mf.sharedMesh == null || mr == null) continue;
                    var mats = mr.sharedMaterials;
                    for (int s = 0; s < mats.Length && s < mf.sharedMesh.subMeshCount; s++)
                        if (mats[s] != null && mats[s].enableInstancing)
                            list.Add(new Piece { Mesh = mf.sharedMesh, Material = mats[s], SubMesh = s, FromRoot = rootInv * mf.transform.localToWorldMatrix, Shadows = ShadowCastingMode.Off });
                }
            }
            var arr = list.ToArray();
            s_pieces[prefab] = arr;
            return arr;
        }

        private static readonly List<Matrix4x4> s_slice = new List<Matrix4x4>(MaxPerDraw);

        private static void Draw(Camera cam)
        {
            foreach (var patch in s_patches.Values)
            {
                if (!patch.Built) continue;
                foreach (var kv in patch.Batches)
                {
                    var rp = new RenderParams(kv.Key.Item3) { camera = cam, shadowCastingMode = patch.Shadows[kv.Key], receiveShadows = true, layer = 0 };
                    var list = kv.Value;
                    for (int start = 0; start < list.Count; start += MaxPerDraw)
                    {
                        int n = Mathf.Min(MaxPerDraw, list.Count - start);
                        s_slice.Clear();
                        for (int i = 0; i < n; i++) s_slice.Add(list[start + i]);
                        VerdantsAscent.Terrain.FatPerfCensus.InstancedCalls++;
                        Graphics.RenderMeshInstanced(rp, kv.Key.Item1, kv.Key.Item2, s_slice);
                    }
                }
            }
        }
    }
}
