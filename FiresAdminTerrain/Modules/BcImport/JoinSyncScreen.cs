using TMPro;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // What the join sync is doing, on the loading screen (Fire, 2026-09-28: "make sure that the send before join is
    // accompanied by an on-screen message explaining that it is loading the cache"). Vanilla's loading tip shows only once
    // the game waits for the spawn, after the sync, so this is a line of its own on Hud.m_loadingScreen: a copy of the tip's
    // text object, so it looks like the game's own. Every change of what it says is logged ("[BcImport] on-screen: ...");
    // a headless peer logs the same lines and draws nothing. Main thread only.
    internal static class JoinSyncScreen
    {
        private const string LineName = "FiresJoinSyncLine";
        private const float RefreshSeconds = 0.25f;
        private const double BytesPerMegabyte = 1024d * 1024d;
        private const float MinimumSecondsForEstimate = 1f;
        private const int PercentScale = 100;

        private static TMP_Text s_line;
        private static string s_kind;
        private static float s_nextRefresh;

        // A download under way: what it is, how far, and roughly how long is left.
        internal static void ShowDownload(string what, long received, long total, float elapsedSeconds)
        {
            string kind = "downloading " + what;
            bool changed = kind != s_kind;
            if (!changed && Time.realtimeSinceStartup < s_nextRefresh) return;
            s_nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
            double totalMb = total / BytesPerMegabyte;
            string text = $"Downloading this world's {what} ({totalMb:0} MB): first join only, later joins load it from this PC.\n"
                + $"{received / BytesPerMegabyte:0} / {totalMb:0} MB ({(total > 0 ? received * PercentScale / total : 0)}%)"
                + TimeLeft(received, total, elapsedSeconds);
            Show(kind, text, changed);
        }

        // Nothing to download: this PC's copy is loading, which blocks the game for a few seconds, so the line is drawn
        // before that starts and stays up through it.
        internal static void ShowLoading(string what)
        {
            string kind = "loading " + what;
            if (kind == s_kind) return;
            Show(kind, $"Loading this world's {what} from this PC...", true);
        }

        internal static void Clear()
        {
            if (s_kind == null) return;
            FiresLogger.LogInfo($"[BcImport] on-screen: cleared ({s_kind}).");
            s_kind = null;
            if (s_line != null) s_line.gameObject.SetActive(false);
        }

        private static void Show(string kind, string text, bool changed)
        {
            s_kind = kind;
            bool drawn = TryDraw(text, out string why);
            if (changed) FiresLogger.LogInfo($"[BcImport] on-screen: {text.Replace('\n', ' ')}" + (drawn ? "" : $" ({why})"));
        }

        private static bool TryDraw(string text, out string why)
        {
            why = null;
            if (FiresCore.Lifecycle.FiresMod.IsHeadless)
            {
                why = "no screen: headless";
                return false;
            }
            if (s_line == null && !TryCreateLine(out why)) return false;
            s_line.text = text;
            s_line.gameObject.SetActive(true);
            return true;
        }

        // A copy of the loading tip, placed where the tip would be, on the loading screen itself rather than inside the
        // tip panel vanilla hides until the spawn wait.
        private static bool TryCreateLine(out string why)
        {
            why = null;
            var hud = Hud.instance;
            if (hud == null || hud.m_loadingScreen == null || hud.m_loadingTip == null)
            {
                why = "no loading screen yet";
                return false;
            }
            var copy = Object.Instantiate(hud.m_loadingTip.gameObject, hud.m_loadingScreen.transform, true);
            copy.name = LineName;
            s_line = copy.GetComponent<TMP_Text>();
            if (s_line == null)
            {
                Object.Destroy(copy);
                why = "the loading tip has no text";
                return false;
            }
            return true;
        }

        private static string TimeLeft(long received, long total, float elapsedSeconds)
        {
            if (received <= 0 || elapsedSeconds < MinimumSecondsForEstimate || received >= total) return "";
            double bytesPerSecond = received / (double)elapsedSeconds;
            return $", about {(total - received) / bytesPerSecond:0} s left";
        }
    }
}
