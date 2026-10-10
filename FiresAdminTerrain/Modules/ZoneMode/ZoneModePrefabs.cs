using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FiresCore.Services;
using FiresCore.World;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Zone mode's prefabs come baked in the embedded "fireszones" bundle (FiresZonesBuilder in ValheimRip1.0): the
    // chunk generators, chunk seeds, the Chunk Hammer, the Eye of the Beholder, the scale-synced skyland dressing and
    // the registry anchor. The bake leaves their links to other vanilla prefabs empty, so the bundle carries no vanilla
    // copies. Each world load copies the effects, drops and smoke from the live vanilla prefab each one was made from,
    // routes their audio to the game's mixer and registers them by name; the registry anchor has no vanilla source and
    // is only registered. Core's shader sweeps, which run after these loads, bind the game's shaders.
    internal static class ZoneModePrefabs
    {
        private const string BundleName = "fireszones";
        internal const string ChunkHammerPrefab = "FiresChunkHammer";
        internal const string ChaosHammerPrefab = "FiresChaosHammer";
        internal const string LocationSeedPrefix = "FiresLocationSeed_";
        private const string GeneratorSource = "smelter";
        private const string SeedSource = "BombOoze";
        private const string HelmetSource = "HelmetOdin";
        private const string HammerSource = "Hammer";
        private const string VinesSource = "vines";
        private const string CharredSeedSource = "AncientSeed";
        private const string SaplingSource = "FirTree_Sapling";
        private const string ChaosGlowSource = "StaffOrbofAhri";
        private const string ChaosGlowPath = "attach/chaosglow/stafffx";
        private const string FxAttach = "attach";
        private const string InstanceSuffix = " (Instance)";
        private const int NestedDataDepth = 3;

        private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<int, GameObject>> ItemByHash =
            AccessTools.FieldRefAccess<ObjectDB, Dictionary<int, GameObject>>("m_itemByHash");
        private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData, GameObject>> ItemByData =
            AccessTools.FieldRefAccess<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData, GameObject>>("m_itemByData");

        private static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();
        private static readonly HashSet<string> s_reportedMissingSources = new HashSet<string>();
        private static bool s_loadTried;

        internal static bool TryGet(string name, out GameObject prefab)
        {
            prefab = null;
            return Load() && s_prefabs.TryGetValue(name, out prefab);
        }

        internal static void RegisterInScene(ZNetScene scene)
        {
            if (scene == null || !Load()) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var costs = new List<KeyValuePair<string, long>>();
            foreach (var prefab in s_prefabs.Values)
            {
                long before = clock.ElapsedTicks;
                CopyVanillaWiring(scene, prefab);
                NetworkPrefabs.RegisterByName(scene, prefab);
                costs.Add(new KeyValuePair<string, long>(prefab.name, clock.ElapsedTicks - before));
            }
            costs.Sort((a, b) => b.Value.CompareTo(a.Value));
            double TicksToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FiresLogger.LogInfo($"[FiresZones] scene wiring: {s_prefabs.Count} prefab(s) in {clock.ElapsedMilliseconds} ms; slowest: "
                + string.Join(", ", costs.Take(5).Select(c => $"{c.Key} {TicksToMs(c.Value):0.0} ms")) + ".");
        }

        // The way ObjectDB.UpdateRegisters lists an item, so inventories, equipment and the menu's character preview
        // all find it.
        internal static void RegisterItems(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0 || !Load()) return;
            var byHash = ItemByHash(db);
            var byData = ItemByData(db);
            foreach (var prefab in s_prefabs.Values)
            {
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null) continue;
                if (!db.m_items.Contains(prefab)) db.m_items.Add(prefab);
                byHash[prefab.name.GetStableHashCode()] = prefab;
                byData[drop.m_itemData.m_shared] = prefab;
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        private static class ObjectDB_CopyOtherDB_ZonePrefabs
        {
            // Ahead of Core's CopyOtherDB shader sweep, so the bundle's materials are already loaded when it runs.
            [HarmonyPriority(Priority.First)]
            private static void Postfix(ObjectDB __instance) => RegisterItems(__instance);
        }

        internal static bool IsLoaded => s_prefabs.Count > 0;

        internal static bool EnsureLoaded() => Load();

        private static bool Load()
        {
            if (s_prefabs.Count > 0) return true;
            if (s_loadTried) return false;
            s_loadTried = true;
            var bundle = FiresAdminTerrain.GetAssetBundleFromResources(BundleName);
            if (bundle == null)
            {
                FiresLogger.LogWarning($"[FiresZones] embedded bundle '{BundleName}' missing — no chunk generators, chunk seeds, "
                    + "Eye of the Beholder or skyland dressing.");
                return false;
            }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>()) s_prefabs[prefab.name] = prefab;
            FiresLogger.LogInfo($"[FiresZones] {s_prefabs.Count} prefab(s) loaded from bundle '{bundle.name}'.");
            return s_prefabs.Count > 0;
        }

        private static string VanillaSourceOf(string prefabName)
        {
            foreach (var tier in ZoneModeChunkTiers.All)
            {
                if (prefabName == tier.MachinePrefab) return GeneratorSource;
                if (prefabName == tier.SpawnerPrefab) return SeedSource;
            }
            if (prefabName.StartsWith(LocationSeedPrefix, StringComparison.Ordinal)) return SeedSource;
            if (prefabName == ChunkHammerPrefab || prefabName == ChaosHammerPrefab) return HammerSource;
            if (prefabName == TreeSaplingPrefabs.CharredSeedItem) return CharredSeedSource;
            if (prefabName == TreeSaplingPrefabs.AshlandsSapling || prefabName == TreeSaplingPrefabs.SnowFirSapling)
                return SaplingSource;
            if (prefabName == ZoneModeEyeOfTheBeholder.HelmetPrefab) return HelmetSource;
            if (prefabName == FiresSkyIslands.ScaledVinesPrefab) return VinesSource;
            return prefabName.StartsWith(FiresSkyIslands.ScaledRootPrefix, StringComparison.Ordinal)
                ? prefabName.Substring(FiresSkyIslands.ScaledRootPrefix.Length)
                : null;
        }

        private static bool IsChunkTool(string name)
            => name == ZoneModeChunkTools.CopyToolPrefab || name == ZoneModeChunkTools.PlaceToolPrefab
                || name == ZoneModeChunkTools.RemoveToolPrefab || name == ZoneModeChunkTools.UndoToolPrefab
                || name == ZoneModeChunkTools.RedoToolPrefab;

        private static void CopyVanillaWiring(ZNetScene scene, GameObject prefab)
        {
            // The registry anchor and the Chaos Hammer's tool pieces have no vanilla source and nothing to carry from one.
            if (prefab.name == ZoneModeRegistryMirror.PrefabName || IsChunkTool(prefab.name)) return;
            string sourceName = VanillaSourceOf(prefab.name);
            var source = sourceName != null ? scene.GetPrefab(sourceName) : null;
            if (source == null)
            {
                if (s_reportedMissingSources.Add(prefab.name))
                    FiresLogger.LogWarning($"[FiresZones] {prefab.name}: vanilla source '{sourceName}' not found — it keeps no effects.");
                return;
            }

            foreach (var component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var twin = TwinOf(source.transform, prefab.transform, component);
                if (twin != null) CopyEffectsAndDrops(twin, component, 0);
            }
            var sourceSmoke = source.GetComponentInChildren<SmokeSpawner>(true);
            if (sourceSmoke != null)
                foreach (var smoke in prefab.GetComponentsInChildren<SmokeSpawner>(true))
                    smoke.m_smokePrefab = sourceSmoke.m_smokePrefab;
            RebindBrokenShaders(source.transform, prefab);
            if (prefab.name == ChaosHammerPrefab) RebindFxFromStaff(scene, prefab);
            // A dedicated server plays no sound. The first call also runs Core's one-time mixer harvest over every ZNetScene
            // prefab, which landed in this join frame on the dedi (R51: 34 ms of the 44 ms scene wiring).
            if (!FiresCore.Lifecycle.FiresMod.IsDedicatedServer) LiveAudioRouting.RouteWorldSfx(prefab);
        }

        // A ripped vanilla shader carries no compiled programs, so a material that ships in our bundle pointing at one
        // draws magenta, or - for particles - as black shards. Core's VanillaShaderRebind repairs those globally by
        // matching shader NAME against the loaded supported ones; this repairs them from the vanilla TWIN instead, which
        // is the same shader instance the game is actually rendering with and so cannot match the wrong copy.
        //
        // "Unsupported" alone misses most of them: a ripped shader that names a Fallback reports isSupported and draws
        // through it. The rip's Custom/Vegetation falls back to Legacy Shaders/Diffuse, so FiresSkyVines drew with no
        // alpha test, as solid cards (RenderDoc, 2026-09-27 frame 13501). So a bundle copy is recognised by identity: a
        // shader that is not the twin's live instance yet carries its name, or sits on the baked copy of the twin's
        // material. The bundle's own materials - the rune bands and the chaos core, authored on Standard - match
        // neither and keep exactly what they shipped with, as does every deliberate retint: the material is kept and
        // only its shader replaced.
        private static bool IsBundleCopyOfLive(Material mine, Material live)
        {
            if (mine.shader == live.shader) return false;
            if (mine.shader == null || !mine.shader.isSupported) return true;
            return mine.shader.name == live.shader.name || BakedName(mine.name) == BakedName(live.name);
        }

        private static string BakedName(string materialName) =>
            materialName.EndsWith(InstanceSuffix, StringComparison.Ordinal)
                ? materialName.Substring(0, materialName.Length - InstanceSuffix.Length)
                : materialName;

        private static void RebindBrokenShaders(Transform sourceRoot, GameObject prefab)
        {
            int repaired = 0;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var twin = TwinOf(sourceRoot, prefab.transform, renderer) as Renderer;
                if (twin == null) continue;

                var ours = renderer.sharedMaterials;
                var theirs = twin.sharedMaterials;
                if (ours == null || theirs == null) continue;

                for (int i = 0; i < ours.Length && i < theirs.Length; i++)
                {
                    var mine = ours[i];
                    var live = theirs[i];
                    if (mine == null || live == null || live.shader == null || !live.shader.isSupported) continue;
                    if (!IsBundleCopyOfLive(mine, live)) continue;
                    mine.shader = live.shader;
                    // Keywords travel with the shader. A ripped shader asset may not declare one the material needs, and
                    // Unity then files it under m_InvalidKeywords - the bake of FirTree_Sapling lands _ALPHATEST_ON
                    // there, which turns alpha clipping OFF and draws needles as solid quads. Assigning a working shader
                    // does NOT revive an invalid keyword, so the live material's enabled set is copied over as well.
                    mine.shaderKeywords = live.shaderKeywords;
                    repaired++;
                }
            }
            if (repaired > 0)
                FiresLogger.LogInfo($"[FiresZones] {prefab.name}: rebound {repaired} bundle shader"
                    + (repaired == 1 ? "" : "s") + " to its vanilla source's live shader.");
        }

        // The Chaos Hammer wears the wizard staff FX, and its vanilla source is the Hammer, so those renderers have no
        // twin above and RebindBrokenShaders skips them. They are bound here from the STAFF instead, wholesale: a
        // particle material cannot survive a bundle build, and unlike the hammer body there is nothing of ours to keep -
        // every renderer under chaosglow was copied verbatim from the staff at bake time, so the live material IS the
        // intended look. Matching is by path under the FX root, which the bake preserves, not by name.
        private static void RebindFxFromStaff(ZNetScene scene, GameObject prefab)
        {
            var source = scene.GetPrefab(ChaosGlowSource);
            var crown = prefab.transform.Find(ChaosGlowPath);
            if (source == null || crown == null)
            {
                if (s_reportedMissingSources.Add(prefab.name + ChaosGlowPath))
                    FiresLogger.LogWarning($"[FiresZones] {prefab.name}: "
                        + (source == null ? $"no live '{ChaosGlowSource}' to take its glow materials from."
                                          : $"no '{ChaosGlowPath}' node, so its glow keeps the bundle materials."));
                return;
            }

            var sourceRoot = source.transform.Find(FxAttach);
            if (sourceRoot == null) sourceRoot = source.transform;

            int bound = 0;
            foreach (var renderer in crown.GetComponentsInChildren<Renderer>(true))
            {
                var node = sourceRoot.Find(PathFrom(crown, renderer.transform));
                var live = node != null ? node.GetComponent(renderer.GetType()) as Renderer : null;
                if (live == null) continue;
                renderer.sharedMaterials = live.sharedMaterials;
                var mine = renderer as ParticleSystemRenderer;
                var theirs = live as ParticleSystemRenderer;
                if (mine != null && theirs != null) mine.trailMaterial = theirs.trailMaterial;
                bound++;
            }
            FiresLogger.LogInfo($"[FiresZones] {prefab.name}: took {bound} glow material set"
                + (bound == 1 ? "" : "s") + $" from the live {ChaosGlowSource}.");
        }

        // The component of the same type at the same place in the vanilla source's hierarchy.
        private static Component TwinOf(Transform sourceRoot, Transform prefabRoot, Component component)
        {
            var node = component.transform == prefabRoot ? sourceRoot : sourceRoot.Find(PathFrom(prefabRoot, component.transform));
            return node != null ? node.GetComponent(component.GetType()) : null;
        }

        private static string PathFrom(Transform root, Transform node)
        {
            string path = node.name;
            for (var parent = node.parent; parent != null && parent != root; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }

        private static void CopyEffectsAndDrops(object from, object to, int depth)
        {
            foreach (var field in to.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(EffectList))
                {
                    if (field.GetValue(from) is EffectList effects)
                        field.SetValue(to, new EffectList { m_effectPrefabs = (EffectList.EffectData[])effects.m_effectPrefabs.Clone() });
                }
                else if (field.FieldType == typeof(DropTable))
                {
                    if (field.GetValue(from) is DropTable drops) field.SetValue(to, drops.Clone());
                }
                else if (depth < NestedDataDepth && IsNestedGameData(field.FieldType))
                {
                    var fromValue = field.GetValue(from);
                    var toValue = field.GetValue(to);
                    if (fromValue != null && toValue != null) CopyEffectsAndDrops(fromValue, toValue, depth + 1);
                }
            }
        }

        private static bool IsNestedGameData(Type type) =>
            type.IsClass && type.IsSerializable && !typeof(UnityEngine.Object).IsAssignableFrom(type)
            && type.Assembly == typeof(EffectList).Assembly;
    }
}
