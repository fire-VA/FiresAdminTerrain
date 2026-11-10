using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1095 (Fire: "water in the deep north should freeze over and become slippery since we now have that vanilla slippery
    // mechanic to work with instead of our previous home made one"). ONE slippery implementation: vanilla 1.0's.
    //   Character.ApplySlippery / UpdateGroundContact (assembly_valheim 1.0.15 Character.cs:834-846, :1102): a character slips when
    //   its m_lastGroundCollider.material.dynamicFriction <= 0 AND (s_slipperyStuffMask & collider.gameObject.layer) != 0, with
    //   s_slipperyStuffMask = LayerMask.GetMask("static_solid", "Default") (:371) = 0x8001. Vanilla ANDs the mask with the layer
    //   INDEX, so of the two only static_solid (15) passes; vanilla's own frozen water is built exactly that way: IcePond1.prefab /
    //   ice1.prefab / BlackIce_*: a MeshCollider on layer 15 with PhysicsMaterial "SlipperyIce" (world/Props/Rocks/materials/
    //   SlipperyIce.physicMaterial: dynamic 0, static 0, friction combine 2 = Minimum). Ice shoes (m_iceShoes) cancel it, as vanilla.
    // A FAT ice surface opts in with OptIn(collider): layer static_solid + vanilla's SlipperyIce material (the loaded asset when
    // found, else an identical copy).
    internal static class VanillaIce
    {
        internal const string Mechanism = "vanilla Character.ApplySlippery (layer static_solid + PhysicsMaterial 'SlipperyIce', dynamic friction 0)";
        private static PhysicsMaterial s_material;
        private static bool s_saidMaterial;

        internal static PhysicsMaterial Material
        {
            get
            {
                if (s_material != null) return s_material;
                foreach (var m in Resources.FindObjectsOfTypeAll<PhysicsMaterial>())
                    if (m != null && m.name == "SlipperyIce") { s_material = m; break; }
                bool vanilla = s_material != null;
                if (!vanilla)
                    s_material = new PhysicsMaterial("SlipperyIce")
                    {
                        dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
                        frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Average,
                    };
                if (!s_saidMaterial)
                {
                    s_saidMaterial = true;
                    FiresLogger.LogInfo($"[WaterIce] slippery via {Mechanism}: material {(vanilla ? "vanilla's loaded SlipperyIce asset" : "a copy of vanilla's SlipperyIce (asset not loaded yet)")}");
                }
                return s_material;
            }
        }

        internal static void OptIn(Collider c)
        {
            if (c == null) return;
            int solid = LayerMask.NameToLayer("static_solid");
            if (solid >= 0) c.gameObject.layer = solid;
            c.sharedMaterial = Material;
        }

        // ---- placed ice pieces (Floating / Land ice): BOTH colliders (Fire: "Both if possible") ----
        // (a) the slip collider (OptIn: static_solid + SlipperyIce) the character stands on, and (b) a snap collider on layer
        // 'piece' 1 cm under it, the same mesh, non-trigger. Vanilla's placement / remove rays (Player.m_placeRayMask and
        // m_removeRayMask include both static_solid and piece, Player.cs:321-323; the piece is found by GetComponentInParent<Piece>)
        // hit the slip surface first; a ray masked to 'piece' only (Infinity Hammer's ruler / snapping) passes static_solid and
        // lands on the snap collider. Non-trigger, so it works whatever Physics.queriesHitTriggers is; the character capsule rests on
        // the higher slip surface. Call SyncSnap after the slip collider's mesh is reassigned.
        private const float SnapBelow = 0.01f;
        internal const string SnapName = "IceSnap";
        private static bool s_saidPiece;

        internal static void AddSnap(MeshCollider slip)
        {
            if (slip == null) return;
            var t = slip.transform.Find(SnapName);
            if (t == null)
            {
                var go = new GameObject(SnapName);
                go.transform.SetParent(slip.transform, false);
                go.transform.localPosition = new Vector3(0f, -SnapBelow, 0f);
                int piece = LayerMask.NameToLayer("piece");
                if (piece >= 0) go.layer = piece;
                var mc = go.AddComponent<MeshCollider>();
                mc.convex = false;
                t = go.transform;
                if (!s_saidPiece)
                {
                    s_saidPiece = true;
                    FiresLogger.LogInfo("[WaterIce] ice piece colliders: slip static_solid + snap piece (non-trigger snap 1 cm under the slip surface, same mesh)");
                }
            }
            SyncSnap(slip);
        }

        internal static void SyncSnap(MeshCollider slip)
        {
            if (slip == null) return;
            var t = slip.transform.Find(SnapName);
            var mc = t != null ? t.GetComponent<MeshCollider>() : null;
            if (mc == null) return;
            mc.sharedMesh = null;
            mc.sharedMesh = slip.sharedMesh;
        }

        // ---- freeze rule ([seasons], Core FiresCore.Bridge.SeasonBridge, bound by reflection, null-safe) ----
        // Deep North = ALWAYS frozen (the painted biome counts: WorldGenerator.GetBiome answers the paint since 0.2.1086; plus Core's
        // snow bases: SnowField.BaseAt(p) > 0 && SnowField.DeepGroundAt(p)). Every other biome: frozen only while SeasonBridge.IsWinter
        // && FreezesLakes (and, when Vedr gives one, inside SnowTerrainBiomeMask); IsThawing counts as not frozen. Ocean and Ashlands
        // never freeze. Without Vedr only the Deep North rule applies.
        private static bool s_bound;
        private static Func<bool> s_available, s_winter, s_freezes, s_thawing;
        private static Func<int> s_mask;
        private static Func<Vector3, float> s_baseAt;
        private static Func<Vector3, bool> s_deepGround;

        private static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            try
            {
                var sb = AccessTools.TypeByName("FiresCore.Bridge.SeasonBridge");
                if (sb != null)
                {
                    s_available = Getter<bool>(sb, "IsAvailable");
                    s_winter = Getter<bool>(sb, "IsWinter");
                    s_freezes = Getter<bool>(sb, "FreezesLakes");
                    s_thawing = Getter<bool>(sb, "IsThawing");
                    s_mask = Getter<int>(sb, "SnowTerrainBiomeMask");
                }
                var sf = AccessTools.TypeByName("FiresCore.Snow.SnowField");
                if (sf != null)
                {
                    var b = AccessTools.Method(sf, "BaseAt", new[] { typeof(Vector3) });
                    if (b != null && b.IsStatic && b.ReturnType == typeof(float)) s_baseAt = (Func<Vector3, float>)Delegate.CreateDelegate(typeof(Func<Vector3, float>), b);
                    var d = AccessTools.Method(sf, "DeepGroundAt", new[] { typeof(Vector3) });
                    if (d != null && d.IsStatic && d.ReturnType == typeof(bool)) s_deepGround = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), d);
                }
            }
            catch (Exception e) { FiresLogger.LogWarning($"[WaterIce] Core season/snow bind failed: {e.Message} (Deep North biome rule only)"); }
            FiresLogger.LogInfo($"[WaterIce] freeze rule bound: SeasonBridge {(s_winter != null ? "yes" : "no")}, FreezesLakes {(s_freezes != null ? "yes" : "no")}, SnowField.BaseAt {(s_baseAt != null ? "yes" : "no")}, DeepGroundAt {(s_deepGround != null ? "yes" : "no")}");
        }

        private static Func<T> Getter<T>(Type t, string name)
        {
            var g = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            return g != null && g.ReturnType == typeof(T) ? (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), g) : null;
        }

        private static bool Safe(Func<bool> f) { try { return f != null && f(); } catch { return false; } }

        internal static bool VedrAvailable { get { Bind(); return Safe(s_available); } }

        // Freeze answer at one point. The DeepGroundAt question is slow in Core (~7-15 ms): callers cache it (SeasonalWaterFreeze
        // asks once per combiner and keeps the answer until the paint or season changes).
        // 0.2.1097: the per-biome part of the rule for WaterStateField (no point questions, no DeepGroundAt): Deep North always;
        // Ocean / Ashlands never; any other biome while Vedr's winter freezes lakes (inside its snow biome mask when it gives one).
        internal static bool BiomeFreezes(Heightmap.Biome biome)
        {
            Bind();
            if (biome == Heightmap.Biome.DeepNorth) return true;
            if ((biome & (Heightmap.Biome.Ocean | Heightmap.Biome.AshLands)) != 0 || biome == Heightmap.Biome.None) return false;
            if (IceField.TestForceFreezing) return true;   // 0.2.1168 fat_water_ice_test (cleared in its finally)
            if (!Safe(s_available) || !Safe(s_winter) || !Safe(s_freezes) || Safe(s_thawing)) return false;
            int mask = 0;
            try { mask = s_mask != null ? s_mask() : 0; } catch { }
            return mask == 0 || (mask & (int)biome) != 0;
        }

        // 0.2.1097: changes when the season part of the rule changes (WaterStateField drops its tiles on a change).
        internal static int SeasonSignature
        {
            get
            {
                Bind();
                int mask = 0;
                try { mask = s_mask != null ? s_mask() : 0; } catch { }
                return (Safe(s_available) ? 1 : 0) | (Safe(s_winter) ? 2 : 0) | (Safe(s_freezes) ? 4 : 0) | (Safe(s_thawing) ? 8 : 0) | (mask << 4)
                    | (IceField.TestForceFreezing ? 1 << 30 : 0);
            }
        }

        // 0.2.1168 (IceField): where ice MAY lie (the timed exposure then decides how much): Deep North always; never ocean / Ashlands;
        // any other biome inside Vedr's snow biome mask (whatever the season: the exposure is 0 in summer and melts down in spring, so the
        // thaw is gradual instead of a switch at the season change).
        internal static bool BiomeCanIce(Heightmap.Biome biome)
        {
            Bind();
            if (biome == Heightmap.Biome.DeepNorth) return true;
            if ((biome & (Heightmap.Biome.Ocean | Heightmap.Biome.AshLands)) != 0 || biome == Heightmap.Biome.None) return false;
            if (IceField.TestForceFreezing) return true;
            if (!Safe(s_available)) return false;
            int mask = 0;
            try { mask = s_mask != null ? s_mask() : 0; } catch { }
            return mask == 0 || (mask & (int)biome) != 0;
        }

        // 0.2.1168 (IceField): the season part of the rule alone - Vedr's winter freezes lakes now (not thawing) / it is thawing.
        internal static bool SeasonFreezing { get { Bind(); return Safe(s_available) && Safe(s_winter) && Safe(s_freezes) && !Safe(s_thawing); } }
        internal static bool SeasonThawing { get { Bind(); return Safe(s_available) && Safe(s_thawing); } }
        // 0.2.1178: which gate holds timed ice off (the 10-28 client "syncs 0" was the server answering F 0 because Vedr's
        // "Freeze lakes and rivers" is off: season signature 0x21B3 = available + winter, FreezesLakes no).
        internal static string FreezeRuleWhy()
        {
            Bind();
            if (IceField.TestForceFreezing) return "test-forced";
            if (!Safe(s_available)) return "no Vedr (seasons unavailable)";
            if (Safe(s_thawing)) return "thawing season";
            if (!Safe(s_winter)) return "not winter";
            if (!Safe(s_freezes)) return "OFF: Vedr setting Freeze lakes and rivers is off (server)";
            return "freezing (winter, lakes freeze)";
        }

        internal static bool ShouldFreeze(Vector3 p, out Heightmap.Biome biome, out string why)
        {
            Bind();
            biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p) : Heightmap.Biome.None;
            if (biome == Heightmap.Biome.DeepNorth) { why = "Deep North"; return true; }
            if ((biome & (Heightmap.Biome.Ocean | Heightmap.Biome.AshLands)) != 0) { why = "never (ocean/ashlands)"; return false; }
            try
            {
                if (s_baseAt != null && s_deepGround != null && s_baseAt(p) > 0f && s_deepGround(p)) { why = "Deep North snow base"; return true; }
            }
            catch { }
            bool available = Safe(s_available);
            bool winter = Safe(s_winter), freezes = Safe(s_freezes), thawing = Safe(s_thawing);
            int mask = 0;
            try { mask = s_mask != null ? s_mask() : 0; } catch { }
            bool inMask = mask == 0 || (mask & (int)biome) != 0;
            bool frozen = available && winter && freezes && !thawing && inMask;
            why = available ? $"season rule: winter {winter}, freezes-lakes {freezes}, thawing {thawing}, snow biome {(mask == 0 ? "any" : inMask.ToString())}" : "no Vedr: Deep North only";
            return frozen;
        }
    }
}
