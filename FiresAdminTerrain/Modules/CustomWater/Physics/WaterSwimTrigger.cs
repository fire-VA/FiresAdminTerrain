using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Handles swimming trigger for custom water bodies.
    /// Attached to a trigger collider and calls SetLiquidLevel on characters/objects.
    /// </summary>
    public class WaterSwimTrigger : MonoBehaviour
    {
        private CustomWaterBase m_water;
        private HashSet<IWaterInteractable> m_inWater = new HashSet<IWaterInteractable>();
        private LiquidType m_liquidType;

        public void Setup(CustomWaterBase water)
        {
            m_water = water;
            m_liquidType = water.LiquidType;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (m_water == null) return;

            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            // Increment water count for this liquid type
            interactable.Increment(m_liquidType);
            m_inWater.Add(interactable);

            // Debug.Log($"[WaterSwimTrigger] {other.name} entered {m_water.GetWaterType()}");
        }

        private void OnTriggerStay(Collider other)
        {
            if (m_water == null) return;

            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            Transform t = interactable.GetTransform();
            if (t == null) return;

            // Get water surface level at this position
            float waterLevel = m_water.GetWaterSurfaceAt(t.position);

            // Set liquid level - this triggers swimming/floating behavior
            interactable.SetLiquidLevel(waterLevel, m_liquidType, this);
        }

        private void OnTriggerExit(Collider other)
        {
            if (m_water == null) return;

            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            // Decrement water count
            if (interactable.Decrement(m_liquidType) == 0)
            {
                // No longer in any water of this type
                interactable.SetLiquidLevel(-10000f, m_liquidType, this);
            }

            m_inWater.Remove(interactable);

            // Debug.Log($"[WaterSwimTrigger] {other.name} exited {m_water.GetWaterType()}");
        }

        private void OnDestroy()
        {
            // Clear all tracked objects when destroyed
            foreach (var interactable in m_inWater)
            {
                if (interactable != null)
                {
                    interactable.Decrement(m_liquidType);
                    interactable.SetLiquidLevel(-10000f, m_liquidType, this);
                }
            }
            m_inWater.Clear();
        }

        /// <summary>
        /// Gets the IWaterInteractable from a collider, checking both the collider's 
        /// GameObject and the attached Rigidbody's GameObject.
        /// </summary>
        private IWaterInteractable GetWaterInteractable(Collider collider)
        {
            if (collider == null) return null;

            // Check attached rigidbody first (for characters with multiple colliders)
            if (collider.attachedRigidbody != null)
            {
                var interactable = collider.attachedRigidbody.GetComponent<IWaterInteractable>();
                if (interactable != null)
                    return interactable;
            }

            // Fallback to collider's gameobject
            return collider.GetComponent<IWaterInteractable>();
        }
    }
}
