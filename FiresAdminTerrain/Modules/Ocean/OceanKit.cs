using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Config;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Ocean
{
    /// <summary>
    /// The ocean overhaul lives in FAT: breath under water, drowning,
    /// treading water and the ocean gems (FRC's FrcEffects, Pearl), moved here from FiresRPGClasses (Ocean\OceanKit.cs).
    /// - Config: 0.2.1335 (Fire 11-02): Advanced Player Movement 0.1.3 owns the four "Ocean" keys (bound + synced there); read with CoreSettings.Get.
    /// - DecideAndStart(): once, at FAT's first ZNet.Awake: DORMANT while FiresRPGClasses still ships its own OceanKit (stage 1 - no FRC
    ///   registration, no swim-field writes, no component), else ACTIVE; never on a headless server (config only there).
    /// - Breath: drains while Core's PlayerSwim.HeadUnderwater (by reflection) says the head is under, except under a voxel cave roof
    ///   (VoxelCaveEnv.CaveRoofAbove: no drowning in a dry cave under the sea); refills 4x above; 10 s warning; at 0, Drowning damage.
    /// - Treading water: floating still at the surface gives stamina back.
    /// - Pearl effects: ocean_breath +20% breath, ocean_swimspeed +6% swim speed, ocean_swimstamina -10% swim drain (per grade),
    ///   ocean_waterbreathing grade 5 = no drain. Swim fields are always written from the player's BASE values (never compounding).
    /// Public read API for the coming ocean biome: Breath, BreathMax, Grade(id), Active.
    /// </summary>
    public static class OceanKit
    {
        public const string BreathId = "ocean_breath";
        public const string SwimSpeedId = "ocean_swimspeed";
        public const string SwimStaminaId = "ocean_swimstamina";
        public const string WaterBreathingId = "ocean_waterbreathing";
        private static readonly string[] Ids = { BreathId, SwimSpeedId, SwimStaminaId, WaterBreathingId };

        internal const float BreathPerGrade = 0.20f, SwimSpeedPerGrade = 0.06f, StaminaPerGrade = 0.10f;
        internal const float RefillRate = 4f, WarnAt = 10f;

        private const string Section = "Ocean";
        private const string RpgGuid = "com.Fire.FiresRPGClasses";
        private const string RpgOceanType = "FiresRPGClasses.Ocean.OceanKit";

        // 0.2.1335: APM's keys through CoreSettings.Get (no APM = these defaults, the same as FAT's old ones). Breath reads them every frame and
        // Get allocates (key string + boxing), so the values are cached and refreshed by CoreSettings.Changed: same behaviour, a live or server
        // change applies at once. A Core without that event (older than 0.2.1074) or a getter-only provider: read every time, nothing goes stale.
        private static bool s_breathEnabled = true;
        private static float s_breathSeconds = 45f, s_drownPercent = 5f, s_treadStamina = 2f;
        private static bool s_changedHooked, s_dirty = true, s_alwaysRead;
        private static MethodInfo s_hasChangeEvent;

        internal static bool BreathEnabled { get { Refresh(); return s_breathEnabled; } }
        internal static float BreathSeconds { get { Refresh(); return s_breathSeconds; } }
        internal static float DrownPercent { get { Refresh(); return s_drownPercent; } }
        internal static float TreadStamina { get { Refresh(); return s_treadStamina; } }

        private static void Refresh()
        {
            if (!s_changedHooked) HookChanged();
            if (!s_dirty && !s_alwaysRead) return;
            s_dirty = false;
            s_breathEnabled = CoreSettings.Get(Section, "Breath", true);
            s_breathSeconds = CoreSettings.Get(Section, "Breath seconds", 45f);
            s_drownPercent = CoreSettings.Get(Section, "Drowning damage percent", 5f);
            s_treadStamina = CoreSettings.Get(Section, "Treading water stamina per second", 2f);
            s_alwaysRead = s_hasChangeEvent == null || !Evented("Breath") || !Evented("Breath seconds") || !Evented("Drowning damage percent") || !Evented("Treading water stamina per second");
        }

        // No provider yet is fine: a new provider raises Changed.
        private static bool Evented(string key) => !CoreSettings.IsProvided(Section, key) || (bool)s_hasChangeEvent.Invoke(null, new object[] { Section, key });

        // By reflection: FAT compiles against Core 0.2.1036, the event is 0.2.1074
        private static void HookChanged()
        {
            s_changedHooked = true;
            try
            {
                var changed = typeof(CoreSettings).GetEvent("Changed");
                var has = typeof(CoreSettings).GetMethod("HasChangeEvent");
                if (changed == null || has == null) return;
                changed.AddEventHandler(null, new Action<string, string>((section, key) => { if (section == Section) s_dirty = true; }));
                s_hasChangeEvent = has;
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Ocean] CoreSettings.Changed not hooked ({e.GetType().Name}: {e.Message}): settings read every time"); }
        }

        /// <summary>True once DecideAndStart chose to run here (false while dormant, on a dedi, or before the decision).</summary>
        public static bool Active { get; private set; }
        /// <summary>Why it is active / dormant (for the test and the log).</summary>
        public static string Decision { get; private set; } = "not decided yet";
        /// <summary>Current breath in seconds for the local player (-1 = not ticking).</summary>
        public static float Breath { get; internal set; } = -1f;
        public static float BreathMax => BreathSeconds * (1f + BreathPerGrade * Grade(BreathId));

        private static readonly int[] Grades = new int[4];
        private static MethodInfo s_getGrade;
        private static bool s_decided;

        /// <summary>The cached Pearl grade for one of the ocean effect ids (0 = none, dormant or no FRC).</summary>
        public static int Grade(string effectId)
        {
            int i = Array.IndexOf(Ids, effectId);
            return i >= 0 ? Grades[i] : 0;
        }

        // ---- the one decision ---------------------------------------------------------------------------

        public static void DecideAndStart()
        {
            if (s_decided) return;
            s_decided = true;
            if (FiresCore.Lifecycle.FiresMod.IsDedicatedServer || Application.isBatchMode)
            {
                Decision = "headless server: config only";
                FiresLogger.LogInfo("[Ocean] " + Decision);
                return;
            }
            string rpg = RpgStillRunsOcean();
            if (rpg != null)
            {
                Decision = $"dormant: RPGClasses {rpg} still runs the ocean";
                FiresLogger.LogInfo("[Ocean] " + Decision);
                return;
            }
            Active = true;
            Decision = "active";
            FiresLogger.LogInfo("[Ocean] active");
            HookFrc();
            if (global::VerdantsAscent.FiresAdminTerrain.RootObject != null && global::VerdantsAscent.FiresAdminTerrain.RootObject.GetComponent<OceanTicker>() == null)
                global::VerdantsAscent.FiresAdminTerrain.RootObject.AddComponent<OceanTicker>();
        }

        // RPGClasses' version when its plugin is loaded AND its assembly still has the ocean type; else null. The plugin set can't change.
        private static string RpgStillRunsOcean()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(RpgGuid, out BepInEx.PluginInfo info) || info?.Instance == null) return null;
            return info.Instance.GetType().Assembly.GetType(RpgOceanType) != null ? info.Metadata.Version.ToString() : null;
        }

        // ---- FRC (by reflection) ------------------------------------------------------------------------

        private static void HookFrc()
        {
            // FiresRPGCrafting is renamed Fires Loot Overhaul: FLO's FloEffects first, FRC's FrcEffects while that is still live -
            // same members, so FAT works on either side of the rename slot
            Type api = Type.GetType("FiresLootOverhaul.Api.FloEffects, FiresLootOverhaul") ?? Type.GetType("FiresRPGCrafting.Api.FrcEffects, FiresRPGCrafting");
            if (api == null) { FiresLogger.LogDebug("[Ocean] no FLO FloEffects (0.1.0+) or FRC FrcEffects (0.0.37+): ocean gems off"); return; }
            MethodInfo register = api.GetMethod("RegisterEffect", new[] { typeof(string), typeof(string), typeof(int), typeof(string) });
            s_getGrade = api.GetMethod("GetGrade", new[] { typeof(Player), typeof(string) });
            if (register == null || s_getGrade == null) { FiresLogger.LogWarning("[Ocean] FLO/FRC found but its effects API is missing; ocean gems stay off"); return; }
            register.Invoke(null, new object[] { BreathId, "Deep breath", 5, "+{0}0% breath under water (2 per grade)" });
            register.Invoke(null, new object[] { SwimSpeedId, "Swift swimmer", 5, "+6% swim speed per grade (grade {0})" });
            register.Invoke(null, new object[] { SwimStaminaId, "Easy strokes", 5, "-10% swim stamina drain per grade (grade {0})" });
            register.Invoke(null, new object[] { WaterBreathingId, "Water breathing", 5, "Grade 5: breathe under water (grade {0})" });
            EventInfo changed = api.GetEvent("EffectsChanged");
            if (changed != null) changed.AddEventHandler(null, new Action(OnEffectsChanged));
            FiresLogger.LogDebug($"[Ocean] hooked {api.Assembly.GetName().Name}'s {api.Name}: 4 ocean effects registered (Pearl)");
            OnEffectsChanged();
        }

        internal static Action Retuned;

        private static void OnEffectsChanged()
        {
            Player me = Player.m_localPlayer;
            for (int i = 0; i < Ids.Length; i++)
                Grades[i] = me != null && s_getGrade != null ? (int)s_getGrade.Invoke(null, new object[] { me, Ids[i] }) : 0;
            FiresLogger.LogDebug($"[Ocean] Pearl grades: breath {Grades[0]}, swim speed {Grades[1]}, swim stamina {Grades[2]}, water breathing {Grades[3]}");
            Retuned?.Invoke();
        }

        // ---- Core's diving (by reflection) --------------------------------------------------------------

        private static PropertyInfo s_headUnder;
        private static float s_nextCoreLookup;

        internal static bool HeadUnderwater(Player me)
        {
            if (s_headUnder == null && Time.unscaledTime >= s_nextCoreLookup)
            {
                s_nextCoreLookup = Time.unscaledTime + 2f;
                s_headUnder = Type.GetType("FiresCore.Movement.PlayerSwim, FiresUnifiedCore")?.GetProperty("HeadUnderwater", BindingFlags.Public | BindingFlags.Static);
                if (s_headUnder != null) FiresLogger.LogDebug("[Ocean] breath reads Core's PlayerSwim.HeadUnderwater");
            }
            if (s_headUnder == null || !(bool)s_headUnder.GetValue(null)) return false;
            // No drowning in a dry cave under the sea - FAT's own cave roof answer, the same one Core's swim asks.
            Vector3 head = me.GetHeadPoint();
            return !VoxelCaveEnv.CaveRoofAbove(head, me.GetLiquidLevel());
        }
    }

    /// <summary>The per-frame part, on FAT's RootObject, only when OceanKit is active on a client.</summary>
    internal sealed class OceanTicker : MonoBehaviour
    {
        private Player _tuned;
        private float _baseSwimSpeed, _baseDrainMin, _baseDrainMax;
        private bool _warned, _treading;
        private float _nextDrown;

        private void Awake() => OceanKit.Retuned = () => { if (_tuned != null) Retune(_tuned); };

        private void Update()
        {
            Player me = Player.m_localPlayer;
            if (me == null) { _tuned = null; OceanKit.Breath = -1f; return; }
            if (me != _tuned)
            {
                _tuned = me;
                _baseSwimSpeed = me.m_swimSpeed;
                _baseDrainMin = me.m_swimStaminaDrainMinSkill;
                _baseDrainMax = me.m_swimStaminaDrainMaxSkill;
                Retune(me);
            }
            TickBreath(me);
        }

        // Always from the BASE values (never multiply the current value, or a re-tune stacks).
        private void Retune(Player me)
        {
            me.m_swimSpeed = _baseSwimSpeed * (1f + OceanKit.SwimSpeedPerGrade * OceanKit.Grade(OceanKit.SwimSpeedId));
            float drain = Mathf.Max(0f, 1f - OceanKit.StaminaPerGrade * OceanKit.Grade(OceanKit.SwimStaminaId));
            me.m_swimStaminaDrainMinSkill = _baseDrainMin * drain;
            me.m_swimStaminaDrainMaxSkill = _baseDrainMax * drain;
        }

        private void OnDestroy()
        {
            if (_tuned != null) { _tuned.m_swimSpeed = _baseSwimSpeed; _tuned.m_swimStaminaDrainMinSkill = _baseDrainMin; _tuned.m_swimStaminaDrainMaxSkill = _baseDrainMax; }
            OceanKit.Retuned = null;
        }

        private void TickBreath(Player me)
        {
            float max = OceanKit.BreathMax;
            if (OceanKit.Breath < 0f) OceanKit.Breath = max;
            bool under = OceanKit.BreathEnabled && !me.IsDead() && OceanKit.HeadUnderwater(me);
            if (!under || OceanKit.Grade(OceanKit.WaterBreathingId) >= 5)
            {
                OceanKit.Breath = Mathf.Min(max, OceanKit.Breath + Time.deltaTime * OceanKit.RefillRate);
                _warned = false;
                if (!under) TreadWater(me);
                return;
            }
            OceanKit.Breath = Mathf.Max(0f, OceanKit.Breath - Time.deltaTime);
            if (!_warned && OceanKit.Breath <= OceanKit.WarnAt)
            {
                _warned = true;
                me.Message(MessageHud.MessageType.Center, $"Breath: {Mathf.CeilToInt(OceanKit.Breath)} s");
            }
            if (OceanKit.Breath > 0f || Time.time < _nextDrown) return;
            _nextDrown = Time.time + 1f;
            var hit = new HitData { m_point = me.GetCenterPoint(), m_hitType = HitData.HitType.Drowning };
            hit.m_damage.m_damage = me.GetMaxHealth() * OceanKit.DrownPercent / 100f;
            me.Damage(hit);
        }

        // Floating still at the surface: a slow stamina refill. Logged once per float.
        private void TreadWater(Player me)
        {
            float rate = OceanKit.TreadStamina;
            Vector3 v = me.GetVelocity(); v.y = 0f;
            bool still = rate > 0f && me.IsSwimming() && v.magnitude < 0.3f && me.GetMoveDir().sqrMagnitude < 0.01f;
            if (still != _treading) { _treading = still; if (still) FiresLogger.LogDebug($"[Ocean] treading water: +{rate:0.0} stamina/s"); }
            if (still) me.AddStamina(rate * Time.deltaTime);
        }
    }
}
