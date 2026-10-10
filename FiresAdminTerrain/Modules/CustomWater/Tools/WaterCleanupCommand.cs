using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // `va_water_cleanup_spread` (0.2.755; Fire 10-06: "Cleanup command"; springs "turned out to be a perf nightmare"). Removes the
    // FiresLiquidWater pours (springs and water volumes, with all the water they spread) from the current world, on the SERVER,
    // where every ZDO is held, so pours in unloaded zones go too; the removal syncs like any destroyed object. Placed water
    // (Vpond / Vpool / Vriver / Vpour disks, Vfall curtains) and the generated rivers and lakes are not pours and are never touched.
    //   va_water_cleanup_spread [dry] [kind spring|volume|all] [near <x> <z> <r>] [keeptool]
    // Fire 10-06: "the cleanup command just cleans up ... it can clean existing pours i dont care, these are all test worlds".
    // So it removes them at once on any world; `dry` only lists what it would remove. Spring/volume placements made from 0.2.755
    // on carry ZDO "va_pour_origin" = "tool", which `keeptool` keeps. Counts per kind are logged (Info: an admin command's
    // result) and printed to the asking console.
    internal static class WaterCleanupCommand
    {
        internal const string Command = "va_water_cleanup_spread";
        internal const string OriginKey = "va_pour_origin";
        private const string AskRpc = "FAT_WaterCleanupAsk", ReplyRpc = "FAT_WaterCleanupReply";
        private const int ListMax = 60;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        private static bool s_registered;
        private static ZRoutedRpc s_rpcFor;

        internal static void RegisterCommand()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "[dry] [kind spring|volume|all] [near <x> <z> <r>] [keeptool] - remove the liquid pours (springs / water volumes and the water "
                + "they spread) on this world, server-side; dry only lists them.",
                args =>
                {
                    string line = string.Join(" ", args.Args.Skip(1).ToArray());
                    if (ZNet.instance == null) { args.Context?.AddString(Command + ": join a world first."); return; }
                    if (ZNet.instance.IsServer()) { foreach (var l in Run(line).Split('\n')) args.Context?.AddString(l); return; }
                    if (ZRoutedRpc.instance == null) { args.Context?.AddString(Command + ": no connection to the server."); return; }
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, line);
                    args.Context?.AddString(Command + ": asked the server; its answer follows.");
                });
        }

        // On every ZNet start (CustomWaterPatches, beside WaterOperationRPC).
        internal static void EnsureRpcs()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(s_rpcFor, ZRoutedRpc.instance)) return;
            s_rpcFor = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<string>(AskRpc, OnAsk);
            ZRoutedRpc.instance.Register<string>(ReplyRpc, OnReply);
        }

        private static void OnAsk(long sender, string line)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string result = AdminCheck.IsAdmin(sender) ? Run(line) : Command + " refused: not an admin.";
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, result);
        }

        private static void OnReply(long sender, string result)
        {
            foreach (var l in result.Split('\n')) global::Console.instance?.AddString(l);
        }

        // The server's work. Returns the report (first line: the counts).
        private static string Run(string line)
        {
            var words = (line ?? "").ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            bool apply = true, keepTool = false;
            string kind = "all";
            Vector3? near = null;
            float nearR = 0f;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w == "apply") apply = true;
                else if (w == "dry") apply = false;
                else if (w == "anyworld") { }   // accepted for older notes; every world is cleaned
                else if (w == "keeptool") keepTool = true;
                else if (w == "kind" && i + 1 < words.Length && (words[i + 1] == "spring" || words[i + 1] == "volume" || words[i + 1] == "all")) kind = words[++i];
                else if (w == "near" && i + 3 < words.Length
                         && float.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                         && float.TryParse(words[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                         && float.TryParse(words[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) { near = new Vector3(x, 0f, z); nearR = r; i += 3; }
                else return $"{Command}: '{w}' is not an argument. {Command} [dry] [kind spring|volume|all] [near <x> <z> <r>] [keeptool]";
            }
            string world = ZNet.instance.GetWorldName() ?? "?";
            if (ZDOMan.instance == null) return $"{Command}: no ZDOs on this peer.";

            int prefab = FiresLiquidWaterPrefab.PrefabName.GetStableHashCode();
            var picked = new List<ZDO>();
            int springs = 0, volumes = 0, keptTool = 0, keptKind = 0, keptFar = 0;
            foreach (var zdo in ObjectsById(ZDOMan.instance).Values.ToList())
            {
                if (zdo == null || zdo.GetPrefab() != prefab) continue;
                bool spring = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_RATE, 0f) > 0f;
                if (kind == "spring" && !spring || kind == "volume" && spring) { keptKind++; continue; }
                if (near.HasValue)
                {
                    Vector3 p = zdo.GetPosition();
                    if (new Vector2(p.x - near.Value.x, p.z - near.Value.z).magnitude > nearR) { keptFar++; continue; }
                }
                if (keepTool && zdo.GetString(OriginKey, "") == "tool") { keptTool++; continue; }
                picked.Add(zdo);
                if (spring) springs++; else volumes++;
            }

            var sb = new StringBuilder();
            sb.Append($"{Command} {(apply ? "APPLY" : "dry")} on '{world}': {picked.Count} pour(s) {(apply ? "removed" : "would go")} ({springs} spring(s), {volumes} water volume(s))");
            sb.Append($"; kept: {keptKind} of another kind, {keptFar} outside the area, {keptTool} tagged tool placement(s)");
            int listed = 0;
            foreach (var zdo in picked.OrderBy(z => z.GetPosition().x).ThenBy(z => z.GetPosition().z))
            {
                if (listed++ >= ListMax) { sb.Append($"\n  ... and {picked.Count - ListMax} more"); break; }
                Vector3 p = zdo.GetPosition();
                bool spring = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_RATE, 0f) > 0f;
                sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  {0} at ({1:0},{2:0.0},{3:0}) r {4:0.0} level {5:0.0}{6}{7}",
                    spring ? "spring" : "volume", p.x, p.y, p.z, zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_RADIUS, 0f),
                    zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_FILL, p.y), zdo.GetInt(FiresLiquidGridOps.ZDO_SPRING_FROZEN, 0) != 0 ? " frozen" : "",
                    zdo.GetString(OriginKey, "") is string o && o.Length > 0 ? " origin " + o : ""));
            }
            if (apply)
                foreach (var zdo in picked) ZDOMan.instance.DestroyZDO(zdo);
            string report = sb.ToString();
            FiresLogger.LogInfo("[FiresWater] " + report.Replace("\n", " |"));
            return report;
        }

        // ── the spring rule (Fire 10-06): springs are a deliberate, small-batch placement ───────────────────────────────────
        internal const int SaneSpringTotal = 24;

        // A tool placement (va_brush_water_spring / va_brush_water_volume) is tagged so this cleanup can keep it, and a world
        // past SaneSpringTotal loaded pours says so.
        internal static void TagToolPour(GameObject go)
        {
            if (go == null) return;
            var nv = go.GetComponentInChildren<ZNetView>(true);
            if (nv != null && nv.IsValid()) nv.GetZDO().Set(OriginKey, "tool");
            int loaded = Object.FindObjectsByType<FiresLiquidRiverController>(FindObjectsSortMode.None).Length;
            if (loaded > SaneSpringTotal)
            {
                string warn = $"{loaded} liquid pours are loaded here (more than {SaneSpringTotal}): pours cost every frame - draw rivers and ponds with the V-tools' water instead.";
                FiresLogger.LogWarning("[FiresWater] " + warn);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, warn, 0, null, false);
            }
        }
    }
}
