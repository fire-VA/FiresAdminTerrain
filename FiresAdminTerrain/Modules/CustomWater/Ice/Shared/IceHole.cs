using UnityEngine;

namespace VerdantsAscent.Water.Ice
{
    /// <summary>
    /// Represents a circular hole punched in floating ice.
    /// Only used by <see cref="FloatingIceController"/>.
    /// </summary>
    public struct IceHole
    {
        public Vector3 Center;
        public float Radius;
        public float TimeCreated;
    }
}
