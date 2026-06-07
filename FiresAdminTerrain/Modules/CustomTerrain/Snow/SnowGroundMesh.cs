using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Snow
{
    /// <summary>
    /// Generates meshes for the snow clutter system:
    /// 
    /// 1. GROUND DISC — A flat hexagonal disc used for the ground-cover layer.
    ///    Hundreds of these overlap at ground level to form a solid white blanket.
    /// 
    /// 2. VOLUMETRIC SNOW — A dense cloud of tiny triangular particles packed
    ///    into a mound shape. Each particle is a single triangle (3 verts, 1 tri)
    ///    — the cheapest possible polygon. Hundreds of these are scattered at
    ///    pseudo-random positions throughout the mound volume, tilted at random
    ///    angles. The particles are so small that individually they're barely
    ///    visible, but packed together they form an opaque, granular snow volume
    ///    that looks like actual accumulated powder rather than geometric shapes.
    ///    When thousands of these mound instances overlap, the result is a thick
    ///    blanket of particulate snow with natural visual depth.
    /// </summary>
    public static class SnowGroundMesh
    {
        private static Mesh _discMesh;
        private static Mesh _augmentedMesh;

        // --- Particle snow configuration ---

        /// <summary>Number of vertical layers to distribute particles across.</summary>
        private const int LAYER_COUNT = 16;

        /// <summary>Particles per layer. Total particles = LAYER_COUNT × PARTICLES_PER_LAYER.</summary>
        private const int PARTICLES_PER_LAYER = 128;

        /// <summary>
        /// Number of angular sectors per layer for stratified placement.
        /// Particles are distributed into sectors first, then jittered within
        /// each sector. This prevents golden-angle spiral artefacts.
        /// </summary>
        private const int SECTORS_PER_LAYER = 16;

        /// <summary>
        /// Number of radial rings per layer for stratified placement.
        /// Combined with SECTORS_PER_LAYER this creates a grid of cells
        /// (SECTORS × RINGS = slots per layer) that particles fill with jitter.
        /// PARTICLES_PER_LAYER / (SECTORS × RINGS) = particles per cell.
        /// </summary>
        private const int RINGS_PER_LAYER = 8;

        /// <summary>
        /// Particle triangle radius — how big each individual snow grain triangle is.
        /// This is a fraction of the mound's base radius. Half-size particles
        /// at double count produce finer grain with the same coverage.
        /// </summary>
        private const float PARTICLE_SIZE_FRACTION = 0.03f;

        /// <summary>
        /// Minimum pitch tilt in radians for particles. Ensures no two particles
        /// in the same area are perfectly coplanar, which prevents z-fighting.
        /// 0.15 rad ? 8.6° — enough depth separation at particle scale.
        /// </summary>
        private const float MIN_PITCH_TILT = 0.15f;

        /// <summary>Number of extra base-filler particles at ground level.</summary>
        private const int BASE_FILLER_COUNT = 160;

        /// <summary>
        /// Gets (or creates) the flat disc mesh for snow ground cover.
        /// Cached after first creation.
        /// </summary>
        public static Mesh GetOrCreateDiscMesh()
        {
            if (_discMesh != null) return _discMesh;

            _discMesh = CreateFlatDiscMesh(8, 0.5f);
            return _discMesh;
        }

        /// <summary>
        /// Takes a vanilla grass billboard mesh and returns a volumetric snow
        /// particle mesh sized to the source mesh's bounding box. The source
        /// mesh's geometry is entirely discarded.
        /// 
        /// Cached after first creation.
        /// </summary>
        public static Mesh GetOrCreateAugmentedMesh(Mesh sourceMesh)
        {
            if (_augmentedMesh != null) return _augmentedMesh;

            if (sourceMesh == null)
            {
                Debug.LogWarning("[SnowGroundMesh] Cannot augment null source mesh");
                return sourceMesh;
            }

            if (!sourceMesh.isReadable)
            {
                Debug.LogWarning($"[SnowGroundMesh] Source mesh '{sourceMesh.name}' is not readable — cannot augment");
                return sourceMesh;
            }

            _augmentedMesh = BuildParticleSnowVolume(sourceMesh);
            return _augmentedMesh;
        }

        /// <summary>
        /// Creates a flat disc mesh with the given number of segments and radius.
        /// The disc lies in the XZ plane (Y=0) so it sits flat on the ground.
        /// </summary>
        private static Mesh CreateFlatDiscMesh(int segments, float radius)
        {
            int vertCount = segments + 1;
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            vertices[0] = Vector3.zero;
            normals[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;

                vertices[i + 1] = new Vector3(x, 0f, z);
                normals[i + 1] = Vector3.up;
                uvs[i + 1] = new Vector2(
                    0.5f + Mathf.Cos(angle) * 0.5f,
                    0.5f + Mathf.Sin(angle) * 0.5f
                );
            }

            int triCount = segments * 3;
            var triangles = new int[triCount];
            for (int i = 0; i < segments; i++)
            {
                triangles[i * 3 + 0] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }

            var mesh = new Mesh();
            mesh.name = "SnowGroundDisc";
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;

            var discColors = new Color[vertCount];
            for (int i = 0; i < vertCount; i++)
                discColors[i] = Color.white;
            mesh.colors = discColors;

            mesh.RecalculateBounds();

            Debug.Log($"[SnowGroundMesh] Created disc mesh: {vertCount} verts, {segments} tris, radius={radius}m");
            return mesh;
        }

        /// <summary>
        /// Builds a volumetric snow mesh from a dense cloud of tiny triangle
        /// particles distributed throughout a mound-shaped volume.
        ///
        /// Each particle is a single equilateral triangle — 3 vertices, 1 face.
        /// This is the absolute minimum geometry per snow grain. With ~2200
        /// half-size particles per mesh, each one is invisible on its own,
        /// but the aggregate creates an opaque, fine-grained snow volume.
        ///
        /// Distribution uses STRATIFIED JITTERED placement: each layer is divided
        /// into a grid of angular sectors × radial rings. Particles are assigned
        /// to cells and then jittered within each cell. This guarantees uniform
        /// coverage without the visible spiral artefacts that golden-angle
        /// (Fibonacci) distributions produce at moderate densities.
        ///
        /// A separate cluster of base-filler particles at ground level ensures
        /// no gaps at the base where the mound meets the terrain.
        /// </summary>
        private static Mesh BuildParticleSnowVolume(Mesh source)
        {
            var bounds = source.bounds;
            float minY = bounds.min.y;
            float maxY = bounds.max.y;
            float height = maxY - minY;
            float baseRadius = Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.1f;
            float cx = bounds.center.x;
            float cz = bounds.center.z;

            float particleSize = baseRadius * PARTICLE_SIZE_FRACTION;

            int totalParticles = LAYER_COUNT * PARTICLES_PER_LAYER + BASE_FILLER_COUNT;
            var verts = new List<Vector3>(totalParticles * 3);
            var norms = new List<Vector3>(totalParticles * 3);
            var tangents = new List<Vector4>(totalParticles * 3);
            var uvs = new List<Vector2>(totalParticles * 3);
            var colors = new List<Color>(totalParticles * 3);
            var tris = new List<int>(totalParticles * 3);

            Vector4 defaultTangent = new Vector4(1f, 0f, 0f, -1f);
            int particleCount = 0;

            // Number of stratified cells per layer
            int cellsPerLayer = SECTORS_PER_LAYER * RINGS_PER_LAYER; // 48
            float sectorArc = Mathf.PI * 2f / SECTORS_PER_LAYER;

            // --- MAIN VOLUME: stratified jittered particle distribution ---
            for (int layer = 0; layer < LAYER_COUNT; layer++)
            {
                float t = (float)layer / (LAYER_COUNT - 1); // 0..1

                // Mound profile: wide base, tapered top
                float layerRadius = baseRadius * (1.0f - 0.65f * t * t);

                // Y range for this layer
                float layerYBase = minY + t * height;
                float ySpread = height / LAYER_COUNT * 0.45f;

                // Particle size varies: bigger at base, smaller at top
                float pSize = particleSize * Mathf.Lerp(1.2f, 0.7f, t);

                // Each layer rotates its sector grid by a different offset so
                // adjacent layers don't have aligned cell boundaries.
                float layerAngleOffset = layer * 0.7853981f; // layer * ?/4 — irrational-ish spacing

                // Distribute particles into stratified cells, then jitter
                for (int p = 0; p < PARTICLES_PER_LAYER; p++)
                {
                    // Deterministic hash seed — use large primes to decorrelate
                    int globalIdx = layer * PARTICLES_PER_LAYER + p;
                    float h1 = Hash01(globalIdx, 0);
                    float h2 = Hash01(globalIdx, 1);
                    float h3 = Hash01(globalIdx, 2);
                    float h4 = Hash01(globalIdx, 3);
                    float h5 = Hash01(globalIdx, 4);

                    // Assign to a cell in the sector×ring grid
                    int cellIdx = p % cellsPerLayer;
                    int sector = cellIdx % SECTORS_PER_LAYER;
                    int ring = cellIdx / SECTORS_PER_LAYER;

                    // Angle: base angle for this sector + jitter within sector arc
                    float angle = layerAngleOffset + sector * sectorArc + h1 * sectorArc;

                    // Radius: stratified into rings with jitter.
                    // Use square-root mapping for uniform area distribution.
                    float ringMin = (float)ring / RINGS_PER_LAYER;
                    float ringMax = (float)(ring + 1) / RINGS_PER_LAYER;
                    float distFrac = Mathf.Lerp(ringMin, ringMax, h2);
                    float dist = Mathf.Sqrt(distFrac) * layerRadius;

                    float px = cx + Mathf.Cos(angle) * dist;
                    float pz = cz + Mathf.Sin(angle) * dist;
                    float py = layerYBase + (h3 - 0.5f) * ySpread;

                    // Random tilt — MIN_PITCH_TILT prevents coplanar z-fighting
                    float tiltAngle = h4 * Mathf.PI * 2f;
                    float tiltPitch = MIN_PITCH_TILT + h5 * (0.5f - MIN_PITCH_TILT);

                    AddSnowParticle(verts, norms, tangents, uvs, colors, tris,
                        px, py, pz, pSize, tiltAngle, tiltPitch, defaultTangent);
                    particleCount++;
                }
            }

            // --- BASE FILLER: particles at ground level to seal gaps against terrain ---
            for (int i = 0; i < BASE_FILLER_COUNT; i++)
            {
                int globalIdx = LAYER_COUNT * PARTICLES_PER_LAYER + i;
                float h1 = Hash01(globalIdx, 0);
                float h2 = Hash01(globalIdx, 1);
                float h3 = Hash01(globalIdx, 2);
                float h4 = Hash01(globalIdx, 3);
                float h5 = Hash01(globalIdx, 4);

                // Stratified sectors for base fillers too
                int sector = i % SECTORS_PER_LAYER;
                float angle = sector * sectorArc + h1 * sectorArc;
                float dist = Mathf.Sqrt(h2) * baseRadius * 0.95f;
                float px = cx + Mathf.Cos(angle) * dist;
                float pz = cz + Mathf.Sin(angle) * dist;
                float py = minY + h3 * height * 0.08f; // very close to ground

                // Slight tilt even on base fillers
                float basePitch = MIN_PITCH_TILT * 0.5f + h4 * MIN_PITCH_TILT;
                AddSnowParticle(verts, norms, tangents, uvs, colors, tris,
                    px, py, pz, particleSize * 1.5f, h5 * Mathf.PI * 2f,
                    basePitch, defaultTangent);
                particleCount++;
            }

            var mesh = new Mesh();
            mesh.name = "SnowParticles_" + source.name;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            Debug.Log($"[SnowGroundMesh] Built particle snow volume from '{source.name}': " +
                      $"{verts.Count} verts, {tris.Count / 3} tris, " +
                      $"{particleCount} particles across {LAYER_COUNT} layers, " +
                      $"particle size ~{particleSize:F4}m");

            return mesh;
        }

        /// <summary>
        /// Adds a single snow particle — one tiny equilateral triangle, single-sided.
        /// 
        /// The triangle is positioned at (px, py, pz), with radius 'size' controlling
        /// how large the equilateral triangle is. The triangle is tilted by 'yaw' and
        /// 'pitch' to give natural variation — mostly horizontal but slightly angled
        /// so the volume doesn't look like perfectly stacked wafers.
        /// 
        /// Single-sided only (front face): the back face was the primary source of
        /// z-fighting because it occupies the exact same depth as the front face.
        /// Since snow is viewed from above and the material has backface culling
        /// disabled, a single winding order is sufficient. The pitch tilt ensures
        /// the front face is always partially visible from typical camera angles.
        /// </summary>
        private static void AddSnowParticle(
            List<Vector3> verts, List<Vector3> norms, List<Vector4> tangents,
            List<Vector2> uvs, List<Color> colors, List<int> tris,
            float px, float py, float pz, float size,
            float yaw, float pitch, Vector4 tangent)
        {
            int vi = verts.Count;

            // Build an equilateral triangle in the XZ plane, then tilt it
            // 3 vertices at 120° apart, radius = size
            float cos0 = Mathf.Cos(yaw);
            float sin0 = Mathf.Sin(yaw);
            float cos1 = Mathf.Cos(yaw + 2.094395f); // +120°
            float sin1 = Mathf.Sin(yaw + 2.094395f);
            float cos2 = Mathf.Cos(yaw + 4.188790f); // +240°
            float sin2 = Mathf.Sin(yaw + 4.188790f);

            // Apply pitch tilt: rotate the Y component by pitch around a horizontal axis
            // For small pitch, this lifts the far edge slightly
            float cp = Mathf.Cos(pitch);
            float sp = Mathf.Sin(pitch);

            Vector3 v0 = TiltAndOffset(cos0 * size, sin0 * size, cp, sp, px, py, pz);
            Vector3 v1 = TiltAndOffset(cos1 * size, sin1 * size, cp, sp, px, py, pz);
            Vector3 v2 = TiltAndOffset(cos2 * size, sin2 * size, cp, sp, px, py, pz);

            // Normal: approximate as up (particles are nearly horizontal)
            Vector3 normal = Vector3.up;

            // Single front face — no back face to avoid z-fighting
            verts.Add(v0); verts.Add(v1); verts.Add(v2);
            norms.Add(normal); norms.Add(normal); norms.Add(normal);
            tangents.Add(tangent); tangents.Add(tangent); tangents.Add(tangent);
            uvs.Add(new Vector2(0.5f, 1f));
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            colors.Add(Color.white); colors.Add(Color.white); colors.Add(Color.white);
            tris.Add(vi); tris.Add(vi + 1); tris.Add(vi + 2);
        }

        /// <summary>
        /// Takes an XZ-plane offset (local triangle vertex), applies a pitch tilt,
        /// and offsets to world position. The pitch rotates around the local X axis
        /// so one edge of the particle lifts slightly.
        /// </summary>
        private static Vector3 TiltAndOffset(float lx, float lz, float cp, float sp, float px, float py, float pz)
        {
            // Tilt: rotate (lz, 0) around X axis by pitch
            // localY = -lz * sin(pitch), localZ = lz * cos(pitch)
            return new Vector3(
                px + lx,
                py + (-lz * sp),
                pz + (lz * cp)
            );
        }

        /// <summary>
        /// Deterministic hash function that maps (index, channel) to a float in [0,1).
        /// Uses integer bit-mixing (similar to Wang hash) to produce well-distributed
        /// values with no visible spatial pattern. Each 'channel' produces an
        /// independent sequence, so position, radius, and tilt are fully decorrelated.
        /// </summary>
        private static float Hash01(int index, int channel)
        {
            uint h = (uint)(index * 1103515245 + channel * 2654435761 + 12345);
            h ^= h >> 16;
            h *= 0x85ebca6bu;
            h ^= h >> 13;
            h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return (h & 0x7FFFFFu) / (float)0x800000u; // 23-bit mantissa ? [0,1)
        }

        /// <summary>
        /// Fractional part of a float (always positive).
        /// </summary>
        private static float Frac(float v)
        {
            return v - Mathf.Floor(v);
        }
    }
}
