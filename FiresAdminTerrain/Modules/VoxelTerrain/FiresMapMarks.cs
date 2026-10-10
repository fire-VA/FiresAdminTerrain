using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Session map pins for the survey commands (va_water_find, va_skyislands
    // pin). Unsaved vanilla pins: they render on the map for click-teleport
    // testing and vanish on logout; Clear() removes the batch in place.
    internal static class FiresMapMarks
    {
        private static readonly List<Minimap.PinData> s_pins = new List<Minimap.PinData>(128);

        internal static int Count => s_pins.Count;

        internal static void Add(Vector3 pos, Minimap.PinType type, string name)
        {
            if (Minimap.instance == null) return;
            var pin = Minimap.instance.AddPin(pos, type, name, false, false);
            if (pin != null) s_pins.Add(pin);
        }

        internal static int Clear()
        {
            int n = s_pins.Count;
            if (Minimap.instance != null)
                for (int i = 0; i < s_pins.Count; i++)
                    if (s_pins[i] != null) Minimap.instance.RemovePin(s_pins[i]);
            s_pins.Clear();
            return n;
        }
    }
}
