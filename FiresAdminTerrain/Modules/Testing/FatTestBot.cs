using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Testing
{
    // FAT's side of the test bot's chat contract (Tools\FDT_TEST_BOT.md): a test shouts `bot <verb>` and waits for the
    // bot's shouted `bot: <reply>`. One Chat.OnNewChatMessage postfix (FatVanillaHooks) feeds every FAT test from here.
    internal static class FatTestBot
    {
        private const string VerbPrefix = "bot ";
        private const string ReplyPrefix = "bot: ";
        internal const float AnswerSeconds = 15f;
        internal const float ParkSeconds = 95f;

        private static readonly List<KeyValuePair<long, string>> s_replies = new List<KeyValuePair<long, string>>();
        private static int s_listeners;

        internal static long BotUid { get; private set; }
        internal static string LastReply { get; private set; }

        internal static void Listen() => s_listeners++;

        internal static void StopListening()
        {
            if (s_listeners > 0) s_listeners--;
            if (s_listeners == 0) s_replies.Clear();
        }

        internal static void OnChatMessage(long senderId, string text)
        {
            if (s_listeners == 0 || string.IsNullOrEmpty(text)) return;
            string line = text.Trim();
            if (line.StartsWith(ReplyPrefix, StringComparison.OrdinalIgnoreCase))
                s_replies.Add(new KeyValuePair<long, string>(senderId, line.Substring(ReplyPrefix.Length).Trim()));
        }

        internal static void Send(string verb)
        {
            s_replies.Clear();
            try { Chat.instance?.SendText(Talker.Type.Shout, VerbPrefix + verb); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatTestBot] could not shout '{verb}': {ex.Message}"); }
        }

        // Waits for the followed bot's reply that matches; LastReply is null when none came in time.
        internal static IEnumerator AwaitReply(Func<string, bool> match, float seconds = AnswerSeconds)
        {
            LastReply = null;
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                foreach (var reply in s_replies)
                {
                    if (BotUid != 0 && reply.Key != BotUid) continue;
                    if (!match(reply.Value)) continue;
                    LastReply = reply.Value;
                    if (BotUid == 0) BotUid = reply.Key;
                    yield break;
                }
                yield return null;
            }
        }

        // `bot follow`: true once the bot answers, which also names it for the later replies.
        internal static IEnumerator Find()
        {
            BotUid = 0;
            Send("follow");
            yield return AwaitReply(r => r.StartsWith("following", StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerator Goto(Vector3 point)
        {
            Send(string.Format(System.Globalization.CultureInfo.InvariantCulture, "goto {0:0} {1:0}", point.x, point.z));
            yield return AwaitReply(r => r.StartsWith("parked at", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith("goto:", StringComparison.OrdinalIgnoreCase), ParkSeconds);
        }

        internal static IEnumerator Away(float metres, float bearingDegrees)
        {
            Send(string.Format(System.Globalization.CultureInfo.InvariantCulture, "away {0:0} {1:0}", metres, bearingDegrees));
            yield return AwaitReply(r => r.StartsWith("parked at", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith("goto:", StringComparison.OrdinalIgnoreCase), ParkSeconds);
        }

        internal static void Follow() => Send("follow");

        // On the bot's OWN client (admintools split b): FDT ignores the bot's own shouts, so the verb goes through FDT 1.1.176's
        // `fdt_bot <verb>` console command, run as if the trusted tester had said it. The reply is still shouted "bot: …" and
        // reaches OnChatMessage like any other.
        internal static void RunLocal(string verb)
        {
            s_replies.Clear();
            try
            {
                if (Console.instance != null) Console.instance.TryRunCommand("fdt_bot " + verb, silentFail: false, skipAllowedCheck: true);
                else FiresLogger.LogWarning($"[FatTestBot] no console to run 'fdt_bot {verb}'");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatTestBot] 'fdt_bot {verb}' threw: {ex.Message}"); }
        }

        // FDT 1.1.173 `bot verify-follow` ([visual]): the bot keeps 8-20 m from the tester on solid ground, outside the case
        // grid, facing what the tester faces, and teleports after the tester itself (Fire, R75: a parked verification bot
        // stood in the sea doing nothing on stream). A re-send replaces the box. Following is true after "verify-follow: on";
        // an older FDT answers "unknown command" or nothing, and the caller parks the bot instead.
        internal const float FollowMin = 8f, FollowMax = 20f;
        internal static bool Following { get; private set; }

        internal static IEnumerator VerifyFollow(string tester, Rect exclude)
        {
            string name = tester.IndexOf(' ') >= 0 ? $"\"{tester}\"" : tester;
            Send(string.Format(System.Globalization.CultureInfo.InvariantCulture, "verify-follow {0} {1:0} {2:0} exclude {3:0} {4:0} {5:0} {6:0}",
                name, FollowMin, FollowMax, exclude.xMin, exclude.yMin, exclude.xMax, exclude.yMax));
            yield return AwaitReply(r => r.StartsWith("verify-follow:", StringComparison.OrdinalIgnoreCase)
                                         || r.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0);
            Following = LastReply != null && LastReply.StartsWith("verify-follow: on", StringComparison.OrdinalIgnoreCase);
        }

        // FDT `bot equip <item>`: a fresh copy from ObjectDB when the bag has none. Replies "equip: <item>" or "equip: refused: …".
        internal static IEnumerator Equip(string item)
        {
            Send("equip " + item);
            yield return AwaitReply(r => r.StartsWith("equip:", StringComparison.OrdinalIgnoreCase));
        }

        internal static void VerifyFollowOff()
        {
            if (!Following) return;
            Send("verify-follow off");
            Following = false;
        }
    }
}
