using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // VAFatToolIcons — generates a distinct, Fires-branded sprite for every
    // native Fires tool piece (NativeToolNames). SpriteFor() hands the sprite
    // straight to Piece.m_icon; the StatusEffect registration is kept only so a
    // player who writes their own IH yaml can still reference "fires_<slug>".
    //
    // Design language (v2 — glance-first):
    //   * FAMILY = glyph. Terrain=chevrons/bar, paint=drop, biome=leaf,
    //     water=wave, ice=diamond, snow=flake, holes=ring, util=distinct marks.
    //   * VARIANT = background colour (per biome / per liquid) + small corner
    //     chip (land/water force, floating island).
    //   * DESTRUCTIVE = the family tile with a red slash across it — family
    //     stays readable instead of every delete being an identical red X.
    //   * Every glyph is stamped with a dark outline so it pops off the tile,
    //     and every tile carries the small flame badge bottom-right (the
    //     "this is a Fires tool" mark).
    //
    // Coordinate note: Texture2D row 0 is the BOTTOM. All drawing here uses
    // y-DOWN (screen-style) coordinates and Plot() flips at write time — the
    // v1 icons drew raw and rendered upside down (raise arrows pointed down).
    //
    // Client-only: pure Texture2D pixel work; headless is gated out.
    public static class VAFatToolIcons
    {
        public const string SlugPrefix = "fires_";
        private const int Size = 64;

        // Generated ONCE; re-added to every ObjectDB (it's rebuilt menu↔game).
        private static List<StatusEffect> _cache;

        // Sprites requested by name before/without a StatusEffect pass.
        private static readonly Dictionary<string, Sprite> _looseSprites
            = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        // Every native Fires tool piece that wants a branded icon. Display names
        // double as the StyleFor() keywords, so a new tool only has to be named
        // for its family to pick the right glyph and colour.
        public static readonly string[] NativeToolNames =
        {
            "Vcarve", "Vfill", "Vdelete", "Vcube", "Vsphere", "Vlevel",
            "Vriver", "Vpour", "Vpool", "Vfall", "Vmop",
        };

        // "Paint Cultivated" -> "fires_paint_cultivated". Non-alnum runs → '_'.
        public static string SlugFor(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return SlugPrefix + "tool";
            var sb = new System.Text.StringBuilder(SlugPrefix.Length + toolName.Length);
            sb.Append(SlugPrefix);
            bool lastUnderscore = true;
            foreach (char c in toolName.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) { sb.Append(c); lastUnderscore = false; }
                else if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
            }
            return sb.ToString().TrimEnd('_');
        }

        public static void Register()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    return; // headless / dedicated server
                if (ObjectDB.instance == null || ObjectDB.instance.m_StatusEffects == null)
                    return;

                if (_cache == null)
                {
                    _cache = new List<StatusEffect>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tool in ReadBrandedTools())
                    {
                        if (!seen.Add(tool.Value)) continue;
                        var sprite = BuildSprite(tool.Key);
                        if (sprite == null) continue;
                        var se = ScriptableObject.CreateInstance<StatusEffect>();
                        se.name = tool.Value;
                        se.m_name = tool.Key;
                        se.m_icon = sprite;
                        UnityEngine.Object.DontDestroyOnLoad(se);
                        _cache.Add(se);
                    }
                    if (_cache.Count > 0)
                        FiresLogger.LogInfo($"VAFatToolIcons: generated {_cache.Count} branded tool icon(s).");
                }

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var se in ObjectDB.instance.m_StatusEffects)
                    if (se != null && se.name != null) present.Add(se.name);

                int added = 0;
                foreach (var se in _cache)
                {
                    if (se == null || present.Contains(se.name)) continue;
                    ObjectDB.instance.m_StatusEffects.Add(se);
                    added++;
                }
                if (added > 0)
                    FiresLogger.LogInfo($"VAFatToolIcons: registered {added} tool icon(s) into ObjectDB.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"VAFatToolIcons.Register failed: {ex.Message}");
            }
        }

        // (toolName, slug) for every tool whose icon references fires_*.
        // The tools are OUR OWN hoe/hammer pieces now, so the branded set is
        // declared in code instead of scraped out of infinity_tools.yaml. The
        // yaml round-trip is gone entirely — a piece takes its sprite straight
        // from SpriteFor(), and nothing has to exist on disk for icons to work.
        private static List<KeyValuePair<string, string>> ReadBrandedTools()
        {
            var result = new List<KeyValuePair<string, string>>(NativeToolNames.Length);
            foreach (var name in NativeToolNames)
                result.Add(new KeyValuePair<string, string>(name, SlugFor(name)));
            return result;
        }

        // Sprite for one tool, built on demand and cached. Shares the Register()
        // cache so a tool that already has a StatusEffect never rasterises twice.
        public static Sprite SpriteFor(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return null;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return null;

            if (_cache != null)
            {
                foreach (var se in _cache)
                    if (se != null && string.Equals(se.m_name, toolName, StringComparison.OrdinalIgnoreCase))
                        return se.m_icon;
            }

            Sprite loose;
            if (_looseSprites.TryGetValue(toolName, out loose) && loose != null) return loose;

            try
            {
                loose = BuildSprite(toolName);
                if (loose != null)
                {
                    UnityEngine.Object.DontDestroyOnLoad(loose);
                    UnityEngine.Object.DontDestroyOnLoad(loose.texture);
                    _looseSprites[toolName] = loose;
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"VAFatToolIcons.SpriteFor({toolName}): {ex.Message}"); }
            return loose;
        }

        // ─────────────────────────────────────────────────────────────
        //  Style spec
        // ─────────────────────────────────────────────────────────────

        private enum Glyph
        {
            ChevronUp, ChevronUp2, ChevronDown, UpDown, FlatBar, Wave, Falls,
            Diamond, Flake, Leaf, Drop, Ring, RingFilled, Dots, Zigzag,
            Padlock, Crosshair, Bars3, LetterI, Bang, Ship, FlameMark
        }

        [Flags]
        private enum Overlay
        {
            None = 0,
            Slash = 1,        // red slash = destructive variant of the family
            SmallUp = 2,      // little chevron top-right (raise op)
            SmallDown = 4,    // little chevron top-right (lower op)
            BarUnder = 8,     // level / channel accent bar under the glyph
            WaveUnder = 16,   // smooth / water-force accent
            ChipLand = 32,    // brown chip top-left (land variant)
            ChipWater = 64,   // blue chip top-left (water variant)
            ChipIsland = 128, // gold chip top-left (floating island variant)
        }

        private struct Spec
        {
            public Color Bg, Fg, Accent;
            public Glyph G;
            public Overlay O;
        }

        private static Spec S(int bg, Glyph g, Overlay o = Overlay.None, int fg = 0xF7F7F7, int accent = -1)
            => new Spec { Bg = Hex(bg), Fg = Hex(fg), G = g, O = o, Accent = accent >= 0 ? Hex(accent) : Hex(fg) };

        private static Spec StyleFor(string name)
        {
            string n = name.ToLowerInvariant();
            bool island = n.Contains("floating island");

            // ── native V-tool pieces (exact names; ahead of the keyword families
            //    so "Vdelete" reads as a restore rather than a generic destroy) ──
            switch (n)
            {
                case "vcarve":  return S(0x8A6238, Glyph.ChevronDown);
                case "vfill":   return S(0x8A6238, Glyph.ChevronUp);
                case "vdelete": return S(0x92703F, Glyph.FlatBar, Overlay.Slash);
                case "vcube":   return S(0x6A5540, Glyph.Diamond);
                case "vsphere": return S(0x6A5540, Glyph.Ring);
                case "vriver":  return S(0x6E8FA8, Glyph.Wave, Overlay.ChipLand);
                case "vpour":   return S(0x2E77C0, Glyph.Drop, Overlay.WaveUnder);
                case "vpool":   return S(0x2E77C0, Glyph.Wave, Overlay.BarUnder);
                case "vfall":   return S(0x2E77C0, Glyph.Falls);
                case "vmop":    return S(0x2E77C0, Glyph.Drop, Overlay.Slash);
            }

            // ── util / mode tools ──
            if (n.Contains("clutter clear")) return S(0x4F9A34, Glyph.Leaf, Overlay.Slash);
            if (n.Contains("height lock")) return S(0x4A4E57, Glyph.Padlock);
            if (n.Contains("toggle placement")) return S(0x4A4E57, Glyph.Crosshair);
            if (n.Contains("seam")) return S(0x9A7B4E, Glyph.Zigzag);
            if (n.Contains("tile info") || n.Contains("where")) return S(0x5B4A78, Glyph.LetterI);
            if (n.Contains("anchor") && n.Contains("list")) return S(0x5B4A78, Glyph.Bars3);
            if (n.Contains("register")) return S(0x5B4A78, Glyph.Bang);

            // ── holes ──
            if (n.Contains("hole punch") || n.Contains("punch")) return S(0x6A5540, Glyph.Ring);
            if (n.Contains("patch")) return S(0x6A5540, Glyph.RingFilled);

            // ── paint (drop + channel accent bar) ──
            if (n.Contains("clear brushed")) return S(0x8A5A28, Glyph.Drop, Overlay.Slash);
            if (n.Contains("paint") && n.Contains("dirt")) return S(0x7C4F22, Glyph.Drop, Overlay.BarUnder, accent: 0xC98A48);
            if (n.Contains("cultivat")) return S(0x3F8A2E, Glyph.Drop, Overlay.BarUnder, accent: 0x7FE05A);
            if (n.Contains("pave")) return S(0x60666F, Glyph.Drop, Overlay.BarUnder, accent: 0xB9C0CB);
            if (n.Contains("paint") && n.Contains("grass")) return S(0x4E9A2E, Glyph.Drop, Overlay.BarUnder, accent: 0x9BEA5F);

            // ── biomes (leaf family; colour = biome) ──
            if (n.Contains("biome"))
            {
                if (n.Contains("clear")) return S(0x4F7A3A, Glyph.Leaf, Overlay.Slash);
                if (n.Contains("meadow")) return S(0x58A82F, Glyph.Leaf);
                if (n.Contains("blackforest")) return S(0x1F5F30, Glyph.Leaf);
                if (n.Contains("plains")) return S(0xC9A02F, Glyph.Leaf, fg: 0x4A3A14, accent: 0x4A3A14);
                if (n.Contains("swamp")) return S(0x6B7034, Glyph.Leaf);
                if (n.Contains("mountain")) return S(0x7E9CC0, Glyph.Leaf);
                if (n.Contains("mistland")) return S(0x6B4FA0, Glyph.Leaf);
                if (n.Contains("ashland") || n.Contains("lava")) return S(0xC63A14, Glyph.Leaf);
                if (n.Contains("deepnorth")) return S(0xDCEBF6, Glyph.Leaf, fg: 0x2F5A7A, accent: 0x2F5A7A);
                return S(0x4F7A3A, Glyph.Leaf);
            }

            // ── ice ops (diamond family) ──
            if (n.Contains("break ice")) return S(0x59C6DC, Glyph.Ring, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("delete") && n.Contains("ice") && n.Contains("land")) return S(0x59C6DC, Glyph.Diamond, Overlay.Slash | Overlay.ChipLand, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("delete") && n.Contains("ice")) return S(0x59C6DC, Glyph.Diamond, Overlay.Slash, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("raise")) return S(0x59C6DC, Glyph.Diamond, Overlay.SmallUp, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("lower")) return S(0x59C6DC, Glyph.Diamond, Overlay.SmallDown, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("level")) return S(0x59C6DC, Glyph.Diamond, Overlay.BarUnder, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("smooth")) return S(0x59C6DC, Glyph.Diamond, Overlay.WaveUnder, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("water force")) return S(0x59C6DC, Glyph.Diamond, Overlay.ChipWater, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("ice") && n.Contains("land force")) return S(0x59C6DC, Glyph.Diamond, Overlay.ChipLand, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("place ice") || n == "ice") return S(0x59C6DC, Glyph.Diamond, fg: 0x123A46, accent: 0x123A46);

            // ── snow ops (flake family) ──
            if (n.Contains("delete") && n.Contains("snow")) return S(0xE6F2FB, Glyph.Flake, Overlay.Slash, fg: 0x35608C, accent: 0x35608C);
            if (n.Contains("snow") && n.Contains("raise")) return S(0xE6F2FB, Glyph.Flake, Overlay.SmallUp, fg: 0x35608C, accent: 0x35608C);
            if (n.Contains("snow") && n.Contains("lower")) return S(0xE6F2FB, Glyph.Flake, Overlay.SmallDown, fg: 0x35608C, accent: 0x35608C);
            if (n.Contains("snow") && n.Contains("level")) return S(0xE6F2FB, Glyph.Flake, Overlay.BarUnder, fg: 0x35608C, accent: 0x35608C);
            if (n.Contains("snow") && n.Contains("smooth")) return S(0xE6F2FB, Glyph.Flake, Overlay.WaveUnder, fg: 0x35608C, accent: 0x35608C);
            if (n.Contains("snow")) return S(0xE6F2FB, Glyph.Flake, fg: 0x35608C, accent: 0x35608C);

            // ── water family (wave; colour = liquid) ──
            if (n.Contains("delete") && n.Contains("water")) return S(0x2E77C0, Glyph.Wave, Overlay.Slash);
            if (n.Contains("waterfall")) return S(0x2E77C0, Glyph.Falls);
            if (n.Contains("swimmable")) return S(0xE06428, Glyph.Wave, Overlay.WaveUnder);
            if (n.Contains("lava")) return S(0xD4491A, Glyph.Wave);
            if (n.Contains("tar")) return S(0x2A241E, Glyph.Wave, fg: 0xD9CFA8, accent: 0xD9CFA8);
            if (n.Contains("boiling")) return S(0xC0622A, Glyph.Wave);
            if (n.Contains("freezing")) return S(0x5FB6D8, Glyph.Wave, fg: 0x123A46, accent: 0x123A46);
            if (n.Contains("water")) return S(0x2E77C0, Glyph.Wave);

            // ── terrain shaping (chevrons/bar; island variant gets gold chip) ──
            Overlay isl = island ? Overlay.ChipIsland : Overlay.None;
            if (n.Contains("delete") && n.Contains("tile")) return S(0x8A6B3E, Glyph.FlatBar, Overlay.Slash);
            if (n.Contains("sharp")) return S(0x8A6238, Glyph.ChevronUp2, isl);
            if (n.Contains("raise")) return S(0x8A6238, Glyph.ChevronUp, isl);
            if (n.Contains("lower")) return S(0x8A6238, Glyph.ChevronDown, isl);
            if (n.Contains("floor") || n.Contains("level") || n.Contains("flat")) return S(0x92703F, Glyph.FlatBar, isl);
            if (n.Contains("smooth")) return S(0x92703F, Glyph.Wave, isl);
            if (n.Contains("grab")) return S(0x92703F, Glyph.UpDown, isl);
            if (n.Contains("noise")) return S(0x92703F, Glyph.Dots, isl);

            // ── hammer-section / future fallbacks ──
            if (n.Contains("pipette")) return S(0x4A4E57, Glyph.Drop);
            if (n.Contains("ship") || n.Contains("landing") || n.Contains("takeoff")) return S(0x2F6E6E, Glyph.Ship);
            if (n.Contains("delete") || n.Contains("clear") || n.Contains("remove") || n.Contains("ruiner")) return S(0x7A2E2E, Glyph.FlatBar, Overlay.Slash);
            return S(0xB5611F, Glyph.FlameMark);
        }

        // ─────────────────────────────────────────────────────────────
        //  Sprite construction (pixel work; y-DOWN coords, Plot() flips)
        // ─────────────────────────────────────────────────────────────

        private static Sprite BuildSprite(string name)
        {
            var spec = StyleFor(name);
            // Tiny per-tool value nudge so same-family neighbours still differ.
            float h = (Hash(name) % 16) / 16f - 0.5f;
            var bg = ShiftValue(spec.Bg, h * 0.07f);

            var px = new Color32[Size * Size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);

            // Rounded tile, top-lit, dark border.
            Color border = Mul(bg, 0.5f);
            const int radius = 11;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!InRounded(x, y, 1, Size - 1, radius)) continue;
                    float t = 1f - (y / (float)Size) * 0.30f;
                    Color c = Mul(bg, 0.88f + 0.26f * t);
                    if (!InRounded(x, y, 4, Size - 4, radius - 3)) c = border;
                    Plot(px, x, y, c);
                }
            }

            // Main glyph, outlined.
            var mask = new bool[Size * Size];
            RasterGlyph(mask, spec.G);
            Stamp(px, mask, spec.Fg, Mul(bg, 0.28f));

            // Overlays.
            if ((spec.O & Overlay.SmallUp) != 0) StampSmallChevron(px, up: true, spec.Fg, Mul(bg, 0.28f));
            if ((spec.O & Overlay.SmallDown) != 0) StampSmallChevron(px, up: false, spec.Fg, Mul(bg, 0.28f));
            if ((spec.O & Overlay.BarUnder) != 0) StampSimple(px, m => MRect(m, 20, 51, 44, 56), spec.Accent, Mul(bg, 0.28f));
            if ((spec.O & Overlay.WaveUnder) != 0) StampSimple(px, m => MWaveRow(m, 16, 48, 53, 3), spec.Accent, Mul(bg, 0.28f));
            if ((spec.O & Overlay.ChipLand) != 0) Chip(px, Hex(0x7C5A34));
            if ((spec.O & Overlay.ChipWater) != 0) Chip(px, Hex(0x2E77C0));
            if ((spec.O & Overlay.ChipIsland) != 0) Chip(px, Hex(0xE0B33A));
            if ((spec.O & Overlay.Slash) != 0) StampSimple(px, MSlash, Hex(0xE0402E), Hex(0x54100A));

            FlameBadge(px);

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
        }

        // y-down → texture (row 0 = bottom) write.
        private static void Plot(Color32[] px, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size) return;
            px[(Size - 1 - y) * Size + x] = c;
        }

        // ── mask primitives (y-down logical space) ───────────────────
        private static void M(bool[] m, int x, int y)
        { if (x >= 0 && y >= 0 && x < Size && y < Size) m[y * Size + x] = true; }

        private static void MRect(bool[] m, int x0, int y0, int x1, int y1)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) M(m, x, y); }

        private static void MDot(bool[] m, int cx, int cy, int r)
        { for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) if (x * x + y * y <= r * r) M(m, cx + x, cy + y); }

        private static void MLine(bool[] m, int x0, int y0, int x1, int y1, int r)
        {
            int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
            if (steps == 0) { MDot(m, x0, y0, r); return; }
            for (int i = 0; i <= steps; i++)
                MDot(m, x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, r);
        }

        private static void MDiamond(bool[] m, int cx, int cy, int r)
        { for (int y = -r; y <= r; y++) { int w = r - Math.Abs(y); for (int x = -w; x <= w; x++) M(m, cx + x, cy + y); } }

        private static void MRing(bool[] m, int cx, int cy, int rOut, int rIn)
        {
            for (int y = -rOut; y <= rOut; y++) for (int x = -rOut; x <= rOut; x++)
            { int d = x * x + y * y; if (d <= rOut * rOut && d >= rIn * rIn) M(m, cx + x, cy + y); }
        }

        private static void MWaveRow(bool[] m, int x0, int x1, int cy, int r)
        {
            for (int x = x0; x <= x1; x++)
            {
                double s = Math.Sin((x - x0) / (double)(x1 - x0) * Math.PI * 2.0);
                MDot(m, x, cy + (int)Math.Round(s * 4.0), r);
            }
        }

        private static void RasterGlyph(bool[] m, Glyph g)
        {
            switch (g)
            {
                case Glyph.ChevronUp:
                    MLine(m, 14, 40, 32, 18, 4); MLine(m, 32, 18, 50, 40, 4); break;
                case Glyph.ChevronUp2:
                    MLine(m, 16, 32, 32, 14, 3); MLine(m, 32, 14, 48, 32, 3);
                    MLine(m, 16, 50, 32, 32, 3); MLine(m, 32, 32, 48, 50, 3); break;
                case Glyph.ChevronDown:
                    MLine(m, 14, 20, 32, 42, 4); MLine(m, 32, 42, 50, 20, 4); break;
                case Glyph.UpDown:
                    MLine(m, 20, 24, 32, 12, 3); MLine(m, 32, 12, 44, 24, 3);
                    MLine(m, 20, 40, 32, 52, 3); MLine(m, 32, 52, 44, 40, 3); break;
                case Glyph.FlatBar:
                    MRect(m, 14, 27, 50, 37); break;
                case Glyph.Wave:
                    MWaveRow(m, 12, 52, 25, 3); MWaveRow(m, 12, 52, 39, 3); break;
                case Glyph.Falls:
                    MRect(m, 19, 12, 24, 36); MRect(m, 29, 12, 34, 36); MRect(m, 39, 12, 44, 36);
                    MWaveRow(m, 14, 50, 46, 3); break;
                case Glyph.Diamond:
                    MDiamond(m, 32, 30, 17); break;
                case Glyph.Flake:
                    MLine(m, 14, 30, 50, 30, 2); MLine(m, 32, 12, 32, 48, 2);
                    MLine(m, 19, 17, 45, 43, 2); MLine(m, 45, 17, 19, 43, 2);
                    MDot(m, 32, 30, 4); break;
                case Glyph.Leaf:
                    MDiamond(m, 32, 28, 15); MRect(m, 30, 42, 34, 52); MLine(m, 32, 16, 32, 40, 1); break;
                case Glyph.Drop:
                    MDot(m, 32, 36, 12); MLine(m, 32, 12, 23, 30, 3); MLine(m, 32, 12, 41, 30, 3);
                    MDiamond(m, 32, 26, 8); break;
                case Glyph.Ring:
                    MRing(m, 32, 30, 16, 9); break;
                case Glyph.RingFilled:
                    MRing(m, 32, 30, 16, 9); MDot(m, 32, 30, 5); break;
                case Glyph.Dots:
                    MDot(m, 22, 22, 6); MDot(m, 43, 20, 4); MDot(m, 24, 42, 4); MDot(m, 42, 40, 6); break;
                case Glyph.Zigzag:
                    MLine(m, 12, 38, 22, 22, 3); MLine(m, 22, 22, 32, 38, 3);
                    MLine(m, 32, 38, 42, 22, 3); MLine(m, 42, 22, 52, 38, 3); break;
                case Glyph.Padlock:
                    MRing(m, 32, 22, 10, 6); MRect(m, 19, 26, 45, 50); break;
                case Glyph.Crosshair:
                    MRing(m, 32, 30, 15, 10);
                    MRect(m, 30, 8, 34, 18) ; MRect(m, 30, 42, 34, 52);
                    MRect(m, 10, 28, 20, 32); MRect(m, 44, 28, 54, 32); break;
                case Glyph.Bars3:
                    MRect(m, 15, 15, 49, 22); MRect(m, 15, 28, 49, 35); MRect(m, 15, 41, 49, 48); break;
                case Glyph.LetterI:
                    MRect(m, 22, 13, 42, 20); MRect(m, 28, 20, 36, 44); MRect(m, 22, 44, 42, 51); break;
                case Glyph.Bang:
                    MRect(m, 27, 12, 37, 38); MDot(m, 32, 49, 6); break;
                case Glyph.Ship:
                    MLine(m, 13, 38, 32, 47, 3); MLine(m, 32, 47, 51, 38, 3); MRect(m, 13, 34, 51, 39);
                    MRect(m, 30, 12, 34, 34);
                    for (int i = 0; i < 14; i++) MRect(m, 34, 14 + i, 34 + (14 - i), 14 + i);
                    break;
                case Glyph.FlameMark:
                    MDot(m, 32, 38, 11); MLine(m, 32, 12, 25, 30, 3); MLine(m, 32, 12, 39, 30, 3);
                    MDiamond(m, 32, 27, 7); break;
            }
        }

        // Outline stamp: dark halo (Chebyshev radius 2) then solid fill.
        private static void Stamp(Color32[] px, bool[] m, Color fill, Color outline)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (m[y * Size + x]) continue;
                    bool nearGlyph = false;
                    for (int dy = -2; dy <= 2 && !nearGlyph; dy++)
                        for (int dx = -2; dx <= 2 && !nearGlyph; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < Size && ny < Size && m[ny * Size + nx]) nearGlyph = true;
                        }
                    if (nearGlyph) Plot(px, x, y, outline);
                }
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (m[y * Size + x]) Plot(px, x, y, fill);
        }

        private static void StampSimple(Color32[] px, Action<bool[]> draw, Color fill, Color outline)
        {
            var m = new bool[Size * Size];
            draw(m);
            Stamp(px, m, fill, outline);
        }

        private static void StampSmallChevron(Color32[] px, bool up, Color fill, Color outline)
        {
            StampSimple(px, m =>
            {
                if (up) { MLine(m, 40, 19, 47, 12, 2); MLine(m, 47, 12, 54, 19, 2); }
                else { MLine(m, 40, 12, 47, 19, 2); MLine(m, 47, 19, 54, 12, 2); }
            }, fill, outline);
        }

        private static void MSlash(bool[] m) => MLine(m, 12, 52, 52, 12, 5);

        // Small rounded chip, top-left corner.
        private static void Chip(Color32[] px, Color c)
        {
            Color edge = Mul(c, 0.5f);
            for (int y = 6; y <= 20; y++)
                for (int x = 6; x <= 20; x++)
                {
                    bool border = x == 6 || x == 20 || y == 6 || y == 20;
                    Plot(px, x, y, border ? edge : c);
                }
        }

        // Flame badge bottom-right: dark disc + flame + hot core. The
        // constant "ours" mark across every tool.
        private static void FlameBadge(Color32[] px)
        {
            Color disc = new Color(0.10f, 0.07f, 0.05f, 1f);
            Color flame = Hex(0xFF7A18);
            Color core = Hex(0xFFD34D);
            for (int y = -9; y <= 9; y++)
                for (int x = -9; x <= 9; x++)
                    if (x * x + y * y <= 81) Plot(px, 51 + x, 51 + y, disc);
            // small teardrop flame (y-down: tip at top)
            for (int y = -6; y <= 5; y++)
            {
                float t = (y + 6) / 11f;
                int w = (int)Math.Round(4.5f * Math.Sin(Math.Min(1f, t) * Math.PI * 0.85f + 0.25f));
                if (w < 0) w = 0;
                for (int x = -w; x <= w; x++) Plot(px, 51 + x, 51 + y, flame);
            }
            for (int y = 0; y <= 4; y++)
                for (int x = -2; x <= 2; x++)
                    if (x * x + (y - 2) * (y - 2) <= 5) Plot(px, 51 + x, 51 + y, core);
        }

        // ── colour helpers ───────────────────────────────────────────
        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        private static Color Mul(Color c, float f) => new Color(Mathf.Clamp01(c.r * f), Mathf.Clamp01(c.g * f), Mathf.Clamp01(c.b * f), c.a);
        private static Color ShiftValue(Color c, float d) => new Color(Mathf.Clamp01(c.r + d), Mathf.Clamp01(c.g + d), Mathf.Clamp01(c.b + d), c.a);
        private static bool InRounded(int x, int y, int lo, int hi, int r)
        {
            if (x < lo || y < lo || x >= hi || y >= hi) return false;
            int dx = Math.Min(x - lo, hi - 1 - x);
            int dy = Math.Min(y - lo, hi - 1 - y);
            if (dx >= r || dy >= r) return true;
            int cx = dx < r ? lo + r : hi - 1 - r;
            int cy = dy < r ? lo + r : hi - 1 - r;
            int ex = x - cx, ey = y - cy;
            return ex * ex + ey * ey <= r * r;
        }
        private static int Hash(string s)
        {
            unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return Math.Abs(h); }
        }
    }
}
