using UnityEngine;

namespace VerdantsAscent.Water.Ice
{
    /// <summary>
    /// A circular footprint that contributes to an ice surface union.
    /// Shared between both land ice and floating ice systems.
    /// </summary>
    public struct IceDiskFootprint
    {
        public Vector3 Center;
        public float Radius;
    }
}
