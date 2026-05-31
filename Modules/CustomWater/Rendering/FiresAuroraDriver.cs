using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Spawns and maintains the aurora curtain GameObject — a procedurally-
    /// generated cylinder around the player's camera that renders the
    /// <c>Custom/FiresAurora</c> shader's ribbon effect over the sky.
    /// </summary>
    public static class FiresAurora
    {
        public const string ShaderName = "Custom/FiresAurora";

        public static bool  Enabled       = false;
        public static float Intensity     = 1.0f;
        public static bool  NightOnly     = true;
        public static bool  DuringStorms  = false;

        // Cylinder geometry sizing — sized so the ribbon band sits in the
        // lower-to-middle sky where real aurora reads strongest. Bottom of
        // the cylinder dips BELOW the horizon (relative to camera) so
        // ribbons sweep from horizon up through the upper sky. Wider radius
        // means each ribbon's angular spread is gentler / less curved.
        private const float CylinderRadius     = 900f;
        private const float CylinderHeight     = 800f;
        private const float CylinderBaseY      = -150f;  // bottom 150m below camera (below horizon)
        private const int   CylinderSegments   = 64;     // smoother circle = thinner per-cell visible artifacts
        private const int   CylinderRows       = 12;     // taller vertical resolution for smoother band fade

        private const float FadeRate = 1.5f;  // intensity units per second

        public static Shader   AuroraShader { get; private set; }
        public static Material AuroraMaterial { get; private set; }

        private static GameObject s_curtainGO;
        private static MeshRenderer s_renderer;
        private static MeshFilter s_filter;
        private static float s_currentIntensity;

        private static readonly int Prop_Intensity = Shader.PropertyToID("_Intensity");

        public static bool TryLoadShader(AssetBundle bundle)
        {
            if (AuroraShader != null) return true;
            if (bundle == null) return false;

            AuroraShader = bundle.LoadAsset<Shader>(ShaderName);
            if (AuroraShader == null)
            {
                foreach (var s in bundle.LoadAllAssets<Shader>())
                {
                    if (s != null && s.name == ShaderName) { AuroraShader = s; break; }
                }
            }
            if (AuroraShader == null) return false;

            AuroraMaterial = new Material(AuroraShader) { name = "FiresAurora_runtime" };
            Debug.Log($"[FiresAurora] Loaded '{ShaderName}' from bundle.");
            return true;
        }

        public static void Tick(Transform anchor)
        {
            if (anchor == null) return;
            if (AuroraShader == null || AuroraMaterial == null) return;

            bool shouldShow = Enabled
                && Intensity > 0.001f
                && (!NightOnly || IsNight())
                && (DuringStorms || !IsStormy());

            EnsureCurtain(anchor);

            // Cross-fade so toggles + night/day transitions don't pop.
            float target = shouldShow ? Intensity : 0f;
            s_currentIntensity = Mathf.MoveTowards(
                s_currentIntensity, target, FadeRate * Time.unscaledDeltaTime);

            AuroraMaterial.SetFloat(Prop_Intensity, s_currentIntensity);

            if (s_curtainGO != null)
            {
                bool active = s_currentIntensity > 0.001f;
                if (s_curtainGO.activeSelf != active) s_curtainGO.SetActive(active);
                if (active)
                {
                    var p = anchor.position;
                    s_curtainGO.transform.position = new Vector3(p.x, p.y + CylinderBaseY, p.z);
                }
            }
        }

        private static void EnsureCurtain(Transform anchor)
        {
            if (s_curtainGO != null) return;

            s_curtainGO = new GameObject("FiresAurora_Curtain");
            UnityEngine.Object.DontDestroyOnLoad(s_curtainGO);

            s_filter = s_curtainGO.AddComponent<MeshFilter>();
            s_filter.sharedMesh = BuildCylinderMesh();

            s_renderer = s_curtainGO.AddComponent<MeshRenderer>();
            s_renderer.sharedMaterial = AuroraMaterial;
            s_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_renderer.receiveShadows = false;
            s_renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            s_renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            s_curtainGO.SetActive(false);
        }

        // Open cylinder (no caps), normals out, UVs go around horizontally
        // and 0..1 vertically — vertex shader maps UV.y to bandT for the
        // altitude-band mask. Single mesh, ~32 verts × 8 rows so it's tiny.
        private static Mesh BuildCylinderMesh()
        {
            int segments = CylinderSegments;
            int rows     = CylinderRows;
            int vertsPerRow = segments + 1;  // duplicate at seam for proper UV
            int vertCount = vertsPerRow * (rows + 1);
            int triCount  = segments * rows * 6;

            var verts = new Vector3[vertCount];
            var uvs   = new Vector2[vertCount];
            var tris  = new int[triCount];

            for (int row = 0; row <= rows; row++)
            {
                float vT = row / (float)rows;
                float y  = vT * CylinderHeight;
                for (int seg = 0; seg <= segments; seg++)
                {
                    float uT = seg / (float)segments;
                    float angle = uT * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * CylinderRadius;
                    float z = Mathf.Sin(angle) * CylinderRadius;
                    int idx = row * vertsPerRow + seg;
                    verts[idx] = new Vector3(x, y, z);
                    uvs[idx]   = new Vector2(uT, vT);
                }
            }

            int t = 0;
            for (int row = 0; row < rows; row++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    int a = row * vertsPerRow + seg;
                    int b = a + 1;
                    int c = a + vertsPerRow;
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "FiresAuroraCylinder" };
            mesh.vertices  = verts;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static bool IsNight()
        {
            try { return EnvMan.instance != null && EnvMan.IsNight(); }
            catch { return false; }
        }

        private static bool IsStormy()
        {
            try
            {
                var env = EnvMan.instance?.GetCurrentEnvironment();
                if (env == null) return false;
                string n = env.m_name ?? string.Empty;
                return n.Contains("Storm") || n.Contains("Rain") || n.Contains("Thunder");
            }
            catch { return false; }
        }
    }

    public class FiresAuroraDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return;

            // First-tick shader load — bundle is loaded by FiresWaterShaderLoader.
            if (FiresAurora.AuroraShader == null)
            {
                // Make sure the bundle is ready before we try to pull our shader.
                FiresWaterShaderLoader.EnsureLoaded();
                if (!FiresAurora.TryLoadShader(FiresWaterShaderLoader.Bundle))
                    return;  // bundle not ready yet, retry next frame
            }

            try { FiresAurora.Tick(cam.transform); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresAurora] Tick failed: {ex.Message}");
                enabled = false;
            }
        }
    }
}
