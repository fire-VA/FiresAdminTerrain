using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Handles damage dealing for hazardous liquid types (boiling water, freezing water, lava).
    /// 
    /// Attach to a WaterMeshCombiner's swim trigger to deal damage to characters in the liquid.
    /// Damage is applied on a timer (not every frame) to match vanilla behavior.
    /// 
    /// SUPPORTED LIQUID TYPES:
    /// - BoilingWater: Fire damage, no wet effect, steam VFX
    /// - FreezingWater: Frost damage, Freezing status, slowed swimming
    /// - Lava: Heavy fire damage, burning on exit, light emission
    /// 
    /// DAMAGE SCALING:
    /// - Base damage is per-second, applied in ticks at DamageInterval
    /// - Resistance (fire/frost) reduces damage normally through vanilla HitData
    /// - Lava damage can be survived with high fire resistance
    /// </summary>
    public class LiquidDamageHandler : MonoBehaviour
    {
        #region Configuration

        /// <summary>
        /// The liquid type this handler deals damage for.
        /// </summary>
        public CustomLiquidType LiquidType { get; set; } = CustomLiquidType.Water;

        /// <summary>
        /// Damage per second (scaled by DamageInterval for per-tick amount).
        /// </summary>
        public float DamagePerSecond { get; set; } = 10f;

        /// <summary>
        /// Time between damage ticks in seconds.
        /// </summary>
        public float DamageInterval { get; set; } = 0.5f;

        /// <summary>
        /// How long the burning/freezing effect continues after leaving the liquid.
        /// </summary>
        public float LingeringDuration { get; set; } = 0f;

        /// <summary>
        /// Swimming speed multiplier while in this liquid (1.0 = normal).
        /// </summary>
        public float SwimSpeedMultiplier { get; set; } = 1f;

        #endregion

        #region State

        private float _damageTimer = 0f;
        private WaterMeshCombiner _combiner;

        /// <summary>
        /// Tracks characters that currently have our status effect applied.
        /// Prevents rapid add/remove toggling by only applying once while in liquid.
        /// </summary>
        private readonly HashSet<int> _activeStatusCharacters = new HashSet<int>();

        /// <summary>
        /// How long the status effect lasts per application (seconds).
        /// Set high so it persists while in liquid and only expires after leaving.
        /// </summary>
        private const float STATUS_EFFECT_DURATION = 30f;

        #endregion

        #region Setup

        /// <summary>
        /// Initializes the damage handler for a specific liquid type.
        /// Sets default damage values based on the liquid type.
        /// </summary>
        public void Initialize(WaterMeshCombiner combiner, CustomLiquidType liquidType)
        {
            _combiner = combiner;
            LiquidType = liquidType;

            switch (liquidType)
            {
                case CustomLiquidType.BoilingWater:
                    DamagePerSecond = 10f;
                    DamageInterval = 0.5f;
                    LingeringDuration = 0f;
                    SwimSpeedMultiplier = 0.8f;
                    break;

                case CustomLiquidType.FreezingWater:
                    DamagePerSecond = 5f;
                    DamageInterval = 1f;
                    LingeringDuration = 3f;
                    SwimSpeedMultiplier = 0.5f;
                    break;

                case CustomLiquidType.Lava:
                    DamagePerSecond = 50f;
                    DamageInterval = 0.5f;
                    LingeringDuration = 5f;
                    SwimSpeedMultiplier = 0.3f;
                    break;

                default:
                    // Non-hazardous - disable
                    enabled = false;
                    return;
            }
        }

        #endregion

        #region Damage Application

        /// <summary>
        /// Called by <see cref="WaterCombinerTrigger"/> when a character is in the liquid.
        /// Applies damage on a timer and maintains status effects.
        /// 
        /// Status effects are applied ONCE when entering and maintained while in liquid.
        /// They are NOT re-applied every tick to avoid rapid add/remove toggling.
        /// </summary>
        public void OnCharacterInLiquid(Character character)
        {
            if (character == null || !enabled) return;

            int charId = character.GetInstanceID();

            // Apply status effect once when character enters (not every tick)
            if (!_activeStatusCharacters.Contains(charId))
            {
                _activeStatusCharacters.Add(charId);

                switch (LiquidType)
                {
                    case CustomLiquidType.FreezingWater:
                        ApplyFreezingStatus(character);
                        break;
                    case CustomLiquidType.BoilingWater:
                    case CustomLiquidType.Lava:
                        ApplyBurningStatus(character);
                        break;
                }
            }

            // Apply damage on timer
            _damageTimer += Time.deltaTime;
            if (_damageTimer < DamageInterval) return;
            _damageTimer = 0f;

            float tickDamage = DamagePerSecond * DamageInterval;

            var hitData = new HitData();
            hitData.m_point = character.transform.position;
            hitData.m_dir = Vector3.up;

            switch (LiquidType)
            {
                case CustomLiquidType.BoilingWater:
                    hitData.m_damage.m_fire = tickDamage;
                    break;

                case CustomLiquidType.FreezingWater:
                    hitData.m_damage.m_frost = tickDamage;
                    break;

                case CustomLiquidType.Lava:
                    hitData.m_damage.m_fire = tickDamage;
                    break;
            }

            character.Damage(hitData);
        }

        /// <summary>
        /// Called when a character exits the liquid.
        /// Stops tracking the character for status effects.
        /// If lingering is configured, the status effect will naturally
        /// expire on its own after LingeringDuration.
        /// If no lingering, removes the status effect immediately.
        /// </summary>
        public void OnCharacterExitLiquid(Character character)
        {
            if (character == null || !enabled) return;

            int charId = character.GetInstanceID();
            _activeStatusCharacters.Remove(charId);

            if (LingeringDuration <= 0f)
            {
                // No lingering - remove status effect immediately
                var seman = character.GetSEMan();
                if (seman != null)
                {
                    switch (LiquidType)
                    {
                        case CustomLiquidType.FreezingWater:
                            seman.RemoveStatusEffect("Freezing".GetStableHashCode());
                            break;
                        case CustomLiquidType.BoilingWater:
                            // Boiling has no lingering - stop burning on exit
                            seman.RemoveStatusEffect("Burning".GetStableHashCode());
                            break;
                    }
                }
            }
            // If LingeringDuration > 0, the status effect stays and expires naturally
            // (Lava burning lingers, Freezing lingers)
        }

        private void ApplyFreezingStatus(Character character)
        {
            var seman = character.GetSEMan();
            if (seman == null) return;

            // Use vanilla Freezing status effect
            var freezingSE = seman.GetStatusEffect("Freezing".GetStableHashCode());
            if (freezingSE != null)
            {
                // Already applied - just reset the timer so it doesn't expire
                freezingSE.ResetTime();
            }
            else
            {
                var se = seman.AddStatusEffect("Freezing".GetStableHashCode());
                if (se != null)
                {
                    se.m_ttl = STATUS_EFFECT_DURATION;
                }
            }
        }

        private void ApplyBurningStatus(Character character)
        {
            var seman = character.GetSEMan();
            if (seman == null) return;

            // Use vanilla Burning status effect
            var burningSE = seman.GetStatusEffect("Burning".GetStableHashCode());
            if (burningSE != null)
            {
                // Already applied - just reset the timer so it doesn't expire
                burningSE.ResetTime();
            }
            else
            {
                var se = seman.AddStatusEffect("Burning".GetStableHashCode());
                if (se != null)
                {
                    se.m_ttl = STATUS_EFFECT_DURATION;
                }
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Gets the HitData.DamageType used by this liquid.
        /// </summary>
        public HitData.DamageType GetDamageType()
        {
            switch (LiquidType)
            {
                case CustomLiquidType.BoilingWater:
                case CustomLiquidType.Lava:
                    return HitData.DamageType.Fire;
                case CustomLiquidType.FreezingWater:
                    return HitData.DamageType.Frost;
                default:
                    return HitData.DamageType.Blunt;
            }
        }

        #endregion
    }
}
