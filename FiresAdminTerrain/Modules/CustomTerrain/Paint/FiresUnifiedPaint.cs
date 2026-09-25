using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Paint
{
    /// <summary>
    /// ONE paint pipeline across the tile/vanilla boundary. A brush stroke used to be
    /// all-or-nothing per system: the op blocker routed anything near a tile anchor to
    /// the tile brush and DESTROYED the vanilla op, and the tile brush only writes
    /// tiles that already exist — so a stroke crossing off anchored terrain painted
    /// nothing on the vanilla side (the dead-straight sand edge on zone borders).
    ///
    /// The unification has two halves:
    ///   * The VANILLA side is driven through vanilla's own owner-RPC
    ///     (TerrainComp "ApplyOperation" -> RPC_ApplyOperation -> PaintCleared) with a
    ///     PAINT-ONLY settings package — vanilla's exact falloff/blend math runs on the
    ///     comp owner, multiplayer-correct, zero ownership games, and no vanilla HEIGHT
    ///     deformation ever (the skyland protection that motivated the old destroy).
    ///   * The TILE side (TileBrush.ApplyPaintToTile) uses the same falloff formula
    ///     (pow(1 - d/r, 0.1) lerp, alpha preserved — vanilla TerrainComp.PaintCleared
    ///     verbatim), so the two sides of any boundary are the same curve.
    /// </summary>
    public static class FiresUnifiedPaint
    {
        private static readonly List<Heightmap> s_hmaps = new List<Heightmap>(8);
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> s_compNview
            = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");

        // Valheim 1.0 rebuilt vanilla's terrain RPC around REGISTERED PREFABS instead of a settings
        // payload: TerrainOp.Settings.Serialize now writes nothing but the source prefab's name hash, and
        // Deserialize resolves the settings back out of ObjectDB.TryGetTerrainOp(hash). A synthetic,
        // built-at-runtime Settings - which is exactly what a brush stroke is, with its own radius and
        // paint type - has no prefab and therefore cannot travel that wire at all. (The RPC also renamed
        // itself to "RPC_ApplyOperation" and grew a rotation field, so the old call was doubly dead.)
        //
        // So the stroke now rides its OWN RPC on the same TerrainComp ZNetView: four primitives out, and
        // the owner rebuilds the Settings locally and hands them to vanilla's private DoOperation. Same
        // destination, same falloff/blend math, same owner-authoritative model - only the transport is
        // ours, because vanilla's no longer carries arbitrary settings.
        private const string PaintRpc = "FiresUnifiedPaint";

        private static readonly System.Reflection.MethodInfo s_doOperation
            = AccessTools.Method(typeof(TerrainComp), "DoOperation",
                new[] { typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings) });

        [HarmonyPatch(typeof(TerrainComp), "Awake")]
        internal static class TerrainComp_Awake_RegisterPaintRpc
        {
            private static void Postfix(TerrainComp __instance)
            {
                if (__instance == null) return;
                var nview = s_compNview(__instance);
                if (nview == null || !nview.IsValid()) return;
                var comp = __instance;
                nview.Register<ZPackage>(PaintRpc, (sender, pkg) => Receive(comp, pkg));
            }
        }

        private static void Receive(TerrainComp comp, ZPackage pkg)
        {
            try
            {
                if (comp == null || pkg == null || s_doOperation == null) return;
                var nview = s_compNview(comp);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

                Vector3 pos = pkg.ReadVector3();
                float radius = pkg.ReadSingle();
                var type = (TerrainModifier.PaintType)pkg.ReadInt();
                bool heightCheck = pkg.ReadBool();

                s_doOperation.Invoke(comp, new object[] { pos, Vector3.zero, PaintSettings(radius, type, heightCheck) });
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresUnifiedPaint] paint RPC failed: {ex.Message}");
            }
        }

        // Paint-only: never level, raise or smooth, so vanilla's DoOperation takes its paintOnly branch
        // and no height deformation can reach anchored/skyland terrain.
        private static TerrainOp.Settings PaintSettings(float radius, TerrainModifier.PaintType type, bool heightCheck)
            => new TerrainOp.Settings
            {
                m_level = false,
                m_raise = false,
                m_smooth = false,
                m_paintCleared = true,
                m_paintType = type,
                m_paintRadius = radius,
                m_paintHeightCheck = heightCheck,
            };

        /// <summary>
        /// Mirror a PAINT stroke into the vanilla terrain system for every zone
        /// heightmap the brush circle touches. Returns how many comps were invoked.
        /// </summary>
        public static int MirrorPaint(Vector3 pos, float radius, TerrainModifier.PaintType type, bool heightCheck)
        {
            int sent = 0;
            try
            {
                s_hmaps.Clear();
                Heightmap.FindHeightmap(pos, radius + 1f, s_hmaps);
                for (int i = 0; i < s_hmaps.Count; i++)
                {
                    var hm = s_hmaps[i];
                    if (hm == null) continue;
                    TerrainComp comp = null;
                    try { comp = hm.GetAndCreateTerrainCompiler(); } catch { }
                    if (comp == null) continue;
                    var nview = s_compNview(comp);
                    if (nview == null || !nview.IsValid()) continue;

                    var pkg = new ZPackage();
                    pkg.Write(pos);
                    pkg.Write(radius);
                    pkg.Write((int)type);
                    pkg.Write(heightCheck);
                    nview.InvokeRPC(PaintRpc, pkg);
                    sent++;
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresUnifiedPaint] MirrorPaint failed at {pos} r={radius}: {ex.Message}");
            }
            return sent;
        }

        /// <summary>
        /// Exact-match a brush colour to a vanilla paint type — the vanilla RPC only
        /// carries the enum. Non-canonical colours return null (tile-only stroke).
        ///
        /// DeepSnow is (1,1,1,1) and is tested first: it is the only canonical mask with
        /// every colour channel high, so nothing else can swallow it. ClearVegetation is
        /// deliberately absent - it shares Reset's RGB and differs only in alpha, which
        /// Match does not read.
        /// </summary>
        public static TerrainModifier.PaintType? ColorToPaintType(Color32 c)
        {
            if (Match(c, Heightmap.m_paintMaskDeepSnow)) return TerrainModifier.PaintType.DeepSnow;
            if (Match(c, Heightmap.m_paintMaskDirt)) return TerrainModifier.PaintType.Dirt;
            if (Match(c, Heightmap.m_paintMaskCultivated)) return TerrainModifier.PaintType.Cultivate;
            if (Match(c, Heightmap.m_paintMaskPaved)) return TerrainModifier.PaintType.Paved;
            if (Match(c, Heightmap.m_paintMaskNothing)) return TerrainModifier.PaintType.Reset;
            return null;
        }

        private static bool Match(Color32 c, Color canonical)
        {
            Color32 k = canonical;
            return Mathf.Abs(c.r - k.r) <= 8 && Mathf.Abs(c.g - k.g) <= 8 && Mathf.Abs(c.b - k.b) <= 8;
        }
    }
}
