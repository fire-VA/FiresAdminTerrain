using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1129 (Fire: "Creatures should make paths in the snow as well... deer or boar or humanoid skeletons carve out paths as they
    // walk through the snow in the loaded area"; "the path they carve out obviously determined by their actual size"; "small animals
    // like hares making a 0.9 m path is fine"). Vanilla only gives the Player prefab an m_deepSnowWalkObj, so FAT's trigger
    // (FatSnowTrail.Patch_Trigger) hands every other walking Character here. Width = max(0.9, 2 x the main collider radius x
    // lossyScale), capped at 6 m (logged), snapped to the nearest registered width. An op carries only its prefab name (no
    // creature identity), so FatSnowTrail joins each creature op to the nearest earlier creature op of the SAME width 0.3-1.8 m
    // away and < 4 s old. Written by the character's ZDO owner only; capped at 30 ops/s overall; config "Creature snow trails".
    internal static class FatCreatureTrail
    {
        internal const float MinWidth = 0.9f, MaxWidth = 6f;
        internal const float RadiusTag = 0.007f;   // the shovel patch uses 0.003: op radius = width / 2 + tag tells them apart
        // 0.2.1171 ([fgn] RpcCensus: RPC_ApplyOperation x27-81 per 5 s at ~1.1 ms each on every peer while Fire stood still, from
        // creatures/companions walking in snow): one stamp per 2.4 m (the owner links up to 2.6 m, as the player's 1163 stride),
        // and at most 12 creature ops/s in total (was 1 m and 30/s).
        private const float Spacing = 3f, OpsPerSecond = 6f;   // 0.2.1182: 3 m / 6 per s (was 2.4 / 12): fewer networked ops ([fgn] RPC census)
        private const float RetryDelay = 0.5f;   // 0.2.1184
        private static int s_retryHeld;
        private static readonly AccessTools.FieldRef<Character, ZNetView> NView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        // 0.9, 1.0, 1.2 .. 2.0 by 0.2, 2.5 .. 6.0 by 0.5
        internal static readonly float[] Widths = BuildWidths();
        private static float[] BuildWidths()
        {
            var w = new List<float> { 0.9f, 1.0f };
            for (float x = 1.2f; x <= 2.001f; x += 0.2f) w.Add(Mathf.Round(x * 10f) / 10f);
            for (float x = 2.5f; x <= 6.001f; x += 0.5f) w.Add(x);
            return w.ToArray();
        }
        internal static string OpName(float width) => "FAT_CreatureTrail_" + Mathf.RoundToInt(width * 100f).ToString("000");
        internal static float RadiusFor(float width) => width * 0.5f + RadiusTag;

        internal static float Snap(float width)
        {
            float best = Widths[0];
            foreach (float w in Widths) if (Mathf.Abs(w - width) < Mathf.Abs(best - width)) best = w;
            return best;
        }

        // A creature op: radius - tag is half a registered width.
        internal static bool HalfFromRadius(float radius, out float half)
        {
            half = radius - RadiusTag;
            float w = half * 2f;
            foreach (float x in Widths) if (Mathf.Abs(x - w) <= 0.002f) return true;
            return false;
        }

        // The computed width (2 x collider radius x lossyScale) and the applied one (the 0.9 floor, the 6 m cap).
        internal static float ComputedWidth(GameObject go)
        {
            if (go == null) return 0f;
            var cap = go.GetComponent<CapsuleCollider>();
            Vector3 s = go.transform.lossyScale;
            float sc = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            if (cap != null) return 2f * cap.radius * sc;
            var col = go.GetComponent<Collider>();
            return col != null ? 2f * Mathf.Max(col.bounds.extents.x, col.bounds.extents.z) : 0f;
        }
        internal static float AppliedWidth(float computed) => Mathf.Clamp(Mathf.Max(MinWidth, computed), MinWidth, MaxWidth);

        private static readonly Dictionary<int, TerrainOp> s_tpl = new Dictionary<int, TerrainOp>();
        private static List<KeyValuePair<string, float>> s_set;
        private static bool s_said;

        internal static bool EnsureTemplates()
        {
            if (s_set == null)
            {
                s_set = new List<KeyValuePair<string, float>>();
                foreach (float w in Widths) s_set.Add(new KeyValuePair<string, float>(OpName(w), RadiusFor(w)));
            }
            if (!FatOpTemplates.Ensure(s_tpl, s_set)) return false;
            if (!s_said)
            {
                s_said = true;
                FiresLogger.LogDebug($"[SnowTrail] creature trail op prefabs registered on {FatOpTemplates.Peer()}: {s_tpl.Count} widths {MinWidth:0.0}-{MaxWidth:0.0} m ({OpName(MinWidth)} .. {OpName(MaxWidth)}, paint r = width / 2 + {RadiusTag})");
                LogTable();
            }
            return true;
        }

        // Fire's size question: the widths the formula gives for the creatures he named (prefab data, read live; -1 = not found).
        private static void LogTable()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;
            var sb = new System.Text.StringBuilder();
            var names = new List<string> { "Deer", "Boar", "Skeleton", "Wolf", "Troll", "Lox", "Hare", "Neck", "Greydwarf", "Goblin", "GoblinBrute", "Seeker", "Abomination", "Fenring" };
            foreach (var p in scene.m_prefabs)
            {
                if (p == null) continue;
                string n = p.name.ToLowerInvariant();
                if ((n.Contains("moose") || n.Contains("elk") || n.Contains("giant") || n.Contains("jotun")) && p.GetComponent<Character>() != null && !names.Contains(p.name)) names.Add(p.name);
            }
            foreach (string n in names)
            {
                var go = scene.GetPrefab(n);
                float c = go != null && go.GetComponent<Character>() != null ? ComputedWidth(go) : -1f;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c < 0f ? $"{n} -1" : $"{n} {c:0.00} -> {Snap(AppliedWidth(c)):0.0} m");
            }
            FiresLogger.LogDebug($"[SnowTrail] creature trail widths (2 x collider radius x scale -> applied, floor {MinWidth:0.0}, cap {MaxWidth:0}): {sb}");
        }

        private sealed class Last { public Vector3 Pos = new Vector3(float.MaxValue, 0f, 0f); public float RetryAt; }
        private static readonly ConditionalWeakTable<Character, Last> s_last = new ConditionalWeakTable<Character, Last>();
        private static float s_tokens = OpsPerSecond, s_tokenAt, s_sumAt;
        private static int s_writes, s_capped, s_bossCaps;
        private static readonly HashSet<int> s_creatures = new HashSet<int>();
        private static readonly Dictionary<string, float> s_typeWidth = new Dictionary<string, float>();
        internal static int Sent;

        // 0.2.1152 FatApi.SnowTrailDisc ([seasons] FiresSnowmen, Fire: a rolling snowball packs a trail as wide as the ball).
        // Same op prefabs and rate budget as the creature trails; the caller is the ball's owner.
        internal static bool Disc(Vector3 p, float radiusM)
        {
            if (!FatSnowTrail.VoxelWorldHere(p) || WorldGenerator.instance == null || !FatSnowTrail.SnowTrailBiome(p) || global::VerdantsAscent.Water.Core.IceFrost.Underwater(p)) return false;
            if (VoxelSnowDepth.DepthMetresAt(p) <= FatSnowTrail.TrailMinDepthMetres) return false;   // 0.2.1154
            float now = Time.unscaledTime;
            s_tokens = Mathf.Min(OpsPerSecond, s_tokens + (now - s_tokenAt) * OpsPerSecond); s_tokenAt = now;
            if (s_tokens < 1f) { s_capped++; return false; }
            if (!EnsureTemplates()) return false;
            float w = Snap(Mathf.Clamp(radiusM * 2f, MinWidth, MaxWidth));
            if (!s_tpl.TryGetValue(OpName(w).GetStableHashCode(), out var tpl) || tpl == null) return false;
            s_tokens -= 1f;
            Object.Instantiate(tpl.gameObject, p, Quaternion.identity);
            Sent++; s_writes++;
            if (!s_discSaid) { s_discSaid = true; FiresLogger.LogDebug($"[SnowTrail] FatApi.SnowTrailDisc first disc: radius {radiusM:0.00} m -> width {w:0.00} m at ({p.x:0},{p.z:0})"); }
            return true;
        }
        private static bool s_discSaid;

        // From FatSnowTrail.Patch_Trigger for every non-player Character (owner-side UpdateWalking).
        internal static void Tick(Character c)
        {
            if (FiresVoxelConfig.CreatureSnowTrails == null || !FiresVoxelConfig.CreatureSnowTrails.Value) return;
            Summary();
            // 0.2.1161 (the lead's profile, Fire standing still: Trigger 27.9 ms/s, 7095 calls, 79 acted): the cheap exits first -
            // owner, then the 1 m spacing - so a creature that hasn't moved a metre never reaches IsSwimming/InWater (the custom-water
            // lookups) or the snow/biome queries. Same gates, same order of effect for the calls that do act.
            var nv = NView(c);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            Vector3 p = c.transform.position;
            var last = s_last.GetOrCreateValue(c);
            if ((last.Pos - p).sqrMagnitude < Spacing * Spacing) return;
            // 0.2.1184 (the lead: Trigger 283 ms / 26,352 calls = 10.7 us on 1182, 4x round45): with the 1182 cap (6 ops/s for ALL
            // creatures) a capped creature kept last.Pos and re-ran the water/biome/depth checks on every checked step. Now a capped
            // creature waits RetryDelay, and the shared bucket is peeked BEFORE the costly checks.
            float tnow = Time.unscaledTime;
            if (tnow < last.RetryAt) { s_retryHeld++; return; }
            if (Mathf.Min(OpsPerSecond, s_tokens + (tnow - s_tokenAt) * OpsPerSecond) < 1f) { last.RetryAt = tnow + RetryDelay; s_capped++; return; }
            if (c.IsDead() || c.IsFlying() || c.IsSwimming() || c.InWater() || !c.IsOnGround()) return;
            // 0.2.1418 (Fire: "player trails in the snow are still FAR too fat"; [seasons] proved r 0.457 / 0.707 creature ops landing
            // on the player's path): a creature walking with a player (a companion, a tame, a mount) cuts no trail of its own while a
            // player is within NearPlayerM - the player's trail is the path there. Creatures away from players keep theirs.
            if (NearAPlayer(p)) { last.Pos = p; s_nearPlayer++; return; }
            if (!FatSnowTrail.VoxelWorldHere(p)) { last.Pos = p; return; }
            var wg = WorldGenerator.instance;
            if (wg == null || !FatSnowTrail.SnowTrailBiome(p) || global::VerdantsAscent.Water.Core.IceFrost.Underwater(p)) { last.Pos = p; return; }   // 0.2.1140: Deep North or Mountain snow
            float start = FatSnowTrail.TrailMinDepthMetres;   // 0.2.1154: trail threshold, not the slow start
            if (VoxelSnowDepth.DepthMetresAt(p) <= start) { last.Pos = p; return; }
            float now = Time.unscaledTime;
            s_tokens = Mathf.Min(OpsPerSecond, s_tokens + (now - s_tokenAt) * OpsPerSecond); s_tokenAt = now;
            if (s_tokens < 1f) { s_capped++; last.RetryAt = now + RetryDelay; return; }   // 0.2.1184: retry after RetryDelay, not every step
            if (!EnsureTemplates()) return;
            float computed = ComputedWidth(c.gameObject);
            if (computed > MaxWidth) s_bossCaps++;
            float w = Snap(AppliedWidth(computed));
            if (!s_tpl.TryGetValue(OpName(w).GetStableHashCode(), out var tpl) || tpl == null) return;
            if (FatTrailNet.AlreadyCut(last.Pos, p)) { last.Pos = p; FatTrailNet.NoteStamp(false); return; }   // 0.2.1182: already trenched here
            s_tokens -= 1f;
            last.Pos = p;
            FatTrailNet.NoteStamp(true);
            if (!FatTrailBatch.TryQueue(tpl.gameObject, p)) Object.Instantiate(tpl.gameObject, p, Quaternion.identity);   // batched when on (FatTrailBatch); else TerrainOp.Awake sends it by its name
            Sent++; s_writes++;
            s_creatures.Add(c.GetInstanceID());
            s_typeWidth[Utils.GetPrefabName(c.gameObject)] = w;
        }

        private const float NearPlayerM = 2.5f;
        private static int s_nearPlayer;
        private static bool NearAPlayer(Vector3 p)
        {
            foreach (var pl in Player.GetAllPlayers())
            {
                if (pl == null) continue;
                Vector3 q = pl.transform.position;
                float dx = q.x - p.x, dz = q.z - p.z;
                if (dx * dx + dz * dz <= NearPlayerM * NearPlayerM) return true;
            }
            return false;
        }

        private static void Summary()
        {
            if (Time.unscaledTime < s_sumAt) return;
            s_sumAt = Time.unscaledTime + 5f;
            if (s_writes > 0 || s_capped > 0 || s_nearPlayer > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_typeWidth) sb.Append(sb.Length > 0 ? ", " : "").Append($"{kv.Key} {kv.Value:0.0} m");
                FiresLogger.LogDebug($"[SnowTrail] creature trails: {s_writes} writes from {s_creatures.Count} creatures, capped {s_capped} (held by the 0.5 s retry {s_retryHeld}, 0.2.1184){(s_bossCaps > 0 ? $", {s_bossCaps} op(s) capped at {MaxWidth:0} m (giants)" : "")}, {s_nearPlayer} step(s) skipped within {NearPlayerM:0.0} m of a player (0.2.1418) ({sb})");
            }
            s_writes = s_capped = s_bossCaps = s_retryHeld = s_nearPlayer = 0; s_creatures.Clear(); s_typeWidth.Clear();
        }
    }
}
