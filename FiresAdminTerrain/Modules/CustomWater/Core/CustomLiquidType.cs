namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Extended liquid type enum for the custom water system.
    /// 
    /// Valheim's vanilla <see cref="LiquidType"/> only has Water and Tar.
    /// This enum extends that to support additional liquid types with
    /// unique behaviors, materials, and interactions.
    /// 
    /// PERSISTENCE: Stored as int in WaterDisk and WaterMeshCombinerAnchor ZDO data.
    /// When adding new types, always append to the end to preserve save compatibility.
    /// 
    /// MATERIAL MAPPING: Each type maps to a captured or generated material
    /// via <see cref="WaterMaterialManager.GetMaterial(CustomLiquidType)"/>.
    /// 
    /// DAMAGE MAPPING: Hazardous types (Boiling, Freezing, Lava) deal damage
    /// through <see cref="LiquidDamageHandler"/>.
    /// </summary>
    public enum CustomLiquidType
    {
        /// <summary>
        /// Standard water. Maps to vanilla LiquidType.Water.
        /// No damage, normal swimming.
        /// </summary>
        Water = 0,

        /// <summary>
        /// Tar/oil. Maps to vanilla LiquidType.Tar.
        /// Applies tarred status, slows movement.
        /// </summary>
        Tar = 1,

        /// <summary>
        /// Frozen water surface (solid walkable ice).
        /// Player can walk on it. Can be broken to reveal water beneath.
        /// </summary>
        Ice = 2,

        /// <summary>
        /// Hot bubbling water (Ashlands-style).
        /// Deals fire damage while swimming. Steam VFX at surface.
        /// </summary>
        BoilingWater = 3,

        /// <summary>
        /// Extremely cold water (Deep North-style).
        /// Deals frost damage, applies Freezing status, slows swimming.
        /// </summary>
        FreezingWater = 4,

        /// <summary>
        /// Swimable lava/magma.
        /// Deals heavy fire damage, emits light, burning status on exit.
        /// </summary>
        Lava = 5,

        /// <summary>
        /// Custom snow drift surface — fluffy floating white mesh you sink
        /// through. Uses the FrozenWaterController machinery (same as Ice)
        /// but the IsSnowMode flag in that controller skips:
        ///   - MeshCollider (no walking on top — sink through it)
        ///   - Under-mesh water volume (_waterGO)
        ///   - Swim trigger
        ///   - Ambient water VFX (no splashes)
        ///   - Hole-break + under-mesh swim walls + climb prompts
        /// Result: pure visual white dome at the painted Y the player can
        /// walk into. Use for snow drifts, snow piles on rooftops, etc.
        /// </summary>
        Snow = 6,
    }

    /// <summary>
    /// Extension methods for <see cref="CustomLiquidType"/>.
    /// </summary>
    public static class CustomLiquidTypeExtensions
    {
        /// <summary>
        /// Converts a <see cref="CustomLiquidType"/> to the vanilla <see cref="LiquidType"/>.
        /// Types that don't have a vanilla equivalent map to Water.
        /// </summary>
        public static LiquidType ToVanillaLiquidType(this CustomLiquidType type)
        {
            switch (type)
            {
                case CustomLiquidType.Tar:
                    return LiquidType.Tar;
                default:
                    return LiquidType.Water;
            }
        }

        /// <summary>
        /// Converts a vanilla <see cref="LiquidType"/> to <see cref="CustomLiquidType"/>.
        /// </summary>
        public static CustomLiquidType ToCustomLiquidType(this LiquidType type)
        {
            switch (type)
            {
                case LiquidType.Tar:
                    return CustomLiquidType.Tar;
                default:
                    return CustomLiquidType.Water;
            }
        }

        /// <summary>
        /// Returns true if this liquid type deals damage to characters.
        /// </summary>
        public static bool IsHazardous(this CustomLiquidType type)
        {
            switch (type)
            {
                case CustomLiquidType.BoilingWater:
                case CustomLiquidType.FreezingWater:
                case CustomLiquidType.Lava:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if this liquid type is a solid surface (walkable).
        /// </summary>
        public static bool IsSolid(this CustomLiquidType type)
        {
            return type == CustomLiquidType.Ice || type == CustomLiquidType.Snow;
        }

        /// <summary>
        /// Returns true if this liquid type supports wave animation.
        /// Ice/Snow are solid (no waves), lava has slow movement.
        /// </summary>
        public static bool HasWaves(this CustomLiquidType type)
        {
            switch (type)
            {
                case CustomLiquidType.Ice:
                case CustomLiquidType.Snow:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Gets a display name for the liquid type.
        /// </summary>
        public static string GetDisplayName(this CustomLiquidType type)
        {
            switch (type)
            {
                case CustomLiquidType.Water: return "Water";
                case CustomLiquidType.Tar: return "Tar";
                case CustomLiquidType.Ice: return "Ice";
                case CustomLiquidType.BoilingWater: return "Boiling Water";
                case CustomLiquidType.FreezingWater: return "Freezing Water";
                case CustomLiquidType.Lava: return "Lava";
                case CustomLiquidType.Snow: return "Snow";
                default: return "Unknown";
            }
        }
    }
}
