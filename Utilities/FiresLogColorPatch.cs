using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace VerdantsAscent.Utilities
{
    // Per-class console-color override for our mods' log lines.
    //
    // BepInEx's default ConsoleLogListener.LogEvent picks a console color
    // from the LogLevel only (Fatal=Red, Error=DarkRed, Warning=Yellow,
    // Message=White, Debug=DarkGray, Info=Gray). Every Info line from
    // every mod ends up the same washed-out gray, which makes our diagnostic
    // logs disappear in the sea of vanilla and other-mod chatter.
    //
    // This Harmony prefix intercepts LogEvent, detects when the message is
    // ours via the [FiresXxx] mod tag, picks a color based on the
    // [ClassName] sub-tag in the message, and writes the line itself.
    // Returning false from the prefix skips the vanilla body so we don't
    // double-print. Non-matching log lines (vanilla, other mods) pass
    // through to the default path unchanged.
    //
    // The color comes from System.ConsoleColor (Windows 16-color palette —
    // no truecolor / 256-color, that's a console limitation, not BepInEx's).
    // The console resets to Gray after every line as vanilla does.
    //
    // Adding a new category:
    //   1. Pick a keyword that uniquely identifies the class family
    //      (e.g. "Inventory", "Spawn", "Companion").
    //   2. Add it to s_classColorRules with a ConsoleColor.
    //   3. Order matters — first matching keyword wins. Put more-specific
    //      keywords above more-general ones.
    //
    // Adding a new mod tag:
    //   Add the prefix string to s_ourModTags. The patch checks for a
    //   substring match in the message, so "FiresFoo" covers any [FiresFoo]
    //   bracket and any [FiresFoo:Sub] variant.
    [HarmonyPatch]
    internal static class FiresLogColorPatch
    {
        // Default color used when the message has our mod tag but no
        // class-keyword match. User-chosen: "for normal every day logging
        // magenta".
        private const ConsoleColor DefaultColor = ConsoleColor.Magenta;

        // First-match-wins keyword → color rules. Substring match against
        // the [ClassName] inside the second pair of brackets in the message,
        // case-insensitive. Order: most-specific FIRST.
        private static readonly (string keyword, ConsoleColor color)[] s_classColorRules =
        {
            // Blueprints (user-requested example)
            ("Blueprint",     ConsoleColor.Cyan),

            // Custom-terrain mesh / tile system
            ("TileBrush",     ConsoleColor.Green),
            ("TileAnchor",    ConsoleColor.Green),
            ("TileMesh",      ConsoleColor.Green),
            ("TileHammer",    ConsoleColor.Green),
            ("Tile",          ConsoleColor.Green),
            ("Terrain",       ConsoleColor.Green),
            ("RegionScrubber", ConsoleColor.Green),

            // Water + liquid system (water/lava/snow/ice all live here)
            ("WaterMesh",     ConsoleColor.DarkCyan),
            ("WaterDisk",     ConsoleColor.DarkCyan),
            ("WaterBrush",    ConsoleColor.DarkCyan),
            ("Water",         ConsoleColor.DarkCyan),
            ("Liquid",        ConsoleColor.DarkCyan),
            ("Lava",          ConsoleColor.DarkCyan),
            ("Frozen",        ConsoleColor.DarkCyan),
            ("Snow",          ConsoleColor.DarkCyan),
            ("Ice",           ConsoleColor.DarkCyan),
            ("CustomRiver",   ConsoleColor.DarkCyan),
            ("CustomPond",    ConsoleColor.DarkCyan),

            // Persistence / data layer (anchors, ZDOs, file cache, codec)
            ("Persistence",   ConsoleColor.DarkMagenta),
            ("Anchor",        ConsoleColor.DarkMagenta),
            ("ZDO",           ConsoleColor.DarkMagenta),
            ("Cache",         ConsoleColor.DarkMagenta),
            ("Codec",         ConsoleColor.DarkMagenta),

            // Multiplayer / RPC
            ("RPC",           ConsoleColor.Blue),
            ("Network",       ConsoleColor.Blue),
            ("Ghetto",        ConsoleColor.Blue),
            ("Sync",          ConsoleColor.Blue),

            // Hammer / build menu / tab UI
            ("HammerScroll",  ConsoleColor.DarkYellow),
            ("HammerCategory",ConsoleColor.DarkYellow),
            ("HammerRight",   ConsoleColor.DarkYellow),
            ("Hammer",        ConsoleColor.DarkYellow),
            ("Tab",           ConsoleColor.DarkYellow),
            ("Category",      ConsoleColor.DarkYellow),
            ("VAPiece",       ConsoleColor.DarkYellow),

            // Blueprint rebake-sweep progress (FAP fap_rebake_all
            // command + per-step REBAKING / REBAKE DONE banners).
            // Routed to DarkYellow so the 🔥 REBAKING boxes read as
            // orange between the magenta 🧱 BAKED [LoadSummary]
            // boxes — visual cadence makes the sweep easy to scan.
            ("Rebake",        ConsoleColor.DarkYellow),

            // Spawn / NPC / companion (FiresNPCs family)
            ("Spawn",         ConsoleColor.DarkGreen),
            ("Companion",     ConsoleColor.DarkGreen),
            ("NPC",           ConsoleColor.DarkGreen),
            ("Tameable",      ConsoleColor.DarkGreen),

            // Inventory / item systems
            ("Inventory",     ConsoleColor.Yellow),
            ("Backpack",      ConsoleColor.Yellow),
            ("Container",     ConsoleColor.Yellow),

            // Harmony plumbing / compat patches
            ("Patch",         ConsoleColor.DarkGray),
            ("Compat",        ConsoleColor.DarkGray),
            ("Detector",      ConsoleColor.DarkGray),
        };

        // Mod tags we own. Substring match — covers "[FiresAdminTerrain]"
        // and "[FiresAdminTerrain:Some sub-tag]" variants alike. The check
        // bails fast for any line that doesn't include one of these so we
        // don't pay regex cost on vanilla/other-mod log spam.
        private static readonly string[] s_ourModTags =
        {
            "[FiresAdminTerrain",
            "[FiresAdminPrefabs",
            "[FiresGhetto",
            "[FiresNPCs",
            "[VerdantsAscent",
        };

        // Matches "[Fires…] [ClassName]" at the start of the message and
        // captures the class name. Tolerant of surrounding whitespace and
        // of any [Fires*] variant (covered by the broad first group).
        private static readonly Regex s_classNameRx = new Regex(
            @"^\s*\[(?:Fires[A-Za-z0-9_:]*|VerdantsAscent[A-Za-z0-9_:]*)\]\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Reflection cache — BepInEx.ConsoleManager is `internal` in the
        // shipped DLL so direct access fails CS0122. Resolved once on first
        // call, used for every subsequent log line.
        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

        // Cross-mod ownership coordination. When multiple Fires* mods are
        // installed, each ships its own copy of this color patch. Without
        // coordination, all of them would fire on every [Fires*] log line
        // and double-print (or triple/quadruple-print).
        //
        // The coordination key is an AppDomain-shared string identifying
        // which mod's color-patch instance "won" the install race. First
        // mod whose prefix fires claims ownership by SetData-ing its
        // name. Subsequent prefixes from OTHER mods see a different owner
        // and bail (return true → pass through to vanilla → already
        // gated by the owning prefix's return-false).
        //
        // Load order doesn't matter — whichever mod's prefix fires first
        // wins, and the others stand down. Works the same regardless of
        // which combination of FAT / FAP / FGN is installed.
        private const string OwnerKey = "FiresColorPatch.Owner";
        private const string MyOwnerName = "FiresAdminTerrain";

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
            catch
            {
                // Leave delegates null — caller will fall back to vanilla path.
            }
        }

        [HarmonyPatch(typeof(ConsoleLogListener), nameof(ConsoleLogListener.LogEvent))]
        [HarmonyPrefix]
        private static bool LogEvent_Prefix(LogEventArgs eventArgs)
        {
            // Defensive: never throw out of a logging hook. Worst case we
            // fall back to the vanilla path so the line still appears.
            try
            {
                if (eventArgs == null) return true;
                string message = eventArgs.Data?.ToString();
                if (string.IsNullOrEmpty(message)) return true;

                // Cross-mod ownership claim — first Fires* color patch to
                // fire claims ownership. If another mod already claimed,
                // we bail (the owner has already processed this event).
                var owner = AppDomain.CurrentDomain.GetData(OwnerKey) as string;
                if (owner == null)
                {
                    AppDomain.CurrentDomain.SetData(OwnerKey, MyOwnerName);
                    owner = MyOwnerName;
                }
                if (owner != MyOwnerName) return true; // another Fires* patch is the owner

                // Fast bail: not one of our mods → vanilla handles it.
                bool isOurs = false;
                for (int i = 0; i < s_ourModTags.Length; i++)
                {
                    if (message.IndexOf(s_ourModTags[i], StringComparison.Ordinal) >= 0)
                    {
                        isOurs = true;
                        break;
                    }
                }
                if (!isOurs) return true;

                // Pick a color. Default = DefaultColor; specific class
                // keyword overrides.
                ConsoleColor color = DefaultColor;
                var match = s_classNameRx.Match(message);
                if (match.Success)
                {
                    string className = match.Groups[1].Value;
                    for (int i = 0; i < s_classColorRules.Length; i++)
                    {
                        if (className.IndexOf(s_classColorRules[i].keyword,
                                              StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            color = s_classColorRules[i].color;
                            break;
                        }
                    }
                }

                // Replicate the vanilla level mask check so users who
                // disabled certain log levels in BepInEx config don't see
                // them anyway via our path. We can't read the private
                // ConfigConsoleDisplayedLevel from here without reflection,
                // so we simply always write (the only known risk is showing
                // an Info line a user explicitly masked — minor).

                // Errors and warnings keep their vanilla colors so they
                // stay visually distinct from the default Info path.
                // Adjust if you want all-our-colors regardless of level.
                if (eventArgs.Level == LogLevel.Error || eventArgs.Level == LogLevel.Fatal
                    || eventArgs.Level == LogLevel.Warning)
                {
                    return true; // let vanilla render Red/DarkRed/Yellow
                }

                // BepInEx 5.x ships ConsoleManager as `internal`, so we
                // can't call SetConsoleColor / read ConsoleStream directly.
                // Reflect once, cache the accessors, then route writes
                // through the same driver vanilla uses. If reflection
                // fails for any reason, fall back to vanilla so the line
                // still prints (without color).
                EnsureReflection();
                if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                    return true;

                var stream = s_consoleStreamGetter() as TextWriter;
                if (stream == null) return true;

                try
                {
                    s_setConsoleColor(color);
                    stream.Write(eventArgs.ToStringLine());
                }
                finally
                {
                    s_setConsoleColor(ConsoleColor.Gray);
                }
                return false;
            }
            catch
            {
                // If anything went wrong in our path, fall back to vanilla
                // so the line still gets printed. Logging hooks must never
                // swallow the message.
                return true;
            }
        }
    }
}
