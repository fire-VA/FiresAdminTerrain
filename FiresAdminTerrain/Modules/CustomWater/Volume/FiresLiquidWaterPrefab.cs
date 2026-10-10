using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── FiresLiquidWater — VANILLA LiquidVolume, water flavored ─────────
    //
    // Round-61. The custom grid-sim volume (WaterVolumeSettle) produced
    // swimmable-but-visually-broken water; meanwhile the reference result
    // (cookie milk's screenshots) is achieved by spawning the VANILLA
    // liquid simulation with water settings. Vanilla `LiquidVolume` (the
    // swamp tar-pit system) is a complete fluid sim:
    //
    //   * background thread settle sim (m_viscocity) that drapes terrain
    //     (m_groundLayer raycasts) and pools in depressions
    //   * its own mesh + MeshCollider (m_collider)
    //   * LiquidSurface trigger → IWaterInteractable → native swim
    //   * LiquidType.Water is a first-class vanilla type
    //   * full ZDO persistence: Awake → HaveSavedData ? CheckLoad()
    //     : InitializeLevels() — heights save compressed to the ZDO, so
    //     a settled pour restores EXACTLY after relog, no custom codec
    //   * vanilla even ships a `water_liquid` material (Custom/Tar
    //     shader, deep blue, glossiness 0.95) — the exact look in the
    //     reference screenshots
    //
    // This class clones the vanilla `TarLiquid` prefab (the only
    // LiquidVolume carrier registered in ZNetScene), flips it to water,
    // swaps the material, and registers it as `FiresLiquidWater` in
    // m_namedPrefabs only (never m_prefabs — same runtime-prefab-trap
    // rules as WaterAnchorPrefab / TileAnchorPrefab).
    //
    // Spawning: LiquidVolume.InitializeLevels pours `m_initialVolume`
    // over an `m_initialArea²` cell square at the grid center during
    // Awake. Per-pour control therefore requires setting those fields
    // BEFORE Awake — instantiate under an inactive parent, set fields,
    // then activate (the InstantiateInactiveClone pattern).
    public static class FiresLiquidWaterPrefab
    {
        public const string PrefabName = "FiresLiquidWater";
        public const string SourcePrefabName = "TarLiquid";

        private static GameObject _prefab;
        private static GameObject _inactiveRoot;
        private static bool _registered;

        public static bool IsRegistered => _registered && _prefab != null;

        // Registered template, for callers that need their OWN spawn shape
        // rather than Spawn()'s basin-integrated pour (the generated-water
        // backend seeds its grid directly). Instantiate it with the round-74
        // pattern — flip the template inactive so the clone is born inactive
        // at its real position — or the ZDO registers at world origin and
        // ZNetScene's zone sweep destroys it within a tick.
        public static GameObject Template => _prefab;

        // Values from the vanilla `water_liquid` material dump
        // (Yoink/c4210710/Materials/water_liquid.txt) — used only if the
        // real material asset can't be found at runtime.
        private static readonly Color WATER_LIQUID_COLOR = new Color(0.083f, 0.126f, 0.151f, 1f);
        private const float WATER_LIQUID_GLOSS = 0.951f;
        private const float WATER_LIQUID_BUMPSCALE = 0.24f;

        // Round-66 — pours can optionally wear the FiresWater shader (the
        // same wrapped material the combiner meshes use) instead of the
        // vanilla water_liquid tar-family look. OFF by default while the
        // pour-destroy bug is under investigation: water_liquid is the
        // shader vanilla tar pits PROVE renders, so it stays the control
        // case. Toggle at runtime via `va_water_pour_shader 0|1` — it
        // re-skins live pours immediately and applies to future spawns.
        public static bool UseFiresWaterShader;

        public static bool Ensure()
        {
            if (ZNetScene.instance == null) return false;

            int hash = PrefabName.GetStableHashCode();

            if (_registered && _prefab != null
                && ZNetScene.instance.m_namedPrefabs != null
                && ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var inScene)
                && inScene == _prefab)
            {
                return true;
            }

            // Session changed — re-insert cached prefab.
            if (_prefab != null && ZNetScene.instance.m_namedPrefabs != null
                && !ZNetScene.instance.m_namedPrefabs.ContainsKey(hash))
            {
                ZNetScene.instance.m_namedPrefabs[hash] = _prefab;
                FiresLogger.LogInfo($"[FiresLiquidWater] Re-inserted cached '{PrefabName}' into new ZNetScene dict.");
                _registered = true;
                return true;
            }

            return BuildAndRegister();
        }

        private static bool BuildAndRegister()
        {
            if (_inactiveRoot == null)
            {
                _inactiveRoot = new GameObject("FiresAdminTerrain_LiquidWaterPool");
                _inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(_inactiveRoot);
            }

            // ── Preferred source: the FAT asset bundle ──────────────────
            //
            // Round-62: `FiresLiquidWater.prefab` is authored IN the Unity
            // project (Assets/Custom/VAitems/pieces/, bundle 'adminterrain')
            // as a proper duplicate of vanilla TarLiquid with:
            //   * LiquidVolume.m_liquidType = Water
            //   * the vanilla `water_liquid` material on the renderer
            //   * the tar poison Aoe component REMOVED
            // A bundle prefab carries a stable AssetID and needs zero
            // runtime surgery. The runtime TarLiquid clone below stays as
            // the fallback for bundles built before the prefab landed.
            GameObject clone = null;
            string sourceDesc;
            try
            {
                var bundle = VerdantsAscent.FiresAdminTerrain.assetBundle;
                var bundled = bundle != null ? bundle.LoadAsset<GameObject>(PrefabName) : null;
                if (bundled != null && bundled.GetComponentInChildren<LiquidVolume>(true) != null)
                {
                    clone = Object.Instantiate(bundled, _inactiveRoot.transform);
                    clone.name = PrefabName;
                    sourceDesc = "FAT bundle";
                }
                else
                {
                    sourceDesc = null;
                }
            }
            catch { sourceDesc = null; }

            // ── Fallback: runtime clone of vanilla TarLiquid ─────────────
            if (clone == null)
            {
                var source = ZNetScene.instance.GetPrefab(SourcePrefabName);
                if (source == null)
                {
                    FiresLogger.LogError($"[FiresLiquidWater] Neither bundle prefab nor '{SourcePrefabName}' available; cannot register.");
                    return false;
                }
                if (source.GetComponentInChildren<LiquidVolume>(true) == null)
                {
                    FiresLogger.LogError($"[FiresLiquidWater] '{SourcePrefabName}' has no LiquidVolume component?!");
                    return false;
                }

                clone = Object.Instantiate(source, _inactiveRoot.transform);
                clone.name = PrefabName;
                sourceDesc = $"runtime {SourcePrefabName} clone";

                // Water-flavor the runtime clone (the bundle prefab has
                // all of this baked in already).
                var lvClone = clone.GetComponentInChildren<LiquidVolume>(true);
                lvClone.m_liquidType = LiquidType.Water;

                // Strip the tar-pit poison AOE — water shouldn't hurt.
                foreach (var aoe in clone.GetComponentsInChildren<Aoe>(true))
                    Object.DestroyImmediate(aoe);

                // Round-75 — strip the tar bubble FX (LiquidVolume spawns
                // m_randomEffectList entries on random wet cells every few
                // seconds; user: no bubbles on water). The authored bundle
                // prefab ships with an empty list already.
                lvClone.m_randomEffectList = new EffectList();
            }

            // Round-91 — pours are BORN wearing Custom/FiresLiquid. The old
            // flow registered the template with vanilla water_liquid and
            // re-skinned per spawn after the fact — plain pours
            // (UseFiresWaterShader=false), ZDO restores and the merge-spawn
            // path never got the skin and rendered vanilla. The shared pour
            // material binds ONCE here; every instantiation inherits it.
            // water_liquid remains the headless / bundle-missing fallback
            // and the `va_water_pour_shader 0` comparison skin.
            {
                var born = GetSharedPourMaterial();
                if (born == null) born = FindOrBuildWaterLiquidMaterial(clone);
                if (born != null)
                {
                    foreach (var r in clone.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        string oldShader = r.sharedMaterial != null ? r.sharedMaterial.shader?.name : "NULL";
                        r.sharedMaterial = born;
                        FiresLogger.LogInfo($"[FiresLiquidWater] template renderer '{r.name}' bound {oldShader} → {born.name} ({born.shader?.name}) at registration.");
                    }
                }
                else
                {
                    FiresLogger.LogWarning("[FiresLiquidWater] No pour material available at registration — bundle material left as-is (may render magenta).");
                }
            }

            var lv = clone.GetComponentInChildren<LiquidVolume>(true);

            // Round-81 — WATER must be SWIMMABLE, not walk-on-top. Vanilla
            // LiquidVolume's solid MeshCollider (m_collider) is the TAR
            // walkable surface — it holds the player UP at the waterline
            // instead of letting them sink in and swim (why they stood
            // chest-deep instead of swimming). Disable it for water: the
            // player falls in and the LiquidSurface trigger drives real
            // swimming (Character.SetLiquidLevel(Water) → InWater → swim,
            // verified in assembly_valheim/Character.cs). Objects still
            // float via the LiquidSurface IWaterInteractable path, and the
            // trigger box (layer 21) is untouched.
            foreach (var mc in clone.GetComponentsInChildren<MeshCollider>(true))
                mc.enabled = false;

            var nview = clone.GetComponent<ZNetView>();
            if (nview != null)
            {
                nview.m_persistent = true;
            }

            int hash = PrefabName.GetStableHashCode();
            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            _prefab = clone;
            _registered = true;

            FiresLogger.LogInfo($"[FiresLiquidWater] Registered '{PrefabName}' from {sourceDesc} " +
                $"(grid width={lv.m_width} scale={lv.m_scale} maxDepth={lv.m_maxDepth} viscocity={lv.m_viscocity} " +
                $"defaultVolume={lv.m_initialVolume} defaultArea={lv.m_initialArea}).");
            return true;
        }

        // Round-77 — GROUND-TRUTH material resolution. The old name-search
        // (`Resources.FindObjectsOfTypeAll` → first "water_liquid") grabbed
        // OUR OWN BUNDLE's broken copy once the bundle was loaded — same
        // name, ripped-shader bytecode, error-shader magenta at queue 2000
        // (log proof: pours rendered `water_liquid Custom/Tar queue=2000`
        // while real tar pits render queue=2899). Resolution now anchors
        // on the LIVE TarLiquid prefab's shader INSTANCE:
        //   1. tarShader = ZNetScene TarLiquid renderer's material.shader —
        //      guaranteed the working in-game Custom/Tar.
        //   2. A name-matched water_liquid is accepted ONLY if it uses that
        //      exact shader instance AND sits on a transparent queue.
        //   3. Otherwise: clone the live tar material and re-tint with the
        //      dumped water_liquid values — always produces a working
        //      material because the shader comes from the live instance.
        private static Material FindOrBuildWaterLiquidMaterial(GameObject clone)
        {
            Material liveTarMat = null;
            var tarPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(SourcePrefabName) : null;
            if (tarPrefab != null)
            {
                var tarRend = tarPrefab.GetComponentInChildren<MeshRenderer>(true);
                if (tarRend != null) liveTarMat = tarRend.sharedMaterial;
            }
            Shader liveTarShader = liveTarMat != null ? liveTarMat.shader : null;

            if (liveTarShader != null)
            {
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat != null && mat.name == "water_liquid"
                        && mat.shader == liveTarShader
                        && mat.renderQueue >= 2500)
                    {
                        FiresLogger.LogInfo($"[FiresLiquidWater] Found REAL vanilla 'water_liquid' (live Custom/Tar instance, queue={mat.renderQueue}).");
                        return mat;
                    }
                }
            }

            // Rebuild from the live tar material — working shader by
            // construction.
            var rebuildSrc = liveTarMat;
            if (rebuildSrc == null)
            {
                var srcRenderer = clone.GetComponentInChildren<MeshRenderer>(true);
                rebuildSrc = srcRenderer != null ? srcRenderer.sharedMaterial : null;
            }
            if (rebuildSrc == null || rebuildSrc.shader == null)
            {
                FiresLogger.LogWarning("[FiresLiquidWater] No live tar material to rebuild from — keeping current look.");
                return null;
            }

            var rebuilt = new Material(rebuildSrc) { name = "water_liquid_fires" };
            if (rebuilt.HasProperty("_Color")) rebuilt.SetColor("_Color", WATER_LIQUID_COLOR);
            if (rebuilt.HasProperty("_Glossiness")) rebuilt.SetFloat("_Glossiness", WATER_LIQUID_GLOSS);
            if (rebuilt.HasProperty("_Metallic")) rebuilt.SetFloat("_Metallic", 0f);
            if (rebuilt.HasProperty("_BumpScale")) rebuilt.SetFloat("_BumpScale", WATER_LIQUID_BUMPSCALE);
            FiresLogger.LogInfo($"[FiresLiquidWater] Rebuilt water_liquid look from LIVE tar material ({rebuilt.shader?.name} queue={rebuilt.renderQueue}).");
            return rebuilt;
        }

        // ── Spawn — pour a water liquid volume ──────────────────────────
        //
        // center: ruler position; center.y is the FILL level. The volume
        // poured = π·r²·depth where depth = fill level minus ground at
        // the center (min 0.5m so a ground-level ruler still pours).
        // The initial square is sized to the ruler radius; vanilla's sim
        // spreads it from there (settling into depressions, draping over
        // edges) and gels naturally as the flow converges.
        public static GameObject Spawn(Vector3 center, float radius)
            => Spawn(center, radius, 1f);

        public static GameObject Spawn(Vector3 center, float radius, float volumeMultiplier)
            => Spawn(center, radius, volumeMultiplier, 0f, 1f);

        // Round-82 — SPRING path: springMode makes the pour a FiresLiquidRiver
        // (source disk at ruler level, bank-following sloped flow). bankOffset
        // X = how far below the local bank the surface sits.
        //
        // Round-85 — MERGE: if the new spring lands inside an existing pour's
        // grid, add it as an extra source disk on that pour (ONE continuous
        // mesh) instead of spawning an overlapping second LiquidVolume — the
        // two grids' surfaces would double-blend + z-fight at the seam
        // ("the meshes need to properly combine"). Beyond grid range the pours
        // abut at ~equal bank-following height, so the seam is minimal.
        public static GameObject SpawnSpring(Vector3 center, float radius, float bankOffset)
        {
            var merge = FindMergeTarget(center, radius);
            if (merge != null)
            {
                merge.AddSource(center, radius, center.y);
                FiresLogger.LogInfo($"[FiresLiquidWater] spring at ({center.x:F1},{center.y:F1},{center.z:F1}) MERGED into existing pour '{merge.name}'.");
                return merge.gameObject;
            }
            return Spawn(center, radius, 1f, springRate: 1f, bankOffset: bankOffset);
        }

        // STILL-WATER spring (lakes/ponds): same spawn/merge, but a freshly
        // spawned pour runs the dead-level cap (SetStillWater) — standing
        // water levels out instead of bank-terracing. Merging into an
        // existing pour keeps THAT pour's mode (a lake source landing in a
        // river's grid must not flatten the river).
        public static GameObject SpawnSpringStill(Vector3 center, float radius, float bankOffset)
        {
            var merge = FindMergeTarget(center, radius);
            if (merge != null)
            {
                merge.AddSource(center, radius, center.y);
                return merge.gameObject;
            }
            var go = Spawn(center, radius, 1f, springRate: 1f, bankOffset: bankOffset);
            var river = go != null ? go.GetComponent<FiresLiquidRiverController>() : null;
            if (river != null) river.SetStillWater(true);
            return go;
        }

        // Closest live spring whose grid fully contains the new source disk
        // (with a margin so the merged disk isn't clipped by the border-drain).
        // Null → no overlap, spawn a fresh volume.
        private static FiresLiquidRiverController FindMergeTarget(Vector3 center, float radius)
        {
            FiresLiquidRiverController best = null;
            float bestDist = float.MaxValue;
            for (int pi = s_activePours.Count - 1; pi >= 0; pi--)
            {
                var lv = s_activePours[pi];
                if (lv == null) continue;
                var root = lv.transform.root.gameObject;
                if (!root.name.StartsWith(PrefabName)) continue;
                var river = root.GetComponent<FiresLiquidRiverController>();
                if (river == null) continue;

                var lp = lv.transform.position;
                float half = lv.m_width * lv.m_scale * 0.5f;
                // Merge whenever the new CENTER sits well inside the grid —
                // requiring the whole disk inside (old margin included the
                // radius) left 20m-spaced lake sources spawning overlapping
                // un-merged pours (double-blend + z-fight sheets).
                float margin = lv.m_scale * 2f;
                if (Mathf.Abs(center.x - lp.x) > half - margin) continue;
                if (Mathf.Abs(center.z - lp.z) > half - margin) continue;

                float d = Vector2.Distance(new Vector2(center.x, center.z), new Vector2(lp.x, lp.z));
                if (d < bestDist) { bestDist = d; best = river; }
            }
            return best;
        }

        // Round-82 — springRate > 0 marks a SPRING: attaches a
        // FiresLiquidRiverController (source disk + bank-following sim that
        // replaces vanilla's flat settle). bankOffset X = surface depth below
        // the local bank. Water Volume passes springRate 0 → plain vanilla pour.
        public static GameObject Spawn(Vector3 center, float radius, float volumeMultiplier, float springRate, float bankOffset)
        {
            if (!IsRegistered && !Ensure())
            {
                FiresLogger.LogError("[FiresLiquidWater] Spawn failed — prefab not registered.");
                return null;
            }

            // Round-68 — physics probe for the pour's base. GetGroundHeight
            // is heightmap-blind (meters low on FAT custom terrain), which
            // both buried the pour GO under the visible ground and inflated
            // depth/volume. center is the ruler position VERBATIM (round-69);
            // the probe finds the real collider under it.
            float groundY = center.y;
            if (Tools.WaterBrushDrag.TryProbeGroundBelow(center, out float g))
            {
                groundY = g;
            }
            else if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(center, out float gh))
            {
                groundY = gh;
            }

            // Round-77 — BASIN-INTEGRATED volume (user spec: "drop our pool
            // at the cursor with the volume calculated so that it doesn't
            // spread past the low spots it's hovered over"). The old
            // π·r²·depth cylinder poured far more than the hovered dent
            // could hold, so the excess flooded outward. Now: sample the
            // real ground (physics probe — heightmap, custom tiles, rocks,
            // pieces) on a grid across the ruler circle and integrate the
            // deficit up to the fill level. The pour is exactly what the
            // basin holds — it settles into the indent and stops.
            float step = Mathf.Clamp(radius / 8f, 0.5f, 2f);
            float cellArea = step * step;
            float basinVolume = 0f;
            int samples = 0, wetSamples = 0;
            float deepest = 0f;
            for (float dz = -radius; dz <= radius; dz += step)
            {
                for (float dx = -radius; dx <= radius; dx += step)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    samples++;
                    var sp = new Vector3(center.x + dx, center.y, center.z + dz);
                    if (!Tools.WaterBrushDrag.TryProbeGroundBelow(sp, out float gy)) continue;
                    float deficit = center.y - gy;
                    if (deficit <= 0f) continue;
                    basinVolume += deficit * cellArea;
                    wetSamples++;
                    if (deficit > deepest) deepest = deficit;
                }
            }

            var lvPrefab = _prefab.GetComponentInChildren<LiquidVolume>(true);
            float cellScale = Mathf.Max(0.01f, lvPrefab.m_scale);
            int area = Mathf.Clamp(Mathf.CeilToInt(radius * 2f / cellScale),
                2, Mathf.Max(2, lvPrefab.m_width - 2));

            // Floor: a click always pours a visible puddle; cap: never more
            // than the grid can express.
            float volume = Mathf.Clamp(
                basinVolume * Mathf.Max(0.1f, volumeMultiplier),
                5f,
                Mathf.PI * radius * radius * lvPrefab.m_maxDepth);
            float depth = Mathf.Max(0.5f, deepest);
            FiresLogger.LogInfo($"[FiresLiquidWater] Basin integration: {wetSamples}/{samples} samples below fill, " +
                $"basin={basinVolume:F0}m³ deepest={deepest:F1}m → pouring {volume:F0}m³ (×{Mathf.Max(0.1f, volumeMultiplier):F1}).");

            // Round-74 — THE DESTROY BUG, caught by the ResetZDO tracer:
            // the old pattern (instantiate under the inactive root →
            // SetParent(null) → set position → SetActive) ACTIVATED the
            // clone at SetParent(null), so ZNetView.Awake registered the
            // ZDO at world origin (TRACE: pos=(0.00, 0.00, 0.00)). Liquid
            // volumes have no transform sync, so the ZDO stayed at zone
            // (0,0) and ZNetScene's zone sweep destroyed the pour within
            // a tick — every pour since 0.2.0 died this way. Correct
            // pattern: momentarily flip the TEMPLATE inactive so the clone
            // is born activeSelf=false at its REAL position, set fields,
            // then activate — Awake fires in the right zone.
            GameObject go;
            bool templateWasActive = _prefab.activeSelf;
            _prefab.SetActive(false);
            try
            {
                go = Object.Instantiate(_prefab,
                    new Vector3(center.x, groundY, center.z), Quaternion.identity);
                go.name = PrefabName;
            }
            finally
            {
                // Template must stay activeSelf=true — ZNetScene's restore
                // path (world reload) instantiates it as-is.
                _prefab.SetActive(templateWasActive);
            }

            var lv = go.GetComponentInChildren<LiquidVolume>(true);
            lv.m_initialVolume = volume;
            lv.m_initialArea = area;

            go.SetActive(true);

            // Round-63 — scheduled render-chain diagnostics on every spawned
            // pour (0.5s → 15s). Round-86 — gated behind the FiresLiquid
            // Diagnostics config toggle (dev-only; off = not even attached).
            if (FiresLiquidConfig.Diagnostics != null && FiresLiquidConfig.Diagnostics.Value)
                go.AddComponent<FiresLiquidWaterDebug>();

            var nv = go.GetComponent<ZNetView>();

            // Round-82 — SPRING = FiresLiquidRiver. Attach the controller that
            // replaces vanilla's flat-settle sim with a source-disk +
            // bank-following flow. fillY (center.y) + bankOffset persist in ZDO
            // so the river re-attaches its controller after reload.
            if (springRate > 0f && nv != null && nv.IsValid())
            {
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_RATE, springRate);
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_FILL, center.y);
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_BANKOFFSET, bankOffset);
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_RADIUS, radius);
                var river = go.GetComponent<FiresLiquidRiverController>()
                    ?? go.AddComponent<FiresLiquidRiverController>();
                river.m_fillY = center.y;
                river.m_bankOffset = bankOffset;
                river.m_sourceCenter = center;
                river.m_sourceRadius = radius;
                // Round-85 — persist the primary source so a later merge restores whole.
                river.SeedPrimarySource();

                // Round-84 — a new source unfreezes nearby FROZEN springs so
                // they resume updating the mesh with the added water ("until
                // another water source is placed").
                UnfreezeOverlapping(center, radius, go);
            }

            string zdoInfo = nv != null && nv.GetZDO() != null
                ? $"zdo={nv.GetZDO().m_uid} zdoPos={nv.GetZDO().GetPosition()}"
                : "zdo=NONE";
            FiresLogger.LogInfo($"[FiresLiquidWater] Spawned pour at ({center.x:F1},{groundY:F1},{center.z:F1}) fillY={center.y:F1} | " +
                $"r={radius:F1} depth={depth:F1} volume={volume:F0}m³ initialArea={area} cells " +
                $"(grid {lv.m_width}x{lv.m_width} @ {lv.m_scale}m = {lv.m_width * lv.m_scale:F0}m extent) | {zdoInfo}");
            return go;
        }

        // Round-91 — ONE shared pour material for everything custom-water:
        // the registered template (born with it), every pour instance, the
        // VoxelGenWater river/lake surfaces. Built once; the config
        // hot-reload and ocean-texture backfill mutate THIS instance in
        // place, so nothing ever re-binds a renderer after spawn.
        private static Material s_pourMaterial;

        public static Material GetSharedPourMaterial()
        {
            if (s_pourMaterial != null) return s_pourMaterial;
            var m = BuildFiresWaterPourMaterial();
            if (m != null && m.shader != null && m.shader.name == "Custom/FiresLiquid")
                s_pourMaterial = m;   // only cache the real thing — wrap fallback retries later
            return m;
        }

        // Re-copy the live ocean textures + config sliders onto the shared
        // instance (e.g. after the ocean material appears post-registration).
        public static void RefreshSharedPourMaterial()
        {
            var mat = s_pourMaterial;
            if (mat == null) return;
            var oceanMat = WaterMaterialManager.GetWaterMaterial(LiquidType.Water);
            if (oceanMat != null)
            {
                foreach (var tp in s_liquidTexProps)
                    if (oceanMat.HasProperty(tp) && mat.HasProperty(tp))
                        mat.SetTexture(tp, oceanMat.GetTexture(tp));
            }
            ApplyFiresWaterNormals(mat);   // ours over vanilla's creased ocean normals
            var hdFoam = GetFiresFoamHD();
            if (hdFoam != null)
            {
                if (mat.HasProperty("_FoamTex")) mat.SetTexture("_FoamTex", hdFoam);
                if (mat.HasProperty("_FoamHighTex")) mat.SetTexture("_FoamHighTex", hdFoam);
                if (mat.HasProperty("_RandomFoamTex")) mat.SetTexture("_RandomFoamTex", hdFoam);
            }
            FiresLiquidConfig.Apply(mat);
        }

        // Round-66 — skin one pour with either the FiresWater wrap (the
        // combiner meshes' material, via WaterMaterialManager → FUC
        // WaterShaderBridge) or the vanilla water_liquid look. The
        // LiquidVolume sim rebuilds its MESH continuously but never
        // touches the renderer's material, so a one-time assignment
        // sticks for the pour's lifetime.
        // Round-77 — the FiresWater pour skin is a per-pour CLONE of the
        // wrapped zone-water material with _FiresLiquidGrid=1 (the shader's
        // wet-mask: clips dry cells of the 64m draped sim mesh via vertex
        // alpha) and calmed waves. Setting the flag on the SHARED wrap
        // would clip zone water by its unused vertex alpha — never do that.
        // Round-83 — DEDICATED Custom/FiresLiquid shader for all custom water.
        // Prefer it; fall back to a per-pour clone of the FiresWater ocean wrap
        // (old path) if the FiresLiquid shader isn't in the loaded bundle yet.
        private static Material BuildFiresWaterPourMaterial()
        {
            // Source of tuned texture/colour values: the live FiresWater ocean
            // material (has the real normal/foam textures assigned).
            var oceanMat = WaterMaterialManager.GetWaterMaterial(LiquidType.Water);

            var liquidShader = GetFiresLiquidShader();
            if (liquidShader != null)
            {
                var mat = new Material(liquidShader) { name = "FiresLiquid_Pour" };
                // FiresLiquid shares FiresWater's property NAMES, so copy the
                // ocean material's assigned textures (the shader defaults are
                // "bump"/"white" placeholders) — colours/floats use the
                // FiresLiquid defaults (= tuned FiresWater defaults).
                if (oceanMat != null)
                {
                    foreach (var tp in s_liquidTexProps)
                        if (oceanMat.HasProperty(tp) && mat.HasProperty(tp))
                            mat.SetTexture(tp, oceanMat.GetTexture(tp));
                }
                // Round-85 — override the pixelated vanilla foam with the HD
                // baked foam (2K seamless) on all three foam layers; the shader's
                // per-layer tiling/speed still differentiates them.
                ApplyFiresWaterNormals(mat);   // ours over vanilla's creased ocean normals
                var hdFoam = GetFiresFoamHD();
                if (hdFoam != null)
                {
                    if (mat.HasProperty("_FoamTex")) mat.SetTexture("_FoamTex", hdFoam);
                    if (mat.HasProperty("_FoamHighTex")) mat.SetTexture("_FoamHighTex", hdFoam);
                    if (mat.HasProperty("_RandomFoamTex")) mat.SetTexture("_RandomFoamTex", hdFoam);
                }
                if (mat.HasProperty("_FiresLiquidGrid")) mat.SetFloat("_FiresLiquidGrid", 1f);
                if (mat.HasProperty("_WaveAmplitude")) mat.SetFloat("_WaveAmplitude", 0.15f);
                if (mat.HasProperty("_FiresStencilComp"))
                {
                    mat.SetFloat("_FiresStencilComp", 8f);   // Always — never stencil-culled
                    mat.SetFloat("_FiresStencilWriteMask", 0f);
                }
                // Round-84 — apply the live config sliders (colour/hue/gloss/
                // fresnel/normals/edge). These override the shader defaults so
                // the user can tune in-game; whatever they land on becomes the
                // baked default.
                FiresLiquidConfig.Apply(mat);
                FiresLogger.LogInfo($"[FiresLiquidWater] built Custom/FiresLiquid material (queue={mat.renderQueue}).");
                return mat;
            }

            // Fallback — clone the FiresWater ocean wrap with liquid-grid + stencil bypass (round-77/78 path). The
            // broker already warned if Core's shader bundle lacks Custom/FiresLiquid; on a dedicated server there is no
            // GPU and the shader is absent by design.
            FiresLogger.LogVerbose("[FiresLiquidWater] Custom/FiresLiquid is not live here — using the FiresWater wrap.");
            if (oceanMat == null) return null;
            if (oceanMat.shader == null || !oceanMat.shader.name.Contains("FiresWater")) return null;
            var wrap = new Material(oceanMat) { name = "FiresWater_LiquidPour" };
            if (wrap.HasProperty("_FiresLiquidGrid")) wrap.SetFloat("_FiresLiquidGrid", 1f);
            if (wrap.HasProperty("_WaveAmplitude")) wrap.SetFloat("_WaveAmplitude", 0.15f);
            if (wrap.HasProperty("_FiresStencilComp"))
            {
                wrap.SetFloat("_FiresStencilComp", 8f);
                wrap.SetFloat("_FiresStencilWriteMask", 0f);
            }
            return wrap;
        }

        private static readonly string[] s_liquidTexProps =
        {
            "_Normal", "_NormalFine", "_FoamTex", "_FoamHighTex", "_RandomFoamTex",
        };

        // ── Round-84 — SWIM registry. Our pours are vanilla LiquidVolumes,
        // not combiners/legacy water, so CustomWaterPatches' swim query
        // (GetValidWaterLevelForCharacter → InWater/IsSwimming/GetLiquidLevel
        // postfixes) never sees them. Track every live pour and expose its
        // surface so the swim patches can trigger swimming at the water top.
        private static readonly System.Collections.Generic.List<LiquidVolume> s_activePours =
            new System.Collections.Generic.List<LiquidVolume>();

        // 0.2.776: the live pours, for the per-source water report (va_water_at).
        internal static System.Collections.Generic.IReadOnlyList<LiquidVolume> ActivePours => s_activePours;

        internal static void RegisterPour(LiquidVolume lv)
        {
            if (lv != null && !s_activePours.Contains(lv)) s_activePours.Add(lv);
        }

        // W2 — idempotency check for auto-placed hydrology water: is any LOADED
        // pour centered within `range` (XZ) of the point?
        public static bool HasPourNear(Vector3 pos, float range)
        {
            float r2 = range * range;
            for (int i = s_activePours.Count - 1; i >= 0; i--)
            {
                var lv = s_activePours[i];
                if (lv == null) { s_activePours.RemoveAt(i); continue; }
                var lp = lv.transform.position;
                float dx = pos.x - lp.x, dz = pos.z - lp.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        // 0.2.763 Vmop (Fire 10-06: "Vmop also doesn't seem to work on this one tiny piece of water in the cave"; his log: "removed
        // 0 generated column(s), 2 drawn water bod(ies) rebuilt dry, recorded 0" - the water was an old cave pool, a pour, which
        // the mop never touched): every loaded pour whose grid reaches into the ring, at the mop's height (within the grid's
        // depth band), is destroyed - claimed, then removed through ZNetScene, so it is gone for every peer and from the save.
        // Returns how many went.
        public static int RemovePoursIn(Vector3 center, float radius)
        {
            int removed = 0;
            for (int i = s_activePours.Count - 1; i >= 0; i--)
            {
                var lv = s_activePours[i];
                if (lv == null) { s_activePours.RemoveAt(i); continue; }
                Vector3 p = lv.transform.position;
                float half = lv.m_width * lv.m_scale * 0.5f;
                float dx = Mathf.Max(Mathf.Abs(center.x - p.x) - half, 0f), dz = Mathf.Max(Mathf.Abs(center.z - p.z) - half, 0f);
                if (dx * dx + dz * dz > radius * radius) continue;
                if (Mathf.Abs(center.y - p.y) > radius + lv.m_maxDepth + 4f) continue;   // a pour far above or below the mop
                var root = lv.transform.root.gameObject;
                var nv = root.GetComponentInChildren<ZNetView>(true);
                try
                {
                    if (nv != null && nv.IsValid())
                    {
                        if (!nv.IsOwner()) nv.ClaimOwnership();
                        ZNetScene.instance?.Destroy(root);
                    }
                    else Object.Destroy(root);
                    s_activePours.RemoveAt(i);
                    removed++;
                }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresWater] Vmop: removing the pour at {p} threw: {ex.Message}"); }
            }
            if (removed > 0)
                FiresLogger.LogDebug($"[FiresWater] Vmop removed {removed} pour(s) (spring / water volume) reaching into r={radius:0.0} at ({center.x:0.0},{center.y:0.0},{center.z:0.0}).");
            return removed;
        }

        // Round-84 — unfreeze frozen springs whose grid the new source overlaps
        // (skips the just-spawned pour `self`).
        private static void UnfreezeOverlapping(Vector3 center, float radius, GameObject self)
        {
            var cxz = new Vector2(center.x, center.z);
            for (int pi = s_activePours.Count - 1; pi >= 0; pi--)
            {
                var lv = s_activePours[pi];
                if (lv == null) continue;
                var root = lv.transform.root.gameObject;
                if (root == self || !root.name.StartsWith(PrefabName)) continue;
                var river = root.GetComponent<FiresLiquidRiverController>();
                if (river == null || !river.m_frozen) continue;
                float reach = radius + lv.m_width * lv.m_scale * 0.5f;
                if (Vector2.Distance(new Vector2(lv.transform.position.x, lv.transform.position.z), cxz) <= reach)
                    river.SetFrozen(false);
            }
        }

        // Highest water surface (world Y) of any pour whose grid covers `pos`
        // AND whose water column actually reaches down to `pos` — the query
        // point must sit ABOVE the pour's BED at that cell. Without the bed
        // check, standing UNDER a floating voxel rock that carries water made
        // every water query (env/fog/swim) think the player was submerged in
        // the water 20m overhead (round-87 fix; real fix long-term = voxel
        // water). Dry cells still return below-ground via GetSurface.
        private const float MinPourDepth = 0.01f;   // 0.2.779: a pour cell shallower than this holds no water (vanilla's 0.001 dry test, rounded up)

        internal static bool IsPour(LiquidVolume lv) => lv != null && s_activePours.Contains(lv);

        // 0.2.779 (swim_test 10-06: a 64 m shore pour claimed the sea beside it at its DRY bed, 13.74 then and 44.91 in Q1, and
        // swimmers there floated on that line): whether the pour holds liquid at the point. Vanilla's LiquidVolume.GetSurface
        // answers bed + height - 0.5 where the depth is 0, so every caller that treats its answer as water must ask this first.
        // The four grid corners around the point, as GetSurface interpolates them. True when the grid cannot be read.
        internal static bool PourCellWet(LiquidVolume lv, Vector3 pos)
        {
            var depths = FiresLiquidGridOps.Depths(lv);
            if (depths == null) return true;
            int num = lv.m_width + 1;
            if (depths.Count < num * num) return true;
            var lp = lv.transform.position;
            float half = lv.m_width * lv.m_scale * 0.5f;
            float gx = Mathf.Clamp((pos.x - lp.x + half) / lv.m_scale, 0f, num - 1);
            float gz = Mathf.Clamp((pos.z - lp.z + half) / lv.m_scale, 0f, num - 1);
            int x0 = (int)gx, z0 = (int)gz, x1 = Mathf.Min(x0 + 1, num - 1), z1 = Mathf.Min(z0 + 1, num - 1);
            // 0.2.780: bilinear, as GetSurface interpolates it (a shore cell with one wet corner can interpolate to ~0 and
            // GetSurface then answers its bed - 0.5; the corner max called that water).
            float tx = gx - x0, tz = gz - z0;
            float wet = Mathf.Lerp(Mathf.Lerp(depths[z0 * num + x0], depths[z0 * num + x1], tx),
                                   Mathf.Lerp(depths[z1 * num + x0], depths[z1 * num + x1], tx), tz);
            return wet > MinPourDepth;
        }

        public static float GetSurfaceAt(Vector3 pos)
        {
            float best = float.MinValue;
            for (int i = s_activePours.Count - 1; i >= 0; i--)
            {
                var lv = s_activePours[i];
                if (lv == null) { s_activePours.RemoveAt(i); continue; }
                var lp = lv.transform.position;
                float half = lv.m_width * lv.m_scale * 0.5f;
                if (Mathf.Abs(pos.x - lp.x) > half || Mathf.Abs(pos.z - lp.z) > half) continue;

                // Bed at the covering cell = transform.y + m_heights[cell].
                var heights = FiresLiquidGridOps.Heights(lv);
                if (heights != null)
                {
                    int num = lv.m_width + 1;
                    int cx = Mathf.Clamp(Mathf.RoundToInt((pos.x - lp.x + half) / lv.m_scale), 0, num - 1);
                    int cz = Mathf.Clamp(Mathf.RoundToInt((pos.z - lp.z + half) / lv.m_scale), 0, num - 1);
                    int idx = cz * num + cx;
                    if (idx >= 0 && idx < heights.Count)
                    {
                        float bedY = lp.y + heights[idx];
                        if (pos.y < bedY - 1.5f) continue;   // under the bed — not in this water
                    }
                    if (!PourCellWet(lv, pos)) continue;   // 0.2.779: a dry cell is not water
                }

                float surf = lv.GetSurface(pos);
                if (surf > best) best = surf;
            }
            return best;
        }

        // Register live pours (spawned AND restored-from-ZDO), from FAT's one
        // LiquidVolume.Awake hook (FatVanillaHooks).
        internal static void OnLiquidVolumeAwake(LiquidVolume volume)
        {
            try
            {
                if (volume != null &&
                    volume.transform.root.gameObject.name.StartsWith(PrefabName))
                    RegisterPour(volume);
            }
            catch { }
        }

        // Custom/FiresLiquid, the HD foam (FiresFoamHD, 2K seamless) and our own water normals (water_nrm /
        // water_nrm_detail, the maps the sandbox water was authored against; vanilla's ocean normals read as creased
        // geometry on a river) all ship in Core's shader bundle, so FAT's water no longer depends on FiresTossinShade.
        private static Shader GetFiresLiquidShader() => FiresCore.Materials.FiresShaderBroker.Get("Custom/FiresLiquid");

        internal static Texture2D GetFiresWaterNormal(bool fine)
            => FiresCore.Materials.FiresShaderBroker.GetTexture(fine ? "water_nrm_detail" : "water_nrm");

        // Applied wherever the foam override is, so creation and refresh agree.
        internal static void ApplyFiresWaterNormals(Material mat)
        {
            if (mat == null) return;
            if (FiresLiquidConfig.UseFiresNormals != null && !FiresLiquidConfig.UseFiresNormals.Value) return;
            var n = GetFiresWaterNormal(false);
            var nf = GetFiresWaterNormal(true);
            if (n != null && mat.HasProperty("_Normal")) mat.SetTexture("_Normal", n);
            if (nf != null && mat.HasProperty("_NormalFine")) mat.SetTexture("_NormalFine", nf);
        }

        internal static Texture2D GetFiresFoamHD() => FiresCore.Materials.FiresShaderBroker.GetTexture("FiresFoamHD");

        // Round-90 — restored springs (ZNetScene re-instantiates the TEMPLATE,
        // which wears vanilla water_liquid) must re-apply the FiresLiquid skin.
        // Called from the controller re-attach patch + controller init.
        internal static void ApplyFiresLiquidSkin(GameObject root) => ApplyPourMaterial(root, true);

        private static void ApplyPourMaterial(GameObject go, bool firesWater)
        {
            try
            {
                Material mat = firesWater
                    ? GetSharedPourMaterial()
                    : FindOrBuildWaterLiquidMaterial(go);
                if (mat == null)
                {
                    FiresLogger.LogWarning($"[FiresLiquidWater] ApplyPourMaterial: no {(firesWater ? "FiresWater" : "water_liquid")} material available — keeping current.");
                    return;
                }
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                    r.sharedMaterial = mat;
                FiresLogger.LogInfo($"[FiresLiquidWater] Pour '{go.name}' skinned with {mat.name} ({mat.shader?.name}) queue={mat.renderQueue}");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresLiquidWater] ApplyPourMaterial threw: {ex.Message}");
            }
        }

        // Re-skin every live pour to the current UseFiresWaterShader
        // choice. Returns how many pours were touched.
        public static int ApplyShaderToLivePours()
        {
            int n = 0;
            for (int pi = s_activePours.Count - 1; pi >= 0; pi--)
            {
                var lv = s_activePours[pi];
                if (lv == null) continue;
                var root = lv.transform.root.gameObject;
                if (!root.name.StartsWith(PrefabName)) continue;
                ApplyPourMaterial(root, UseFiresWaterShader);
                n++;
            }
            FiresLogger.LogInfo($"[FiresLiquidWater] ApplyShaderToLivePours firesWater={UseFiresWaterShader} → {n} pour(s)");
            return n;
        }

        // Round-84 — config hot-reload path. Round-91: the shared material
        // mutates IN PLACE (every pour + gen-water surface reads the same
        // instance), then any pour toggled off the skin gets re-bound.
        public static int ReapplyFiresLiquidToAll()
        {
            RefreshSharedPourMaterial();
            var shared = GetSharedPourMaterial();
            int n = 0;
            for (int pi = s_activePours.Count - 1; pi >= 0; pi--)
            {
                var lv = s_activePours[pi];
                if (lv == null) continue;
                var root = lv.transform.root.gameObject;
                if (!root.name.StartsWith(PrefabName)) continue;
                if (shared != null)
                {
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                        if (r.sharedMaterial != shared) r.sharedMaterial = shared;
                }
                n++;
            }
            return n;
        }

        // Round-91 — pours 32m apart overlap 64m grids, and a rebuilt mesh's
        // recalculated bounds can hug a sparse wet region; at some camera
        // angles one volume frustum-culls while its neighbour stays visible —
        // a sharp square hole in the water sheet. Force full-grid bounds with
        // vertical headroom after every rebuild.
        private static readonly AccessTools.FieldRef<LiquidVolume, Mesh> s_meshRef =
            AccessTools.FieldRefAccess<LiquidVolume, Mesh>("m_mesh");

        private static void ExpandLiquidBounds(LiquidVolume lv)
        {
            try
            {
                var mesh = s_meshRef(lv);
                if (mesh == null) return;
                float span = Mathf.Max(8f, lv.m_width * lv.m_scale);
                var b = mesh.bounds;
                mesh.bounds = new Bounds(b.center, new Vector3(
                    Mathf.Max(b.size.x, span), b.size.y + 30f, Mathf.Max(b.size.z, span)));
            }
            catch { }
        }

        [HarmonyPatch(typeof(LiquidVolume), "PostBuildMesh")]
        private static class LiquidVolume_PostBuildMesh_Bounds
        {
            [HarmonyPostfix]
            private static void Postfix(LiquidVolume __instance) => ExpandLiquidBounds(__instance);
        }

        [HarmonyPatch(typeof(LiquidVolume), "RebuildMesh")]
        private static class LiquidVolume_RebuildMesh_Bounds
        {
            [HarmonyPostfix]
            private static void Postfix(LiquidVolume __instance) => ExpandLiquidBounds(__instance);
        }
    }

    // 0.2.779: a FAT pour's trigger (vanilla LiquidSurface.UpdateFloaters) writes GetSurface to everything inside its square
    // every update, and Character.SetLiquidLevel keeps the last writer. Over a dry cell that wrote the pour's bed - 0.5 over the
    // sea's level (swim_test 10-06: a float line 12.24 under a sea at 26; "NOT DIVING"). A pour now writes only where it holds
    // liquid; over a dry cell it writes nothing while another liquid volume of that type also holds the body (that one keeps
    // writing its own level), and "no liquid" (-10000) when it is the body's only one, so nothing swims on a dry pour bed.
    // Vanilla LiquidSurfaces (tar pits, Ashlands) are untouched.
    [HarmonyPatch(typeof(LiquidSurface), "UpdateFloaters")]
    internal static class PourFloatersOnlyWhereWet
    {
        internal static readonly AccessTools.FieldRef<LiquidSurface, LiquidVolume> LiquidOf =
            AccessTools.FieldRefAccess<LiquidSurface, LiquidVolume>("m_liquid");
        private static readonly AccessTools.FieldRef<LiquidSurface, System.Collections.Generic.List<IWaterInteractable>> InWaterOf =
            AccessTools.FieldRefAccess<LiquidSurface, System.Collections.Generic.List<IWaterInteractable>>("m_inWater");

        private static bool Prefix(LiquidSurface __instance)
        {
            var lv = LiquidOf(__instance);
            if (!FiresLiquidWaterPrefab.IsPour(lv)) return true;
            var inWater = InWaterOf(__instance);
            if (inWater == null || inWater.Count == 0) return false;
            for (int i = inWater.Count - 1; i >= 0; i--)
            {
                var who = inWater[i];
                Transform t = who?.GetTransform();
                if (who == null || !t) { inWater.RemoveAt(i); continue; }
                Vector3 p = t.position;
                if (FiresLiquidWaterPrefab.PourCellWet(lv, p))
                {
                    who.SetLiquidLevel(lv.GetSurface(p), lv.m_liquidType, __instance);
                    continue;
                }
                int holders = who.Increment(lv.m_liquidType) - 1;   // how many volumes of this type hold it (read, then put back)
                who.Decrement(lv.m_liquidType);
                if (holders <= 1) who.SetLiquidLevel(-10000f, lv.m_liquidType, __instance);
            }
            return false;
        }
    }

    // 0.2.780 (swim_test 07:35 on 0.2.779: the dry pour still read 53.74, its bed - 0.5, though FAT's own GetSurfaceAt said none):
    // vanilla's Floating.GetWaterLevel / GetLiquidLevel / IsUnderWater each take the max with LiquidSurface.GetSurface of every
    // liquid trigger at the point, and over a dry cell that is the bed. For a FAT pour a dry cell now answers "no liquid" (-10000),
    // so boats, items, the underwater view and every other caller see no water there. Vanilla tar pits and Ashlands untouched.
    [HarmonyPatch(typeof(LiquidSurface), nameof(LiquidSurface.GetSurface))]
    internal static class PourSurfaceOnlyWhereWet
    {
        private static void Postfix(LiquidSurface __instance, Vector3 p, ref float __result)
        {
            var lv = PourFloatersOnlyWhereWet.LiquidOf(__instance);
            if (FiresLiquidWaterPrefab.IsPour(lv) && !FiresLiquidWaterPrefab.PourCellWet(lv, p)) __result = -10000f;
        }
    }
}
