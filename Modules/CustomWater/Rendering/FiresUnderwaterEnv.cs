using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Registers a "FiresUnderwater" environment with vanilla <see cref="EnvMan"/>
    /// and toggles it on/off as the local player's eye crosses any water
    /// surface. Replaces the simpler <see cref="UnderwaterFogDriver"/> path:
    /// instead of just overriding <c>RenderSettings.fog</c>, this drives the
    /// game's own env system so fog + sun color + sun intensity + ambient +
    /// wet status + (eventually) ambient audio + particles all switch in
    /// lockstep with vanilla's 2-second blend.
    ///
    /// WHY THIS DESIGN
    /// EnvMan already manages all per-biome environment state and blends
    /// between envs on a 2-second timer (<c>EnvMan.m_transitionDuration</c>).
    /// Registering our env via <see cref="EnvMan.AppendEnvironment"/> and
    /// activating via <see cref="EnvMan.SetForceEnvironment"/> means we get
    /// that blending for free, and any other mod that reads
    /// <c>EnvMan.GetCurrentEnvironment().m_name</c> can react to the player
    /// being underwater without coordinating with us.
    ///
    /// MULTIPLAYER
    /// EnvMan runs per-client. SetForceEnvironment only affects the client
    /// that calls it. So each player sees the underwater env when THEIR own
    /// eye is submerged; other players see vanilla biome env. Correct.
    /// </summary>
    public static class FiresUnderwaterEnv
    {
        public const string EnvName = "FiresUnderwater";

        // Master switch — driven from ConfigManager.configFiresWaterUnderwaterEnv
        // via FiresAdminTerrain.ApplyFiresWaterConfig. When false, the driver
        // ensures any active force-env from us is released and stays off.
        public static bool Enabled = false;

        // Particle tuning — applied to each cloned ParticleSystem on the
        // borrowed env. Static so they can be tweaked at runtime via the
        // BepInEx ConfigManager (future work) or console; cloning happens
        // once per env registration so any change after that requires
        // re-registration to apply. Defaults tuned per user feedback:
        //   * Half speed reads as "drifting bubbles" not "burst sparks"
        //   * Blue-green tint replaces the purple/pink Yagluth-arena hue
        //   * Reduced alpha so white blobs read as translucent bubbles
        //     rather than opaque snowballs
        public static float ParticleSpeedMultiplier = 0.35f;
        public static Color ParticleTint = new Color(0.55f, 0.95f, 1.0f, 1.0f);
        public static float ParticleAlphaMultiplier = 0.55f;
        // Yagluth's particles are sized for a boss arena (huge "energy orbs"
        // up to several meters wide). At 1.0 scale, swimming through them
        // looks ridiculous. 0.25 puts them in "small floating debris /
        // dust mote / mini-bubble" range — appropriate for underwater.
        public static float ParticleScaleMultiplier = 0.25f;

        // Persistent parent for our cloned particle GameObjects. Created
        // lazily on first borrow, persists across scene loads via
        // DontDestroyOnLoad. Nuked + recreated when EnvMan resets (scene
        // transition) so we don't accumulate stale clones.
        //
        // EXPOSED so FiresUnderwaterEnvDriver.LateUpdate can move it to
        // track the player camera each frame — env particles emit at
        // their parent's world position, and the donor envs' particles
        // weren't designed to follow the player. Without per-frame
        // positioning, particles emit at world origin (or wherever the
        // donor was) and are invisible to the player.
        public static GameObject ParticleParent => s_particleParentGO;
        private static GameObject s_particleParentGO;

        // Names of vanilla envs whose m_psystems we'll borrow if our own
        // is empty. First match wins. These names are the AUTHORITATIVE list
        // confirmed from FiresNPCs/Modules/UnderwaterBubble/
        // UnderwaterBubbleGenerator.cs:EnvironmentOptions, which was dumped
        // straight from EnvMan.m_environments. Speculative names (Yagluth,
        // AshLands_misc, GoblinKingMistlands) were stripped — they do not
        // exist and were a guess on my part.
        //
        // Order: boss envs first because they have the most visible "atmosphere"
        // particles (Yagluth's bubble-like motes, Bonemass's swamp gas,
        // Moder's snow); then dungeon interiors (Caves / Crypt) as fallback
        // since they have ambient dust; then biome envs as last resort.
        private static readonly string[] PARTICLE_DONOR_ENV_NAMES = new[]
        {
            "GoblinKing",      // Yagluth's arena — floating bubble-like particles (user's pick)
            "Queen",           // Mistlands queen — dust/glow particles
            "Bonemass",        // Swamp boss — green gas particles
            "Moder",           // Mountain boss — snow particles
            "Fader",           // Ashlands boss — ember particles
            "Eikthyr",         // Meadows boss — light motes
            "GDKing",          // Elder — leaf particles
            "Caves",           // Generic cave ambient
            "Mistlands_clear", // Mist particles
            "SwampRain",       // Rain + mist
        };

        /// <summary>
        /// Build the EnvSetup we register with EnvMan. Tuned for "under a
        /// few meters of water on a clear day" — feel free to retune the
        /// colors / densities; the only structural rules are:
        ///   * m_alwaysDark = true     -> EnvMan skips daylight calc so the
        ///                                 surface above doesn't leak in
        ///   * m_isWet = true          -> player gets wet-status (matches
        ///                                 vanilla water behavior)
        ///   * m_windMin/Max = 0       -> no wind sound underwater
        ///   * fogDensity >= 0.10      -> visible foggy haze at any depth
        ///   * sun colors tinted green -> what light DOES make it through
        ///                                 reads as filtered through water
        /// </summary>
        public static EnvSetup CreateUnderwaterEnv()
        {
            var env = new EnvSetup
            {
                m_name = EnvName,
                m_default = false,

                // Gameplay flags
                m_isWet = true,
                m_isFreezing = false,
                m_isFreezingAtNight = false,
                m_isCold = false,
                m_isColdAtNight = false,
                // alwaysDark=true (like caves) was too aggressive — it killed
                // the sun entirely so we lost god rays and any sense of
                // "light filtering down from above." Flipped to false so the
                // directional light still contributes; intensity is dropped
                // in m_lightIntensity* below to keep it feeling absorbed.
                m_alwaysDark = false,

                // Ambient — brighter than v1 so non-direct-lit surfaces
                // (rocks, fish, ourselves looking down) read as visible
                // teal rather than near-black. Night still drops dark.
                m_ambColorNight = new Color(0.06f, 0.14f, 0.18f, 1f),
                m_ambColorDay   = new Color(0.18f, 0.32f, 0.38f, 1f),

                // Fog color — main "atmosphere of water" tint. Brightened
                // and slightly shifted teal-blue. Day reads as "clear-ish
                // tropical water," night reads as "moonlit cold deep."
                m_fogColorNight   = new Color(0.04f, 0.12f, 0.18f, 1f),
                m_fogColorMorning = new Color(0.10f, 0.28f, 0.34f, 1f),
                m_fogColorDay     = new Color(0.14f, 0.36f, 0.42f, 1f),
                m_fogColorEvening = new Color(0.10f, 0.26f, 0.32f, 1f),

                // Sun-fog — the side of the fog facing the sun. Much
                // brighter than base fog so god rays read against the dim
                // ambient. Light cyan tone matches what shallow water does
                // to sunlight in real life (red absorbed, blue/green pass).
                m_fogColorSunNight   = new Color(0.08f, 0.22f, 0.28f, 1f),
                m_fogColorSunMorning = new Color(0.32f, 0.55f, 0.55f, 1f),
                m_fogColorSunDay     = new Color(0.48f, 0.72f, 0.72f, 1f),
                m_fogColorSunEvening = new Color(0.28f, 0.48f, 0.50f, 1f),

                // Fog density — v3 dropped further (0.04 → 0.025 day)
                // because at 0.04 the sun shafts get absorbed by ~86% at
                // 50m depth and the player can't see the sun unless right
                // against the surface. 0.025 is comparable to vanilla
                // clear-weather biomes; the "watery" feel now comes from
                // the shader's underwater pass tint rather than env fog,
                // so dropping density doesn't lose the underwater look.
                m_fogDensityNight   = 0.05f,
                m_fogDensityMorning = 0.035f,
                m_fogDensityDay     = 0.025f,
                m_fogDensityEvening = 0.035f,

                // Sun light — directional. Intensity 0.6 day (was 0.4) so
                // god rays and surface highlights are visible. Color
                // shifted cooler/cyan to read as "filtered through water."
                m_sunColorNight   = new Color(0.30f, 0.50f, 0.55f, 1f),
                m_sunColorMorning = new Color(0.55f, 0.78f, 0.80f, 1f),
                m_sunColorDay     = new Color(0.68f, 0.90f, 0.90f, 1f),
                m_sunColorEvening = new Color(0.50f, 0.72f, 0.72f, 1f),
                m_lightIntensityDay   = 0.6f,
                m_lightIntensityNight = 0.10f,
                m_sunAngle = 60f,

                // Wind — no audible wind underwater. Stops the wind whoosh
                // ambient that vanilla biomes layer in.
                m_windMin = 0f,
                m_windMax = 0f,

                // Effects — no env object, no particles, no rain clouds.
                // m_psystems left as an empty array so vanilla EnvMan code
                // that iterates over it doesn't NRE.
                m_envObject = null,
                m_psystems = new GameObject[0],
                m_psystemsOutsideOnly = false,
                m_rainCloudAlpha = 0f,

                // Audio — no custom ambient loop in v1 (would need an
                // authored underwater hum AudioClip in the bundle). Leaving
                // ambientLoop null + ambientList empty means AudioMan falls
                // back to whatever's queued for the biome, which is wrong
                // underwater but not actively bad. Authoring an underwater
                // ambient is a future pass.
                m_ambientLoop = null,
                m_ambientVol = 0.3f,
                m_ambientList = string.Empty,

                // Music — leave empty strings so EnvMan doesn't try to
                // switch tracks when we go under. Whatever biome music was
                // playing keeps playing (player would otherwise notice an
                // abrupt mood swing on each dive).
                m_musicMorning = string.Empty,
                m_musicEvening = string.Empty,
                m_musicDay     = string.Empty,
                m_musicNight   = string.Empty,
            };
            return env;
        }

        /// <summary>
        /// Idempotent registration. Safe to call every frame while waiting
        /// for EnvMan to come up — scans m_environments first so we only
        /// AppendEnvironment if our env isn't there. Survives scene
        /// transitions (new EnvMan instance after returning from main menu)
        /// because the scan re-detects absence and re-registers.
        ///
        /// Also retries particle borrowing each call: the donor env (Yagluth
        /// etc) may not be in m_environments yet on the first register.
        /// Cheap early-out if we already have particles.
        /// </summary>
        public static bool TryRegister()
        {
            if (EnvMan.instance == null) return false;
            var envs = EnvMan.instance.m_environments;
            if (envs == null) return false;

            EnvSetup ourEnv = null;
            for (int i = 0; i < envs.Count; i++)
            {
                if (envs[i] != null && envs[i].m_name == EnvName)
                {
                    ourEnv = envs[i];
                    break;
                }
            }

            if (ourEnv == null)
            {
                // Fresh EnvMan (scene transition / new world) — nuke any
                // stale clone parent from the previous EnvMan instance.
                // Without this, returning to a world and re-registering
                // would leave the old clones DontDestroyOnLoad'd and we'd
                // accumulate every time.
                if (s_particleParentGO != null)
                {
                    try { UnityEngine.Object.Destroy(s_particleParentGO); } catch { }
                    s_particleParentGO = null;
                }
                try
                {
                    ourEnv = CreateUnderwaterEnv();
                    EnvMan.instance.AppendEnvironment(ourEnv);
                    Debug.Log($"[FiresUnderwaterEnv] Registered env '{EnvName}' (count now {EnvMan.instance.m_environments.Count}).");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FiresUnderwaterEnv] AppendEnvironment failed: {ex}");
                    return false;
                }
            }

            // Try to borrow particles from a donor env. No-op if we already
            // have any (don't trample previously-borrowed array). Will
            // succeed on a later frame if the donor env streams in later.
            if (ourEnv.m_psystems == null || ourEnv.m_psystems.Length == 0)
            {
                TryBorrowParticles(ourEnv, envs);
            }
            return true;
        }

        /// <summary>
        /// Walk donor names in priority order; for the first match, CLONE
        /// each particle GameObject and apply our tuning (slower, tinted
        /// blue-green, more translucent) so the originals stay untouched.
        ///
        /// Why cloning vs sharing the array reference:
        ///   * Sharing meant tuning our env would mutate Yagluth-arena
        ///     particles too — wrong.
        ///   * Cloning gives us our own ParticleSystem instances we can
        ///     tune in isolation without affecting any vanilla env.
        ///   * EnvMan.SetEnv compares m_psystems by reference identity,
        ///     so our array (cloned, distinct from the donor's) gets
        ///     properly enable/disable'd when transitioning to/from us.
        ///
        /// Clones live under a DontDestroyOnLoad parent so they persist
        /// across zones. Nuked on EnvMan reset (see TryRegister).
        /// </summary>
        // Frame-counter for diagnostic logging — when borrow keeps failing
        // we log a one-shot summary every ~5s of what envs ARE available
        // so the user can adjust PARTICLE_DONOR_ENV_NAMES or know that
        // EnvMan just hasn't loaded anything with particles yet.
        private static float s_lastBorrowFailLogTime;
        private const float BORROW_FAIL_LOG_INTERVAL = 5.0f;

        private static void TryBorrowParticles(EnvSetup ourEnv, List<EnvSetup> envs)
        {
            // Pass 1: priority donor list.
            for (int d = 0; d < PARTICLE_DONOR_ENV_NAMES.Length; d++)
            {
                string donorName = PARTICLE_DONOR_ENV_NAMES[d];
                for (int i = 0; i < envs.Count; i++)
                {
                    var donor = envs[i];
                    if (donor == null || donor.m_name != donorName) continue;
                    if (donor.m_psystems == null || donor.m_psystems.Length == 0) continue;

                    if (DoBorrow(ourEnv, donor, $"priority:{donorName}")) return;
                }
            }

            // Pass 2: any-env fallback. Walk every loaded env and use the
            // first one with non-empty particles, regardless of name. This
            // catches the case where boss envs (which we prefer) haven't
            // been visited yet so they aren't in m_environments, but some
            // other env (Meadows, Forest, whatever the player has been to)
            // has its own atmospheric particles loaded.
            for (int i = 0; i < envs.Count; i++)
            {
                var donor = envs[i];
                if (donor == null) continue;
                if (donor.m_psystems == null || donor.m_psystems.Length == 0) continue;
                if (DoBorrow(ourEnv, donor, $"fallback:{donor.m_name}")) return;
            }

            // Borrow failed — log periodically what envs ARE loaded so we
            // can see what's missing. Throttled so we don't spam.
            if (Time.unscaledTime - s_lastBorrowFailLogTime > BORROW_FAIL_LOG_INTERVAL)
            {
                s_lastBorrowFailLogTime = Time.unscaledTime;
                LogAvailableEnvs(envs);
            }
        }

        private static bool DoBorrow(EnvSetup ourEnv, EnvSetup donor, string reasonTag)
        {
            EnsureParticleParent();
            var clones = CloneAndTuneParticles(donor.m_psystems);
            if (clones.Length == 0) return false;

            ourEnv.m_psystems = clones;
            ourEnv.m_psystemsOutsideOnly = false;
            Debug.Log(
                $"[FiresUnderwaterEnv] Cloned + tuned {clones.Length} particle system(s) from env '{donor.m_name}' " +
                $"[{reasonTag}] " +
                $"(speed x{ParticleSpeedMultiplier}, tint {ParticleTint}, alpha x{ParticleAlphaMultiplier}).");
            return true;
        }

        private static void LogAvailableEnvs(List<EnvSetup> envs)
        {
            int withParticles = 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("[FiresUnderwaterEnv] No donor env had particles. ");
            sb.Append("Loaded envs (");
            sb.Append(envs.Count);
            sb.Append(" total): ");
            for (int i = 0; i < envs.Count; i++)
            {
                if (envs[i] == null) continue;
                int psCount = envs[i].m_psystems != null ? envs[i].m_psystems.Length : 0;
                if (psCount > 0) { withParticles++; sb.Append('*'); }
                sb.Append(envs[i].m_name);
                if (psCount > 0) { sb.Append('('); sb.Append(psCount); sb.Append(')'); }
                if (i < envs.Count - 1) sb.Append(", ");
            }
            sb.Append(". * = has particles. Will retry every frame.");
            Debug.Log(sb.ToString());
            if (withParticles == 0)
            {
                Debug.LogWarning(
                    "[FiresUnderwaterEnv] No loaded env has any particles. Boss arena envs " +
                    "(GoblinKing, Bonemass, etc.) load lazily on player entry — visit one to " +
                    "make its particles available for borrowing.");
            }
        }

        private static void EnsureParticleParent()
        {
            if (s_particleParentGO != null) return;
            s_particleParentGO = new GameObject("FiresUnderwater_ParticleClones");
            UnityEngine.Object.DontDestroyOnLoad(s_particleParentGO);
            // Parent stays active so EnvMan can toggle individual children
            // via SetParticleArrayEnabled. Children themselves start inactive
            // (see CloneAndTuneParticles) and EnvMan flips them on/off as
            // the env transitions.
        }

        private static GameObject[] CloneAndTuneParticles(GameObject[] sources)
        {
            var list = new List<GameObject>(sources.Length);
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                try
                {
                    // Parent under our DontDestroyOnLoad parent. Use
                    // instantiateInWorldSpace=false so the clone takes the
                    // parent's transform (which the driver will pin to the
                    // player camera each frame). instantiateInWorldSpace=true
                    // was wrong — it kept the donor's stale world position
                    // (Yagluth-arena XYZ or 0,0,0 if never spawned) so the
                    // particles emitted somewhere the player would never be.
                    var clone = UnityEngine.Object.Instantiate(src, s_particleParentGO.transform, false);
                    clone.name = src.name + "_Fires";
                    clone.transform.localPosition = Vector3.zero;
                    clone.transform.localRotation = Quaternion.identity;

                    // CRITICAL: leave the GameObject ACTIVE. Vanilla EnvMan
                    // (assembly_valheim/EnvMan.cs:127 SetParticleArrayEnabled)
                    // only toggles ParticleSystem.emission.enabled — it never
                    // calls SetActive. An inactive GameObject's ParticleSystem
                    // doesn't tick at all, so an emission flip from EnvMan
                    // would be a no-op. Earlier `clone.SetActive(false)` was
                    // the actual reason no bubbles showed despite borrow
                    // succeeding.
                    //
                    // To keep things silent until OUR env activates, we set
                    // emission.enabled=false on every child ParticleSystem
                    // right after cloning. EnvMan will flip them on when our
                    // env becomes current.
                    clone.SetActive(true);
                    SetEmissionEnabled(clone, false);
                    ApplyParticleTuning(clone);
                    list.Add(clone);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresUnderwaterEnv] Clone of '{src.name}' failed: {ex.Message}");
                }
            }
            return list.ToArray();
        }

        // Mirrors vanilla EnvMan.SetParticleArrayEnabled but for a single
        // clone — used to silence emission immediately after cloning so the
        // particles don't visibly leak before our env activates.
        private static void SetEmissionEnabled(GameObject psObj, bool enabled)
        {
            var systems = psObj.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                try
                {
                    var em = systems[i].emission;
                    em.enabled = enabled;
                }
                catch { /* skip — partial tune is fine */ }
            }
        }

        /// <summary>
        /// Walk every ParticleSystem on a cloned GameObject (including
        /// children) and apply speed/color/alpha/scale tuning. Each step
        /// guarded individually — particle systems vary wildly in which
        /// modules they use, so a throw on one module shouldn't kill the
        /// rest of the tuning on the same system.
        ///
        /// What we touch:
        ///   1. main.simulationSpeed + startSpeedMultiplier   — slow down
        ///   2. main.startSize* (multipliers)                 — scale down
        ///   3. main.startColor (Color / TwoColors / Gradient) — base tint
        ///   4. colorOverLifetime.color (Gradient)             — life tint
        ///   5. ParticleSystemRenderer.material._Color/_TintColor — material tint
        ///
        /// Items 4 + 5 were missing in the first pass — that's why the
        /// cream-colored Yagluth particles came through unchanged. The
        /// material is what most env particle shaders sample for color.
        /// </summary>
        private static void ApplyParticleTuning(GameObject psObj)
        {
            var systems = psObj.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var ps in systems)
            {
                if (ps == null) continue;

                // 1+2+3: main module — speed, scale, base startColor.
                try
                {
                    var main = ps.main;
                    main.simulationSpeed = main.simulationSpeed * ParticleSpeedMultiplier;
                    main.startSpeedMultiplier = main.startSpeedMultiplier * ParticleSpeedMultiplier;
                    main.startSizeMultiplier = main.startSizeMultiplier * ParticleScaleMultiplier;
                    // Some systems use independent X/Y/Z size mults — scale
                    // those too so non-uniform sizing still gets reduced.
                    main.startSizeXMultiplier = main.startSizeXMultiplier * ParticleScaleMultiplier;
                    main.startSizeYMultiplier = main.startSizeYMultiplier * ParticleScaleMultiplier;
                    main.startSizeZMultiplier = main.startSizeZMultiplier * ParticleScaleMultiplier;

                    main.startColor = TintGradient(main.startColor);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresUnderwaterEnv] main module tune failed on '{ps.name}': {ex.Message}");
                }

                // 4: colorOverLifetime — common on bubble/glow particles;
                // without this, the renderer tints the start color but
                // life-driven gradients re-apply the donor's colors over
                // the particle's lifetime, washing out our tint.
                try
                {
                    var col = ps.colorOverLifetime;
                    if (col.enabled)
                    {
                        col.color = TintGradient(col.color);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresUnderwaterEnv] colorOverLifetime tune failed on '{ps.name}': {ex.Message}");
                }

                // 5: renderer material. Most env particle shaders sample
                // _Color or _TintColor to tint the texture — without
                // multiplying these by our tint, the donor's bright cream
                // bubble texture shows through unmodified regardless of
                // the startColor.
                //
                // Use .material (not .sharedMaterial) so we get an
                // instance unique to this clone — mutating sharedMaterial
                // would mutate the donor's runtime appearance too.
                try
                {
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        var mat = renderer.material;
                        TintMaterialProperty(mat, "_Color");
                        TintMaterialProperty(mat, "_TintColor");
                        TintMaterialProperty(mat, "_EmissionColor");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresUnderwaterEnv] renderer mat tune failed on '{ps.name}': {ex.Message}");
                }
            }
        }

        // Apply our tint to one of the 4 MinMaxGradient modes. Used for
        // both main.startColor and colorOverLifetime.color so a single
        // helper keeps the gradient-key walk in one place.
        private static ParticleSystem.MinMaxGradient TintGradient(ParticleSystem.MinMaxGradient g)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(TintColor(g.color));
                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(
                        TintColor(g.colorMin),
                        TintColor(g.colorMax));
                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(TintGradientKeys(g.gradient));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(
                        TintGradientKeys(g.gradientMin),
                        TintGradientKeys(g.gradientMax));
                default:
                    return g;
            }
        }

        // Walk a Gradient's color keys and apply our tint to each. Alpha
        // keys are scaled by ParticleAlphaMultiplier separately so the
        // donor's fade-in/out shape is preserved.
        private static Gradient TintGradientKeys(Gradient g)
        {
            if (g == null) return null;
            var copy = new Gradient { mode = g.mode };
            var srcColors = g.colorKeys;
            var srcAlphas = g.alphaKeys;
            var newColors = new GradientColorKey[srcColors.Length];
            for (int i = 0; i < srcColors.Length; i++)
            {
                var k = srcColors[i];
                newColors[i] = new GradientColorKey(
                    new Color(k.color.r * ParticleTint.r,
                              k.color.g * ParticleTint.g,
                              k.color.b * ParticleTint.b),
                    k.time);
            }
            var newAlphas = new GradientAlphaKey[srcAlphas.Length];
            for (int i = 0; i < srcAlphas.Length; i++)
            {
                var k = srcAlphas[i];
                newAlphas[i] = new GradientAlphaKey(k.alpha * ParticleAlphaMultiplier, k.time);
            }
            copy.SetKeys(newColors, newAlphas);
            return copy;
        }

        private static void TintMaterialProperty(Material mat, string propName)
        {
            if (mat == null || !mat.HasProperty(propName)) return;
            var c = mat.GetColor(propName);
            mat.SetColor(propName, new Color(
                c.r * ParticleTint.r,
                c.g * ParticleTint.g,
                c.b * ParticleTint.b,
                c.a * ParticleAlphaMultiplier));
        }

        private static Color TintColor(Color c)
        {
            return new Color(
                c.r * ParticleTint.r,
                c.g * ParticleTint.g,
                c.b * ParticleTint.b,
                c.a * ParticleAlphaMultiplier);
        }

        public static void Apply()
        {
            if (EnvMan.instance == null) return;
            EnvMan.instance.SetForceEnvironment(EnvName);
        }

        /// <summary>
        /// Clears the force-env so EnvMan returns to whatever biome env it
        /// would otherwise pick. Safe to call when no force-env is active
        /// (vanilla SetForceEnvironment short-circuits on equal value).
        /// </summary>
        public static void Release()
        {
            if (EnvMan.instance == null) return;
            EnvMan.instance.SetForceEnvironment(string.Empty);
        }
    }

    /// <summary>
    /// Per-frame driver that polls the local player's eye position vs the
    /// nearest water surface and flips the FiresUnderwater env on/off.
    /// Attach exactly one instance to a persistent GameObject (FAT's
    /// RootObject is fine).
    ///
    /// LateUpdate so we see the player's final per-frame position (camera
    /// follow may have just moved the eye).
    /// </summary>
    public class FiresUnderwaterEnvDriver : MonoBehaviour
    {
        private bool m_active;

        // Shader global ID — pushed every LateUpdate so the FiresWater
        // shader's underwater pass knows whether the player is actually
        // submerged. Without this, the back-face pass can't distinguish
        // "player swimming underwater looking up at surface" from
        // "player on a boat looking at distant tall wave back-faces" —
        // both produce camDepth > 0 at the back-face pixel.
        private static readonly int s_propPlayerSubmerged
            = Shader.PropertyToID("_FiresPlayerSubmerged");

        private void OnDisable()
        {
            // Don't strand the env-force across disable — restore vanilla
            // env decision before going dormant.
            if (m_active) { FiresUnderwaterEnv.Release(); m_active = false; }
            // Clear the shader global so other shaders / a fresh scene
            // don't inherit a stale submerged flag.
            Shader.SetGlobalFloat(s_propPlayerSubmerged, 0f);
        }

        private void LateUpdate()
        {
            if (!FiresUnderwaterEnv.Enabled)
            {
                if (m_active) { FiresUnderwaterEnv.Release(); m_active = false; }
                Shader.SetGlobalFloat(s_propPlayerSubmerged, 0f);
                return;
            }

            // Register lazily — first frame EnvMan exists, we slot our env
            // in. Idempotent if already registered.
            FiresUnderwaterEnv.TryRegister();

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Shader.SetGlobalFloat(s_propPlayerSubmerged, 0f);
                return;
            }

            Vector3 eye;
            try
            {
                eye = player.m_eye != null
                    ? player.m_eye.position
                    : player.transform.position + Vector3.up * 1.6f;
            }
            catch
            {
                Shader.SetGlobalFloat(s_propPlayerSubmerged, 0f);
                return;
            }

            float liquidY;
            try { liquidY = Floating.GetLiquidLevel(eye); }
            catch
            {
                Shader.SetGlobalFloat(s_propPlayerSubmerged, 0f);
                return;
            }

            // 0.05m epsilon to avoid flickering between active/inactive when
            // the player's eye sits right on the surface (e.g., treading
            // water). > -1000f filters Floating's "no water here" sentinel.
            bool submerged = liquidY > eye.y + 0.05f && liquidY > -1000f;

            // Push to shader EVERY frame (not just on state change) so a
            // freshly-compiled material on a newly-streamed water volume
            // sees the current state immediately. Cheap (one float set).
            Shader.SetGlobalFloat(s_propPlayerSubmerged, submerged ? 1f : 0f);

            if (submerged && !m_active)
            {
                FiresUnderwaterEnv.Apply();
                m_active = true;
            }
            else if (!submerged && m_active)
            {
                FiresUnderwaterEnv.Release();
                m_active = false;
            }

            // Pin the cloned-particle parent to the player camera every
            // frame so borrowed env particles (Yagluth bubbles etc) emit
            // around the player wherever they are. Without this, the
            // particles emit at the donor env's static world position
            // (or 0,0,0) and are invisible. Done every frame regardless
            // of active state so the parent is in position the moment we
            // dive — avoids a "spawn area lag" on the first frame.
            PinParticleParentToPlayer(eye);
        }

        private static void PinParticleParentToPlayer(Vector3 eye)
        {
            var parent = FiresUnderwaterEnv.ParticleParent;
            if (parent == null) return;
            // Use the player's eye position so particles spawn at head
            // height — most env particle shapes are box/sphere centered
            // on the parent transform, so head-centered reads as "around
            // the camera" both above-water (when env transitioning) and
            // below.
            parent.transform.position = eye;
        }
    }
}
