using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1111 (Fire: "Clicking the shovel on ground should only clear out a thin 1 m wide patch"). Vanilla clears snow on EVERY
    // Deep North melee swing (Attack.DoMeleeAttack, Attack.cs:1174-1185): Player.m_snowShovelDefault / m_snowShovelStrong at
    // GetSnowShovelPoint() (Attack.cs:1216-1219), a 0.5 m sphere (m_snowShovelRadius) for pieces. That op stays vanilla's and FAT
    // writes nothing for it (FatSnowTrail). Only the snow shovel ITEM, swung at voxel ground (no build piece in vanilla's 0.5 m
    // sphere), adds a FAT patch: a copy of vanilla's own walk op (the Player prefab's m_deepSnowWalkObj) registered per width
    // (0.2.1116, see EnsureTemplates), so it travels, applies and saves exactly like the walk trail, and FatSnowTrail writes it as
    // a disc with the trench's straight walls. Snow only: the voxel ground is never carved (VoxelOnlyPatches skips the shovel).
    internal static class FatShovelPatch
    {
        // 0.2.1112 (Fire: "Maybe actually give the shovel mouse wheel radius selection like the hoe"): the wheel sets the patch
        // width while the shovel is out on voxel Deep North ground, 0.5-4 m in 0.5 m steps, shown by a ground ring in the V-tool
        // gizmo style; kept for the session. The op carries it as its paint radius = width / 2 + RadiusTag, so every peer reads
        // the width off the op and a 2 m patch (r 1) never reads as vanilla's r 1 walk op.
        internal const float DefaultWidth = 1.0f, MinWidth = 0.5f, MaxWidth = 4.0f, WidthStep = 0.5f;
        internal const float RadiusTag = 0.003f;
        internal static float PatchWidth { get; private set; } = DefaultWidth;
        // 0.2.1113 (round #13: hit sent to (265.5,822.2), measured at (264.0,825.0) - the proof searched the strongest G near its own
        // aim point and found the walked trench 3 m away: "along 4.10 m, across 0.84 m"): where the last patch op was sent.
        internal static Vector3 LastHitAt;
        internal static int Hits;
        private static float s_otherLogAt;
        private static int s_pieceMask;
        private static readonly Collider[] s_hits = new Collider[16];

        internal static bool IsSnowShovel(ItemDrop.ItemData w, out string name)
        {
            name = w == null ? "none" : $"{w.m_shared?.m_name} / {(w.m_dropPrefab != null ? w.m_dropPrefab.name : "?")}";
            if (w == null) return false;
            string n = w.m_shared?.m_name ?? "", p = w.m_dropPrefab != null ? w.m_dropPrefab.name : "";
            return n.IndexOf("snowshovel", System.StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("snowshovel", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── 0.2.1121 swing why-line (the lead, round #17: the wheel swing left no "[Shovel] ground hit" and no melee-op line, and the
        // log could not say why). Every local-player swing in Deep North logs once (<= 1/s): did it reach Attack.DoMeleeAttack, did it
        // take the snow branch, and if not, why. Attack.Start arms it, DoMeleeAttack marks it reached, Player.Update reports a swing
        // that never got there (cancelled before the trigger, or not a melee attack).
        private static float s_swingLogAt, s_swingStartAt = -1f;
        private static bool s_swingTriggered, s_swingReached;
        private static string s_swingWeapon = "none", s_swingType = "";

        private static readonly System.Collections.Generic.HashSet<Heightmap.Biome> s_patchBiomesSaid = new System.Collections.Generic.HashSet<Heightmap.Biome>();
        private static readonly System.Collections.Generic.HashSet<string> s_refusalsSaid = new System.Collections.Generic.HashSet<string>();
        private static void SwingWhy(string weapon, bool reached, bool taken, string why)
        {
            if (!taken && reached && !string.IsNullOrEmpty(why) && FiresLogger.DebugEnabled)
            {
                int cut = why.IndexOf(" at ("); string key = cut > 0 ? why.Substring(0, cut) : why;   // once per reason, not per position
                if (s_refusalsSaid.Add(key)) FiresLogger.LogDebug($"[Shovel] first refusal for this reason: {why} (weapon {weapon}; once per reason)");
            }
            s_swingStartAt = -1f;
            if (Time.unscaledTime < s_swingLogAt) return;
            s_swingLogAt = Time.unscaledTime + 1f;
            FiresLogger.LogDebug($"[Shovel] swing: weapon {weapon}, DoMeleeAttack reached {(reached ? "y" : "n")}, snow branch taken {(taken ? "y" : "n")}{(taken ? "" : $" (why not: {why})")}");
        }

        // 0.2.1140 (Fire: Mountain = "Visual + trails only"): the shovel works in Deep North and Mountain; the point itself must
        // also carry snow (FatSnowTrail.SnowTrailBiome) where the patch is written.
        private static bool InDeepNorth() => Player.m_localPlayer != null && FatSnowTrail.SnowTrailBiome(Player.m_localPlayer.transform.position);   // 0.2.1154: snow depth, any biome

        [HarmonyPatch(typeof(Attack), "Start")]
        private static class Patch_SwingStart
        {
            private static void Postfix(Attack __instance, bool __result)
            {
                try
                {
                    if (!__result || __instance.m_character == null || __instance.m_character != Player.m_localPlayer || !InDeepNorth()) return;
                    IsSnowShovel(__instance.m_weapon, out s_swingWeapon);
                    s_swingType = __instance.m_attackType.ToString();
                    s_swingStartAt = Time.unscaledTime; s_swingTriggered = false; s_swingReached = false;
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] bail: swing start threw: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Attack), "OnAttackTrigger")]
        private static class Patch_SwingTrigger
        {
            private static void Prefix(Attack __instance)
            {
                if (__instance.m_character != null && __instance.m_character == Player.m_localPlayer) s_swingTriggered = true;
            }
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_SwingUnreached
        {
            private static void Postfix(Player __instance)
            {
                if (s_swingStartAt < 0f || __instance != Player.m_localPlayer || Time.unscaledTime - s_swingStartAt < 2f) return;
                if (s_swingReached) { s_swingStartAt = -1f; return; }
                SwingWhy(s_swingWeapon, false, false, s_swingTriggered
                    ? $"the attack triggered but is type {s_swingType}, not a melee swing"
                    : "the attack never triggered (cancelled or interrupted before the hit frame)");
            }
        }

        // FTS (FiresTossinShade 0.3.271+) by reflection, no hard reference: without FTS (or an older one) this is always false and
        // FAT keeps its own 0.5 m piece check below.
        // 0.2.1405 (Fire 11-07, piece snow moves to Vedr): Vedr's VedrApi.ShovelHitsPiece (VedrApi 14, same signature) first, then FTS's.
        private static System.Reflection.MethodInfo s_ftsHitsPiece;
        private static bool s_ftsLooked;
        private static bool FtsShovelHitsPiece(Character who, Vector3 at)
        {
            if (!s_ftsLooked)
            {
                s_ftsLooked = true;
                var sig = new[] { typeof(Character), typeof(Vector3) };
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                string from = "Vedr (VedrApi.ShovelHitsPiece)";
                s_ftsHitsPiece = System.Type.GetType("Vedr.VedrApi, Norger.Vedr", false)?.GetMethod("ShovelHitsPiece", flags, null, sig, null);
                if (s_ftsHitsPiece == null) { from = "FTS (FtsSnowApi.ShovelHitsPiece)"; s_ftsHitsPiece = System.Type.GetType("VerdantsAscent.Water.Core.FtsSnowApi, FiresTossinShade", false)?.GetMethod("ShovelHitsPiece", flags, null, sig, null); }
                FiresLogger.LogDebug($"[Shovel] piece-clear hook {(s_ftsHitsPiece != null ? $"found: {from}; a swing that clears a piece digs no ground patch" : "not found (no Vedr 14+ / FTS 0.3.271+): FAT's own 0.5 m piece check only")} (0.2.1405)");
            }
            if (s_ftsHitsPiece == null) return false;
            try { return s_ftsHitsPiece.Invoke(null, new object[] { who, at }) is bool b && b; }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] FtsSnowApi.ShovelHitsPiece threw: {ex.Message} (treated as no piece)"); return false; }
        }

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class Patch_Swing
        {
            private static void Prefix(Attack __instance)
            {
                if (__instance.m_character != null && __instance.m_character == Player.m_localPlayer) s_swingReached = true;
            }

            private static void Postfix(Attack __instance)
            {
                try
                {
                    var ch = __instance.m_character;
                    if (ch == null || ch != Player.m_localPlayer) return;
                    IsSnowShovel(__instance.m_weapon, out string wn);
                    if (!InDeepNorth()) { SwingWhy(wn, true, false, "no snow under the player (> 0.05 m any biome, or Deep North; 0.2.1154)"); return; }
                    if (!__instance.m_snowShovel) { SwingWhy(wn, true, false, "this attack has no snow flag (m_snowShovel off)"); return; }
                    Vector3 at = __instance.GetSnowShovelPoint();
                    if (!OnHeightmapToo && !FatSnowTrail.VoxelWorldHere(at)) { SwingWhy(wn, true, false, $"not voxel ground at ({at.x:0.0},{at.z:0.0}) - vanilla's heightmap op runs"); return; }
                    if (!FatSnowTrail.SnowTrailBiome(at) || global::VerdantsAscent.Water.Core.IceFrost.Underwater(at)) { SwingWhy(wn, true, false, $"no snow at ({at.x:0.0},{at.z:0.0}) (Deep North, or Mountain with Core snow)"); return; }
                    bool shovel = IsSnowShovel(__instance.m_weapon, out string wname);
                    if (!shovel)
                    {
                        SwingWhy(wname, true, false, "not the snow shovel - vanilla's melee snow op is not applied on voxel ground");
                        if (Time.unscaledTime >= s_otherLogAt) { s_otherLogAt = Time.unscaledTime + 5f; FiresLogger.LogDebug($"[Shovel] non-shovel hit (weapon {wname}): no FAT patch"); }
                        return;
                    }
                    // 0.2.1150 (Fire: "snow is not cleared from the piece we click"; the lead's rule: one swing does one thing): when FTS
                    // clears a build piece this swing (a snowy piece in the 0.5 m sphere, or under the crosshair within 4 m), FAT digs no
                    // ground patch. FTS's FtsSnowApi.ShovelHitsPiece is pure, so the answer is the same whichever postfix runs first.
                    if (FtsShovelHitsPiece(ch, at))
                    {
                        FiresLogger.LogDebug($"[Shovel] FTS clears a piece this swing (weapon {wname}): no ground patch");
                        SwingWhy(wname, true, false, "FTS clears a piece this swing (FtsSnowApi.ShovelHitsPiece)");
                        return;
                    }
                    if (s_pieceMask == 0) s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
                    int n = Physics.OverlapSphereNonAlloc(at, __instance.m_snowShovelRadius, s_hits, s_pieceMask);
                    for (int i = 0; i < n; i++)
                        if (s_hits[i] != null && s_hits[i].GetComponentInParent<WearNTear>() != null)
                        {
                            FiresLogger.LogDebug($"[Shovel] piece hit (weapon {wname}) on '{Utils.GetPrefabName(s_hits[i].GetComponentInParent<WearNTear>().gameObject)}': no ground patch (the piece's snow is FTS's)");
                            SwingWhy(wname, true, false, "piece hit - the piece's snow is FTS's");
                            return;
                        }
                    // 0.2.1116 (round #15: "1 patch op(s) sent, 0 written on this peer", the patch measured 5.02 m along): an op travels as
                    // its PREFAB NAME only (TerrainOp.Settings.Serialize, TerrainOp.cs:103-107) and the owner applies that prefab's own
                    // settings (Deserialize, :109-117), so the 0.2.1111-1115 radius set on the spawned snow_walk copy never left this
                    // peer: the owner got a plain r 1 walk op, joined it to the last walk point as a trench segment, and the wider
                    // wheel widths never existed. Now each width is its own registered op prefab (FAT_ShovelPatch_05 .. _40).
                    if (!EnsureTemplates() || !s_tpl.TryGetValue(PatchOpName(PatchWidth).GetStableHashCode(), out var tpl) || tpl == null)
                    { FiresLogger.LogWarning("[Shovel] no snow_walk prefab on the Player / no ObjectDB: no FAT patch"); SwingWhy(wname, true, false, "no patch op prefab (no snow_walk / ObjectDB)"); return; }
                    at = SnapToTexel(at, tpl.m_settings != null && tpl.m_settings.m_halfOffset);   // 0.2.1120: the patch centres on a 1 m paint texel
                    Object.Instantiate(tpl.gameObject, at, Quaternion.identity);   // active at the root: TerrainOp.Awake sends it by its name
                    LastHitAt = at; Hits++;
                    if (WorldGenerator.instance != null && FiresLogger.DebugEnabled)
                    {
                        var sb = WorldGenerator.instance.GetBiome(at.x, at.z);
                        if (s_patchBiomesSaid.Add(sb))
                            FiresLogger.LogDebug($"[Shovel] first patch on {sb} at ({at.x:0.0},{at.z:0.0}): {PatchWidth:0.0} m op sent, snow before {VoxelSnowDepth.DepthMetresAt(at):0.00} m (the owner cuts it to the trench floor) - once per biome");
                    }
                    SwingWhy(wname, true, true, "");
                    FiresLogger.LogDebug($"[Shovel] ground hit at ({at.x:0.0},{at.z:0.0}) (weapon {wname}): patch {PatchWidth:0.0} m op sent (r {RadiusFor(PatchWidth):0.000}) (snow only, ground not carved)");
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] bail: patch threw: {ex.Message}"); }
            }
        }

        // 0.2.1405 (FatApi.ShovelGroundAt; was the ground half of FTS's FtsSnowApi.ShovelAt): one shovel swing's ground patch at `at`
        // for any character (a companion), the same checks and op as the player's swing above, at the default 1 m width (FTS's
        // old patch was r 0.5 = 1 m too). The op is FAT's registered FAT_ShovelPatch_10, so it travels by name like the player's and
        // the TerrainComp's owner applies it (vanilla TerrainOp; callable on any peer). true = the patch op was sent.
        private static float s_apiLogAt;
        internal static bool GroundPatchAt(Character who, Vector3 at, out string why)
        {
            why = "";
            if (who == null) { why = "no character"; return false; }
            if (!OnHeightmapToo && !FatSnowTrail.VoxelWorldHere(at)) { why = "not voxel ground (and heightmap patches are off)"; return false; }
            if (!FatSnowTrail.SnowTrailBiome(at) || global::VerdantsAscent.Water.Core.IceFrost.Underwater(at)) { why = "no snow there"; return false; }
            if (FtsShovelHitsPiece(who, at)) { why = "a snowy piece is hit (the piece clear is the piece-snow owner's)"; return false; }
            if (s_pieceMask == 0) s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
            int n = Physics.OverlapSphereNonAlloc(at, 0.5f, s_hits, s_pieceMask);
            for (int i = 0; i < n; i++)
                if (s_hits[i] != null && s_hits[i].GetComponentInParent<WearNTear>() != null) { why = "a piece is in the 0.5 m sphere"; return false; }
            if (!EnsureTemplates() || !s_tpl.TryGetValue(PatchOpName(DefaultWidth).GetStableHashCode(), out var tpl) || tpl == null) { why = "no patch op prefab (no snow_walk / ObjectDB)"; return false; }
            at = SnapToTexel(at, tpl.m_settings != null && tpl.m_settings.m_halfOffset);
            Object.Instantiate(tpl.gameObject, at, Quaternion.identity);
            why = $"patch {DefaultWidth:0.0} m op sent at ({at.x:0.0},{at.z:0.0})";
            return true;
        }

        internal static void ApiWhy(Character who, bool did, string why)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_apiLogAt) return;
            s_apiLogAt = Time.unscaledTime + 2f;
            FiresLogger.LogDebug($"[Shovel] FatApi.ShovelGroundAt by {(who != null ? who.m_name : "null")}: {(did ? "dug" : "not dug")}: {why} (0.2.1405)");
        }

        internal static float RadiusFor(float width) => width * 0.5f + RadiusTag;

        // 0.2.1120: paint texels sit on whole metres (zone centres are multiples of 64, 1 m texels); an op with m_halfOffset is
        // applied 0.5 m down-left of its position, so its position snaps to the half metre.
        internal static Vector3 SnapToTexel(Vector3 p, bool halfOffset)
        {
            float h = halfOffset ? 0.5f : 0f;
            return new Vector3(Mathf.Round(p.x - h) + h, p.y, Mathf.Round(p.z - h) + h);
        }

        // ── 0.2.1116 patch op prefabs ────────────────────────────────────────────────────────────────────────────────────────
        // One inactive copy of the Player's snow_walk op per width, named FAT_ShovelPatch_<width x 10>, its paint radius = the
        // width's tagged radius, and registered in ObjectDB.m_terrainOpsByHash by that name's hash on EVERY peer (client, host,
        // dedicated server), so the owner deserialises the patch op with the patch radius. UpdateRegisters clears the table
        // (ObjectDB.Awake / CopyOtherDB), so the entries are put back after it and checked before each received op.
        // 0.2.1376 (Fire: "we need the shovel to work for our vedr snow on normal heightmap the same it does for our voxel shovel"; his log: "not voxel ground",
        // and vanilla's own op only runs in EnvMan's Deep North, so Vedr snow elsewhere got nothing): the same patch, ring and wheel on heightmap ground with snow.
        internal static bool OnHeightmapToo => FiresVoxelConfig.ShovelOnHeightmap == null || FiresVoxelConfig.ShovelOnHeightmap.Value;

        // Heightmap Deep North: vanilla's shovel op is 7.6 m wide on top of the patch, so the shovel's own one stays off there too. Other weapons keep theirs.
        internal static bool IsShovelStrongOp(TerrainOp.Settings s) => s != null && (s == s_mStrong || s == s_mStrongDb || Same(s, s_mStrong));

        internal static string PatchOpName(float width) => "FAT_ShovelPatch_" + Mathf.RoundToInt(width * 10f).ToString("00");
        private static readonly System.Collections.Generic.Dictionary<int, TerrainOp> s_tpl = new System.Collections.Generic.Dictionary<int, TerrainOp>();
        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>> s_set;
        private static bool s_tplSaid;

        // 0.2.1129: the registration itself is FatOpTemplates (shared with FatCreatureTrail's op prefabs).
        internal static bool EnsureTemplates()
        {
            if (s_set == null)
            {
                s_set = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>>();
                for (float w = MinWidth; w <= MaxWidth + 0.001f; w += WidthStep) s_set.Add(new System.Collections.Generic.KeyValuePair<string, float>(PatchOpName(w), RadiusFor(w)));
            }
            if (!FatOpTemplates.Ensure(s_tpl, s_set)) return false;
            if (!s_tplSaid)
            {
                s_tplSaid = true;
                FiresLogger.LogDebug($"[Shovel] patch op prefabs registered on {FatOpTemplates.Peer()}: {s_tpl.Count} widths {MinWidth:0.0}-{MaxWidth:0.0} m ({PatchOpName(MinWidth)} .. {PatchOpName(MaxWidth)}, snow_walk settings, paint r = width / 2 + {RadiusTag})");
            }
            return true;
        }

        internal static void EnsureAll(string where)   // internal: FatTrailBatch resolves op names before a batch
        {
            try { EnsureTemplates(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] bail: templates at {where} threw: {ex.Message}"); }
            try { FatCreatureTrail.EnsureTemplates(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] bail: creature templates at {where} threw: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
        private static class Patch_DbRegisters
        {
            private static void Postfix() => EnsureAll("UpdateRegisters");
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Patch_SceneAwake
        {
            private static void Postfix() => EnsureAll("ZNetScene.Awake");
        }

        // Before anything reads a received op (FatTerrainOpGuard's prefix deserialises too): the patch / creature names must resolve.
        [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
        private static class Patch_ReceiveTemplates
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => EnsureAll("receive");
        }

        // ── 0.2.1116 vanilla's melee snow op on voxel ground ─────────────────────────────────────────────────────────────────
        // Round #15: the snow shovel's swing sent vanilla's Player.m_snowShovelStrong op (Attack.cs:1183) - Cultivate r 3.8,
        // strength +1.30, not the -0.2 assumed in 1111 - and FAT's writer took it as "any other Cultivate op" and wrote vanilla's
        // Lerp disc: "[SnowTrail] G written 23 cells ... by a Cultivate op r 3.8 strength 1.30" at the hit, the 5 m patch. Now
        // m_snowShovelDefault / m_snowShovelStrong never run on FAT voxel ground, on whichever peer owns the terrain (that is where
        // every op is applied): no G, no height. Pieces keep vanilla's ChangeSnow (Attack.cs:1185-1203, not a terrain op).
        private static TerrainOp.Settings s_mStrong, s_mStrongDb, s_mDefault, s_mDefaultDb;
        private static string s_mStrongName = "?", s_mDefaultName = "?";
        private static float s_meleeLearnAt, s_blockLogAt;
        internal static int MeleeBlocked;

        internal static bool IsVanillaMeleeSnowOp(TerrainOp.Settings s, out string name)
        {
            name = null;
            if (s == null) return false;
            if ((s_mStrongDb == null || s_mDefaultDb == null) && Time.unscaledTime >= s_meleeLearnAt)
            {
                s_meleeLearnAt = Time.unscaledTime + 5f;
                var pl = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player")?.GetComponent<Player>() : null;
                if (pl != null)
                {
                    Learn(pl.m_snowShovelStrong, ref s_mStrong, ref s_mStrongDb, ref s_mStrongName);
                    Learn(pl.m_snowShovelDefault, ref s_mDefault, ref s_mDefaultDb, ref s_mDefaultName);
                }
            }
            if (s == s_mStrong || s == s_mStrongDb || Same(s, s_mStrong)) { name = s_mStrongName; return true; }
            if (s == s_mDefault || s == s_mDefaultDb || Same(s, s_mDefault)) { name = s_mDefaultName; return true; }
            return false;
        }

        private static void Learn(GameObject go, ref TerrainOp.Settings own, ref TerrainOp.Settings db, ref string name)
        {
            if (go == null) return;
            name = Utils.GetPrefabName(go.name);
            own = go.GetComponent<TerrainOp>()?.m_settings;
            if (ObjectDB.instance != null && ObjectDB.instance.TryGetTerrainOp(name, out TerrainOp op) && op != null) db = op.m_settings;
        }

        private static bool Same(TerrainOp.Settings a, TerrainOp.Settings b)
            => b != null && a.m_paintCleared == b.m_paintCleared && a.m_paintType == b.m_paintType && a.m_level == b.m_level && a.m_raise == b.m_raise
               && a.m_smooth == b.m_smooth && Mathf.Abs(a.m_paintRadius - b.m_paintRadius) < 0.0005f && Mathf.Abs(a.m_paintStrength - b.m_paintStrength) < 0.0005f;

        internal static void NoteBlocked(Vector3 pos, TerrainOp.Settings s, string name)
        {
            MeleeBlocked++;
            if (Time.unscaledTime < s_blockLogAt) return;
            s_blockLogAt = Time.unscaledTime + 2f;
            string peer = ZNet.instance == null ? "?" : ZNet.instance.IsServer() ? (ZNet.instance.IsDedicated() ? "dedicated server" : "host") : "client";
            FiresLogger.LogDebug($"[Shovel] vanilla melee snow op '{name}' ({FatTerrainOpGuard.Describe(s)}) at ({pos.x:0.0},{pos.z:0.0}) on FAT voxel ground: not applied on {peer} - no G, no height ({MeleeBlocked} this session)");
        }

        // A FAT patch op: radius - RadiusTag is half a width on the 0.5 m grid within MinWidth..MaxWidth.
        internal static bool HalfFromRadius(float radius, out float half)
        {
            half = radius - RadiusTag;
            float w = half * 2f;
            if (w < MinWidth - 0.01f || w > MaxWidth + 0.01f) return false;
            return Mathf.Abs(w / WidthStep - Mathf.Round(w / WidthStep)) * WidthStep <= 0.002f;
        }

        // The one width setter: the wheel and the proof test both come through here.
        internal static float StepWidth(int notches)
        {
            float w = Mathf.Clamp(PatchWidth + notches * WidthStep, MinWidth, MaxWidth);
            if (!Mathf.Approximately(w, PatchWidth))
            {
                PatchWidth = w;
                FiresLogger.LogDebug($"[Shovel] patch width {w:0.0} m (wheel)");
            }
            return PatchWidth;
        }

        // The ring shows (and the wheel is the width) while the local player holds the snow shovel in Deep North over voxel ground.
        private static bool s_ringOn;
        private static LineRenderer s_ring;
        private const int RingPoints = 48;
        private static readonly Color RingGreen = new Color(0.30f, 0.95f, 0.35f, 1f);   // VoxelBrushDrag GIZMO_GREEN
        private static readonly Vector3[] s_ringPts = new Vector3[RingPoints];
        private static int s_ringMask;
        private static int RingMask() => s_ringMask != 0 ? s_ringMask : (s_ringMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv", "item", "piece", "piece_nonsolid", "Water"));

        private static bool RingWanted(Player p, out Vector3 at)
        {
            at = Vector3.zero;
            if (p == null || p != Player.m_localPlayer || p.IsDead() || InventoryGui.IsVisible() || Menu.IsVisible()) return false;
            if (!IsSnowShovel(p.GetCurrentWeapon(), out _)) return false;
            if (!FatSnowTrail.SnowTrailBiome(p.transform.position)) return false;   // 0.2.1154: snow depth, any biome
            float range = p.GetCurrentWeapon().m_shared?.m_attack?.m_attackRange ?? 1.5f;
            at = p.transform.position + p.transform.forward * range;   // vanilla's GetSnowShovelPoint, on the ground
            return OnHeightmapToo || FatSnowTrail.VoxelWorldHere(at);
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_Wheel
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                try
                {
                    s_ringOn = RingWanted(__instance, out Vector3 at);
                    if (!s_ringOn) { if (s_ring != null && s_ring.enabled) s_ring.enabled = false; return; }
                    if (!FatInput.ShiftHeld && !FiresCore.Input.FiresInputBlock.IsTyping())
                    {
                        float n = FatInput.WheelNotches();
                        if (n != 0f) StepWidth((int)n);
                    }
                    DrawRing(SnapToTexel(at, false), PatchWidth * 0.5f);   // 0.2.1381: centred where the patch lands
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] bail: wheel threw: {ex.Message}"); }
            }
        }

        // How far above the terrain collider the snow is drawn here, on the 1 m paint grid: the terrain shader's own curve, the same one Core SnowMeshRaise.Apply uses
        // (0.2.1138 there: "so a chunk without the shader draws the same height"), so both draw paths answer alike.
        private const float SnowDrawMetres = 2.1f;
        private static float DrawnSnowAtTexel(Heightmap hm, int x, int y)
        {
            Color c = hm.GetPaintMask(x, y);
            return Mathf.GammaToLinearSpace(c.g) * SnowDrawMetres * (1f - Mathf.Clamp01(c.b * 10f + c.r));
        }

        private static float DrawnSnowMetres(Heightmap hm, Vector3 p)
        {
            Vector3 o = hm.transform.position;
            float fx = (p.x - o.x) / hm.m_scale + hm.m_width / 2, fy = (p.z - o.z) / hm.m_scale + hm.m_width / 2;
            int x = Mathf.Clamp(Mathf.FloorToInt(fx), 0, hm.m_width - 1), y = Mathf.Clamp(Mathf.FloorToInt(fy), 0, hm.m_width - 1);
            float tx = Mathf.Clamp01(fx - x), ty = Mathf.Clamp01(fy - y);
            return Mathf.Lerp(Mathf.Lerp(DrawnSnowAtTexel(hm, x, y), DrawnSnowAtTexel(hm, x + 1, y), tx), Mathf.Lerp(DrawnSnowAtTexel(hm, x, y + 1), DrawnSnowAtTexel(hm, x + 1, y + 1), tx), ty);
        }

        private static void DrawRing(Vector3 c, float r)
        {
            if (s_ring == null)
            {
                var go = new GameObject("FAT_ShovelRing");
                Object.DontDestroyOnLoad(go);
                s_ring = go.AddComponent<LineRenderer>();
                s_ring.useWorldSpace = true;
                s_ring.loop = true;
                s_ring.widthMultiplier = 0.08f;
                s_ring.material = VoxelBrushDrag.SharedLineMat();
                s_ring.textureMode = LineTextureMode.Tile;
                s_ring.positionCount = RingPoints;
                s_ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                s_ring.startColor = s_ring.endColor = RingGreen;
            }
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i * Mathf.PI * 2f / RingPoints;
                var p = new Vector3(c.x + Mathf.Cos(a) * r, c.y, c.z + Mathf.Sin(a) * r);
                if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 8f, RingMask(), QueryTriggerInteraction.Ignore))
                {
                    p.y = hit.point.y;
                    // 0.2.1381 (Fire: "the ruler is shown under the snow not laid on top"): the terrain collider has no snow on it; the raise is only drawn.
                    var hm = hit.collider.GetComponent<Heightmap>();
                    if (hm != null) p.y += DrawnSnowMetres(hm, p);
                }
                p.y += 0.1f;
                s_ringPts[i] = p;
            }
            s_ring.SetPositions(s_ringPts);
            s_ring.enabled = true;
        }

        // Camera zoom stands down while the ring is up, as for the V-tools (VoxelToolPieces); FAT's own read passes ReadingRaw.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
        private static class Patch_ZInput_Scroll_Shovel
        {
            private static void Postfix(ref float __result)
            {
                if (__result == 0f || FatInput.ReadingRaw || !s_ringOn || FatInput.ShiftHeld) return;
                __result = 0f;
            }
        }
    }
}
