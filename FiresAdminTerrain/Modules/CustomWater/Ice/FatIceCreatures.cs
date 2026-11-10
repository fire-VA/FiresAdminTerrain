using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1100 (Fire, image 573: Greydwarfs walk across a frozen Deep North pond with steady footing - "and enemies should slip on
    // our ice too"). Vanilla's slide has NO player gate: Character.UpdateWalking (Character.cs:1102) sets m_slipping and calls
    // ApplySlippery (:834-846, :1245) for every walking Character whose last ground collider has dynamicFriction <= 0 on a
    // static_solid/Default layer - our ice qualifies (VanillaIce.OptIn). But each creature prefab carries its OWN slip tuning, and
    // monsters' is mild: Greydwarf m_slipperyControl 0.035 / run 0.018, min slide speed 0.3, max 7 (Greydwarf.prefab:955-966) vs the
    // player's 0.01 / 0.005 - at a walking pace they keep most of their grip. On OUR ice only, a non-player character the local peer
    // owns takes the player's slip tuning while it stands on it and gets its own back when it steps off; vanilla ice and every other
    // surface are untouched. AI still steers (m_moveDir is never touched), it just slides like the player does.
    internal static class FatIceCreatures
    {
        private const float Control = 0.01f, ControlRun = 0.005f, MinSpeed = 0.02f, MaxSpeed = 14f, Breaking = 0.999f;

        private static readonly AccessTools.FieldRef<Character, Collider> LastGround = AccessTools.FieldRefAccess<Character, Collider>("m_lastGroundCollider");

        private sealed class Saved { public float C, CR, Min, Max, Brk; }
        private static readonly Dictionary<Character, Saved> s_onIce = new Dictionary<Character, Saved>();
        private static readonly HashSet<string> s_said = new HashSet<string>();

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        private static class Patch_GroundContact
        {
            private static void Postfix(Character __instance)
            {
                long t0 = global::VerdantsAscent.Terrain.Voxel.FatHooks.Start();
                bool acted = false;
                try { acted = Contact(__instance); }
                finally { global::VerdantsAscent.Terrain.Voxel.FatHooks.Stop(global::VerdantsAscent.Terrain.Voxel.FatHooks.Hook.IceCreatures, t0, acted); }
            }

            // 0.2.1107: the same decision; the ground collider's material (a native getter) is read once per call, not twice, and not
            // at all while no FAT ice material exists. (No per-collider verdict cache: a collider can get its ice material after it
            // was first stood on, and a stale "not ice" would change behaviour.)
            private static bool Contact(Character __instance)
            {
                if (__instance == null || __instance.IsPlayer()) return false;
                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;
                var ground = LastGround(__instance);
                bool ours = false;
                PhysicsMaterial gm = null;
                if (ground != null)
                {
                    var iceMat = VanillaIce.Material;
                    gm = ground.sharedMaterial;
                    ours = iceMat != null && gm != null && gm == iceMat;
                }
                bool onIce = s_onIce.ContainsKey(__instance);
                if (ours && !onIce)
                {
                    s_onIce[__instance] = new Saved { C = __instance.m_slipperyControl, CR = __instance.m_slipperyControlRun, Min = __instance.m_slippingMinSpeed, Max = __instance.m_slipperyMaxSpeed, Brk = __instance.m_slipperyBreaking };
                    __instance.m_slipperyControl = Control; __instance.m_slipperyControlRun = ControlRun;
                    __instance.m_slippingMinSpeed = MinSpeed; __instance.m_slipperyMaxSpeed = MaxSpeed; __instance.m_slipperyBreaking = Breaking;
                    if (FirstFor(__instance, true))
                        Say(__instance, true, $"standing on FAT ice '{ground.name}' (SlipperyIce, layer {LayerMask.LayerToName(ground.gameObject.layer)}): player slip tuning while on it (control {Control}, was {s_onIce[__instance].C})");
                    return true;
                }
                if (!ours && onIce && ground != null)
                    Restore(__instance);
                else if (!ours && !onIce && gm != null && gm.dynamicFriction <= 0f && FirstFor(__instance, false))
                    Say(__instance, false, $"on slippery '{ground.name}' that is not FAT ice: vanilla's own slide, untouched");
                return ours;
            }

            // 0.2.1107: the once-per-prefab lines were formatted (name + message strings) on every step a creature stood on slippery
            // ground before Say dropped them; now a per-character check comes first, so the strings are built once per character.
            private static readonly HashSet<int> s_seen = new HashSet<int>();
            private static bool FirstFor(Character c, bool applied)
            {
                int key = c.GetInstanceID() * 2 + (applied ? 1 : 0);
                if (s_seen.Contains(key)) return false;
                if (s_seen.Count > 8192) s_seen.Clear();
                s_seen.Add(key);
                return true;
            }
        }

        [HarmonyPatch(typeof(Character), "OnDestroy")]
        private static class Patch_Destroy
        {
            private static void Prefix(Character __instance) { if (__instance != null) s_onIce.Remove(__instance); }
        }

        private static void Restore(Character c)
        {
            if (!s_onIce.TryGetValue(c, out var s)) return;
            c.m_slipperyControl = s.C; c.m_slipperyControlRun = s.CR; c.m_slippingMinSpeed = s.Min; c.m_slipperyMaxSpeed = s.Max; c.m_slipperyBreaking = s.Brk;
            s_onIce.Remove(c);
        }

        private static void Say(Character c, bool applied, string why)
        {
            string name = Utils.GetPrefabName(c.gameObject);
            if (!s_said.Add(name + (applied ? "+" : "-"))) return;
            FiresLogger.LogInfo($"[Ice] {name} on ice: slippery applied {(applied ? "y" : "n")} ({why})");
        }
    }
}
