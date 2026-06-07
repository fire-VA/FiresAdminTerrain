using UnityEngine;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Handles scaling input for environment boxes.
    /// Works similar to Structure Tweaks - Shift+Scroll to scale.
    /// 
    /// Keybinds:
    /// - Shift + Scroll: Scale uniformly
    /// - Ctrl + Scroll: Scale height only
    /// - Alt + Scroll: Scale width/depth only
    /// - Shift + Ctrl + Scroll: Fine scale (0.1m increments)
    /// </summary>
    public class EnvironmentBoxScaler : MonoBehaviour
    {
        [Header("Scale Settings")]
        public float ScaleStep = 1f;
        public float FineScaleStep = 0.1f;

        private static EnvironmentBoxScaler _instance;
        public static EnvironmentBoxScaler Instance => _instance;

        private EnvironmentBoxController _hoveredBox;
        private float _scrollCooldown = 0f;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            if (_scrollCooldown > 0)
            {
                _scrollCooldown -= Time.deltaTime;
            }

            // Only process when player exists and isn't in menu
            if (Player.m_localPlayer == null) return;
            if (InventoryGui.IsVisible()) return;
            if (Menu.IsVisible()) return;
            if (Console.IsVisible()) return;
            if (TextInput.IsVisible()) return;

            // Update hovered box
            UpdateHoveredBox();

            // Lock toggle (Ctrl+L). This must run BEFORE the scale/visibility
            // gates so admins can always unlock a locked box without first
            // unlocking it. The toggle itself enforces admin privileges.
            ProcessLockToggleInput();

            // Process scaling input (no-op when the hovered box is locked)
            ProcessScaleInput();

            // Process visibility toggle (no-op when the hovered box is locked)
            ProcessVisibilityInput();
        }

        private void ProcessLockToggleInput()
        {
            if (_hoveredBox == null) return;
            if (!IsLocalPlayerAdmin()) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            // Strict modifier match: Ctrl+L only, no extra modifiers, to avoid
            // colliding with anything else (e.g. Ctrl+Shift+L bound elsewhere).
            if (ctrl && !shift && !alt && Input.GetKeyDown(KeyCode.L))
            {
                _hoveredBox.ToggleLock();
            }
        }

        private void UpdateHoveredBox()
        {
            _hoveredBox = null;

            var cam = GameCamera.instance?.transform;
            if (cam == null) return;

            Ray ray = new Ray(cam.position, cam.forward);
            RaycastHit hit;

            // Use layermask that includes triggers
            if (Physics.Raycast(ray, out hit, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                _hoveredBox = hit.collider.GetComponent<EnvironmentBoxController>();
                if (_hoveredBox == null)
                {
                    _hoveredBox = hit.collider.GetComponentInParent<EnvironmentBoxController>();
                }
            }

            // Also check if we're inside an env box (fallback)
            if (_hoveredBox == null)
            {
                _hoveredBox = EnvironmentBoxController.GetPlayerEnvBox();
            }
        }

        private void ProcessScaleInput()
        {
            if (_hoveredBox == null) return;
            if (_scrollCooldown > 0) return;

            // Admin-only scaling
            if (!IsLocalPlayerAdmin()) return;

            // Locked boxes cannot be resized. Silent no-op so an admin
            // accidentally scrolling near a finished box doesn't bump it.
            if (_hoveredBox.Locked) return;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) < 0.01f) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            // Only scale if shift is held (matches Structure Tweaks pattern)
            if (!shift) return;

            float step = (shift && ctrl) ? FineScaleStep : ScaleStep;
            float delta = scroll > 0 ? step : -step;

            if (ctrl && !alt)
            {
                // Ctrl + Shift + Scroll = Height only
                _hoveredBox.ScaleHeight(delta);
            }
            else if (alt && !ctrl)
            {
                // Alt + Shift + Scroll = Width/Depth only
                _hoveredBox.ScaleHorizontal(delta);
            }
            else
            {
                // Shift + Scroll = Uniform scale
                _hoveredBox.ScaleUniform(delta);
            }

            // Show feedback
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                $"Size: {_hoveredBox.BoxSize.x:F1} x {_hoveredBox.BoxSize.y:F1} x {_hoveredBox.BoxSize.z:F1}");

            _scrollCooldown = 0.1f;
        }

        private void ProcessVisibilityInput()
        {
            if (_hoveredBox == null) return;

            // Admin-only visibility toggle
            if (!IsLocalPlayerAdmin()) return;

            // Locked boxes block visibility cycle too.
            if (_hoveredBox.Locked) return;

            // Make sure we're not typing
            if (Chat.instance != null && Chat.instance.HasFocus()) return;

            // Check if shift is held
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!shift) return;

            // Shift+H key to toggle visibility
            if (Input.GetKeyDown(KeyCode.H))
            {
                Debug.Log("[EnvironmentBoxScaler] Shift+H pressed, cycling visibility");
                _hoveredBox.CycleVisibility();
            }
        }

        /// <summary>
        /// Checks if the local player has admin privileges.
        /// Uses the same admin check pattern as NpcController.
        /// </summary>
        private static bool IsLocalPlayerAdmin()
        {
            // Server is always admin
            if (ZNet.instance?.IsServer() == true)
                return true;

            // Check configSync admin status (used by mod's admin syncing system)
            if (FiresAdminTerrain.Instance?.configSync?.IsAdmin == true)
                return true;

            return false;
        }

        /// <summary>
        /// Ensures the scaler exists.
        /// </summary>
        public static void EnsureExists()
        {
            if (_instance == null)
            {
                var go = new GameObject("EnvironmentBoxScaler");
                go.AddComponent<EnvironmentBoxScaler>();
            }
        }
    }
}
