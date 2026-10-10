using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Builds and registers the "VATile" ZNetView prefab that backs every
    // TileAnchor. Cloned at runtime from piece_dirtfloor_vad in the
    // FiresAdminTerrain bundle so the SoftReference / AssetID is inherited
    // cleanly (per memory `feedback_runtime_prefab_trap` — never new GameObject
    // + register; always clone from a bundle prefab).
    //
    // FAKE-NULL HYGIENE — matches the canonical pattern from Ascend.cs:540
    // and FAP's VAExternalBundleLoader.ScrubFakeNulls. Other mods loaded on
    // the same profile (BalrondAmazingNature, FAP itself, VegOverhaul,
    // ExpandWorldData, TechPriestDhakhar, etc.) can leave Unity fake-null
    // GameObjects in ZNetScene.m_prefabs / m_namedPrefabs. When IH's
    // SpriteHelper.FindSprite later does
    //
    //     m_namedPrefabs.GroupBy(kvp => kvp.Value.name.ToLower())
    //
    // on those dicts, the fake-null entry throws NRE in Object.GetName(),
    // CustomMenu.AddTools bails, and ZERO hammer tools render — even though
    // the YAML parsed fine and our own prefab is valid.
    //
    // Defense-in-depth:
    //   1. Scrub m_prefabs + m_namedPrefabs of fake-nulls BEFORE registering
    //   2. Validate our clone's .name is accessible AFTER Instantiate
    //   3. Scrub AGAIN after registering (paranoia, race compensation)
    //   4. Clear IH's SpriteHelper.PrefabNames cache via reflection so IH
    //      rebuilds from the scrubbed dict on next FindSprite call
    //   5. Patch PieceTable.UpdateAvailable.Prefix to scrub right before
    //      IH's UpdateAvailable.Postfix runs — catches late prefab churn
    //      between our Awake postfix and the player equipping a hammer
    //
    // Visual / behavior strip is DISABLE-not-DESTROY: every Renderer,
    // Collider, AudioSource, and non-essential MonoBehaviour gets
    // `.enabled = false`, but the components themselves stay attached so
    // the GameObject's Unity object state stays fully valid for cross-mod
    // prefab iteration.
    //
    // We register only in m_namedPrefabs (not m_prefabs) — ZoneSystem and
    // ZNetScene.GetPrefab(hash) lookups go through m_namedPrefabs; m_prefabs
    // is the iteration surface for build menus / PieceTables where our
    // anchor has no business appearing.
    public static class TileAnchorPrefab
    {
        public const string PrefabName = "VATile";
        public const string SourcePrefabName = "piece_dirtfloor_vad";

        private static GameObject _prefab;
        private static GameObject _inactiveRoot; // inactive prefab pool (see GetOrCreateInactiveRoot)
        private static bool _registered;

        // Cached reflection handles for InfinityHammer.SpriteHelper.PrefabNames.
        // Looked up once on first use; null = IH not loaded or API changed.
        private static FieldInfo _ihSpriteHelperPrefabNamesField;
        private static bool _ihReflectionAttempted;

        public static GameObject Prefab => _prefab;
        public static bool IsRegistered => _registered && _prefab != null;

        public static bool Ensure()
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogWarning("[TileAnchorPrefab] Ensure: ZNetScene.instance is null — call after ZNetScene.Awake.");
                return false;
            }

            int hash = PrefabName.GetStableHashCode();

            // ── Session-change detection ────────────────────────────────────
            // Static state (_registered, _prefab, _inactiveRoot) survives logout
            // → main menu → login because the AppDomain stays alive. But the
            // NEW ZNetScene that comes up on the second world-load has a fresh
            // m_namedPrefabs dict that does NOT contain our cached _prefab.
            // Old behavior: _registered=true → Ensure() short-circuits → VATile
            // missing from new dict → ZoneSystem.CreateObjects can't spawn
            // anchors from their saved ZDOs → terrain looks empty even though
            // the world save still has all the data.
            //
            // _prefab itself survives across the teardown because it's parented
            // under _inactiveRoot which has DontDestroyOnLoad. So we just need
            // to re-insert it into the new dict — no bundle reload, no clone,
            // no fresh ZNetView wiring.
            if (_registered && _prefab != null)
            {
                bool stillInScene = ZNetScene.instance.m_namedPrefabs != null
                    && ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var inSceneRef)
                    && inSceneRef == _prefab;

                if (stillInScene)
                {
                    FiresLogger.LogVerbose("[TileAnchorPrefab] Ensure: already registered in current ZNetScene, no-op.");
                    return true;
                }

                FiresLogger.LogVerbose("[TileAnchorPrefab] Ensure: session-change detected (logout/login same process) — re-inserting cached VATile into new ZNetScene dict.");
                ScrubFakeNulls("pre-reinsert");
                ZNetScene.instance.m_namedPrefabs[hash] = _prefab;
                ScrubFakeNulls("post-reinsert");

                if (ValidateRegistration(hash))
                {
                    ClearInfinityHammerSpriteCache("session-change");
                    // Drop any stale TileAnchor registry entries pointing at
                    // destroyed-but-fake-null anchors from the previous session.
                    // New anchors will register fresh on their Awake.
                    TileAnchorRegistry.ScrubFakeNulls();
                    try { WecTerrainBridge.Initialize(); }
                    catch (Exception ex) { FiresLogger.LogError($"[TileAnchorPrefab] WecTerrainBridge.Initialize threw - {ex}"); }
                    FiresLogger.LogVerbose("[TileAnchorPrefab] Ensure: SUCCESS via session-change reinsert.");
                    EmitRegistrationSummary();
                    return true;
                }

                // Cached prefab failed validation (Unity destroyed it under us,
                // or some mod stripped components). Fall through to full re-register.
                FiresLogger.LogWarning("[TileAnchorPrefab] Ensure: cached prefab failed validation after re-insert — clearing static cache and re-registering from scratch.");
                _registered = false;
                _prefab = null;
                _inactiveRoot = null;
            }

            int beforeDictCount = ZNetScene.instance.m_namedPrefabs?.Count ?? -1;
            int beforeListCount = ZNetScene.instance.m_prefabs?.Count ?? -1;
            FiresLogger.LogVerbose($"[TileAnchorPrefab] Ensure: starting | hash={hash} | dict={beforeDictCount} list={beforeListCount}");

            // ZNetScene.Awake's "tile anchor" step is 146-215 ms of one join frame (R42); this says which part.
            var steps = new StepTimes();

            // ── Step 1: scrub pre-existing fake-nulls left by other mods ──
            ScrubFakeNulls("pre-register");
            steps.Lap("scrub");

            // ── Step 2: idempotency check ─────────────────────────────────
            if (ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var existing) && existing != null)
            {
                _prefab = existing;
                _registered = true;
                FiresLogger.LogVerbose($"[TileAnchorPrefab] '{PrefabName}' already in m_namedPrefabs (hash={hash}); reusing.");
                ClearInfinityHammerSpriteCache("idempotent-reuse");
                EmitRegistrationSummary();
                return true;
            }

            // ── Step 3: load source prefab from bundle ────────────────────
            GameObject source = LoadSourcePrefab();
            if (source == null) return false;
            steps.Lap("bundle load");

            // ── Step 4: clone + configure + validate ──────────────────────
            var clone = CloneAndPrepare(source);
            if (clone == null) return false;
            steps.Lap("clone");

            // ── Step 5: register in m_namedPrefabs only ───────────────────
            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            FiresLogger.LogVerbose($"[TileAnchorPrefab] Added '{PrefabName}' to m_namedPrefabs (hash={hash}). Dict size {beforeDictCount} → {ZNetScene.instance.m_namedPrefabs.Count}.");

            // ── Step 6: scrub again — paranoia + race compensation ────────
            ScrubFakeNulls("post-register");
            steps.Lap("scrub again");

            // ── Step 7: validate our entry survived the scrub ─────────────
            if (!ValidateRegistration(hash))
            {
                FiresLogger.LogError($"[TileAnchorPrefab] Ensure: registration validation FAILED. Aborting.");
                return false;
            }

            // ── Step 8: clear IH's sprite cache so it rebuilds clean ──────
            ClearInfinityHammerSpriteCache("post-register");
            steps.Lap("validate + IH sprite cache");

            _prefab = clone;
            _registered = true;
            FiresLogger.LogVerbose($"[TileAnchorPrefab] Ensure: SUCCESS — VATile fully registered and IH cache cleared.");
            EmitRegistrationSummary();
            steps.Lap("summary");

            // TileHammerCompat (IH's ruler snapped onto 2D tiles) and TileBrushDrag (the tile brush tools) are not started: the 2D
            // tile layer is retired (Fire, 2026-09-29). The prefab itself stays registered so saved anchors still load and can
            // be purged.

            // World Edit Commands sorts after FiresAdminTerrain, so its assembly only exists by ZNetScene.Awake. The bridge
            // stays for its voxel-world refusal of `void`; its tile route is off (WecTerrainBridge.IsTargetingTile).
            try { WecTerrainBridge.Initialize(); }
            catch (Exception ex) { FiresLogger.LogError($"[TileAnchorPrefab] WecTerrainBridge.Initialize threw - {ex}"); }
            steps.Lap("WEC bridge");

            // Round-W-1 — water brush drag pump. Same DontDestroyOnLoad
            // attach as the terrain drag handler, so the water brush
            // commands can also drag a continuous stroke.
            try { VerdantsAscent.Water.Tools.WaterBrushDrag.EnsureAttached(); }
            catch (Exception ex) { FiresLogger.LogError($"[TileAnchorPrefab] WaterBrushDrag.EnsureAttached threw — {ex}"); }

            // Round-57 — IH ruler ring tint (green=terrain, blue=water).
            try { VerdantsAscent.Terrain.Lifecycle.RulerColorDriver.EnsureAttached(); }
            catch (Exception ex) { FiresLogger.LogError($"[TileAnchorPrefab] RulerColorDriver.EnsureAttached threw — {ex}"); }
            steps.Lap("drag + ruler");
            FiresLogger.LogInfo($"[TileAnchorPrefab] tile anchor steps: {steps}.");

            return true;
        }

        private static GameObject LoadSourcePrefab()
        {
            GameObject source = null;

            var bundle = FiresAdminTerrain.assetBundle;
            if (bundle != null)
            {
                source = bundle.LoadAsset<GameObject>(SourcePrefabName);
                if (source != null)
                {
                    int compCount = source.GetComponentsInChildren<Component>(includeInactive: true).Length;
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] LoadSourcePrefab: '{SourcePrefabName}' loaded from FiresAdminTerrain bundle | activeSelf={source.activeSelf} components={compCount}");
                }
                else
                {
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] LoadSourcePrefab: '{SourcePrefabName}' not found in FiresAdminTerrain bundle — trying ZNetScene fallback.");
                }
            }
            else
            {
                FiresLogger.LogWarning("[TileAnchorPrefab] LoadSourcePrefab: FiresAdminTerrain bundle is null.");
            }

            if (source == null && ZNetScene.instance != null)
            {
                source = ZNetScene.instance.GetPrefab(SourcePrefabName);
                if (source != null)
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] LoadSourcePrefab: '{SourcePrefabName}' loaded from ZNetScene registry (fallback).");
            }

            if (source == null)
            {
                FiresLogger.LogError($"[TileAnchorPrefab] LoadSourcePrefab: cannot find source prefab '{SourcePrefabName}' anywhere. VATile cannot be registered.");
            }

            return source;
        }

        private static GameObject CloneAndPrepare(GameObject source)
        {
            // CRITICAL: parent the template clone under an INACTIVE root. This
            // is the FAP / VAClutterStandinFactory pattern.
            //
            // The OLD approach (clone.SetActive(false)) caused vanilla
            // "ZDO X not used when creating object VATile" warnings on world
            // reload — ZoneSystem.CreateObject does:
            //     ZNetView.m_initZDO = zdo;
            //     Instantiate(prefab, pos, rot);   // ← no parent; inherits prefab.activeSelf
            //     if (ZNetView.m_initZDO != null) warn(...)
            // If the template prefab has activeSelf=false, the spawned instance
            // is also inactive, ZNetView.Awake never fires, m_initZDO is never
            // consumed, the warning fires every frame.
            //
            // FIX: keep the template clone activeSelf=TRUE but parent under an
            // inactive root. activeInHierarchy=false → template's own Awake
            // does NOT fire (no phantom registration). Spawned instances have
            // no parent → activeSelf=true is inherited cleanly → Awake fires
            // properly → m_initZDO is consumed → no warning.
            var root = GetOrCreateInactiveRoot();

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(source, root.transform);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[TileAnchorPrefab] CloneAndPrepare: Instantiate threw — {ex}");
                return null;
            }

            if (clone == null)
            {
                FiresLogger.LogError("[TileAnchorPrefab] CloneAndPrepare: Instantiate returned null.");
                return null;
            }

            clone.name = PrefabName;
            // DontDestroyOnLoad already applies via inactive root.
            // SetActive(true) — even if source was inactive, we want the template's
            // activeSelf=true so spawned instances inherit it. Awake still doesn't
            // fire on the template because activeInHierarchy=false (root inactive).
            clone.SetActive(true);
            FiresLogger.LogVerbose($"[TileAnchorPrefab] CloneAndPrepare: clone created under inactive root — activeSelf={clone.activeSelf} activeInHierarchy={clone.activeInHierarchy} nameAccessOK={SafeNameAccessible(clone)}. Template will NOT Awake; spawned instances WILL.");

            DisableVisualsAndBehaviors(clone);

            // Ensure ZNetView is present + configured.
            var nview = clone.GetComponent<ZNetView>();
            if (nview == null)
            {
                FiresLogger.LogWarning($"[TileAnchorPrefab] Source '{SourcePrefabName}' had no ZNetView; adding one.");
                nview = clone.AddComponent<ZNetView>();
            }
            nview.enabled = true;
            nview.m_persistent = true;
            nview.m_distant = true;
            nview.m_syncInitialScale = false;
            // Prioritized so ZNetScene.CreateObjectsSorted services us BEFORE
            // Default-type ZDOs. Vanilla sorts by descending ObjectType, so
            // Solid (2) > Prioritized (1) > Default (0). Crucial because no
            // anchor = no mesh = no collision = player falls through the
            // skyland.
            nview.m_type = ZDO.ObjectType.Prioritized;
            FiresLogger.LogVerbose($"[TileAnchorPrefab] CloneAndPrepare: ZNetView configured (persistent={nview.m_persistent} distant={nview.m_distant} type={nview.m_type}).");

            clone.AddComponent<TileAnchor>();
            FiresLogger.LogVerbose($"[TileAnchorPrefab] CloneAndPrepare: TileAnchor attached.");

            // Final sanity check before handoff.
            if (!SafeNameAccessible(clone))
            {
                FiresLogger.LogError($"[TileAnchorPrefab] CloneAndPrepare: clone .name access THROWS after configuration — this clone would crash IH/BC/EWD. Destroying and aborting.");
                try { UnityEngine.Object.Destroy(clone); } catch { }
                return null;
            }

            return clone;
        }

        // DISABLE every visual / behavior component on the prefab tree.
        // Components stay attached so the GameObject's Unity object state
        // remains valid for cross-mod prefab iteration. Whitelist:
        // Transform + ZNetView. TileAnchor is added AFTER this method.
        private static void DisableVisualsAndBehaviors(GameObject go)
        {
            int renderers = 0, colliders = 0, audios = 0, particles = 0, lights = 0, mbs = 0;

            foreach (var r in go.GetComponentsInChildren<Renderer>(includeInactive: true))
                if (r != null && r.enabled) { r.enabled = false; renderers++; }

            foreach (var c in go.GetComponentsInChildren<Collider>(includeInactive: true))
                if (c != null && c.enabled) { c.enabled = false; colliders++; }

            foreach (var a in go.GetComponentsInChildren<AudioSource>(includeInactive: true))
                if (a != null && a.enabled) { a.enabled = false; audios++; }

            foreach (var p in go.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
                if (p != null && p.gameObject.activeSelf) { var em = p.emission; em.enabled = false; particles++; }

            foreach (var l in go.GetComponentsInChildren<Light>(includeInactive: true))
                if (l != null && l.enabled) { l.enabled = false; lights++; }

            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (mb == null) continue;
                if (mb is ZNetView) continue;
                if (mb.enabled) { mb.enabled = false; mbs++; }
            }

            FiresLogger.LogVerbose($"[TileAnchorPrefab] DisableVisualsAndBehaviors '{go.name}': renderers={renderers} colliders={colliders} audio={audios} particles={particles} lights={lights} mbs={mbs}");
        }

        // ─── Scrub helpers (matches Ascend.cs:540 + FAP pattern) ──────────

        // Walks ZNetScene.m_prefabs + m_namedPrefabs and removes any fake-null
        // GameObject. Unity's overloaded == operator catches both real-null
        // and fake-null (destroyed) objects. Also catches partially-destroyed
        // objects whose .name access throws by wrapping in try/catch.
        //
        // siteLabel: short tag describing where this was called from (for log).
        // Returns total count of entries removed.
        public static int ScrubFakeNulls(string siteLabel)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogVerbose($"[TileAnchorPrefab] ScrubFakeNulls ({siteLabel}): ZNetScene.instance is null, skipping.");
                return 0;
            }

            int listScrubbed = 0;
            try
            {
                listScrubbed = ZNetScene.instance.m_prefabs.RemoveAll(p => p == null);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[TileAnchorPrefab] ScrubFakeNulls ({siteLabel}): m_prefabs.RemoveAll threw — {ex.Message}");
            }

            int dictScrubbed = 0;
            List<int> deadHashes = null;
            var dict = ZNetScene.instance.m_namedPrefabs;
            if (dict != null)
            {
                try
                {
                    foreach (var kvp in dict)
                    {
                        bool isDead = false;
                        // Unity-aware null check (catches fake-null via overloaded ==)
                        if (kvp.Value == null) isDead = true;
                        else
                        {
                            // Defensive: try .name access — catches partially-destroyed
                            // objects whose underlying native handle is gone.
                            try { var _ = kvp.Value.name; }
                            catch { isDead = true; }
                        }
                        if (isDead) (deadHashes ??= new List<int>()).Add(kvp.Key);
                    }
                    if (deadHashes != null)
                    {
                        foreach (var k in deadHashes) dict.Remove(k);
                        dictScrubbed = deadHashes.Count;
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[TileAnchorPrefab] ScrubFakeNulls ({siteLabel}): m_namedPrefabs scrub threw — {ex.Message}");
                }
            }

            int total = listScrubbed + dictScrubbed;
            if (total > 0)
            {
                string sampleHashes = deadHashes != null
                    ? string.Join(",", deadHashes.Take(8).Select(h => h.ToString()))
                    + (deadHashes.Count > 8 ? "…" : "")
                    : "";
                FiresLogger.LogVerbose($"[TileAnchorPrefab] ScrubFakeNulls ({siteLabel}): REMOVED {listScrubbed} from m_prefabs + {dictScrubbed} from m_namedPrefabs (sample hashes: [{sampleHashes}]). Dict size now {dict?.Count ?? -1}.");
            }
            else
            {
                FiresLogger.LogVerbose($"[TileAnchorPrefab] ScrubFakeNulls ({siteLabel}): clean — dict={dict?.Count ?? -1} list={ZNetScene.instance.m_prefabs?.Count ?? -1}.");
            }

            return total;
        }

        // Reflectively clear InfinityHammer.SpriteHelper.PrefabNames so IH
        // rebuilds its name-cache on next FindSprite call. Without this, IH's
        // lazily-built cache captures stale state from before we scrubbed.
        // Safe no-op if IH isn't loaded or its API has changed.
        public static void ClearInfinityHammerSpriteCache(string siteLabel)
        {
            try
            {
                if (!_ihReflectionAttempted)
                {
                    _ihReflectionAttempted = true;
                    var ihAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "InfinityHammer");
                    if (ihAssembly == null)
                    {
                        FiresLogger.LogVerbose("[TileAnchorPrefab] ClearInfinityHammerSpriteCache: InfinityHammer assembly not loaded — IH integration skipped (this is fine if you don't use IH).");
                        return;
                    }
                    var spriteHelper = ihAssembly.GetType("InfinityHammer.SpriteHelper");
                    if (spriteHelper == null)
                    {
                        FiresLogger.LogWarning("[TileAnchorPrefab] ClearInfinityHammerSpriteCache: IH assembly loaded but SpriteHelper type not found (IH version mismatch?).");
                        return;
                    }
                    // Try public first, then non-public — IH may flip visibility across versions
                    _ihSpriteHelperPrefabNamesField = spriteHelper.GetField("PrefabNames",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (_ihSpriteHelperPrefabNamesField == null)
                    {
                        FiresLogger.LogWarning("[TileAnchorPrefab] ClearInfinityHammerSpriteCache: SpriteHelper.PrefabNames field not found (IH API changed?). IH cache won't be cleared — first hammer-equip may still hit NRE if late fake-nulls appear.");
                        return;
                    }
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] ClearInfinityHammerSpriteCache: IH detected, PrefabNames field bound — runtime clears now active.");
                }

                if (_ihSpriteHelperPrefabNamesField == null) return;

                var dict = _ihSpriteHelperPrefabNamesField.GetValue(null) as IDictionary;
                if (dict != null && dict.Count > 0)
                {
                    int count = dict.Count;
                    dict.Clear();
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] ClearInfinityHammerSpriteCache ({siteLabel}): cleared {count} entries from IH's PrefabNames cache — IH will rebuild on next FindSprite.");
                }
                else
                {
                    FiresLogger.LogVerbose($"[TileAnchorPrefab] ClearInfinityHammerSpriteCache ({siteLabel}): IH cache already empty.");
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[TileAnchorPrefab] ClearInfinityHammerSpriteCache ({siteLabel}) threw — {ex.Message}");
            }
        }

        private static bool SafeNameAccessible(GameObject go)
        {
            try { var _ = go.name; return true; }
            catch { return false; }
        }

        // 🪨 TILE PREFAB mini-banner — the default-visible summary that
        // replaces the per-step Ensure chatter (now verbose-gated). Fired
        // once per successful registration path.
        private static void EmitRegistrationSummary()
        {
            try
            {
                if (_prefab == null) return;
                int components = _prefab.GetComponentsInChildren<Component>(includeInactive: true).Length;
                int renderers = _prefab.GetComponentsInChildren<Renderer>(includeInactive: true).Length;
                int colliders = _prefab.GetComponentsInChildren<Collider>(includeInactive: true).Length;
                VerdantsAscent.Utilities.VAFatLoadSummary.EmitTilePrefab(components, renderers, colliders);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[TileAnchorPrefab] EmitRegistrationSummary threw — {ex.Message}");
            }
        }

        // Inactive parent for the VATile template clone. Pattern from FAP's
        // VAClutterStandinFactory — the template stays activeSelf=true so
        // spawned instances inherit activeSelf=true, but its
        // activeInHierarchy=false (parent inactive) so its own Awake doesn't
        // fire and the template doesn't do work / create ZDOs / register
        // anything as a phantom.
        private static GameObject GetOrCreateInactiveRoot()
        {
            if (_inactiveRoot != null) return _inactiveRoot;
            _inactiveRoot = new GameObject("FiresAdminTerrain_PrefabPool");
            _inactiveRoot.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_inactiveRoot);
            FiresLogger.LogVerbose("[TileAnchorPrefab] GetOrCreateInactiveRoot: created 'FiresAdminTerrain_PrefabPool' (inactive, DontDestroyOnLoad).");
            return _inactiveRoot;
        }

        private static bool ValidateRegistration(int hash)
        {
            if (ZNetScene.instance == null) return false;
            var dict = ZNetScene.instance.m_namedPrefabs;
            if (dict == null) return false;

            if (!dict.TryGetValue(hash, out var go) || go == null)
            {
                FiresLogger.LogError($"[TileAnchorPrefab] ValidateRegistration: VATile (hash={hash}) NOT present in m_namedPrefabs after Add. Likely scrubbed as fake-null — clone is invalid.");
                return false;
            }

            if (!SafeNameAccessible(go))
            {
                FiresLogger.LogError($"[TileAnchorPrefab] ValidateRegistration: VATile present but .name access throws — THIS WILL CRASH IH/BC/EWD. Removing and aborting.");
                dict.Remove(hash);
                return false;
            }

            FiresLogger.LogVerbose($"[TileAnchorPrefab] ValidateRegistration: VATile present and accessible | .name='{go.name}' activeSelf={go.activeSelf}");
            return true;
        }

        // ─── Harmony patches ──────────────────────────────────────────────

        // FAT's one ZNetScene.Awake hook (FatVanillaHooks) calls Ensure after
        // vanilla builds m_namedPrefabs from m_prefabs, registering VATile.

        // From the prefix of FAT's one PieceTable.UpdateAvailable hook
        // (FatVanillaHooks): scrubs fake-nulls right before IH's
        // UpdateAvailable.Postfix runs CustomMenu.AddTools → Tool.get_Icon
        // → SpriteHelper.FindSprite (which iterates m_namedPrefabs). Catches
        // late prefab churn between ZNetScene.Awake and the player equipping
        // a hammer. Cheap: O(n) over the dict (~2000 entries) per call;
        // early-out logs nothing at verbose-off.
        //
        // Also clears IH's PrefabNames cache if we scrubbed anything, so IH
        // doesn't keep stale state captured from a previous polluted iteration.
        internal static void ScrubBeforeUpdateAvailable()
        {
            int scrubbed = ScrubFakeNulls("pre-PieceTable.UpdateAvailable");
            if (scrubbed > 0)
            {
                // Late pollution detected — IH may have already cached a
                // bad snapshot. Force it to rebuild.
                ClearInfinityHammerSpriteCache("late-scrub");
            }
        }
    }
}
