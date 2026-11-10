using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1323 (Core hotfix 1078 contract, agreed with the Core owner): Core's voxel SnowRefill eased EVERY paint-mask G above base
    // back to base, and cultivated gardens share G with snow trails (vanilla snow_walk paints Cultivate, FAT trail ops write G), so
    // gardens were erased; Core 1078 turned the refill off. Core re-enables it ONLY on texels FAT marks here as trail cuts.
    //  * storage: per TerrainComp, ZDO byte array "fat_trailcut", one bit per m_paintMask texel (bit i = byte i >> 3, bit i & 7).
    //    Length != ceil(paintMask.Length / 8) = absent (size-validated on every read; ZDO data only the comp's owner writes).
    //  * set: ONLY by a trail op on the comp's OWNER, inside TerrainComp.InternalDoOperation - the one place every op lands there
    //    (vanilla RPC_ApplyOperation -> DoOperation, FatTrailBatch.Receive, the owner's own ops through the RPC to itself), on the
    //    texels whose paint the op changed (0.2.1324: G up or down, or m_modifiedPaint newly set; 1323 = G up only) (FatSnowTrail's voxel writer runs in the same call, so its trench field is included).
    //  * trail vs player cultivate: an op reaches the owner as its PREFAB NAME only (TerrainOp.Settings.Serialize hashes the name,
    //    Deserialize returns that prefab's own m_settings object). The settings object is looked up by reference in
    //    ObjectDB.m_terrainOpsByHash: "snow_walk" (vanilla deep-snow walk, incl. creature walk), "FAT_ShovelPatch_NN" and
    //    "FAT_CreatureTrail_NNN" (FatOpTemplates copies of snow_walk) are trail ops. Everything else is not - the Cultivator's
    //    cultivate op and the hoe are other prefabs, and FAT's brushes (FiresUnifiedPaint, Studio, WaterShapingTools) call
    //    DoOperation with settings objects they build themselves, which are in no prefab table.
    //  * clear: any non-trail paint op (cultivator, hoe, FAT paint brushes) clears the bit on every texel of its paint footprint,
    //    so player work always wins. Negative-strength Cultivate ops (vanilla melee snow_decrease, fire melters) are snow ops, not
    //    player work: they never clear. 0.2.1379 ([classes-ui]'s review of Core 1155, via [wishbone]: Core refills only marked texels, so snow these ops lowered never refilled): they
    //    set the mark on texels they lower, except a texel already edited and unmarked, which may be real soil. Core clears a bit through FatApi.ClearTrailCut when it eases the texel to base.
    //  * persist: the owner writes the ZDO in a TerrainComp.Save postfix, exactly when vanilla saves the paint (TCData).
    internal static class FatTrailCut
    {
        internal const string ZdoKey = "fat_trailcut";
        private static readonly int s_keyHash = ZdoKey.GetStableHashCode();
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> NView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<TerrainComp, int> Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");   // 0.2.1324

        private sealed class State { public byte[] Bits; public uint Rev = uint.MaxValue; public bool Dirty; }
        private static readonly ConditionalWeakTable<TerrainComp, State> s_state = new ConditionalWeakTable<TerrainComp, State>();

        private static int s_setUp, s_setDown, s_setNewPaint;   // 0.2.1324: the marks split by how the trail op changed the paint
        private static int s_set, s_clearedCultivate, s_clearedCore, s_marked, s_rejected;
        private static float s_logAt;
        private static bool s_infoSaid;

        internal static int ExpectedBytes(TerrainComp tc)
        {
            var pm = tc != null ? PaintMask(tc) : null;
            return pm == null ? -1 : (pm.Length + 7) >> 3;
        }

        // The comp's current mask: the owner's live array while it has unsaved changes, else the ZDO's (re-read when its data
        // revision moves; a wrong length = absent). create = allocate an empty mask (owner, a trail op is about to set bits).
        private static State Get(TerrainComp tc, bool create)
        {
            if (tc == null) return null;
            int len = ExpectedBytes(tc);
            if (len <= 0) return null;
            var st = s_state.GetValue(tc, _ => new State());
            var nv = NView(tc);
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (!st.Dirty && zdo != null && zdo.DataRevision != st.Rev)
            {
                st.Rev = zdo.DataRevision;
                var raw = zdo.GetByteArray(s_keyHash);
                if (raw == null) st.Bits = null;
                else if (raw.Length != len) { st.Bits = null; s_rejected++; }   // size check: stale / foreign data = absent
                else st.Bits = (byte[])raw.Clone();   // our own copy: the ZDO's stored array is never mutated in place
            }
            if (st.Bits != null && st.Bits.Length != len) st.Bits = null;
            if (st.Bits == null && create) st.Bits = new byte[len];
            return st;
        }

        internal static byte[] Mask(TerrainComp tc)
        {
            try { return Get(tc, false)?.Bits; } catch { return null; }
        }

        internal static bool IsSet(TerrainComp tc, int index)
        {
            try
            {
                var b = Get(tc, false)?.Bits;
                return b != null && index >= 0 && (index >> 3) < b.Length && (b[index >> 3] & (1 << (index & 7))) != 0;
            }
            catch { return false; }
        }

        // Core: owner only; marks the comp dirty so the next Save writes the ZDO.
        internal static void ClearFromCore(TerrainComp tc, int index)
        {
            try
            {
                if (tc == null || !tc.IsOwner()) return;
                var st = Get(tc, false);
                if (st?.Bits == null || index < 0 || (index >> 3) >= st.Bits.Length) return;
                byte m = (byte)(1 << (index & 7));
                if ((st.Bits[index >> 3] & m) == 0) return;
                st.Bits[index >> 3] &= (byte)~m; st.Dirty = true; s_clearedCore++;
                Tick();
            }
            catch { }
        }

        // ── trail vs player op, by the op's prefab (the settings object Deserialize returned) ──
        private static readonly Dictionary<TerrainOp.Settings, string> s_names = new Dictionary<TerrainOp.Settings, string>();
        private static int s_namesCount = -1; private static float s_namesAt = -999f;
        internal static string PrefabName(TerrainOp.Settings s)
        {
            var db = ObjectDB.instance;
            if (s == null || db == null || db.m_terrainOpsByHash == null) return null;
            // rebuilt when the table changes size (UpdateRegisters / FatOpTemplates re-adds) or at most every 30 s
            if (db.m_terrainOpsByHash.Count != s_namesCount || Time.unscaledTime - s_namesAt > 30f)
            {
                s_names.Clear();
                foreach (var kv in db.m_terrainOpsByHash)
                    if (kv.Value != null && kv.Value.m_settings != null) s_names[kv.Value.m_settings] = Utils.GetPrefabName(kv.Value.gameObject.name);
                s_namesCount = db.m_terrainOpsByHash.Count; s_namesAt = Time.unscaledTime;
            }
            return s_names.TryGetValue(s, out var n) ? n : null;
        }

        internal static bool IsTrailOp(TerrainOp.Settings s, out string name)
        {
            name = null;
            if (s == null || !s.m_paintCleared || s.m_paintType != TerrainModifier.PaintType.Cultivate || s.m_paintStrength <= 0f) return false;
            var walk = FatSnowTrail.WalkPrefab();
            var ws = walk != null ? walk.GetComponent<TerrainOp>()?.m_settings : null;
            if (ws != null && ReferenceEquals(s, ws)) { name = "snow_walk"; return true; }
            name = PrefabName(s);
            if (name == null) return false;   // a settings object no prefab owns = a FAT brush (player work)
            return name == "snow_walk" || name.StartsWith("FAT_ShovelPatch_") || name.StartsWith("FAT_CreatureTrail_");
        }

        // ── owner apply ──
        private static float[] s_before = new float[0];
        private static bool[] s_beforeMod = new bool[0];   // 0.2.1324: m_modifiedPaint before the trail op
        private static TerrainComp s_snapComp;
        private static bool s_snapLower;   // 0.2.1379: this snapshot is for a negative snow op
        private static int s_setLower, s_skipSoil;
        private static bool s_lowerSaid;

        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static class Patch_Op
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(TerrainComp __instance, TerrainOp.Settings modifier)
            {
                s_snapComp = null;
                try
                {
                    if (__instance == null || modifier == null || !modifier.m_paintCleared || !__instance.IsOwner()) return;
                    bool lower = modifier.m_paintType == TerrainModifier.PaintType.Cultivate && modifier.m_paintStrength < 0f;
                    if (!lower && !IsTrailOp(modifier, out _)) return;
                    s_snapLower = lower;
                    var pm = PaintMask(__instance);
                    if (pm == null) return;
                    if (s_before.Length < pm.Length) s_before = new float[pm.Length];
                    for (int i = 0; i < pm.Length; i++) s_before[i] = pm[i].g;
                    // 0.2.1324 (trail marks for any G change): m_modifiedPaint too, so a texel the op newly paints is marked
                    var mp = ModifiedPaint(__instance);
                    if (s_beforeMod.Length < pm.Length) s_beforeMod = new bool[pm.Length];
                    for (int i = 0; i < pm.Length; i++) s_beforeMod[i] = mp != null && i < mp.Length && mp[i];
                    s_snapComp = __instance;
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailCut] snapshot skipped: {ex.Message}"); }
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
            {
                try
                {
                    if (__instance == null || modifier == null || !modifier.m_paintCleared || !__instance.IsOwner()) return;
                    var pm = PaintMask(__instance);
                    if (pm == null) return;
                    if (s_snapComp == __instance)
                    {
                        s_snapComp = null;
                        // 0.2.1324 (trail marks for any G change): mark every texel whose paint the trail op CHANGED - G up, G down
                        // (heightmap snow_walk lowers G below Core's base) or m_modifiedPaint newly set; 1323 marked G-up only.
                        var mp = ModifiedPaint(__instance);
                        State st = null; int n = 0;
                        if (s_snapLower)
                        {
                            for (int i = 0; i < pm.Length && i < s_before.Length; i++)
                            {
                                if (pm[i].g - s_before[i] >= -0.0005f) continue;
                                byte m = (byte)(1 << (i & 7));
                                var known = st ?? Get(__instance, false);
                                bool marked = known?.Bits != null && (known.Bits[i >> 3] & m) != 0;
                                if (marked) continue;
                                if (i < s_beforeMod.Length && s_beforeMod[i]) { s_skipSoil++; continue; }   // edited and unmarked: may be real soil, spring must not unset it
                                if (st == null) { st = Get(__instance, true); if (st?.Bits == null) return; if (!Has(st.Bits)) s_marked++; }
                                st.Bits[i >> 3] |= m; n++; s_setLower++;
                            }
                            if (n > 0)
                            {
                                st.Dirty = true; s_set += n;
                                if (!s_lowerSaid) { s_lowerSaid = true; FiresLogger.LogInfo("[TrailCut] snow lowered by a melee or fire op is marked for Core's refill; edited unmarked ground is left alone (0.2.1379)"); }
                            }
                            Tick();
                            return;
                        }
                        for (int i = 0; i < pm.Length && i < s_before.Length; i++)
                        {
                            float dg = pm[i].g - s_before[i];
                            bool newPaint = mp != null && i < mp.Length && mp[i] && i < s_beforeMod.Length && !s_beforeMod[i];
                            if (dg <= 0.0005f && dg >= -0.0005f && !newPaint) continue;
                            if (st == null) { st = Get(__instance, true); if (st?.Bits == null) return; if (!Has(st.Bits)) s_marked++; }
                            byte m = (byte)(1 << (i & 7));
                            if ((st.Bits[i >> 3] & m) != 0) continue;
                            st.Bits[i >> 3] |= m; n++;
                            if (dg > 0.0005f) s_setUp++; else if (dg < -0.0005f) s_setDown++; else s_setNewPaint++;
                        }
                        if (n > 0) { st.Dirty = true; s_set += n; }
                        Tick();
                        return;
                    }
                    if (modifier.m_paintType == TerrainModifier.PaintType.Cultivate && modifier.m_paintStrength < 0f) return;   // snow op, not player work
                    if (IsTrailOp(modifier, out _)) return;   // trail op without a snapshot (should not happen): never clears
                    // player work (cultivator / hoe / FAT brush): clear the bit on every texel of the paint footprint
                    var st2 = Get(__instance, false);
                    if (st2?.Bits == null) return;
                    var hm = Hmap(__instance);
                    if (hm == null) return;
                    Vector3 wp = pos;
                    if (modifier.m_halfOffset) { wp.x -= 0.5f; wp.z -= 0.5f; }
                    hm.WorldToVertexMask(wp, out int cx, out int cy);
                    int pitch = Width(__instance) + 1;
                    float f = Mathf.Max(modifier.m_paintRadius, 0.5f) / hm.m_scale;
                    int r = Mathf.CeilToInt(f) + 1;   // + 1: vanilla's edge spread and the bilinear neighbour
                    int c = 0;
                    for (int y = Mathf.Max(0, cy - r); y <= Mathf.Min(pitch - 1, cy + r); y++)
                        for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(pitch - 1, cx + r); x++)
                        {
                            if (!modifier.m_square && Vector2.Distance(new Vector2(cx, cy), new Vector2(x, y)) > f + 1f) continue;
                            int i = y * pitch + x;
                            if ((i >> 3) >= st2.Bits.Length) continue;
                            byte m = (byte)(1 << (i & 7));
                            if ((st2.Bits[i >> 3] & m) == 0) continue;
                            st2.Bits[i >> 3] &= (byte)~m; c++;
                        }
                    if (c > 0) { st2.Dirty = true; s_clearedCultivate += c; }
                    Tick();
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailCut] op skipped: {ex.Message}"); }
            }
        }

        private static bool Has(byte[] b) { for (int i = 0; i < b.Length; i++) if (b[i] != 0) return true; return false; }

        // The owner writes the mask with the paint: every vanilla / FAT Save (DoOperation, FatTrailBatch, the refill push).
        [HarmonyPatch(typeof(TerrainComp), "Save")]
        private static class Patch_Save
        {
            private static void Postfix(TerrainComp __instance)
            {
                try
                {
                    if (__instance == null || !__instance.IsOwner() || !s_state.TryGetValue(__instance, out var st) || !st.Dirty) return;
                    var nv = NView(__instance);
                    var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                    if (zdo == null || st.Bits == null || st.Bits.Length != ExpectedBytes(__instance)) { st.Dirty = false; return; }
                    zdo.Set(s_keyHash, (byte[])st.Bits.Clone());   // a copy: ZDOExtraData keeps the reference it is given
                    st.Rev = zdo.DataRevision; st.Dirty = false;
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailCut] save skipped: {ex.Message}"); }
            }
        }

        private static void Tick()
        {
            if (!s_infoSaid) { s_infoSaid = true; FiresLogger.LogInfo("[TrailCut] trail-cut mark active (0.2.1324: any G change by a trail op, up or down, or newly painted): Core refill eases only marked texels"); }
            if (Time.unscaledTime < s_logAt) return;
            s_logAt = Time.unscaledTime + 10f;
            if (s_set == 0 && s_clearedCultivate == 0 && s_clearedCore == 0 && s_rejected == 0) return;
            int comps = 0;
            foreach (var tc in TerrainComp.s_instances) if (tc != null && s_state.TryGetValue(tc, out var st) && st.Bits != null && Has(st.Bits)) comps++;
            FiresLogger.LogDebug($"[TrailCut] 10 s: bits set {s_set} (trail ops: G up {s_setUp}, G down {s_setDown}, newly painted {s_setNewPaint}; snow ops lowered {s_setLower}, possible soil left alone {s_skipSoil}), cleared {s_clearedCultivate} (cultivate / player paint) + {s_clearedCore} (Core refill), comps newly marked {s_marked}, comps with a mark {comps}, wrong-size ZDO masks ignored {s_rejected} (0.2.1324)");
            s_setUp = s_setDown = s_setNewPaint = s_setLower = s_skipSoil = 0;
            s_set =s_clearedCultivate = s_clearedCore = s_marked = s_rejected = 0;
        }
    }
}
