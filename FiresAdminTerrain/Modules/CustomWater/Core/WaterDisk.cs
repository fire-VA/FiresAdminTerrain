using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Shape of a water disk (circle, square, or rectangle).
    /// Matches Infinity Hammer ruler shapes.
    /// </summary>
    public enum WaterDiskShape
    {
        /// <summary>Circular disk defined by Radius.</summary>
        Circle = 0,
        /// <summary>Square disk defined by Width (Width x Width).</summary>
        Square = 1,
        /// <summary>Rectangular disk defined by Width (X) and Depth (Z).</summary>
        Rectangle = 2
    }

    /// <summary>
    /// Represents a single painted water surface disk (like TerrainDisk for terrain).
    /// This is pure data - no MonoBehaviour, no GameObject overhead.
    /// 
    /// A WaterDisk defines:
    /// - Position: Where the water was painted (X, Y=surface level, Z)
    /// - Radius: How far the water surface extends from center (for circles)
    /// - Width/Depth: Dimensions for square/rectangle shapes
    /// - MaxDepth: How deep below the surface the water goes (finds terrain as container)
    /// 
    /// The water mesh combiner uses these to calculate where water exists:
    /// - Water surface = WaterDisk.Position.y (painted level)
    /// - Water bottom = terrain height (container)
    /// - Water exists where surface > terrain
    /// </summary>
    public class WaterDisk
    {
        #region Stable Hash ID System

        /// <summary>
        /// Generates a stable unique hash for a water disk based on position and creation time.
        /// This prevents ID collisions during simultaneous multiplayer operations.
        /// 
        /// The hash combines:
        /// - Position (quantized to prevent floating point issues)
        /// - High-resolution timestamp
        /// - Random component for same-frame protection
        /// </summary>
        public static int GenerateStableHash(Vector3 position)
        {
            // Quantize position to 0.1m grid to prevent floating point comparison issues
            int qx = Mathf.RoundToInt(position.x * 10f);
            int qy = Mathf.RoundToInt(position.y * 10f);
            int qz = Mathf.RoundToInt(position.z * 10f);

            // Get high-resolution time component
            long ticks = DateTime.UtcNow.Ticks;
            int timePart = (int)(ticks & 0xFFFFFF); // Lower 24 bits of ticks

            // Combine with pseudo-random for same-frame protection
            int random = UnityEngine.Random.Range(0, 256);

            // Mix everything together using prime multipliers
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + qx;
                hash = hash * 31 + qy;
                hash = hash * 31 + qz;
                hash = hash * 31 + timePart;
                hash = hash * 31 + random;
                return hash;
            }
        }

        #endregion

        /// <summary>
        /// Stable unique hash ID for this disk.
        /// Used for multiplayer sync and persistence to prevent ID collisions.
        /// </summary>
        public int StableHash { get; private set; }

        /// <summary>
        /// World position of the disk center.
        /// Y component is the water SURFACE level at this point.
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Radius of the water disk in meters (used for Circle shape).
        /// For Square/Rectangle shapes, this is set to half the diagonal for compatibility.
        /// </summary>
        public float Radius { get; set; }

        /// <summary>
        /// Maximum depth below the surface level.
        /// Water will fill down from Position.y to terrain, up to this depth.
        /// </summary>
        public float MaxDepth { get; set; }

        /// <summary>
        /// Shape of this water disk (Circle, Square, or Rectangle).
        /// </summary>
        public WaterDiskShape Shape { get; set; }

        /// <summary>
        /// Width of the water area in the X direction (for Square/Rectangle shapes).
        /// For Circle shape, this equals Radius * 2.
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// Length of the water area in the Z direction (for Rectangle shape).
        /// For Circle shape, this equals Radius * 2.
        /// For Square shape, this equals Width.
        /// </summary>
        public float Length { get; set; }

        /// <summary>
        /// The liquid type (Water, Tar, etc.) - vanilla enum.
        /// </summary>
        public LiquidType LiquidType { get; set; }

        /// <summary>
        /// Extended liquid type for the custom water system.
        /// Takes precedence over <see cref="LiquidType"/> when set to a non-vanilla value.
        /// </summary>
        public CustomLiquidType CustomLiquidType { get; set; }

        /// <summary>
        /// ZDO ID for persistence (0 if not persisted yet).
        /// </summary>
        public ZDOID ZdoId { get; set; }

        /// <summary>
        /// Creates a new circular WaterDisk with the specified parameters.
        /// Automatically generates a stable hash ID.
        /// </summary>
        public WaterDisk(Vector3 position, float radius, float maxDepth = 5f, LiquidType liquidType = LiquidType.Water)
        {
            Position = position;
            Radius = radius;
            MaxDepth = maxDepth;
            LiquidType = liquidType;
            CustomLiquidType = liquidType.ToCustomLiquidType();
            ZdoId = ZDOID.None;
            StableHash = GenerateStableHash(position);
            Shape = WaterDiskShape.Circle;
            Width = radius * 2f;
            Length = radius * 2f;
        }

        /// <summary>
        /// Creates a circular WaterDisk with a specific stable hash (for restoration from save data).
        /// </summary>
        public WaterDisk(Vector3 position, float radius, float maxDepth, LiquidType liquidType, int stableHash)
        {
            Position = position;
            Radius = radius;
            MaxDepth = maxDepth;
            LiquidType = liquidType;
            CustomLiquidType = liquidType.ToCustomLiquidType();
            ZdoId = ZDOID.None;
            StableHash = stableHash;
            Shape = WaterDiskShape.Circle;
            Width = radius * 2f;
            Length = radius * 2f;
        }

        /// <summary>
        /// Creates a WaterDisk with an extended custom liquid type.
        /// </summary>
        public WaterDisk(Vector3 position, float radius, float maxDepth, CustomLiquidType customLiquidType, int stableHash = 0)
        {
            Position = position;
            Radius = radius;
            MaxDepth = maxDepth;
            CustomLiquidType = customLiquidType;
            LiquidType = customLiquidType.ToVanillaLiquidType();
            ZdoId = ZDOID.None;
            StableHash = stableHash == 0 ? GenerateStableHash(position) : stableHash;
            Shape = WaterDiskShape.Circle;
            Width = radius * 2f;
            Length = radius * 2f;
        }

        /// <summary>
        /// Creates a shaped WaterDisk (square or rectangle).
        /// </summary>
        public WaterDisk(Vector3 position, WaterDiskShape shape, float width, float length, float maxDepth = 5f, LiquidType liquidType = LiquidType.Water)
        {
            Position = position;
            Shape = shape;
            Width = width;
            Length = (shape == WaterDiskShape.Square) ? width : length;
            MaxDepth = maxDepth;
            LiquidType = liquidType;
            CustomLiquidType = liquidType.ToCustomLiquidType();
            ZdoId = ZDOID.None;
            StableHash = GenerateStableHash(position);
            // Set Radius to half-diagonal for compatibility with circle-based overlap checks
            float halfW = Width * 0.5f;
            float halfL = Length * 0.5f;
            Radius = Mathf.Sqrt(halfW * halfW + halfL * halfL);
        }

        /// <summary>
        /// Checks if a world position is within this disk's shape bounds.
        /// For circles: uses radial distance.
        /// For squares/rectangles: uses axis-aligned box check.
        /// </summary>
        public bool ContainsPosition(Vector3 worldPos)
        {
            float dx = worldPos.x - Position.x;
            float dz = worldPos.z - Position.z;

            if (Shape == WaterDiskShape.Circle)
            {
                return (dx * dx + dz * dz) <= Radius * Radius;
            }
            else
            {
                float halfW = Width * 0.5f;
                float halfL = Length * 0.5f;
                return Mathf.Abs(dx) <= halfW && Mathf.Abs(dz) <= halfL;
            }
        }

        /// <summary>
        /// Checks if a world position is within this disk's bounds plus blend distance.
        /// </summary>
        public bool ContainsPositionWithBlend(Vector3 worldPos, float blendDistance)
        {
            float dx = worldPos.x - Position.x;
            float dz = worldPos.z - Position.z;

            if (Shape == WaterDiskShape.Circle)
            {
                float totalRadius = Radius + blendDistance;
                return (dx * dx + dz * dz) <= totalRadius * totalRadius;
            }
            else
            {
                float halfW = Width * 0.5f + blendDistance;
                float halfL = Length * 0.5f + blendDistance;
                return Mathf.Abs(dx) <= halfW && Mathf.Abs(dz) <= halfL;
            }
        }

        /// <summary>
        /// Gets the distance from a world position to this disk's edge.
        /// Negative means inside, positive means outside.
        /// For circles: radial distance minus radius.
        /// For squares/rectangles: signed distance to nearest edge.
        /// </summary>
        public float GetEdgeDistance(Vector3 worldPos)
        {
            float dx = worldPos.x - Position.x;
            float dz = worldPos.z - Position.z;

            if (Shape == WaterDiskShape.Circle)
            {
                return Mathf.Sqrt(dx * dx + dz * dz) - Radius;
            }
            else
            {
                float halfW = Width * 0.5f;
                float halfL = Length * 0.5f;
                // Signed distance to rectangle
                float edgeX = Mathf.Abs(dx) - halfW;
                float edgeZ = Mathf.Abs(dz) - halfL;

                if (edgeX <= 0f && edgeZ <= 0f)
                {
                    // Inside: return negative distance to nearest edge
                    return Mathf.Max(edgeX, edgeZ);
                }
                else if (edgeX > 0f && edgeZ > 0f)
                {
                    // Outside corner: Euclidean distance to corner
                    return Mathf.Sqrt(edgeX * edgeX + edgeZ * edgeZ);
                }
                else
                {
                    // Outside one edge only
                    return Mathf.Max(edgeX, edgeZ);
                }
            }
        }

        /// <summary>
        /// Gets the distance from a world position to this disk's center (horizontal only).
        /// For shape-aware code, prefer GetEdgeDistance() instead.
        /// </summary>
        public float GetHorizontalDistance(Vector3 worldPos)
        {
            float dx = worldPos.x - Position.x;
            float dz = worldPos.z - Position.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Gets the half-width and half-length of this disk's XZ footprint.
        /// For circles, both are equal to Radius.
        /// </summary>
        public void GetHalfExtents(out float halfWidth, out float halfLength)
        {
            if (Shape == WaterDiskShape.Circle)
            {
                halfWidth = Radius;
                halfLength = Radius;
            }
            else
            {
                halfWidth = Width * 0.5f;
                halfLength = Length * 0.5f;
            }
        }

        /// <summary>
        /// Gets the water surface level at a position (just the disk's Y for now).
        /// Future: Could support per-disk wave offsets.
        /// </summary>
        public float GetSurfaceLevelAt(Vector3 worldPos)
        {
            // For now, water surface is flat at Position.y
            // Could add wave offsets here in the future
            return Position.y;
        }

        /// <summary>
        /// Checks if this disk overlaps with another disk (including blend distance).
        /// </summary>
        public bool OverlapsWith(WaterDisk other, float blendDistance = 0f)
        {
            if (other == null) return false;

            float dx = Position.x - other.Position.x;
            float dz = Position.z - other.Position.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float combinedRadius = Radius + other.Radius + blendDistance * 2f;

            return dist < combinedRadius;
        }

        /// <summary>
        /// Creates a copy of this disk, preserving the stable hash and shape.
        /// </summary>
        public WaterDisk Clone()
        {
            return new WaterDisk(Position, Radius, MaxDepth, LiquidType, StableHash)
            {
                ZdoId = ZdoId,
                CustomLiquidType = CustomLiquidType,
                Shape = Shape,
                Width = Width,
                Length = Length
            };
        }

        /// <summary>
        /// Serializes this disk to a byte array for network transmission.
        /// Format v2: Adds shape, width, length after custom liquid type.
        /// </summary>
        public byte[] Serialize()
        {
            using (var ms = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(ms))
            {
                writer.Write(StableHash);
                writer.Write(Position.x);
                writer.Write(Position.y);
                writer.Write(Position.z);
                writer.Write(Radius);
                writer.Write(MaxDepth);
                writer.Write((int)LiquidType);
                writer.Write((int)CustomLiquidType);
                // v2: shape data
                writer.Write((int)Shape);
                writer.Write(Width);
                writer.Write(Length);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserializes a WaterDisk from a byte array.
        /// Backward compatible: reads shape data only if present.
        /// </summary>
        public static WaterDisk Deserialize(byte[] data)
        {
            using (var ms = new System.IO.MemoryStream(data))
            using (var reader = new System.IO.BinaryReader(ms))
            {
                int stableHash = reader.ReadInt32();
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();
                float radius = reader.ReadSingle();
                float maxDepth = reader.ReadSingle();
                LiquidType liquidType = (LiquidType)reader.ReadInt32();

                // Read CustomLiquidType if data is long enough (backward compatibility)
                CustomLiquidType customType = liquidType.ToCustomLiquidType();
                if (ms.Position < ms.Length)
                {
                    customType = (CustomLiquidType)reader.ReadInt32();
                }

                // v2: Read shape data if present (backward compatibility)
                WaterDiskShape shape = WaterDiskShape.Circle;
                float width = radius * 2f;
                float length = radius * 2f;
                if (ms.Position < ms.Length)
                {
                    shape = (WaterDiskShape)reader.ReadInt32();
                    width = reader.ReadSingle();
                    length = reader.ReadSingle();
                }

                var disk = new WaterDisk(new Vector3(x, y, z), radius, maxDepth, liquidType, stableHash);
                disk.CustomLiquidType = customType;
                disk.Shape = shape;
                disk.Width = width;
                disk.Length = length;
                return disk;
            }
        }
    }
}
