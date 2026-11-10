using System;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent.Terrain.HeightmapSnow
{
    /// <summary>
    /// 0.2.1306 ([lead], Fire's hand check on 1305: the seam closes only after walking, the trail draws a few steps then never): a
    /// read-only dump, run ONLY on command (console or a -firesspawncommand chain). `fat_zone_paint_dump [x,z]` (default: the player's
    /// zone) writes F:\FatDumps\&lt;time&gt;_zone_X_Z_{paint_R,paint_G,paint_B,paint_A,vcol_R,vcol_G,vcol_B,vcol_A,comp_G,comp_mod}.png -
    /// the vanilla heightmap's paint mask texture (what the terrain shader samples, incl. Core's injected G), its render mesh's vertex
    /// colours (the shader's snow weight), the zone TerrainComp's own paint G + modified-paint flags - plus a .txt with the 6 m around
    /// the player (each vertex: texture G, vertex colour, comp G / modified) and the comp's operation count. No behaviour change.
    /// </summary>
    internal static class FatZonePaintDump
    {
        private const string Tag = "[ZonePaintDump]";
        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_hmPaint = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> s_hmMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> s_compPaint = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> s_compMod = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, int> s_compOps = AccessTools.FieldRefAccess<TerrainComp, int>("m_operations");

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_zone_paint_dump", "[diag] dump the zone's vanilla heightmap paint mask, vertex colours and TerrainComp paint to F:\\FatDumps (PNG + txt). Args: [x,z] zone (default: yours)",
                args =>
                {
                    try { Dump(args.Args.Length > 1 ? args.Args[1] : null); }
                    catch (Exception ex) { FiresLogger.LogWarning($"{Tag} failed: {ex.GetType().Name}: {ex.Message}"); }
                }, isCheat: true);
        }

        private static void Dump(string zoneArg)
        {
            if (Player.m_localPlayer == null || ZoneSystem.instance == null) { FiresLogger.LogWarning($"{Tag} refused: no player / world"); return; }
            Vector3 pp = Player.m_localPlayer.transform.position;
            Vector2s zone = ZoneSystem.GetZone(pp);
            if (!string.IsNullOrEmpty(zoneArg))
            {
                var s = zoneArg.Split(',');
                if (s.Length == 2 && int.TryParse(s[0], out int zx) && int.TryParse(s[1], out int zz)) zone = new Vector2s(zx, zz);
            }
            Vector3 zc = ZoneSystem.GetZonePos(zone);
            var hm = Heightmap.FindHeightmap(zc);
            if (hm == null) { FiresLogger.LogWarning($"{Tag} zone {zone.x},{zone.y}: no loaded heightmap"); return; }
            string dir = @"F:\FatDumps";
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_zone_{zone.x}_{zone.y}");
            var sb = new StringBuilder();
            sb.AppendLine($"zone {zone.x},{zone.y} heightmap '{hm.name}' at {hm.transform.position} width {hm.m_width} scale {hm.m_scale}; player at {pp} (FAT 0.2.1306)");

            // 1. the paint mask texture the terrain shader samples
            var tex = s_hmPaint(hm);
            Color[] tp = null; int tw = 0;
            if (tex != null)
            {
                try { tp = tex.GetPixels(); tw = tex.width; } catch (Exception ex) { sb.AppendLine($"paint texture not readable: {ex.Message}"); }
                if (tp != null) { WriteChannels(stem + "_paint", tp, tw, tex.height); sb.AppendLine($"paint mask texture {tw}x{tex.height} (format {tex.format}, updateCount {tex.updateCount})"); }
            }
            else sb.AppendLine("no paint mask texture");

            // 2. the render mesh's vertex colours (the shader's snow weight)
            var mesh = s_hmMesh(hm);
            Color32[] vc = null; int vw = hm.m_width + 1;
            if (mesh != null)
            {
                vc = mesh.colors32;
                if (vc != null && vc.Length == vw * vw)
                {
                    var asC = new Color[vc.Length]; for (int i = 0; i < vc.Length; i++) asC[i] = vc[i];
                    WriteChannels(stem + "_vcol", asC, vw, vw);
                    sb.AppendLine($"render mesh {mesh.vertexCount} vertices, colours {vc.Length}");
                }
                else sb.AppendLine($"render mesh colours {(vc == null ? 0 : vc.Length)} (expected {vw * vw}) - not written");
            }
            else sb.AppendLine("no render mesh");

            // 3. the zone TerrainComp's own paint (what ops wrote) + modified flags
            var comp = TerrainComp.FindTerrainCompiler(zc);
            Color[] cp = null; bool[] cm = null;
            if (comp != null)
            {
                cp = s_compPaint(comp); cm = s_compMod(comp);
                sb.AppendLine($"TerrainComp operations {s_compOps(comp)}, paint {(cp == null ? 0 : cp.Length)}, modified {(cm == null ? 0 : cm.Length)}");
                if (cp != null && cp.Length == vw * vw)
                {
                    var g = new Color[cp.Length]; var m = new Color[cp.Length];
                    for (int i = 0; i < cp.Length; i++) { g[i] = new Color(cp[i].g, cp[i].g, cp[i].g, 1f); m[i] = cm != null && i < cm.Length && cm[i] ? Color.white : Color.black; }
                    WritePng(stem + "_comp_G.png", g, vw, vw); WritePng(stem + "_comp_mod.png", m, vw, vw);
                }
            }
            else sb.AppendLine("no TerrainComp in this zone (no op landed here)");

            // 4. the 6 m around the player, per vertex
            sb.AppendLine("vertex (world x,z): texture RGBA | vertex colour RGBA | comp G (modified)");
            hm.WorldToVertexMask(pp, out int px, out int py);
            int r = Mathf.CeilToInt(6f / hm.m_scale);
            for (int y = Math.Max(0, py - r); y <= Math.Min(hm.m_width, py + r); y++)
                for (int x = Math.Max(0, px - r); x <= Math.Min(hm.m_width, px + r); x++)
                {
                    int i = y * vw + x;
                    string t = tp != null && tw == vw && i < tp.Length ? $"{tp[i].r:0.00} {tp[i].g:0.00} {tp[i].b:0.00} {tp[i].a:0.00}" : "-";
                    string v = vc != null && i < vc.Length ? $"{vc[i].r} {vc[i].g} {vc[i].b} {vc[i].a}" : "-";
                    string c = cp != null && i < cp.Length ? $"{cp[i].g:0.00}{(cm != null && i < cm.Length && cm[i] ? " (mod)" : "")}" : "-";
                    float wx = hm.transform.position.x + (x - hm.m_width * 0.5f) * hm.m_scale, wz = hm.transform.position.z + (y - hm.m_width * 0.5f) * hm.m_scale;
                    sb.AppendLine($"[{x},{y}] ({wx:0.0},{wz:0.0}): {t} | {v} | {c}");
                }
            File.WriteAllText(stem + ".txt", sb.ToString());
            FiresLogger.LogInfo($"{Tag} zone {zone.x},{zone.y}: wrote {stem}_*.png + .txt (paint mask {(tp != null ? "yes" : "no")}, vertex colours {(vc != null ? "yes" : "no")}, TerrainComp {(comp != null ? "yes" : "no")}) (0.2.1306)");
        }

        private static void WriteChannels(string stem, Color[] px, int w, int h)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                var o = new Color[px.Length];
                for (int i = 0; i < px.Length; i++) { float v = ch == 0 ? px[i].r : ch == 1 ? px[i].g : ch == 2 ? px[i].b : px[i].a; o[i] = new Color(v, v, v, 1f); }
                WritePng($"{stem}_{"RGBA"[ch]}.png", o, w, h);
            }
        }

        private static void WritePng(string path, Color[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try { t.SetPixels(px); t.Apply(false); File.WriteAllBytes(path, t.EncodeToPNG()); }
            finally { UnityEngine.Object.Destroy(t); }
        }
    }
}
