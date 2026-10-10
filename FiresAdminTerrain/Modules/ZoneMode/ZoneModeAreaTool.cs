using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chaos Hammer's "Save an area" ring (Fire, 2026-09-27: mouse wheel on the ring like Infinity Hammer, then the
    // popup). While the tool is selected an upright cylinder follows the point the admin looks at: the wheel sizes its
    // radius, Shift+wheel moves its top and Ctrl+wheel its bottom, both measured from the aimed point. The swing then
    // opens the save popup with these numbers, editable. Camera zoom stands down while it is selected
    // (VoxelToolPieces' scroll patch asks WantsWheel). The Paint entries share it as a single flat ring whose radius the
    // wheel sets.
    internal sealed class ZoneModeAreaTool : MonoBehaviour
    {
        private const float DefaultRadius = 16f;
        private const float DefaultBelow = 16f;
        private const float DefaultAbove = 48f;
        private const float RadiusStep = 1f;
        private const float HeightStep = 2f;
        private const float MinHeight = 1f;
        private const float LookReach = 600f;
        private const float WaterLevel = 30f;
        private const int RingSegments = 64;
        private const int Posts = 4;
        private const float LineWidth = 0.3f;
        private const string LineShader = "Sprites/Default";

        private static readonly Color TopColor = new Color(0.45f, 0.85f, 1f, 0.9f);
        private static readonly Color BottomColor = new Color(1f, 0.6f, 0.25f, 0.9f);
        private static readonly string[] AimLayers = { "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle" };

        private static ZoneModeAreaTool s_instance;
        private static float s_radius = DefaultRadius;
        private static float s_below = DefaultBelow;
        private static float s_above = DefaultAbove;

        // A Paint entry (BiomePaint) draws one flat ring at the aimed ground and the wheel sizes only its radius.
        private const float DefaultPaintRadius = 16f;
        private const float PaintLift = 0.5f;
        private static readonly Color PaintColor = new Color(0.55f, 1f, 0.45f, 0.9f);
        private static float s_paintRadius = DefaultPaintRadius;

        internal static int PaintRadius => Mathf.RoundToInt(s_paintRadius);

        private LineRenderer m_top;
        private LineRenderer m_bottom;
        private LineRenderer[] m_posts;
        private GameObject m_root;

        internal static int Radius => Mathf.RoundToInt(s_radius);

        // Hosted on FAT's own plugin object, so it ticks in every world, zone mode or not.
        internal static void Ensure()
        {
            if (s_instance != null) return;
            var host = FiresAdminTerrain.Instance;
            if (host != null) s_instance = host.gameObject.AddComponent<ZoneModeAreaTool>();
        }

        internal static bool WantsWheel(Player player)
            => player != null && (ZoneModeChunkTools.IsSaveAreaSelected(player) || ZoneModeChunkTools.IsPaintSelected(player));

        // The cylinder as it stands now round `aim`, in whole metres.
        internal static void Shape(Vector3 aim, out int radius, out int bottom, out int top)
        {
            radius = Radius;
            bottom = Mathf.FloorToInt(aim.y - s_below);
            top = Mathf.CeilToInt(aim.y + s_above);
        }

        // The point under the crosshair: land, rock or a build, else where the view meets the sea.
        internal static bool TryGetAim(out Vector3 point)
        {
            point = default;
            var camera = Utils.GetMainCamera();
            if (camera == null) return false;
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            if (Physics.Raycast(ray, out var hit, LookReach, LayerMask.GetMask(AimLayers)))
            {
                point = hit.point;
                return true;
            }
            if (ray.direction.y >= 0f) return false;
            float distance = (WaterLevel - ray.origin.y) / ray.direction.y;
            if (distance <= 0f || distance > LookReach) return false;
            point = ray.origin + ray.direction * distance;
            return true;
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (ZoneModeChunkTools.TryGetZoneTool(player, out string zoneAction, out bool zoneArea) && TryGetAim(out var zoneAim))
            {
                DrawZone(player, zoneAim, zoneAction, zoneArea);
                return;
            }
            m_lastZoneMessage = null;
            if (m_zone != null) m_zone.gameObject.SetActive(false);
            if (!WantsWheel(player) || !TryGetAim(out var aim))
            {
                if (m_root != null) m_root.SetActive(false);
                return;
            }
            if (ZoneModeChunkTools.IsPaintSelected(player))
            {
                ReadPaintWheel(player);
                DrawPaint(aim);
                return;
            }
            ReadWheel();
            Draw(aim);
        }

        private static void ReadPaintWheel(Player player)
        {
            float wheel = VerdantsAscent.Terrain.FatInput.WheelNotches();
            if (Mathf.Approximately(wheel, 0f)) return;
            s_paintRadius = Mathf.Clamp(s_paintRadius + Mathf.Sign(wheel) * RadiusStep, BiomePaint.MinRadius, BiomePaint.MaxRadius);
            player.Message(MessageHud.MessageType.TopLeft, $"Paint: radius {PaintRadius} m");
        }

        private void DrawPaint(Vector3 aim)
        {
            EnsureLines();
            m_root.SetActive(true);
            var centre = new Vector3(Mathf.Round(aim.x), 0f, Mathf.Round(aim.z));
            m_top.gameObject.SetActive(true);   // 0.2.753: the zone highlight hides it
            m_top.startColor = m_top.endColor = PaintColor;
            Circle(m_top, centre, PaintRadius, aim.y + PaintLift);
            m_bottom.gameObject.SetActive(false);
            foreach (var post in m_posts) post.gameObject.SetActive(false);
        }

        private static void ReadWheel()
        {
            float wheel = VerdantsAscent.Terrain.FatInput.WheelNotches();
            if (Mathf.Approximately(wheel, 0f)) return;
            float sign = Mathf.Sign(wheel);
            if (VerdantsAscent.Terrain.FatInput.ShiftHeld)
                s_above = Mathf.Clamp(s_above + sign * HeightStep, MinHeight, ZoneModeAreaBlueprint.MaxSpan);
            else if (VerdantsAscent.Terrain.FatInput.CtrlHeld)
                s_below = Mathf.Clamp(s_below + sign * HeightStep, MinHeight, ZoneModeAreaBlueprint.MaxSpan);
            else
                s_radius = Mathf.Clamp(s_radius + sign * RadiusStep, ZoneModeAreaBlueprint.MinRadius, ZoneModeAreaBlueprint.MaxRadius);
            var player = Player.m_localPlayer;
            if (player != null)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Area: radius {Radius} m, {Mathf.RoundToInt(s_below)} m below to {Mathf.RoundToInt(s_above)} m above the aim");
        }

        private void Draw(Vector3 aim)
        {
            EnsureLines();
            m_root.SetActive(true);
            m_top.gameObject.SetActive(true);   // 0.2.753: the zone highlight hides it
            m_top.startColor = m_top.endColor = TopColor;
            m_bottom.gameObject.SetActive(true);
            foreach (var post in m_posts) post.gameObject.SetActive(true);
            Shape(aim, out int radius, out int bottom, out int top);
            var centre = new Vector3(Mathf.Round(aim.x), 0f, Mathf.Round(aim.z));
            Circle(m_top, centre, radius, top);
            Circle(m_bottom, centre, radius, bottom);
            for (int i = 0; i < Posts; i++)
            {
                float angle = i * Mathf.PI * 2f / Posts;
                var foot = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                m_posts[i].SetPosition(0, foot + Vector3.up * bottom);
                m_posts[i].SetPosition(1, foot + Vector3.up * top);
            }
        }

        // ── The aimed zone's outline for the chunk tools (0.2.753) ─────────────────────────────────────────────────────
        // A 64 x 64 square (3 x 3 zones for Close area) following the ground along its edges, green when the swing would go
        // through and red when it would be refused for a reason this client can see (the server still decides: it alone
        // knows tombstones). The reason shows top-left when the zone or the verdict changes.
        private const int ZoneEdgePoints = 24;
        private const float ZoneLift = 0.6f;
        private static readonly Color ZoneGo = new Color(0.4f, 1f, 0.45f, 0.95f);
        private static readonly Color ZoneRefused = new Color(1f, 0.3f, 0.25f, 0.95f);
        private LineRenderer m_zone;
        private string m_lastZoneMessage;

        private void DrawZone(Player player, Vector3 aim, string action, bool area)
        {
            EnsureLines();
            m_root.SetActive(true);
            m_top.gameObject.SetActive(false);
            m_bottom.gameObject.SetActive(false);
            foreach (var post in m_posts) post.gameObject.SetActive(false);
            m_zone.gameObject.SetActive(true);

            var zone = ZoneSystem.GetZone(aim);
            string why = PredictRefusal(action, zone, player);
            m_zone.startColor = m_zone.endColor = why == null ? ZoneGo : ZoneRefused;
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf * (area ? 3f : 1f);
            var corners = new[]
            {
                new Vector2(centre.x - half, centre.z - half), new Vector2(centre.x + half, centre.z - half),
                new Vector2(centre.x + half, centre.z + half), new Vector2(centre.x - half, centre.z + half),
            };
            int i = 0;
            for (int edge = 0; edge < 4; edge++)
            {
                var a = corners[edge];
                var b = corners[(edge + 1) % 4];
                for (int k = 0; k < ZoneEdgePoints; k++, i++)
                {
                    var p = Vector2.Lerp(a, b, k / (float)ZoneEdgePoints);
                    float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(new Vector3(p.x, 0f, p.y)) : WaterLevel;
                    m_zone.SetPosition(i, new Vector3(p.x, Mathf.Max(ground, WaterLevel) + ZoneLift, p.y));
                }
            }

            string message = why == null
                ? $"{action} {(area ? "the 3x3 round " : "chunk ")}({zone.x},{zone.y})"
                : $"{action} ({zone.x},{zone.y}): no, {why}";
            if (message != m_lastZoneMessage)
            {
                m_lastZoneMessage = message;
                player.Message(MessageHud.MessageType.TopLeft, message, 0, null, false);
            }
        }

        // The refusals this peer can predict, worded as the server words them.
        private static string PredictRefusal(string action, Vector2s zone, Player player)
        {
            if (!ZoneModeRegistry.ModeActive) return "this world is not one zone at a time";
            if (!ZoneOpenSet.InRange(zone.x, zone.y)) return "it is beyond the zone grid";
            bool open = ZoneModeRegistry.IsOpen(zone);
            switch (action)
            {
                case "Open": return open ? "it is open already" : null;
                case "Take back": return open ? null : "it is closed, there is nothing to take back";
            }
            if (!open) return "it is not open";
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            foreach (var other in Player.GetAllPlayers())
            {
                if (other == null) continue;
                Vector3 p = other.transform.position;
                if (Mathf.Max(0f, Mathf.Abs(p.x - centre.x) - ZoneSystem.c_ZoneSizeHalf) <= ZoneModePark.ClosePlayerClearance
                    && Mathf.Max(0f, Mathf.Abs(p.z - centre.z) - ZoneSystem.c_ZoneSizeHalf) <= ZoneModePark.ClosePlayerClearance)
                    return $"{other.GetPlayerName()} is within {ZoneModePark.ClosePlayerClearance:0} m";
            }
            return null;
        }

        private static void Circle(LineRenderer line, Vector3 centre, float radius, float height)
        {
            for (int i = 0; i < RingSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / RingSegments;
                line.SetPosition(i, new Vector3(centre.x + Mathf.Cos(angle) * radius, height, centre.z + Mathf.Sin(angle) * radius));
            }
        }

        private void EnsureLines()
        {
            if (m_root != null) return;
            m_root = new GameObject("fires_area_ring");
            DontDestroyOnLoad(m_root);
            var material = new Material(Shader.Find(LineShader));
            m_top = Line("top", material, TopColor, RingSegments, true);
            m_bottom = Line("bottom", material, BottomColor, RingSegments, true);
            m_posts = new LineRenderer[Posts];
            for (int i = 0; i < Posts; i++) m_posts[i] = Line("post", material, TopColor, 2, false);
            m_zone = Line("zone", material, ZoneGo, ZoneEdgePoints * 4, true);
        }

        private LineRenderer Line(string name, Material material, Color color, int points, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_root.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = LineWidth;
            line.positionCount = points;
            line.loop = loop;
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private void OnDestroy()
        {
            if (m_root != null) Destroy(m_root);
            if (s_instance == this) s_instance = null;
        }
    }
}
