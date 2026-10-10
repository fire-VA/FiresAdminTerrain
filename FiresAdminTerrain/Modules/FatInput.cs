using UnityEngine;

namespace VerdantsAscent.Terrain
{
    // FAT's tool input through the game's own input system (0.2.791; Fire 10-06 via the lead: "the V-tools read their input
    // through the game's input system ... the feel for Fire's mouse must stay IDENTICAL; rebinds and gamepad start working").
    // Every FAT tool read used raw UnityEngine.Input; it now goes through ZInput, the layer vanilla's own tools read:
    //   press      the "Attack" button (default: left mouse) or "JoyAttack"
    //   secondary  the "Block" button (default: right mouse) or "JoyBlock"
    //   keys       ZInput.GetKey / GetKeyDown on the same KeyCodes (LeftAlt, Shift, Ctrl, PageUp/Down, Escape, Enter)
    //   wheel      ZInput.GetMouseScrollWheel, in NOTCHES: ZInput scales the wheel to 18 per notch where the old
    //              Input.GetAxis("Mouse ScrollWheel") gave 0.1 and Input.mouseScrollDelta.y gave 1, so one notch still moves
    //              a tool exactly one old step, in the same direction.
    // FAT zeroes the wheel for vanilla while one of its tools is selected (so the hammer does not change piece); its own reads
    // pass ReadingRaw to see it.
    internal static class FatInput
    {
        private const float ZInputPerNotch = 18f;     // <Mouse>/scroll/y (120 per notch on Windows) x ZInput's 0.15
        private const float LegacyAxisPerNotch = 0.1f;

        // True while FAT reads the wheel itself: the zeroing postfixes on ZInput.GetMouseScrollWheel let it through.
        internal static bool ReadingRaw { get; private set; }

        internal static bool PrimaryHeld => ZInput.GetButton("Attack") || ZInput.GetButton("JoyAttack");
        internal static bool SecondaryHeld => ZInput.GetButton("Block") || ZInput.GetButton("JoyBlock");
        // 0.2.871 (Fire, team-wide via [lead] 10-09): no FAT key while the player types in any field (console, chat, search
        // bars), through Core's FiresInputBlock.IsTyping. Escape and Enter still pass: they close or submit FAT's own fields.
        internal static bool Key(KeyCode key) => !Blocked(key) && ZInput.GetKey(key, false);
        internal static bool KeyDown(KeyCode key) => !Blocked(key) && ZInput.GetKeyDown(key, false);
        private static bool Blocked(KeyCode key) =>
            key != KeyCode.Escape && key != KeyCode.Return && key != KeyCode.KeypadEnter && FiresCore.Input.FiresInputBlock.IsTyping();
        internal static bool AltHeld => Key(KeyCode.LeftAlt);
        internal static bool ShiftHeld => Key(KeyCode.LeftShift) || Key(KeyCode.RightShift);
        internal static bool CtrlHeld => Key(KeyCode.LeftControl) || Key(KeyCode.RightControl);

        // Whole wheel notches this frame (+ away from the player, as before); 0 when the wheel did not move.
        internal static float WheelNotches()
        {
            float raw;
            ReadingRaw = true;
            try { raw = ZInput.GetMouseScrollWheel(); }
            finally { ReadingRaw = false; }
            if (Mathf.Approximately(raw, 0f)) return 0f;
            return Mathf.Sign(raw) * Mathf.Max(1f, Mathf.Round(Mathf.Abs(raw) / ZInputPerNotch));
        }

        // The old Input.GetAxis("Mouse ScrollWheel") value for the same wheel movement.
        internal static float WheelAxis() => WheelNotches() * LegacyAxisPerNotch;
    }
}
