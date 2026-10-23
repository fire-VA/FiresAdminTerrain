using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace VerdantsAscent.Terrain.Voxel
{
    // More stats for the installed mods:
    // FAT's card in the Core hub's Stats tab (order band 300). By name, so a Core older than
    // 0.2.510 (no FiresShell.RegisterStatsSection / StatRow) just skips it. Clients only (Core no-ops on a dedi). Rows are cheap reads
    // of counters FAT already keeps; Core calls them only while the tab shows.
    internal static class FatStatsSection
    {
        private static Type s_rowType, s_stateType;
        private static ConstructorInfo s_rowCtor;

        internal static void TryRegister()
        {
            try
            {
                var shell = Type.GetType("FiresCore.UI.Shell.FiresShell, FiresUnifiedCore");
                s_rowType = Type.GetType("FiresCore.UI.Shell.StatRow, FiresUnifiedCore");
                s_stateType = Type.GetType("FiresCore.UI.Shell.StatState, FiresUnifiedCore");
                if (shell == null || s_rowType == null || s_stateType == null) { FiresLogger.LogDebug("[FatStats] Core has no stats sections API (needs 0.2.510+): FAT card skipped"); return; }
                s_rowCtor = s_rowType.GetConstructor(new[] { typeof(string), typeof(string), s_stateType, typeof(string) });
                var listOfRow = typeof(IList<>).MakeGenericType(s_rowType);
                var funcType = typeof(Func<>).MakeGenericType(listOfRow);
                var register = shell.GetMethod("RegisterStatsSection", new[] { typeof(string), typeof(string), typeof(int), funcType, typeof(float) });
                if (s_rowCtor == null || register == null) { FiresLogger.LogWarning("[FatStats] stats API shape differs from the spec: FAT card skipped"); return; }
                // () => (IList<StatRow>)Rows()
                var body = Expression.Convert(Expression.Call(typeof(FatStatsSection).GetMethod(nameof(Rows), BindingFlags.NonPublic | BindingFlags.Static)), listOfRow);
                var fn = Expression.Lambda(funcType, body).Compile();
                register.Invoke(null, new object[] { "fat", "Terrain (FAT)", 300, fn, 1f });
                FiresLogger.LogInfo("[FatStats] FAT stats section registered (order 300, every 1 s)");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatStats] register failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // state: 0 Normal, 1 Good, 2 Warn, 3 Bad, 4 Muted (the spec's StatState values)
        private static object Row(string label, string value, int state = 0, string tip = null)
            => s_rowCtor.Invoke(new object[] { label, value, Enum.ToObject(s_stateType, state), tip });

        private static object Rows()
        {
            var inv = CultureInfo.InvariantCulture;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(s_rowType));
            list.Add(Row("Wrapped zones", VoxelZoneStreamer.WrappedZoneCount.ToString("N0", inv)));
            list.Add(Row("Voxel chunks", VoxelWorld.ChunkCount.ToString("N0", inv), 0, "all voxel chunks held, uniform ones included"));
            list.Add(Row("Full chunks", VoxelWorld.BudgetedChunkCount.ToString("N0", inv), 0, "chunks with a mesh (the RAM governor's budget)"));
            list.Add(Row("Wrap radius", VoxelZoneStreamer.GovernorRadius.ToString("0", inv) + " m", 0, "the RAM governor eases this down when Full chunks run over budget"));
            list.Add(Row("Heal queue", VoxelZoneStreamer.PriorityHealCount.ToString("N0", inv), VoxelZoneStreamer.PriorityHealCount > 50 ? 2 : 0, "chunk-border re-mirrors waiting"));
            int g = VoxelZoneStreamer.GhostCount;
            list.Add(Row("Ghost zones", g.ToString("N0", inv), g == 0 ? 4 : 0, "zones generated ahead of players this session (server/host only)"));
            if (g > 0)
            {
                double avg = VoxelZoneStreamer.GhostMs / g, max = VoxelZoneStreamer.GhostMaxMs;
                list.Add(Row("Ghost ms avg / max", avg.ToString("0.0", inv) + " / " + max.ToString("0", inv), max > 100 ? 2 : 0, "one ghost zone spawn; max > 100 ms = a visible hitch"));
            }
            return list;
        }
    }
}
