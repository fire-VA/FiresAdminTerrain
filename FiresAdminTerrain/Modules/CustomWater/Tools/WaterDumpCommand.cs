using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// `va_water_dump` console command — exhaustive scan of every water-like
    /// object currently in the scene, grouped/sorted by Y level so stacked
    /// water layers become obvious in the output.
    ///
    /// What it finds:
    ///   * Any active Renderer whose material's shader name matches
    ///     a water-shader pattern (FiresWater, water, water_lod, etc.)
    ///   * Any WaterVolume component
    ///   * Any GameObject named "water"/"WaterPlane"/"OceanWater"/"water_lod"/etc.
    ///   * Any MeshRenderer on a "skybox" layer that uses a water shader
    ///     (shouldn't happen, but flagged if it does)
    ///
    /// Output format (one line per find):
    ///   y=  30.0 | layer=4 (Water) | shader='Custom/FiresWater' | path='ZoneRoot/water (Clone)' | dist=12m
    ///
    /// Lines sorted ASCENDING by y so the layer stack reads bottom-to-top.
    /// Tail summary lists distinct Y "bands" detected (clusters within 0.5m)
    /// — that's where stacked layers show up at a glance.
    ///
    /// Output goes to:
    ///   1. Console terminal (truncated to 50 lines so it stays readable)
    ///   2. Full report file: BepInEx/config/FiresAdminTerrain/water_dump.txt
    /// </summary>
    public static class WaterDumpCommand
    {
        private const string CommandName = "va_water_dump";
        private const int    TerminalLineLimit = 50;
        private const float  BandClusterMeters = 0.5f;
        private const string DumpFileName = "water_dump.txt";

        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(
                CommandName,
                "Dumps every water-like renderer/volume/object in the scene, sorted by Y. " +
                "Full report written to BepInEx/config/FiresAdminTerrain/water_dump.txt",
                args => Execute(args), isCheat: false);
        }

        // ─── Match heuristics ─────────────────────────────────────────────

        private static readonly string[] WaterShaderPatterns =
        {
            "fireswater", "water_lod", "/water", "custom/water", "valheim/water"
        };

        private static readonly string[] WaterNamePatterns =
        {
            "water", "ocean", "river", "pond", "waterfall", "wavevolume"
        };

        private static bool IsWaterShader(string shaderNameLower)
        {
            if (string.IsNullOrEmpty(shaderNameLower)) return false;
            for (int i = 0; i < WaterShaderPatterns.Length; i++)
                if (shaderNameLower.Contains(WaterShaderPatterns[i])) return true;
            return false;
        }

        private static bool IsWaterName(string nameLower)
        {
            if (string.IsNullOrEmpty(nameLower)) return false;
            for (int i = 0; i < WaterNamePatterns.Length; i++)
                if (nameLower.Contains(WaterNamePatterns[i])) return true;
            return false;
        }

        // ─── Find / classify / format ─────────────────────────────────────

        private struct Find
        {
            public Vector3 pos;
            public int     layer;
            public string  shader;
            public string  path;
            public string  kind;   // "Renderer", "WaterVolume", "NamedGO"
            public string  vis;    // "on" / "DISABLED" / "CULLED" — the square-hole discriminator
            public float   dist;
        }

        private static string Hierarchy(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }

        private static void Execute(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            Vector3 origin = player != null ? player.transform.position : Vector3.zero;

            var finds = new List<Find>(256);
            var seenGOs = new HashSet<int>();

            // 1) Every Renderer in the scene whose material uses a water shader
            var renderers = Object.FindObjectsOfType<Renderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                var mat = r.sharedMaterial;
                if (mat == null || mat.shader == null) continue;
                string shaderLower = mat.shader.name.ToLowerInvariant();
                if (!IsWaterShader(shaderLower)) continue;

                int id = r.gameObject.GetInstanceID();
                if (!seenGOs.Add(id)) continue;

                Vector3 pos = r.bounds.center;
                finds.Add(new Find {
                    pos    = pos,
                    layer  = r.gameObject.layer,
                    shader = mat.shader.name,
                    path   = Hierarchy(r.transform),
                    kind   = "Renderer",
                    vis    = !r.enabled ? "DISABLED" : (r.isVisible ? "on" : "CULLED"),
                    dist   = Vector3.Distance(pos, origin),
                });
            }

            // 2) Every WaterVolume component
            var volumes = Object.FindObjectsOfType<WaterVolume>(includeInactive: false);
            for (int i = 0; i < volumes.Length; i++)
            {
                var wv = volumes[i];
                if (wv == null) continue;
                int id = wv.gameObject.GetInstanceID();
                if (!seenGOs.Add(id))
                {
                    // We already logged this as a renderer — add a tag to that
                    // find rather than duplicating the line.
                    for (int j = 0; j < finds.Count; j++)
                    {
                        if (finds[j].path.EndsWith(wv.name) || finds[j].path.Contains(wv.name))
                        {
                            var f = finds[j];
                            f.kind = "Renderer+WaterVolume";
                            finds[j] = f;
                            break;
                        }
                    }
                    continue;
                }

                Vector3 pos = wv.transform.position;
                string shaderName = "<no renderer>";
                string vis = "n/a";
                var rOnGO = wv.GetComponent<Renderer>();
                if (rOnGO == null && wv.m_waterSurface != null) rOnGO = wv.m_waterSurface;
                if (rOnGO != null)
                {
                    if (rOnGO.sharedMaterial != null && rOnGO.sharedMaterial.shader != null)
                        shaderName = rOnGO.sharedMaterial.shader.name;
                    vis = !rOnGO.enabled ? "DISABLED" : (rOnGO.isVisible ? "on" : "CULLED");
                }

                finds.Add(new Find {
                    pos    = pos,
                    layer  = wv.gameObject.layer,
                    shader = shaderName,
                    path   = Hierarchy(wv.transform),
                    kind   = "WaterVolume",
                    vis    = vis,
                    dist   = Vector3.Distance(pos, origin),
                });
            }

            // 3) Any GameObject with a water-ish name that we haven't seen yet
            var allGOs = Object.FindObjectsOfType<Transform>(includeInactive: false);
            for (int i = 0; i < allGOs.Length; i++)
            {
                var t = allGOs[i];
                if (t == null) continue;
                int id = t.gameObject.GetInstanceID();
                if (seenGOs.Contains(id)) continue;
                if (!IsWaterName(t.name.ToLowerInvariant())) continue;
                seenGOs.Add(id);

                string shaderName = "<no renderer>";
                var rOnGO = t.GetComponent<Renderer>();
                if (rOnGO != null && rOnGO.sharedMaterial != null && rOnGO.sharedMaterial.shader != null)
                    shaderName = rOnGO.sharedMaterial.shader.name;

                finds.Add(new Find {
                    pos    = t.position,
                    layer  = t.gameObject.layer,
                    shader = shaderName,
                    path   = Hierarchy(t),
                    kind   = "NamedGO",
                    dist   = Vector3.Distance(t.position, origin),
                });
            }

            // Sort ascending by Y so the layer stack reads bottom-up
            finds.Sort((a, b) => a.pos.y.CompareTo(b.pos.y));

            // Detect distinct Y bands (clusters within BandClusterMeters)
            var bands = new List<(float yMin, float yMax, int count)>();
            for (int i = 0; i < finds.Count; i++)
            {
                float y = finds[i].pos.y;
                bool grouped = false;
                for (int b = 0; b < bands.Count; b++)
                {
                    if (Mathf.Abs(bands[b].yMin - y) <= BandClusterMeters ||
                        Mathf.Abs(bands[b].yMax - y) <= BandClusterMeters)
                    {
                        bands[b] = (
                            Mathf.Min(bands[b].yMin, y),
                            Mathf.Max(bands[b].yMax, y),
                            bands[b].count + 1);
                        grouped = true;
                        break;
                    }
                }
                if (!grouped) bands.Add((y, y, 1));
            }

            // ── Format the report ──────────────────────────────────────────
            var report = new StringBuilder();
            report.AppendLine("==== va_water_dump ====");
            report.AppendLine($"Player @ ({origin.x:F1}, {origin.y:F1}, {origin.z:F1})");
            report.AppendLine($"Found {finds.Count} water-related object(s) in {bands.Count} distinct Y band(s):");
            report.AppendLine();
            report.AppendLine("Y BANDS (clusters within 0.5m — these are the 'stacked layers' to investigate):");
            for (int b = 0; b < bands.Count; b++)
            {
                report.AppendLine($"  band#{b} y=[{bands[b].yMin:F2} .. {bands[b].yMax:F2}]  count={bands[b].count}");
            }
            report.AppendLine();
            report.AppendLine("FULL LIST (sorted by Y ascending):");
            for (int i = 0; i < finds.Count; i++)
            {
                var f = finds[i];
                report.AppendLine(
                    $"  y={f.pos.y,8:F2} | xz=({f.pos.x,8:F1},{f.pos.z,8:F1}) | " +
                    $"layer={f.layer,2} | kind={f.kind,-22} | vis={f.vis ?? "?",-8} | " +
                    $"shader='{f.shader}' | dist={f.dist,7:F1}m | path='{f.path}'");
            }

            // Terminal: print header + bands + first TerminalLineLimit lines
            args.Context.AddString($"[va_water_dump] {finds.Count} water object(s), {bands.Count} band(s)");
            for (int b = 0; b < bands.Count; b++)
                args.Context.AddString($"  band#{b} y=[{bands[b].yMin:F2}..{bands[b].yMax:F2}] count={bands[b].count}");
            args.Context.AddString("---- first " + TerminalLineLimit + " entries (full list in dump file) ----");
            int shown = 0;
            for (int i = 0; i < finds.Count && shown < TerminalLineLimit; i++, shown++)
            {
                var f = finds[i];
                args.Context.AddString(
                    $"y={f.pos.y:F1} l={f.layer} k={f.kind} sh='{ShortShader(f.shader)}' p='{ShortPath(f.path)}'");
            }
            if (finds.Count > TerminalLineLimit)
                args.Context.AddString($"... {finds.Count - TerminalLineLimit} more lines in dump file ...");

            // File: full report
            try
            {
                string cfgDir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminTerrain");
                Directory.CreateDirectory(cfgDir);
                string filePath = Path.Combine(cfgDir, DumpFileName);
                File.WriteAllText(filePath, report.ToString());
                args.Context.AddString($"Full report written to: {filePath}");
                FiresLogger.LogInfo($"[WaterDumpCommand] Wrote {finds.Count} entries to {filePath}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"Failed to write dump file: {ex.Message}");
                FiresLogger.LogWarning($"[WaterDumpCommand] write failed: {ex}");
            }
        }

        private static string ShortShader(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int slash = s.LastIndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        private static string ShortPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length <= 50) return s;
            return "..." + s.Substring(s.Length - 47);
        }
    }
}
