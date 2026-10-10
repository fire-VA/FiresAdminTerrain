using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.821 (Fire 10-07, looking UP the shaft from a big sinkhole's grotto: "see the river above as if from underwater",
    // while the grotto keeps its own environment). Client render only, no EnvMan, no sync (every client knows the shafts
    // from the sidecar, FatSinkFallCuts).
    //   * RIVER UNDERSIDE: Custom/FiresInlandWater draws Cull Off; its back face takes the underwater branch (refraction,
    //     _UwTint, _UwDepthFade) but a FLAT back face is clipped while the global _FiresFogCamSubmerged < 0.5 (the dry-eye
    //     "underside in mid-air" guard). While the camera is inside a big shaft's footprint BELOW the river bed, the river
    //     sheet renderers of the chunks over that shaft get a per-renderer MaterialPropertyBlock with _FiresFogCamSubmerged
    //     = 1 (a block overrides the global for that renderer only), so from below the river reads as seen from underwater.
    //   * WATER BAND: a FAT-owned flat disc at the river BED height across the shaft (r = shaft + DiscOverlap, rim buried in
    //     the wall), drawn with the same sheet material and the same block: between it and the river surface the shaft
    //     shows a band of river water over the grotto.
    // Both are switched off (block removed, disc hidden) the moment the camera leaves, so from above nothing changes.
    internal static class FatShaftUnderwater
    {
        private const float BedMargin = 0.3f, DiscOverlap = 1f, DiscDrop = 0.05f, ChamberReach = 16f;
        private const bool DrawBand = false;   // 0.2.832: the water-sheet band disc is retired (FTS fog band)
        private const int DiscSegments = 48;
        // 0.2.837 ([generator], Core 0.2.318): "underside seen from dry air" - back face drawn with the refraction and ripples, tinted
        // by the sheet's own column only (was _FiresFogCamSubmerged = 1, which tinted the 25 m of air to an opaque flat sheet: 109.webp).
        private static readonly int P_Submerged = Shader.PropertyToID("_FiresUndersideDry");

        private static int s_active = -1;   // the shaft index the camera is under, -1 = none
        private static readonly List<MeshRenderer> s_blocked = new List<MeshRenderer>();
        private static MaterialPropertyBlock s_mpb;
        private static readonly MaterialPropertyBlock s_own = new MaterialPropertyBlock();
        private static GameObject s_disc;
        private static bool s_logged;

        // From VoxelCaveEnv's poll.
        internal static void Tick()
        {
            var camera = global::Utils.GetMainCamera();
            int want = -1;
            float bed = float.NaN;
            if (camera != null && FiresWorldGenBase.Active)
            {
                var p = camera.transform.position;
                // 0.2.824 (Fire 97.webp at (-459,-3503), 18 m off a r 14 shaft's centre: no band): engaged anywhere within
                // ChamberReach of the shaft's centre below its bed (the grotto under it), not only in the shaft's footprint.
                for (int i = 0; FatSinkFallCuts.TryShaft(i, out Vector2 c, out float r, out float b); i++)
                {
                    if (float.IsNaN(b) || p.y >= b - BedMargin) continue;
                    if (new Vector2(p.x - c.x, p.z - c.y).magnitude > r + ChamberReach) continue;
                    want = i;
                    break;
                }
            }
            if (want == s_active) return;
            Release();
            if (want < 0) return;
            Engage(want);
        }

        private static void Engage(int shaft)
        {
            if (!FatSinkFallCuts.TryShaft(shaft, out Vector2 centre, out float radius, out float bed)) return;
            var material = VoxelChunkView.BoundSheetMaterial;
            if (material == null) return;
            // The river's surface over the shaft (upstream of the lip, where the generated river still stands).
            float surface = bed + 1f;
            if (GenWaterField.TryWaterSurfaceSmooth(centre.x, centre.y, out float level, out _) && level > bed) surface = level;
            s_active = shaft;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            s_mpb.Clear();
            s_mpb.SetFloat(P_Submerged, 1f);
            // River sheets: every loaded chunk view whose x/z overlaps the shaft, in the chunk band holding the surface.
            int ky = Mathf.FloorToInt(surface / VoxelChunk.CELLS);
            int x0 = Mathf.FloorToInt((centre.x - radius) / VoxelChunk.CELLS), x1 = Mathf.FloorToInt((centre.x + radius) / VoxelChunk.CELLS);
            int z0 = Mathf.FloorToInt((centre.y - radius) / VoxelChunk.CELLS), z1 = Mathf.FloorToInt((centre.y + radius) / VoxelChunk.CELLS);
            for (int kx = x0; kx <= x1; kx++)
                for (int kz = z0; kz <= z1; kz++)
                    for (int dy = -1; dy <= 0; dy++)
                    {
                        if (!VoxelWorld.TryGetView(new Vector3Int(kx, ky + dy, kz), out var view) || view == null) continue;
                        var r = view.WaterRenderer;
                        if (r == null) continue;
                        r.GetPropertyBlock(s_own); s_own.SetFloat(P_Submerged, 1f); r.SetPropertyBlock(s_own);   // only our property, over any other
                        s_blocked.Add(r);
                    }
            // 0.2.832: retired - FiresTossinShade 0.3.155 draws the band at river height as fog (FatCaveSignals.TryGetShaft).
            if (DrawBand)
            {
            // The band's floor: a disc at the bed.
            if (s_disc == null) s_disc = BuildDisc(material);
            s_disc.transform.position = new Vector3(centre.x, bed - DiscDrop, centre.y);
            s_disc.transform.localScale = new Vector3(radius + DiscOverlap, 1f, radius + DiscOverlap);
            var discRenderer = s_disc.GetComponent<MeshRenderer>();
            discRenderer.sharedMaterial = material;
            discRenderer.SetPropertyBlock(s_mpb);
            // The disc's depth (UV2.x, the shader's water column) is the river's depth over the bed.
            var mesh = s_disc.GetComponent<MeshFilter>().sharedMesh;
            var uv2 = new List<Vector4>(mesh.vertexCount);
            for (int i = 0; i < mesh.vertexCount; i++) uv2.Add(new Vector4(surface - bed, 0f, 0f, 0f));
            mesh.SetUVs(2, uv2);
            s_disc.SetActive(true);
            }
            if (!s_logged)
            {
                s_logged = true;
                FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[ShaftUnderwater] under shaft #{0} at ({1:0},{2:0}): river surface {3:0.0}, bed {4:0.0}, {5} river sheet(s) seen from below, band disc r {6:0.0}",
                    shaft, centre.x, centre.y, surface, bed, s_blocked.Count, radius + DiscOverlap));
            }
        }

        private static void Release()
        {
            foreach (var r in s_blocked) if (r != null) { r.GetPropertyBlock(s_own); s_own.SetFloat(P_Submerged, 0f); r.SetPropertyBlock(s_own); }   // 0.2.837: clear only ours
            s_blocked.Clear();
            if (s_disc != null) s_disc.SetActive(false);
            s_active = -1;
        }

        // A unit disc (radius 1, scaled per shaft) in the x/z plane, facing up, centred on the origin.
        private static GameObject BuildDisc(Material material)
        {
            var go = new GameObject("FatShaftUnderwaterBand");
            Object.DontDestroyOnLoad(go);
            var verts = new List<Vector3> { Vector3.zero };
            var normals = new List<Vector3> { Vector3.up };
            var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            var tris = new List<int>();
            for (int i = 0; i < DiscSegments; i++)
            {
                float a = i * Mathf.PI * 2f / DiscSegments;
                verts.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                normals.Add(Vector3.up);
                uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
                int next = (i + 1) % DiscSegments;
                tris.Add(0); tris.Add(next + 1); tris.Add(i + 1);
            }
            var mesh = new Mesh { name = "fat_shaft_band" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sharedMaterial = material;
            go.SetActive(false);
            return go;
        }
    }
}
