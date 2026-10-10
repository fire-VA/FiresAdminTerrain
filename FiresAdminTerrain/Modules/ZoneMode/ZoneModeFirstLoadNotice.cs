using HarmonyLib;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A converted world's first load blocks the game for minutes: vanilla upgrades an older save, then the park sets every
    // explored zone aside, and nothing can draw while either runs, so it looks like a hang. Starting such a world from the
    // menu first says so (Fire, 2026-09-27: "a loading message ... to let people know its going to take a bit and they
    // are not frozen"). Start goes on as vanilla would; the popup shows only for a world whose next load does that work.
    internal static class ZoneModeFirstLoadNotice
    {
        private const string Header = "First load takes a while";
        private const string ParkNotice =
            "'{0}' is set up one zone at a time on this first load: Valheim upgrades the old save, then every zone it has " +
            "explored is set aside, to come back as it was when you open it. On a large world the screen FREEZES for a few " +
            "minutes (about four on a big Better Continents map) and Windows may call the game \"Not Responding\". It is " +
            "working: do not close the game.\n\nOnce you are in, log out normally once so the converted world is saved; " +
            "after that it loads quickly.\n\nStart now?";
        private const string UpgradeNotice =
            "'{0}' is an older save, and Valheim upgrades it on this first load. On a large world that takes a few minutes, " +
            "and Windows may call the game \"Not Responding\" meanwhile. It is working: leave it running.\n\nStart now?";

        private static readonly AccessTools.FieldRef<FejdStartup, World> SelectedWorldRef =
            AccessTools.FieldRefAccess<FejdStartup, World>("m_world");

        private static bool s_confirmed;

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnWorldStart))]
        private static class AskBeforeLongLoad
        {
            private static bool Prefix(FejdStartup __instance)
            {
                if (s_confirmed)
                {
                    s_confirmed = false;
                    return true;
                }
                var world = SelectedWorldRef(__instance);
                string notice = NoticeFor(world);
                if (notice == null) return true;
                FiresLogger.LogInfo($"[FiresZones] '{world.m_name}': asked before its long first load.");
                UnifiedPopup.Push(new YesNoPopup(Header, notice, () =>
                {
                    UnifiedPopup.Pop();
                    s_confirmed = true;
                    __instance.OnWorldStart();
                }, UnifiedPopup.Pop, localizeText: false));
                return false;
            }
        }

        private static string NoticeFor(World world)
        {
            if (world == null || world.m_dataError != World.SaveDataError.None) return null;
            if (ZoneModePark.ConvertsOnNextLoad(world)) return string.Format(ParkNotice, world.m_name);
            if (!world.IsChunkedSave() && FiresWorldProfile.Read(world.m_name)?.IsClone == true)
                return string.Format(UpgradeNotice, world.m_name);
            return null;
        }
    }
}
