using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1090 (Fire, live 1088: "now were stuck in the pickaxe animation, and cannot do anything"). Vanilla ends an attack only
    // when the animator leaves the "attack"-tagged state (Humanoid.InAttack reads the animator tag; Attack.Update stops the
    // attack once InAttack goes false, Attack.cs:320-353). If the animator never leaves that state (speed held near 0, or an
    // animation swap mid-swing), InAttack stays true and every input that checks it is refused. FAT's dig path never touches
    // the animator, but the player cannot be left stuck, so this guard:
    //  * [PlayerAttack] dig line: what FAT's voxel dig did for the LOCAL player's swing (hit, decision, ms), rate-limited;
    //  * [PlayerAttack] stuck: the local player in an attack for more than 5 s -> vanilla's cleanup once (Attack.Stop, clear the
    //    attack and the action queue, animator speed 1, "attack_abort"); still stuck 2 s later -> Animator.Rebind (a full pose
    //    reset). Each step logs what it found and did.
    internal static class FatPlayerAttackGuard
    {
        private const float StuckAfter = 5f, RebindAfter = 2f;

        private static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
        private static readonly AccessTools.FieldRef<Humanoid, Attack> PreviousAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_previousAttack");
        private static readonly AccessTools.FieldRef<Character, Animator> Anim = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> ZAnim = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
        private static readonly System.Reflection.MethodInfo ClearQueue = AccessTools.Method(typeof(Humanoid), "ClearActionQueue");

        private static float s_inSince = -1f, s_clearedAt = -1f, s_digLogAt;
        private static Attack s_attack;
        private static bool s_announced;

        internal static void DigLine(Vector3 at, string what, long t0)
        {
            if (Time.unscaledTime < s_digLogAt) return;
            s_digLogAt = Time.unscaledTime + 5f;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FiresLogger.LogInfo($"[PlayerAttack] dig hit at ({at.x:0.0},{at.y:0.0},{at.z:0.0}): {what}, {ms:0.0} ms");
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_PlayerUpdate
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                if (!s_announced) { s_announced = true; FiresLogger.LogInfo("[PlayerAttack] stuck-attack guard executing (clears an attack held over 5 s)"); }
                float now = Time.time;
                bool inAttack;
                try { inAttack = __instance.InAttack(); } catch { return; }
                var attack = CurrentAttack(__instance);
                if (!inAttack) { s_inSince = -1f; s_clearedAt = -1f; s_attack = null; return; }
                if (s_inSince < 0f || attack != s_attack) { s_inSince = now; s_attack = attack; s_clearedAt = -1f; return; }
                float held = now - s_inSince;
                if (s_clearedAt < 0f && held > StuckAfter)
                {
                    s_clearedAt = now;
                    var anim = Anim(__instance);
                    string name = attack?.m_attackAnimation ?? "none";
                    float speed = anim != null ? anim.speed : -1f;
                    try
                    {
                        attack?.Stop();
                        if (attack != null) { PreviousAttack(__instance) = attack; CurrentAttack(__instance) = null; }
                        ClearQueue?.Invoke(__instance, null);
                        if (anim != null) anim.speed = 1f;
                        ZAnim(__instance)?.SetTrigger("attack_abort");
                    }
                    catch (System.Exception e) { FiresLogger.LogWarning($"[PlayerAttack] stuck cleanup threw: {e.GetType().Name}: {e.Message}"); }
                    FiresLogger.LogWarning($"[PlayerAttack] stuck {held:0.0} s in attack '{name}' (animator speed {speed:0.0000}, attack done {(attack != null && attack.IsDone())}): cleared (Stop, action queue, speed 1, attack_abort)");
                }
                else if (s_clearedAt > 0f && now - s_clearedAt > RebindAfter)
                {
                    s_clearedAt = now + 3600f;   // once per stuck spell
                    var anim = Anim(__instance);
                    if (anim != null) { anim.Rebind(); anim.speed = 1f; }
                    FiresLogger.LogWarning($"[PlayerAttack] still in the attack state {held:0.0} s after the cleanup: animator rebound (pose reset)");
                }
            }
        }
    }
}
