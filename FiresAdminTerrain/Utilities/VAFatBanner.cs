using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAFatBanner — ported from FAP/FGN banner pattern. Emits two
    // banners at different load phases:
    //
    //   PrintBig()  → BIG ASCII floating island with trees + waterfall.
    //                 Fires at the very top of Awake as the loading
    //                 announcement. Multi-color zones: sky, clouds,
    //                 trees (varied greens), terrain, waterfall.
    //
    //   Print()     → Compact 8-line tree + waterfall + brand panel.
    //                 Fires at the end of FinishInit replacing the
    //                 old plain "mod loaded" log line.
    //
    // Color philosophy for the floating island:
    //   The art depicts a chunk of terrain torn from the ground,
    //   floating in the sky with trees on top and a waterfall pouring
    //   off the underside. Color zones:
    //     - Sky background (top + bottom):  Blue / DarkBlue
    //     - Clouds:                         White / Gray (puffy)
    //     - Trees (top of island):          Green / DarkGreen alternating
    //     - Island grass + dirt top:        DarkYellow / DarkGray
    //     - Island rocky base:              DarkGray / Gray
    //     - Waterfall stream:               Cyan / DarkCyan / Blue
    //     - Pool / mist at base:            DarkCyan
    //   Trees deliberately use TWO greens (Green + DarkGreen) per the
    //   user's request — bepinex console palette gives us both so we
    //   may as well use both for varied foliage.
    //
    // Both paths use direct console writes (via BepInEx.ConsoleManager
    // reflection) for multi-color. Plain fallback path emits via
    // Debug.Log if reflection fails.
    public static class VAFatBanner
    {
        private readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

        // Color aliases for the compact banner.
        private const ConsoleColor Sky      = ConsoleColor.Blue;
        private const ConsoleColor SkyDeep  = ConsoleColor.DarkBlue;
        private const ConsoleColor Cloud    = ConsoleColor.White;
        private const ConsoleColor CloudDim = ConsoleColor.Gray;
        private const ConsoleColor TreeLight = ConsoleColor.Green;
        private const ConsoleColor TreeDark = ConsoleColor.DarkGreen;
        private const ConsoleColor Grass    = ConsoleColor.DarkYellow; // dirt/grass top of island
        private const ConsoleColor Rock     = ConsoleColor.DarkGray;
        private const ConsoleColor RockHi   = ConsoleColor.Gray;
        private const ConsoleColor Water    = ConsoleColor.Cyan;
        private const ConsoleColor WaterDim = ConsoleColor.DarkCyan;
        private const ConsoleColor Brand    = ConsoleColor.Green;
        private const ConsoleColor Tagline  = ConsoleColor.Magenta;

        // ─────────────────────────────────────────────────────────────
        //  COMPACT banner — small floating island + brand panel
        // ─────────────────────────────────────────────────────────────
        // 11-line compressed version of the big banner: 3 little trees
        // on top of a small floating island with a 3-line waterfall
        // pouring off the underside. Bookends the BIG banner emitted
        // at the top of Awake.
        private static readonly Segment[][] s_compactLines =
        {
            new[]
            {
                new Segment("        ", Sky),
                new Segment("♣", TreeDark),
                new Segment(" ", Sky),
                new Segment("♠", TreeLight),
                new Segment(" ", Sky),
                new Segment("♣", TreeDark),
                new Segment("        ", Sky),
            },
            new[]
            {
                new Segment("       ", Sky),
                new Segment("░▒▓███▓▒░", Grass),
                new Segment("       ", Sky),
            },
            new[]
            {
                new Segment("       ", Sky),
                new Segment("▓▓▓███▓▓▓", Rock),
                new Segment("       ", Sky),
            },
            new[]
            {
                new Segment("        ", Sky),
                new Segment("▒▒▓▓▓▒▒", Rock),
                new Segment("       ", Sky),
            },
            new[]
            {
                new Segment("          ", Sky),
                new Segment("│", Water),
                new Segment("            ", Sky),
            },
            new[]
            {
                new Segment("          ", Sky),
                new Segment("│", Water),
                new Segment("            ", Sky),
            },
            new[]
            {
                new Segment("         ", Sky),
                new Segment("╲", WaterDim),
                new Segment("│", Water),
                new Segment("╱", WaterDim),
                new Segment("           ", Sky),
            },
            new[]
            {
                new Segment("        ", Sky),
                new Segment("░▒▓▓▓▒░", WaterDim),
                new Segment("        ", Sky),
            },
            // Frame top — 31-char total line width to match the FGN
            // compact banner exactly + leave room for the title with
            // a small padding on each side. Width = 3 lead spaces +
            // `╔` + 26 dashes + `╗`.
            new[] { new Segment("   ╔══════════════════════════╗", Rock) },

            // Brand line. The console advances ONE column for a bare
            // pictograph (no VS16), however wide the font paints it:
            //   `   ║  ` (6) + 🌲(1) + ` FIRES ADMIN TERRAIN` (20)
            //   + `   ║` (4) = 31, matching the frame.
            new[]
            {
                new Segment("   ║  ", Rock),
                new Segment("🌲 FIRES ADMIN TERRAIN", Brand),
                new Segment("   ║", Rock),
            },
            new[] { new Segment("   ╚══════════════════════════╝", Rock) },
            new Segment[0],
            new[] { new Segment("   Fires Admin Terrain Loaded", Tagline) },
        };

        // ─────────────────────────────────────────────────────────────
        //  Public entry points
        // ─────────────────────────────────────────────────────────────

        // Routes a one-line banner header through the plugin's BepInEx log source
        // (not Debug.Log) so it prints once instead of colour+white. FUC colours it
        // by the Fires source name.
        private static void EmitHeader(string text)
        {
            if (FiresAdminTerrain.Log != null) FiresAdminTerrain.Log.LogInfo(text);
            else FiresLogger.LogInfo(text);
        }

        public static void Print()
        {
            try { EmitHeader("Fires Admin Terrain Loaded."); }
            catch { }

            try
            {
                if (!TryWriteSegmented(s_compactLines))
                    WritePlainFallbackSegmented(s_compactLines);
            }
            catch
            {
                try { WritePlainFallbackSegmented(s_compactLines); } catch { }
            }
        }

        public static void PrintBig()
        {
            try { EmitHeader("Fires Admin Terrain Loading..."); }
            catch { }

            try
            {
                if (!TryWriteColoredBig())
                    WritePlainFallbackBig();
            }
            catch
            {
                try { WritePlainFallbackBig(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  BIG banner — floating island with trees + waterfall
        // ─────────────────────────────────────────────────────────────
        // Per-CHARACTER coloring driven by the line's Zone. The `%`
        // background characters render as SKY (Blue) at every column,
        // while non-`%` content characters render with the zone's
        // content color (clouds white, trees green, rock gray,
        // waterfall cyan, pool blue, etc.).
        //
        // Per-zone "object index" rotation: each contiguous run of
        // non-`%` chars within a tree/rock/waterfall row gets a
        // slightly different shade — alternating tree leaves
        // Green/DarkGreen, rocks Gray/DarkGray, waterfall Cyan/DarkCyan
        // — so individual trees/rocks/streams look distinct instead
        // of blending into one solid blob.
        //
        // Zone enum tells RenderBigLine what to do with the
        // non-background characters at each position. The sky color
        // for `%` fills is uniform Blue across the whole image so the
        // floating island reads as suspended in clear sky.
        //
        // Line zones (top-to-bottom):
        //   Lines 1-5: empty sky → all Sky
        //   Lines 6-11: cloud forming top-left → Cloud (white/gray content)
        //   Lines 12-22: trees on island top → TreeRow (green leaves + brown trunks)
        //   Lines 23-30: grass + dirt surface → Grass (greens + dirt darkyellow)
        //   Lines 31-50: rocky island body → Rock (gray)
        //   Lines 51-60: waterfall streams → Waterfall (cyan)
        //   Lines 61-67: pool / mist at base → Pool (blue + spray)
        //   Lines 68+: bottom sky → all Sky
        private enum Zone
        {
            Sky,         // empty — all `%` rendered as sky
            Cloud,       // (unused now — kept for future banners that DO have clouds)
            TreeCanopy,  // pure-green tree leaves at the very top (no trunk chars)
            TreeRow,     // tree leaves + visible trunks (trunks render brown)
            Grass,       // dirt + grass top of island
            Rock,        // rocky island underside
            Waterfall,   // falling water streams
            Pool,        // pool / mist at base
        }

        private static readonly (string text, Zone zone)[] s_bigLines = new (string, Zone)[]
        {
            ("", Zone.Sky),

            // ── Empty sky (top) — all `%` rendered as Sky (Blue)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),

            // ── Tree canopy at the very top of the island — these are
            //    the leafy crown of the tallest tree, NOT clouds. Rendered
            //    as alternating Green/DarkGreen leaves with no trunk
            //    chars (TreeCanopy zone treats every non-`%` as foliage).
            ("%%%%%%%%%%%%%%%%%%%%%%#=:..........:+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),
            ("%%%%%%%%%%%%%%%%%%%%%%-.................:+#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),
            ("%%%%%%%%%%%%%%%%%%%%%#*+-...............-=#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),
            ("%%%%%%%%%%%%%%%%*-:............:*#####%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),
            ("%%%%%%%%%%%%%%%=...........................-#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),
            ("%%%%%%%%%%%%%%%%%#=-:.......................:#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeCanopy),

            // ── Trees + island top emerging
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%#:*%#.......::-=*%%%#+-==*%%#=-+#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%*-::::::-+%%.=%%%==%%%%%%+-:--=%%=....#%%-..+%%-::-+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%+........=%%+.*%#.+%%%%%.......*%#....-%%#..:#%=....:#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%#+-....................:#%%%%*.......+%%:...:#%%:..#%+.....#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%=........... ..........=%%%%%*.......+%%:...:#%%-..#%*.....#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),

            // ── Dense tree row (cluster on top of island grass)
            ("%%%%%%%%%%%%##**+::+++.-**#%%-.-%%%%%%+.......+%%:...:#%%-..*%*...:*%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%%.-%#.+%%%#:.:%%%%%+....... .+%%-...:*%%=..*%#:..=%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%%%=...-#%%:.:%%%%%#..........+%%-....*%%=..+%#:..=%%%%%%%**+.........*%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%%%%%*=..=-..*%%%%%#..........+%%-....*%%+..+%#-..=%%%%%%%#++==-.....:=+*%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%%%%%%%#:...-%%%%%%+..........+%%-....*%%+..=%#-...*%%%%#-::...............%%%%%%%%%", Zone.TreeRow),

            // ── Island grass + dirt surface (transitions to terrain)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%:..=%%%%-............+%%-....*%%+..-%%-....*%%%%%+-..............=%%%%%%%%%", Zone.Grass),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%=..=%%#:.............+%%-....*%%+..-%%-....*%#%%%%%%%%*::...........:=#%%%%", Zone.Grass),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%*..-%%#...:..........+%%-....*%%*..:%%=....-.+%**%%%%%%#-.==-.----==+*%%%%%", Zone.Grass),
            ("%%%%%%%%%%%%%%%%%%%%%=#%%+..:#%%%%%%%%%+......*%%-....+%%*..:%%=......*.+%%%%%%%%--%#:+%%%%%%%%%%%%%", Zone.Grass),
            ("%%%%%%%%%%%%%%%%%%**#%.-%:...*%%%*-:-+%%%=....*%%-....+%%*..:%%=.........##+:=%%*...-#%%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%%%#:.:.=...+%%*-...*=.*%%=::-*%%=:::-*%%#::-%%+:.............*%+.:%#=+%%%%%%%%%%%%%", Zone.TreeRow),
            ("%%%%%%%%%%%%%%%%%#+-.......-%%=........:%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%####%%+..=.:-*%%%%%%%%%%%%", Zone.Grass),
            ("%%%%%%%%%%%%%%%%%#=-:......:==:.........--===---==------------------------------:.....+%%%%%%%%%%%%%", Zone.Rock),

            // ── Island rocky underside (the inverted-triangle dirt base)
            ("%%%%%%%%%%%%%%%+......................................-+*##%%%%%%+:...:*+:..:++:.........+%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%*................................................-*%%+....+%%-..-%#=:::--=+#%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%*.........................:-=++++++++++++++++******%%%:....#%#:..+%%%%%%*=:=%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%#-................................:-=+#%%%%%%%%%%%%%%%-....*%%=..:%%-.....:#%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%#-...................................:%#-.........+%%-....+%%=..:%%=....#%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%+=............:....................-...........*%%-....=%%=..:%%+...-%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%#:...........+-...............................*%%-....-%%=..:%%*..-%%%%%%%%%%%%%%", Zone.Cloud),
            ("%%%%%%%%%%%%%%%%%%%#:...........-##-.............................*%%-....:%%+..:%%#=#%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%=.............+#%%%%#+:......................#%%-....:%%+..:#%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%+..............:--=+#%#:....................#%%-....:%%+..:#%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%#=..................*%#:...................#%%:.....%%*..:#%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%*..............*%%%*=-:..............#%%:.....%%*..:*%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%*................+*#####*+:..........#%%-.....%%*...*%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%:...................................#%%:.....%%*...+%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%#.................................-*%%%:.....%%#:..+%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%+..............................-%%%%%:.....%%#:..+%%%%%%%%%%%%%%%%%%%", Zone.Rock),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%*==+*=......................+%%%%%:.....%%#:..+%%%%%%%%%%%%%%%%%%%", Zone.Rock),

            // ── Waterfall streams (the vertical-line patterns)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%+..................=*%%%%%%%:..::.%%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%#-............:*#%%%%%%%%%%:..-:.%%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%=..............+%%%%%%%%:..=-.%%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%#=:........-*%%%%%%%%%:..=-.#%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%#*+++*%%%%%%%%%%%%:..+-.#%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%:..+-.#%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%:..*=.*%%:..=%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%#:..#+.*%%:..-%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),

            // ── Pool / mist at base (the wider water blob)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%..............-#%%%%%#-...:#%%%%%%#:..............+%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%.............-%%%%%%#......:#%%%%*...............*%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%....*########%%%%%%+.........*%%%******....=#***#%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%....---------=#%%%+....=%:....+%%%%%%%%....+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%.............*%%%=....+%%%=....=%%%%%%%....+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%....=++++===#%%#:....*%%%%%=....=%%%%%%....+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%....*%%%%%%%%%#:....#%+----:.....-%%%%%....+%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%....*%%%%%%%%*....:#%-............:#%%%.   +%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Waterfall),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%****#%%%%%%%%***+*#%*++*************%%%****#%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Pool),

            // ── Bottom sky (sky/ocean horizon below floating island)
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),
            ("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", Zone.Sky),

            ("", Zone.Cloud),
        };

        // ─────────────────────────────────────────────────────────────
        //  Write paths
        // ─────────────────────────────────────────────────────────────

        private static bool TryWriteSegmented(Segment[][] lines)
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();
                foreach (var line in lines)
                {
                    foreach (var seg in line)
                    {
                        s_setConsoleColor(seg.Color);
                        stream.Write(seg.Text);
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        private static void WritePlainFallbackSegmented(Segment[][] lines)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var line in lines)
                {
                    sb.Clear();
                    foreach (var seg in line) sb.Append(seg.Text);
                    FiresLogger.LogInfo(sb.ToString());
                }
            }
            catch { }
        }

        // Picks the color for a single character inside a line, given
        // the line's Zone and the per-line "object index" (which
        // increments each time we transition from `%` background to a
        // non-`%` content run — so consecutive trees/rocks/streams get
        // alternating shades for variety).
        //
        // The `%` background always renders Sky (Blue) regardless of
        // zone, so the entire image floats over a consistent sky
        // backdrop. Non-`%` content takes the zone's color, optionally
        // varied by objectIndex % 2 for the zones that have a two-tone
        // palette (TreeRow / Rock / Waterfall / Pool).
        private static ConsoleColor ColorForChar(Zone zone, char c, int objectIndex)
        {
            if (c == '%') return Sky; // background sky is uniform

            switch (zone)
            {
                case Zone.Sky:
                    // Should be rare — content chars in a sky-zone line.
                    return Sky;
                case Zone.Cloud:
                    // Cloud puffs: alternate dense (White) and wispy (Gray).
                    return (c == '#' || c == '+') ? Cloud : CloudDim;
                case Zone.TreeCanopy:
                    // Pure leaf canopy at the very top of the island — no
                    // trunk-char override (unlike TreeRow which paints
                    // `*:-` brown). All content alternates Green / DarkGreen
                    // per object blob for variegated foliage.
                    return (objectIndex % 2 == 0) ? TreeLight : TreeDark;
                case Zone.TreeRow:
                    // Tree leaves: alternate Green / DarkGreen per tree blob.
                    // Trunk-like chars (`*` `:` `-`) hint at trunks → use
                    // DarkYellow for the woody tones.
                    if (c == '*' || c == ':' || c == '-')
                        return Grass; // trunk brown
                    return (objectIndex % 2 == 0) ? TreeLight : TreeDark;
                case Zone.Grass:
                    // Grass + dirt: `+` `=` are dirt, `#` `*` are grass tufts.
                    if (c == '+' || c == '=' || c == '-')
                        return Grass; // dirt brown
                    return TreeDark; // grass tuft
                case Zone.Rock:
                    // Alternate DarkGray and Gray per rock blob for chunky
                    // boulder texture.
                    return (objectIndex % 2 == 0) ? Rock : RockHi;
                case Zone.Waterfall:
                    // Alternate Cyan / DarkCyan per stream segment.
                    return (objectIndex % 2 == 0) ? Water : WaterDim;
                case Zone.Pool:
                    // Pool water: Cyan/DarkCyan alternating, with Gray for
                    // the `*` `+` froth-spray particles.
                    if (c == '*' || c == '+')
                        return CloudDim; // foam
                    return (objectIndex % 2 == 0) ? Water : WaterDim;
                default:
                    return CloudDim;
            }
        }

        // Per-character renderer for the big banner. For each line:
        //   1. Walk char-by-char
        //   2. Track an "object index" that increments at every
        //      sky→content boundary (so individual tree/rock blobs get
        //      alternating shades)
        //   3. Buffer consecutive same-color chars into segments and
        //      emit each segment with one SetConsoleColor + Write call
        //      (vastly cheaper than per-character SetConsoleColor)
        //   4. WriteLine to close the row
        private static bool TryWriteColoredBig()
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();

                var sb = new StringBuilder(128);
                foreach (var line in s_bigLines)
                {
                    string text = line.text;
                    Zone zone = line.zone;

                    if (string.IsNullOrEmpty(text))
                    {
                        stream.WriteLine();
                        continue;
                    }

                    int objectIndex = -1;
                    bool prevSky = true; // start "in sky" so first non-% bumps to objectIndex 0
                    ConsoleColor pendingColor = Sky;
                    sb.Length = 0;

                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        bool isSky = (c == '%');

                        // Object-index bump on sky→content transition.
                        if (!isSky && prevSky)
                            objectIndex++;
                        prevSky = isSky;

                        ConsoleColor color = ColorForChar(zone, c, objectIndex);

                        // Flush the pending segment when the color changes.
                        if (sb.Length == 0)
                        {
                            pendingColor = color;
                        }
                        else if (color != pendingColor)
                        {
                            s_setConsoleColor(pendingColor);
                            stream.Write(sb.ToString());
                            sb.Length = 0;
                            pendingColor = color;
                        }
                        sb.Append(c);
                    }

                    // Flush whatever's left, then close the row.
                    if (sb.Length > 0)
                    {
                        s_setConsoleColor(pendingColor);
                        stream.Write(sb.ToString());
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        private static void WritePlainFallbackBig()
        {
            try
            {
                foreach (var line in s_bigLines)
                    FiresLogger.LogInfo(line.text);
            }
            catch { }
        }

        // BepInEx.ConsoleManager reflection. Same pattern as
        // VAFapBanner / VAGhettoBanner — see those classes for the
        // rationale on why we go through reflection.
        private static void EnsureReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;
            try
            {
                var asm = typeof(BepInEx.Logging.ConsoleLogListener).Assembly;
                var consoleManagerType = asm.GetType("BepInEx.ConsoleManager", throwOnError: false);
                if (consoleManagerType == null) return;

                var streamProp = consoleManagerType.GetProperty("ConsoleStream",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (streamProp != null)
                {
                    var getMethod = streamProp.GetGetMethod(nonPublic: true);
                    if (getMethod != null)
                    {
                        s_consoleStreamGetter = (Func<object>)Delegate.CreateDelegate(
                            typeof(Func<object>), getMethod);
                    }
                }

                var setColorMethod = consoleManagerType.GetMethod("SetConsoleColor",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(ConsoleColor) },
                    modifiers: null);
                if (setColorMethod != null)
                {
                    s_setConsoleColor = (Action<ConsoleColor>)Delegate.CreateDelegate(
                        typeof(Action<ConsoleColor>), setColorMethod);
                }
            }
            catch { /* leave delegates null — caller falls back */ }
        }
    }
}
