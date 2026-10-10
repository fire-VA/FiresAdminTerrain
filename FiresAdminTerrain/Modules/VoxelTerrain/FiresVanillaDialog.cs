using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.UI;
using GUIFramework;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── THE MAIN MENU'S OWN PANEL, BORROWED ─────────────────────────────────
    // Our world options used to open a parchment panel of our own beside menus that look nothing like it. This clones
    // vanilla's New World panel instead - its frame, its buttons, its fonts - and lays the same Core MenuDialog field
    // rows inside it, so a dialog raised from Select World looks like it belongs to the game.
    //
    // NOTHING about the panel's shape is assumed, because it is Iron Gate's and there is no prefab to read it from:
    // m_createWorldPanel is a serialized scene reference. The first attempt parented the rows to the CLONE ROOT, which
    // turned out to be a full-screen container with the wooden frame as a child - the rows stretched across the whole
    // screen behind the panel. So the row host is now DERIVED: it is the transform vanilla's own input fields share as
    // a parent, which is by definition the panel's body and carries the panel's scale.
    //
    // Vanilla's own contents are then switched off rather than left underneath: its Name and Seed rows, their labels
    // and its "5 characters minimum" hint all survive a clone and were drawing on top of ours. Everything that draws
    // text is switched off except the buttons and the title, and the title is RETARGETED to ours rather than adding a
    // second one over it.
    //
    // Anything not found sends the whole call back to MenuDialog, so a wrong guess costs the vanilla look and nothing
    // else.
    internal static class FiresVanillaDialog
    {
        private const float RowsTopGap = 18f;
        private const float ButtonsGap = 18f;
        private const float RowInset = 24f;
        private const float ErrorHeight = 26f;
        private const int ErrorFontSize = 14;
        private const int MinButtons = 2;
        private const float MinTitleFont = 20f;
        private const string GamepadHintName = "gamepad_hint";

        private static readonly Color ErrorInk = new Color(0.72f, 0.19f, 0.14f);

        private static GameObject s_panel;
        private static IList<MenuDialog.Field> s_fields;
        private static Action s_onOk;
        private static Func<string> s_validate;
        private static TMPro.TextMeshProUGUI s_error;

        internal static bool IsOpen => s_panel != null;

        internal static void Show(string title, IList<MenuDialog.Field> fields, Action onOk,
            Func<string> validate = null, string okLabel = "OK", string cancelLabel = "Cancel")
        {
            Close(restore: true);
            if (!TryBuild(title, fields, okLabel, cancelLabel))
            {
                MenuDialog.Show(title, fields, onOk, null, validate, okLabel, cancelLabel);
                return;
            }
            s_fields = fields;
            s_onOk = onOk;
            s_validate = validate;
        }

        private static bool TryBuild(string title, IList<MenuDialog.Field> fields, string okLabel, string cancelLabel)
        {
            var source = FejdStartup.instance != null ? FejdStartup.instance.m_createWorldPanel : null;
            if (source == null || source.transform.parent == null)
                return Refused("FejdStartup has no create-world panel to borrow");

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.name = "FiresWorldOptions";

                // The panel drew correctly but nothing in it could be clicked, typed in or ticked. UI that renders and
                // ignores every control is not a listener problem - a wrong listener fires the WRONG action, not none -
                // it is the pointer never arriving. Two things can do that to a clone and neither is visible: an
                // ancestor CanvasGroup with interactable or blocksRaycasts off (vanilla dims the menu behind a panel
                // that way), and a sibling drawn after us. ignoreParentGroups is the only switch that beats an
                // ancestor group, and SetAsLastSibling puts us last in our own canvas.
                clone.transform.SetAsLastSibling();
                var group = clone.GetComponent<CanvasGroup>() ?? clone.AddComponent<CanvasGroup>();
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
                group.ignoreParentGroups = true;

                var buttons = clone.GetComponentsInChildren<Button>(true);
                if (buttons.Length < MinButtons)
                { UnityEngine.Object.Destroy(clone); return Refused($"the panel carries {buttons.Length} button(s), needs {MinButtons}"); }

                var host = RowHost(clone);
                if (host == null)
                { UnityEngine.Object.Destroy(clone); return Refused("no row host inside the panel"); }

                // A cloned Button keeps FejdStartup's SERIALIZED onClick entries, and RemoveAllListeners does not drop
                // those - only replacing the event object does. Without this, OK would still start a new world.
                foreach (var button in buttons)
                {
                    button.onClick = new Button.ButtonClickedEvent();
                    button.gameObject.SetActive(true);
                }
                buttons[0].onClick.AddListener(Confirm);
                buttons[1].onClick.AddListener(Cancel);
                Relabel(buttons[0], okLabel);
                Relabel(buttons[1], cancelLabel);
                for (int i = MinButtons; i < buttons.Length; i++) buttons[i].gameObject.SetActive(false);

                var kept = Kept(clone, buttons);
                var titleText = TitleOf(clone, kept);
                if (titleText != null)
                {
                    titleText.text = title ?? "";
                    Keep(kept, titleText.transform, clone.transform);
                }
                SilenceVanillaContents(clone, kept);

                float top = titleText != null ? TitleBottom(host, titleText) + RowsTopGap : RowsTopGap;
                var rows = NewRect("FiresRows", host);
                rows.anchorMin = new Vector2(0f, 1f);
                rows.anchorMax = new Vector2(1f, 1f);
                rows.pivot = new Vector2(0.5f, 1f);
                rows.offsetMin = new Vector2(RowInset, rows.offsetMin.y);
                rows.offsetMax = new Vector2(-RowInset, rows.offsetMax.y);
                rows.anchoredPosition = new Vector2(0f, -top);
                rows.sizeDelta = new Vector2(-RowInset * 2f, MenuDialog.RowsHeightFor(fields != null ? fields.Count : 0));
                MenuDialog.BuildRowsIn(rows, fields);
                Readable(rows, titleText);

                s_error = UIBuilderHelper.CreateLabel(host, "Error", "", ErrorFontSize, ErrorInk,
                    new Vector2(0f, 1f), new Vector2(1f, 1f), TMPro.TextAlignmentOptions.MidlineLeft);
                s_error.rectTransform.pivot = new Vector2(0.5f, 1f);
                s_error.rectTransform.sizeDelta = new Vector2(-RowInset * 2f, ErrorHeight);
                s_error.rectTransform.anchoredPosition = new Vector2(0f, -(top + rows.sizeDelta.y));

                PlaceButtonsBelow(host, buttons, top + rows.sizeDelta.y + ErrorHeight);

                Enable(clone);
                clone.SetActive(true);
                s_panel = clone;
                Diagnose(clone, buttons);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] vanilla panel dialog could not be built ({ex.GetType().Name}: "
                    + $"{ex.Message}); falling back to the Fires panel.");
                if (clone != null) UnityEngine.Object.Destroy(clone);
                return false;
            }
        }

        // The panel's body: the transform vanilla's own inputs hang from. Their shared parent is inside the frame and
        // carries its scale, where the clone ROOT is a full-screen container and stretches rows across the screen.
        private static RectTransform RowHost(GameObject clone)
        {
            var inputs = clone.GetComponentsInChildren<GuiInputField>(true);
            if (inputs.Length == 0) return clone.transform as RectTransform;

            Transform shared = inputs[0].transform.parent;
            for (int i = 1; i < inputs.Length && shared != null; i++)
                shared = CommonAncestor(shared, inputs[i].transform.parent);
            return (shared as RectTransform) ?? clone.transform as RectTransform;
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            for (var walk = a; walk != null; walk = walk.parent)
                for (var other = b; other != null; other = other.parent)
                    if (walk == other) return walk;
            return a;
        }

        // Everything the buttons need, so switching off vanilla's contents cannot take a button's label with it.
        private static HashSet<Transform> Kept(GameObject clone, Button[] buttons)
        {
            var kept = new HashSet<Transform>();
            foreach (var button in buttons)
            {
                Keep(kept, button.transform, clone.transform);
                foreach (var child in button.GetComponentsInChildren<Transform>(true)) kept.Add(child);
            }
            return kept;
        }

        private static void Keep(HashSet<Transform> kept, Transform node, Transform root)
        {
            for (var walk = node; walk != null && walk != root.parent; walk = walk.parent) kept.Add(walk);
        }

        // Vanilla's own title: the largest text that is not part of a button. Retargeted rather than hidden, so ours
        // lands in the frame's own header with the game's font and colour.
        private static TMPro.TextMeshProUGUI TitleOf(GameObject clone, HashSet<Transform> kept)
            => clone.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true)
                .Where(text => text != null && !kept.Contains(text.transform) && text.fontSize >= MinTitleFont)
                .OrderByDescending(text => text.fontSize)
                .FirstOrDefault();

        // The Name and Seed rows, their labels and the "5 characters minimum" hint all survive the clone and would draw
        // over ours. Images are left alone: they are the frame itself.
        private static void SilenceVanillaContents(GameObject clone, HashSet<Transform> kept)
        {
            foreach (var graphic in clone.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null || graphic is Image || kept.Contains(graphic.transform)) continue;
                graphic.gameObject.SetActive(false);
            }
            foreach (var input in clone.GetComponentsInChildren<GuiInputField>(true))
                input.gameObject.SetActive(false);
        }

        private static float TitleBottom(RectTransform host, TMPro.TextMeshProUGUI title)
        {
            var corners = new Vector3[4];
            title.rectTransform.GetWorldCorners(corners);
            Vector3 local = host.InverseTransformPoint(corners[0]);
            float hostTop = host.rect.yMax;
            return Mathf.Max(0f, hostTop - local.y);
        }

        // Vanilla's New World panel has exactly two rows and puts its buttons at a fixed height. Six rows run
        // straight through them, which is what the Clone dialog looked like. The buttons are moved below whatever
        // was actually built, and the frame grows if they no longer fit inside it.
        private static void PlaceButtonsBelow(RectTransform host, Button[] buttons, float contentHeight)
        {
            if (host == null || buttons == null || buttons.Length < MinButtons) return;

            var first = buttons[0].transform as RectTransform;
            var second = buttons[1].transform as RectTransform;
            if (first == null || second == null) return;

            float buttonHeight = Mathf.Max(first.rect.height, second.rect.height);
            float needed = contentHeight + ButtonsGap + buttonHeight + ButtonsGap;
            float grow = needed - host.rect.height;
            if (grow > 0f)
            {
                host.sizeDelta = new Vector2(host.sizeDelta.x, host.sizeDelta.y + grow);
                var frame = host.parent as RectTransform;
                if (frame != null && frame != host)
                    frame.sizeDelta = new Vector2(frame.sizeDelta.x, frame.sizeDelta.y + grow);
            }

            float y = -(contentHeight + ButtonsGap);
            Seat(first, -(first.rect.width * 0.5f + ButtonsGap * 0.5f), y);
            Seat(second, second.rect.width * 0.5f + ButtonsGap * 0.5f, y);
        }

        private static void Seat(RectTransform rect, float x, float y)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
        }

        // Core paints every row label and value with UIFontConfig.Colors.ParchmentInk - near-black ink meant for a
        // light parchment field, which on this wooden panel cannot be read. The rows are repainted with the text
        // colour the game itself is using on this screen, so they follow vanilla instead of a Fires palette.
        private static void Readable(RectTransform rows, TMPro.TextMeshProUGUI template)
        {
            if (rows == null) return;
            var fejd = FejdStartup.instance;
            TMPro.TMP_Text source = fejd != null && fejd.m_worldSourceInfo != null
                ? (TMPro.TMP_Text)fejd.m_worldSourceInfo
                : template;
            if (source == null) return;

            foreach (var text in rows.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (text == null) continue;
                text.color = source.color;
                if (source.font != null) text.font = source.font;
            }
        }

        // Every bail-out used to return false in silence, so a fall back to the Fires panel looked identical to the
        // feature being off, and the one run that mattered logged nothing at all. Each refusal now says which check
        // failed.
        private static bool Refused(string why)
        {
            FiresLogger.LogInfo($"[FiresWorldGen] clone dialog: using the Fires panel instead - {why}.");
            return false;
        }

        // A cloned vanilla control inherits vanilla's STATE, not just its look - and vanilla ships this panel with its
        // controls disabled, because the New World button only lights once the name passes the "5 characters minimum"
        // rule the panel itself advertises. A Selectable with interactable=false draws completely normally and has no
        // hover transition, which is why Cancel did not light up: the pointer was arriving and the button was refusing
        // it. BcWorldListMenu hit the same thing cloning m_publicServerToggle and fixes it the same way.
        //
        // Applied to every Selectable, including the rows Core just built, so a disabled template cannot leak through
        // any control. gamepad_hint children are stripped too: they advertise a button that now does something else.
        private static void Enable(GameObject clone)
        {
            foreach (var selectable in clone.GetComponentsInChildren<Selectable>(true))
                if (selectable != null) selectable.interactable = true;

            foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                if (child != null && child.name == GamepadHintName) child.gameObject.SetActive(false);
        }

        // One line, once per open, naming everything that can swallow a click. If the panel is still dead after the
        // CanvasGroup fix this says which of them did it, instead of another round of guessing: the canvas we actually
        // landed in and its order, whether anything can raycast into it at all, every ancestor group with its switches,
        // and whether the buttons themselves believe they are interactable.
        private static void Diagnose(GameObject clone, Button[] buttons)
        {
            var canvas = clone.GetComponentInParent<Canvas>();
            var raycaster = canvas != null ? canvas.GetComponent<GraphicRaycaster>() : null;
            var groups = new List<string>();
            for (var walk = clone.transform; walk != null; walk = walk.parent)
            {
                var found = walk.GetComponent<CanvasGroup>();
                if (found == null) continue;
                groups.Add($"{walk.name}(a={found.alpha:0.##} int={found.interactable} ray={found.blocksRaycasts}"
                    + $" ign={found.ignoreParentGroups})");
            }

            FiresLogger.LogInfo("[FiresWorldGen] clone dialog: "
                + $"canvas={(canvas != null ? canvas.name : "NONE")} "
                + $"order={(canvas != null ? canvas.sortingOrder.ToString() : "-")} "
                + $"override={(canvas != null && canvas.overrideSorting)} "
                + $"raycaster={(raycaster != null ? (raycaster.enabled ? "on" : "DISABLED") : "MISSING")} "
                + $"eventsystem={(UnityEngine.EventSystems.EventSystem.current != null)} "
                + $"sibling={clone.transform.GetSiblingIndex()}/{clone.transform.parent.childCount} "
                + $"ok={(buttons.Length > 0 && buttons[0].interactable)} "
                + $"cancel={(buttons.Length > 1 && buttons[1].interactable)} "
                + $"groups=[{(groups.Count > 0 ? string.Join(", ", groups) : "none")}]");
        }

        private static void Relabel(Button button, string label)
        {
            if (button == null || string.IsNullOrEmpty(label)) return;
            var text = button.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (text != null) text.text = label;
        }

        private static void Confirm()
        {
            string problem = null;
            try { problem = s_validate != null ? s_validate() : null; }
            catch (Exception ex) { problem = ex.Message; }
            if (!string.IsNullOrEmpty(problem))
            {
                if (s_error != null) s_error.text = problem;
                return;
            }
            var ok = s_onOk;
            Close(restore: false);
            ok?.Invoke();
        }

        private static void Cancel() => Close(restore: true);

        // RestoreFields must never be able to keep the panel open. It ran BEFORE the destroy with nothing catching
        // it, so anything it threw left Cancel looking dead - the click arrived, the handler died, the dialog
        // stayed. The panel is torn down whatever happens.
        private static void Close(bool restore)
        {
            if (restore)
            {
                try { MenuDialog.RestoreFields(s_fields); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresWorldGen] the dialog's fields could not be restored on cancel "
                        + $"({ex.GetType().Name}: {ex.Message}); it is closed anyway.");
                }
            }
            if (s_panel != null) UnityEngine.Object.Destroy(s_panel);
            s_panel = null;
            s_fields = null;
            s_onOk = null;
            s_validate = null;
            s_error = null;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }
    }
}
