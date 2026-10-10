using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Pure data struct representing a custom water placement.
    /// Similar to TerrainDisk for the custom terrain system.
    /// 
    /// This is stored in WaterAnchor ZDOs for persistence.
    /// </summary>
    [Serializable]
    public struct WaterCell
    {
        /// <summary>
        /// Unique ID for this water cell within a zone.
        /// </summary>
        public int Id;

        /// <summary>
        /// Center position of the water volume in world coordinates.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// Size of the water volume (Width, MaxDepth, Length).
        /// </summary>
        public Vector3 Size;

        /// <summary>
        /// Type of water (Pond, River, Waterfall).
        /// </summary>
        public CustomWaterType WaterType;

        /// <summary>
        /// Liquid type (Water or Tar).
        /// </summary>
        public LiquidType LiquidType;

        /// <summary>
        /// Wave intensity multiplier.
        /// </summary>
        public float WaveIntensity;

        /// <summary>
        /// Flow viscosity (rivers only) - affects flow speed.
        /// </summary>
        public float Viscosity;

        /// <summary>
        /// Flow direction in degrees (rivers only) - 0 = North.
        /// </summary>
        public float FlowDirection;

        /// <summary>
        /// Flow speed in m/s (rivers only).
        /// </summary>
        public float FlowSpeed;

        /// <summary>
        /// Water level as percentage of box height (ponds only).
        /// </summary>
        public float WaterLevel;

        /// <summary>
        /// Drop height in meters (waterfalls only).
        /// </summary>
        public float DropHeight;

        /// <summary>
        /// Flow rate in volume per second (waterfalls only).
        /// </summary>
        public float FlowRate;

        /// <summary>
        /// Creates a new water cell for a pond.
        /// </summary>
        public static WaterCell CreatePond(int id, Vector3 position, Vector3 size, float waterLevel = 0.5f)
        {
            return new WaterCell
            {
                Id = id,
                Position = position,
                Size = size,
                WaterType = CustomWaterType.Pond,
                LiquidType = LiquidType.Water,
                WaveIntensity = 0.5f,
                WaterLevel = waterLevel
            };
        }

        /// <summary>
        /// Creates a new water cell for a river.
        /// </summary>
        public static WaterCell CreateRiver(int id, Vector3 position, Vector3 size, float flowDirection = 0f, float flowSpeed = 2f)
        {
            return new WaterCell
            {
                Id = id,
                Position = position,
                Size = size,
                WaterType = CustomWaterType.River,
                LiquidType = LiquidType.Water,
                WaveIntensity = 0.3f,
                Viscosity = 1f,
                FlowDirection = flowDirection,
                FlowSpeed = flowSpeed
            };
        }

        /// <summary>
        /// Creates a new water cell for a waterfall.
        /// </summary>
        public static WaterCell CreateWaterfall(int id, Vector3 position, float width, float dropHeight, float flowRate)
        {
            return new WaterCell
            {
                Id = id,
                Position = position,
                Size = new Vector3(width, dropHeight, 1f),
                WaterType = CustomWaterType.Waterfall,
                LiquidType = LiquidType.Water,
                WaveIntensity = 0f,
                DropHeight = dropHeight,
                FlowRate = flowRate
            };
        }

        /// <summary>
        /// Serializes this cell to a byte array for ZDO storage.
        /// </summary>
        public byte[] Serialize()
        {
            using (var writer = new System.IO.BinaryWriter(new System.IO.MemoryStream()))
            {
                writer.Write(Id);
                writer.Write(Position.x);
                writer.Write(Position.y);
                writer.Write(Position.z);
                writer.Write(Size.x);
                writer.Write(Size.y);
                writer.Write(Size.z);
                writer.Write((int)WaterType);
                writer.Write((int)LiquidType);
                writer.Write(WaveIntensity);
                writer.Write(Viscosity);
                writer.Write(FlowDirection);
                writer.Write(FlowSpeed);
                writer.Write(WaterLevel);
                writer.Write(DropHeight);
                writer.Write(FlowRate);

                return ((System.IO.MemoryStream)writer.BaseStream).ToArray();
            }
        }

        /// <summary>
        /// Deserializes a water cell from a byte array.
        /// </summary>
        public static WaterCell Deserialize(byte[] data)
        {
            using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(data)))
            {
                return new WaterCell
                {
                    Id = reader.ReadInt32(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Size = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    WaterType = (CustomWaterType)reader.ReadInt32(),
                    LiquidType = (LiquidType)reader.ReadInt32(),
                    WaveIntensity = reader.ReadSingle(),
                    Viscosity = reader.ReadSingle(),
                    FlowDirection = reader.ReadSingle(),
                    FlowSpeed = reader.ReadSingle(),
                    WaterLevel = reader.ReadSingle(),
                    DropHeight = reader.ReadSingle(),
                    FlowRate = reader.ReadSingle()
                };
            }
        }

        public override string ToString()
        {
            return $"WaterCell[{Id}] {WaterType} at ({Position.x:F1}, {Position.y:F1}, {Position.z:F1})";
        }
    }
}
