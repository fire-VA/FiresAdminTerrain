using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // va_water_census — answers "how many waters are rendering HERE" from
    // inside the game: every enabled water-ish renderer whose footprint
    // covers the player, with surface height, owner path, material and
    // shader — plus what the gen field, the heightmap and the gen mesh
    // each believe about the same spot. One command, one log block.
    public static class WaterCensus
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("va_water_census",
                "Lists every water surface rendering at the player position (details to log)",
                (args) => Run(args));
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            var p = Player.m_localPlayer;
            if (p == null) { args.Context?.AddString("no local player"); return; }
            Vector3 pos = p.transform.position;
            var sb = new StringBuilder(1024);
            sb.AppendLine($"[WaterCensus] at ({pos.x:0.0},{pos.z:0.0}) playerY={pos.y:F2}:");

            int found = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var mat = r.sharedMaterial;
                string shader = mat != null && mat.shader != null ? mat.shader.name : "";
                string mname = mat != null ? mat.name : "";
                bool watery = Has(shader, "water") || Has(shader, "liquid")
                           || Has(mname, "water") || Has(mname, "liquid")
                           || Has(r.gameObject.name, "water") || Has(r.gameObject.name, "liquid");
                if (!watery) continue;
                var b = r.bounds;
                if (pos.x < b.min.x - 4f || pos.x > b.max.x + 4f
                    || pos.z < b.min.z - 4f || pos.z > b.max.z + 4f) continue;
                found++;
                sb.AppendLine($"  [{found}] surfY={b.max.y:F2} (center {b.center.y:F2}) go='{Path(r.transform)}' " +
                              $"mat='{mname}' shader='{shader}' size=({b.size.x:0}x{b.size.z:0})");
            }
            sb.AppendLine($"  => {found} water renderer(s) over this spot");

            if (FiresWorldGenBase.TryGetGenWaterSmooth(pos.x, pos.z, out float lvl, out float depth))
                sb.AppendLine($"  gen field: smoothLvl={lvl:F2} smoothDepth={depth:F2} IsWetAt={FiresWorldGenBase.IsWetAt(pos.x, pos.z)}");
            bool hm = Heightmap.GetHeight(pos, out float g);
            sb.AppendLine($"  heightmap ground={(hm ? g.ToString("F2") : "n/a")} biome={p.GetCurrentBiome()}");
            sb.AppendLine($"  gen mesh SurfaceAt={VoxelGenWaterMesh.SurfaceAt(pos):F2} (-10000 = dry)");

            FiresLogger.LogInfo(sb.ToString());
            args.Context?.AddString($"water census: {found} surface(s) — details in log");
        }

        private static bool Has(string s, string needle)
            => !string.IsNullOrEmpty(s) && s.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(64);
            int guard = 0;
            while (t != null && guard++ < 6) { sb.Insert(0, "/" + t.name); t = t.parent; }
            return sb.ToString();
        }
    }
}
