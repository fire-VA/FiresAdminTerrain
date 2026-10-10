using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    internal enum AttachSide { Bottom, Top, Back, Front, Left, Right }

    // How a prefab meets rock, in its own frame with its root scale applied: the face that goes against the surface,
    // how far the prefab stands out of it, and how far it is meant to reach into the rock behind it.
    internal struct SurfaceAnchor
    {
        public AttachSide Side;
        public Vector3 Attach;
        public Vector3 FaceCenter;
        public Vector3 SpanA, SpanB;
        public float Reach;
        public float Buried;
        public bool Scalable;
        public Bounds Mesh;

        public bool Valid => Reach > 0f;
        public float Radius => Mathf.Max(SpanA.magnitude, SpanB.magnitude);
        public bool FaceHoldsUp => Side != AttachSide.Bottom && Side != AttachSide.Top;
    }

    // Puts a prefab's attach face flat on the voxel surface whichever way the surface faces: cave floor, wall, ceiling,
    // or a skyland's cliff and underside. The face goes against the rock along the density gradient and sinks until
    // every corner touches, and the rest of the prefab must stand in air. A prefab attaches by its bottom unless
    // 'Attach sides' lists it; the defaults come from how vanilla rooms place each prop. A bottom face is the pivot
    // plane when the pivot lies inside the mesh, because vanilla grounds its props at the pivot.
    internal static class VoxelSurfaceSnap
    {
        // The crypt roots (root07/08/11/12) hang by their TOP (Fire 10-05: "placed upside down so we see the top of the root
        // poking out instead of the root itself"). The 09-27 survey found them ambiguous in vanilla (50-75 %) and the lineup
        // render read as 'lying flat', so they had the bottom default: on a ceiling that buried their base and pointed the tip
        // end into the cave.
        // 0.2.720: YggdrasilRoot (the Mistlands sky root, as a mini cave root) hung by its top too. 0.2.765 (Fire 10-06: "the
        // ydrissil roots should be turned 180 when on the cealing ... with the ends buried a bit in the mesh"): it is modelled as
        // an arch whose two ends meet the ground, so it attaches by its BOTTOM like on a floor: on a ceiling the ends go into the
        // rock and the arch hangs down. Greydwarf_Root stays on the bottom default: its base goes into the rock and it grows down.
        internal const string DefaultAttachSides = "caverock_ice_stalagtite:top,CreepProp_hanging01:top,hanging_hairstrands:top,Pickable_FrostCoreHanger:top,CreepProp_wall01:front,root07:top,root08:top,root11:top,root12:top";
        private static readonly string[] OldDefaultAttachSides =
        {
            "caverock_ice_stalagtite:top,CreepProp_hanging01:top,hanging_hairstrands:top,Pickable_FrostCoreHanger:top,CreepProp_wall01:front",
            "caverock_ice_stalagtite:top,CreepProp_hanging01:top,hanging_hairstrands:top,Pickable_FrostCoreHanger:top,CreepProp_wall01:front,root07:top,root08:top,root11:top,root12:top,YggdrasilRoot:top",
        };

        private const string Section = "VoxelTerrain Surface Mounting";
        private const float RayStep = 0.25f;
        private const float NormalStep = 0.6f;
        private const float MinGradient = 1e-3f;
        private const float SolidDensity = 127f;
        private const float Embed = 0.08f;
        private const float EmbedShare = 0.2f;
        private const float MinSink = 0.35f;
        private const float SinkShare = 0.6f;
        private const float MaxSink = 1.2f;
        private const float MeshlessReach = 0.3f;
        private const float MeshlessSpan = 0.15f;
        private const float UprightJitter = 25f;
        private const float MinFaceUp = 0.2f;
        private static readonly float[] BodyProbes = { 0.4f, 0.7f, 1f };

        private static ConfigEntry<string> s_attachSides;
        private static string s_parsedFrom;
        private static readonly Dictionary<string, AttachSide> s_sides = new Dictionary<string, AttachSide>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<GameObject, SurfaceAnchor> s_anchors = new Dictionary<GameObject, SurfaceAnchor>();
        private static readonly HashSet<string> s_warned = new HashSet<string>();

        internal static void Bind(ConfigFile cfg)
        {
            s_attachSides = cfg.Bind(Section, "Attach sides", DefaultAttachSides,
                "Which face of a prefab goes against the rock when FAT mounts it on a voxel surface. prefab:side pairs; side is "
                + "bottom, top, back (-Z), front (+Z), left (-X) or right (+X) of the prefab as modelled. Unlisted prefabs attach "
                + "by their bottom, so on a ceiling they hang upside down and on a wall they stick straight out. List a prefab "
                + "that is modelled already hanging (top) or modelled lying against a wall (its wall side).");
            // A cfg still holding the old default (untouched by its owner) takes the new one, so the roots' fix reaches it.
            if (Array.IndexOf(OldDefaultAttachSides, s_attachSides.Value) >= 0)
            {
                s_attachSides.Value = DefaultAttachSides;
                FiresLogger.LogInfo($"[FiresVoxel] '{Section} / Attach sides' was an old default: now hangs the crypt roots and YggdrasilRoot by their top.");
            }
        }

        internal static SurfaceAnchor Anchor(GameObject prefab)
        {
            if (prefab == null) return default;
            RefreshSides();
            if (s_anchors.TryGetValue(prefab, out var cached)) return cached;
            var anchor = Measure(prefab, s_sides.TryGetValue(prefab.name, out var side) ? side : AttachSide.Bottom);
            s_anchors[prefab] = anchor;
            return anchor;
        }

        // The anchor a prefab would have with a given attach side (not cached): how an older mount placed it.
        internal static SurfaceAnchor AnchorAs(GameObject prefab, AttachSide side) => prefab == null ? default : Measure(prefab, side);

        // Pose for the prefab with its attach face on the rock at `surface`, `normal` pointing out of the rock. Fails
        // where a corner of the face would hang off an edge or the prefab would run into rock.
        internal static bool TryMount(GameObject prefab, Vector3 surface, Vector3 normal, float scale, System.Random rng,
            out Vector3 position, out Quaternion rotation)
        {
            position = surface;
            rotation = Quaternion.identity;
            var anchor = Anchor(prefab);
            if (!anchor.Valid) return false;
            if (!anchor.Scalable) scale = 1f;
            rotation = Orient(anchor, normal, rng);
            float buried = anchor.Buried * scale;
            float allowed = Mathf.Clamp(anchor.Reach * scale * SinkShare, MinSink, MaxSink) + buried;
            float gap = 0f;
            for (int corner = 0; corner < 4; corner++)
            {
                var span = ((corner & 1) == 0 ? anchor.SpanA : -anchor.SpanA) + ((corner & 2) == 0 ? anchor.SpanB : -anchor.SpanB);
                var at = surface + rotation * (span * scale);
                if (DensityAt(at) > 0f) continue;
                if (!TryRaySurface(at, -normal, allowed, out _, out float cornerGap)) return false;
                gap = Mathf.Max(gap, cornerGap);
            }
            float reach = anchor.Reach * scale;
            var face = surface - normal * (Mathf.Max(0f, gap - buried) + SeatDepth(anchor, scale));
            for (int i = 0; i < BodyProbes.Length; i++)
                if (DensityAt(face + normal * (reach * BodyProbes[i])) > 0f) return false;
            position = face - rotation * (anchor.FaceCenter * scale);
            return true;
        }

        private static Quaternion Orient(SurfaceAnchor anchor, Vector3 normal, System.Random rng)
            => Orient(anchor, normal, (float)rng.NextDouble(), (float)rng.NextDouble());

        // The attach face turned onto the rock. A face that carries the prefab's up (a wall-modelled prop) keeps that up
        // pointing skyward along the surface, within a small jitter; otherwise the prefab spins freely about its attach
        // axis. spin and jitter are in [0,1).
        internal static Quaternion Orient(SurfaceAnchor anchor, Vector3 normal, float spin, float jitter)
        {
            var onto = Quaternion.FromToRotation(anchor.Attach, -normal);
            if (anchor.FaceHoldsUp)
            {
                var skyward = Vector3.ProjectOnPlane(Vector3.up, normal);
                if (skyward.sqrMagnitude > MinFaceUp * MinFaceUp)
                {
                    float angle = Vector3.SignedAngle(onto * Vector3.up, skyward, normal) + (jitter * 2f - 1f) * UprightJitter;
                    return Quaternion.AngleAxis(angle, normal) * onto;
                }
            }
            return onto * Quaternion.AngleAxis(spin * 360f, anchor.Attach);
        }

        // How far the attach face goes into the rock where it sits flush: none for a prefab vanilla sinks below its
        // pivot, otherwise a sliver so the seam never shows.
        internal static float SeatDepth(SurfaceAnchor anchor, float scale)
            => anchor.Buried > 0f ? 0f : Mathf.Min(Embed, anchor.Reach * scale * EmbedShare);

        // The first rock along a ray from air, at the zero crossing of the trilinear density the terrain mesh is built from.
        internal static bool TryRaySurface(Vector3 from, Vector3 direction, float reach, out Vector3 surface, out float distance)
        {
            surface = from;
            distance = 0f;
            float previous = DensityAt(from);
            if (previous > 0f) return false;
            for (float d = RayStep; d <= reach; d += RayStep)
            {
                float here = DensityAt(from + direction * d);
                if (here <= 0f)
                {
                    previous = here;
                    continue;
                }
                distance = d - RayStep + RayStep * previous / (previous - here);
                surface = from + direction * distance;
                return true;
            }
            return false;
        }

        // Out of the rock: the negative density gradient.
        internal static Vector3 SurfaceNormal(Vector3 at, Vector3 fallback)
        {
            var gradient = new Vector3(
                DensityAt(at + Vector3.right * NormalStep) - DensityAt(at - Vector3.right * NormalStep),
                DensityAt(at + Vector3.up * NormalStep) - DensityAt(at - Vector3.up * NormalStep),
                DensityAt(at + Vector3.forward * NormalStep) - DensityAt(at - Vector3.forward * NormalStep));
            return gradient.sqrMagnitude > MinGradient ? -gradient.normalized : fallback;
        }

        internal static float DensityAt(Vector3 p)
        {
            int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y), z0 = Mathf.FloorToInt(p.z);
            float fx = p.x - x0, fy = p.y - y0, fz = p.z - z0;
            float x00 = Mathf.Lerp(DensitySample(x0, y0, z0), DensitySample(x0 + 1, y0, z0), fx);
            float x10 = Mathf.Lerp(DensitySample(x0, y0 + 1, z0), DensitySample(x0 + 1, y0 + 1, z0), fx);
            float x01 = Mathf.Lerp(DensitySample(x0, y0, z0 + 1), DensitySample(x0 + 1, y0, z0 + 1), fx);
            float x11 = Mathf.Lerp(DensitySample(x0, y0 + 1, z0 + 1), DensitySample(x0 + 1, y0 + 1, z0 + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
        }

        private static float DensitySample(int x, int y, int z)
        {
            var key = new Vector3Int(FloorDiv(x, VoxelChunk.CELLS), FloorDiv(y, VoxelChunk.CELLS), FloorDiv(z, VoxelChunk.CELLS));
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return SolidDensity;
            sbyte d = chunk.SampleDensityWorld(new Vector3(x, y, z));
            return d == sbyte.MinValue ? SolidDensity : d;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

        private static SurfaceAnchor Measure(GameObject prefab, AttachSide side)
        {
            var view = prefab.GetComponent<ZNetView>();
            var anchor = new SurfaceAnchor { Side = side, Scalable = view != null && view.m_syncInitialScale };
            var toRoot = Matrix4x4.Scale(prefab.transform.localScale) * prefab.transform.worldToLocalMatrix;
            bool any = false;
            var bounds = new Bounds();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && filter.GetComponent<MeshRenderer>() != null)
                    Grow(ref bounds, ref any, filter.sharedMesh.bounds, toRoot * filter.transform.localToWorldMatrix);
            foreach (var skinned in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skinned.sharedMesh != null)
                    Grow(ref bounds, ref any, skinned.sharedMesh.bounds, toRoot * skinned.transform.localToWorldMatrix);
            if (!any)
                bounds = new Bounds(new Vector3(0f, MeshlessReach * 0.5f, 0f), new Vector3(MeshlessSpan * 2f, MeshlessReach, MeshlessSpan * 2f));
            anchor.Mesh = bounds;

            Vector3 min = bounds.min, max = bounds.max, c = bounds.center, e = bounds.extents;
            var acrossXZ = (a: new Vector3(e.x, 0f, 0f), b: new Vector3(0f, 0f, e.z));
            switch (side)
            {
                case AttachSide.Bottom:
                    float plane = Mathf.Clamp(0f, min.y, max.y);
                    anchor.Attach = Vector3.down;
                    anchor.FaceCenter = new Vector3(c.x, plane, c.z);
                    (anchor.SpanA, anchor.SpanB) = acrossXZ;
                    anchor.Reach = max.y - plane;
                    anchor.Buried = plane - min.y;
                    break;
                case AttachSide.Top:
                    anchor.Attach = Vector3.up;
                    anchor.FaceCenter = new Vector3(c.x, max.y, c.z);
                    (anchor.SpanA, anchor.SpanB) = acrossXZ;
                    anchor.Reach = max.y - min.y;
                    break;
                case AttachSide.Back:
                case AttachSide.Front:
                    bool back = side == AttachSide.Back;
                    anchor.Attach = back ? Vector3.back : Vector3.forward;
                    anchor.FaceCenter = new Vector3(c.x, c.y, back ? min.z : max.z);
                    anchor.SpanA = new Vector3(e.x, 0f, 0f);
                    anchor.SpanB = new Vector3(0f, e.y, 0f);
                    anchor.Reach = max.z - min.z;
                    break;
                default:
                    bool left = side == AttachSide.Left;
                    anchor.Attach = left ? Vector3.left : Vector3.right;
                    anchor.FaceCenter = new Vector3(left ? min.x : max.x, c.y, c.z);
                    anchor.SpanA = new Vector3(0f, e.y, 0f);
                    anchor.SpanB = new Vector3(0f, 0f, e.z);
                    anchor.Reach = max.x - min.x;
                    break;
            }
            return anchor;
        }

        private static void Grow(ref Bounds bounds, ref bool any, Bounds local, Matrix4x4 toRoot)
        {
            var ext = local.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = toRoot.MultiplyPoint3x4(local.center + new Vector3((corner & 1) == 0 ? -ext.x : ext.x,
                    (corner & 2) == 0 ? -ext.y : ext.y, (corner & 4) == 0 ? -ext.z : ext.z));
                if (!any)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else bounds.Encapsulate(p);
            }
        }

        // A cfg edit applies to the next prefab measured; every cached anchor is dropped with it.
        private static void RefreshSides()
        {
            string csv = s_attachSides != null ? s_attachSides.Value ?? "" : DefaultAttachSides;
            if (csv == s_parsedFrom) return;
            s_parsedFrom = csv;
            s_sides.Clear();
            s_anchors.Clear();
            foreach (var raw in csv.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0) continue;
                int colon = item.LastIndexOf(':');
                if (colon > 0 && TryParseSide(item.Substring(colon + 1).Trim(), out var side))
                    s_sides[item.Substring(0, colon).Trim()] = side;
                else if (s_warned.Add(item))
                    FiresLogger.LogWarning($"[FiresVoxel] '{Section} / Attach sides': '{item}' is not prefab:side "
                        + "(bottom, top, back, front, left, right) - skipped.");
            }
        }

        private static bool TryParseSide(string text, out AttachSide side)
        {
            switch (text.ToLowerInvariant())
            {
                case "bottom": case "-y": side = AttachSide.Bottom; return true;
                case "top": case "+y": side = AttachSide.Top; return true;
                case "back": case "-z": side = AttachSide.Back; return true;
                case "front": case "+z": side = AttachSide.Front; return true;
                case "left": case "-x": side = AttachSide.Left; return true;
                case "right": case "+x": side = AttachSide.Right; return true;
                default: side = AttachSide.Bottom; return false;
            }
        }
    }
}
