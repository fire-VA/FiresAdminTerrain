using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chunk Generator's windows in vanilla's own clothes (Fire, 2026-09-27: "use vanilla ui elements not our own", like
    // a workbench; Tools\CHUNK_GENERATOR.md). The recipe window is a clone of the live crafting panel with its recipe rows,
    // requirement slots and Craft button; the fuel window is a clone of the chest panel whose grid shows the fuels the
    // player carries, with vanilla's stack-split dialog as the amount step. Everything is taken from InventoryGui at
    // runtime, so nothing of IronGate's ships. A clone is made inactive and keeps only UI components; every persistent
    // listener is switched off and gamepad hints go (Tools\CLONED_VANILLA_UI_PERSISTENT_LISTENERS.md). Anything missing
    // returns false, and the caller falls back to its own window.
    internal static class ZoneModeChunkGeneratorWindows
    {
        private const string GuiUtilsAssembly = "assembly_guiutils";
        private const string GamepadHint = "gamepad_hint";
        private const string RowIcon = "icon";
        private const string RowName = "name";
        private const string RowSelected = "selected";
        private const string RowDurability = "Durability";
        private const string RowQuality = "QualityLevel";
        private const string ResIcon = "res_icon";
        private const string ResName = "res_name";
        private const string ResAmount = "res_amount";
        private const string UseLabel = "Use this recipe";
        private const string InUseLabel = "In use";
        private const string RecipesTab = "Recipes";
        private const string FuelViewName = "chunkgeneratorfuel";
        private const int FuelColumns = 8;
        private const int FuelRows = 4;
        private static readonly Color Unowned = new Color(0.66f, 0.66f, 0.66f, 1f);
        private static readonly HashSet<string> KeptGameTypes = new HashSet<string>
            { "InventoryGrid", "InventoryElement", "ScrollRectEnsureVisible", "SplitDialog" };

        private static readonly HashSet<string> s_reported = new HashSet<string>();

        private static Smelter s_machine;
        private static Player s_player;
        private static RecipeWindow s_recipes;
        private static InventoryGrid s_grid;

        internal static InventoryGrid FuelGrid => s_grid;
        private static Inventory s_fuelView;
        private static TMP_Text s_fuelTitle;
        private static SplitDialog s_split;
        private static ItemDrop s_splitFuel;

        private sealed class RecipeWindow
        {
            internal RectTransform List;
            internal TMP_Text Name;
            internal TMP_Text Description;
            internal Image Icon;
            internal Transform[] Requirements;
            internal Button Craft;
            internal TMP_Text CraftLabel;
            internal readonly List<KeyValuePair<ChunkTier, GameObject>> Rows = new List<KeyValuePair<ChunkTier, GameObject>>();
        }

        // ── The hopper: recipes ──────────────────────────────────────────────────────────────────────────────────
        internal static bool TryOpenRecipes(Smelter smelter, Player player, ChunkTier selected)
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_crafting == null || gui.m_recipeElementPrefab == null || gui.m_recipeListRoot == null
                || gui.m_craftButton == null || gui.m_recipeName == null || gui.m_recipeDecription == null)
                return Missing("the crafting panel");
            var root = ZoneModeChunkGeneratorMenu.OpenCanvas(smelter, player, VanillaScale(gui));
            var panel = CloneUi(gui.m_crafting, root, true);
            if (panel == null) { ZoneModeChunkGeneratorMenu.Close(); return Missing("a clone of the crafting panel"); }
            s_machine = smelter;
            s_player = player;

            var window = new RecipeWindow
            {
                List = Twin<RectTransform>(gui.m_crafting, panel, gui.m_recipeListRoot),
                Name = Twin<TMP_Text>(gui.m_crafting, panel, gui.m_recipeName),
                Description = Twin<TMP_Text>(gui.m_crafting, panel, gui.m_recipeDecription),
                Icon = Twin<Image>(gui.m_crafting, panel, gui.m_recipeIcon),
                Craft = Twin<Button>(gui.m_crafting, panel, gui.m_craftButton),
            };
            if (window.List == null || window.Name == null || window.Description == null || window.Craft == null)
            {
                ZoneModeChunkGeneratorMenu.Close();
                return Missing("the crafting panel's recipe list, text or Craft button in its clone");
            }
            window.CraftLabel = window.Craft.GetComponentInChildren<TMP_Text>(true);
            var requirements = new List<Transform>();
            foreach (var slot in gui.m_recipeRequirementList)
            {
                var twin = slot != null ? Twin<Transform>(gui.m_crafting, panel, slot.transform) : null;
                if (twin != null) requirements.Add(twin);
            }
            window.Requirements = requirements.ToArray();
            ShowRecipePage(panel, window);

            foreach (var part in new Component[]
            {
                gui.m_craftCancelButton, gui.m_variantButton, gui.m_qualityPanel, gui.m_craftProgressPanel, gui.m_minStationLevelIcon,
                gui.m_minStationLevelText, gui.m_craftingStationLevelRoot, gui.m_tabUpgrade, gui.m_repairButton, gui.m_repairPanel,
                gui.m_repairPanelSelection, gui.m_itemCraftType, gui.m_upgradeItemIcon, gui.m_upgradeItemName, gui.m_upgradeItemQuality,
                gui.m_upgradeItemNextQuality, gui.m_upgradeItemIndex,
            })
                Hide(Twin<Component>(gui.m_crafting, panel, part));
            if (gui.m_upgradeItemQualityArrow != null) Hide(Twin<Transform>(gui.m_crafting, panel, gui.m_upgradeItemQualityArrow.transform));

            var stationName = Twin<TMP_Text>(gui.m_crafting, panel, gui.m_craftingStationName);
            if (stationName != null) stationName.text = smelter.m_name;
            var stationIcon = Twin<Image>(gui.m_crafting, panel, gui.m_craftingStationIcon);
            if (stationIcon != null && ZoneModeChunkGenerator.TryGetPrefab(out var prefab))
            {
                stationIcon.sprite = prefab.GetComponent<Piece>()?.m_icon;
                stationIcon.enabled = stationIcon.sprite != null;
            }
            var tab = Twin<Button>(gui.m_crafting, panel, gui.m_tabCraft);
            if (tab != null)
            {
                tab.interactable = false;
                var label = tab.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = RecipesTab;
            }

            foreach (Transform old in window.List) UnityEngine.Object.Destroy(old.gameObject);
            int index = 0;
            foreach (var recipe in ZoneModeChunkTiers.All)
            {
                if (!ZoneModeChunkGenerator.IsKnown(recipe)) continue;
                var row = UnityEngine.Object.Instantiate(gui.m_recipeElementPrefab, window.List);
                Silence(row);
                row.SetActive(true);
                ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, index * -gui.m_recipeListSpace);
                var icon = row.transform.Find(RowIcon)?.GetComponent<Image>();
                if (icon != null) { icon.sprite = SeedIcon(recipe); icon.color = Color.white; }
                var name = row.transform.Find(RowName)?.GetComponent<TMP_Text>();
                if (name != null) { name.text = Localize(recipe.RecipeName) + $" x{recipe.SeedsPerBatch}"; name.color = Color.white; }
                Hide(row.transform.Find(RowDurability));
                Hide(row.transform.Find(RowQuality));
                var chosen = recipe;
                row.GetComponent<Button>()?.onClick.AddListener(() => Select(chosen));
                window.Rows.Add(new KeyValuePair<ChunkTier, GameObject>(recipe, row));
                index++;
            }
            float listHeight = Mathf.Max(gui.m_recipeListRoot.rect.height, index * gui.m_recipeListSpace);
            window.List.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, listHeight);
            window.Craft.onClick.AddListener(() => Choose(s_recipesSelected));
            s_recipes = window;
            Select(selected);
            return true;
        }

        private static ChunkTier s_recipesSelected;

        private static void Select(ChunkTier recipe)
        {
            var window = s_recipes;
            if (window == null || s_machine == null) return;
            s_recipesSelected = recipe;
            var zdo = s_machine.GetComponent<ZNetView>().GetZDO();
            var current = ZoneModeChunkGenerator.CurrentRecipe(zdo);
            foreach (var row in window.Rows)
            {
                var highlight = row.Value.transform.Find(RowSelected);
                if (highlight != null) highlight.gameObject.SetActive(row.Key == recipe);
            }
            window.Name.text = Localize(recipe.RecipeName);
            if (window.Icon != null) { window.Icon.sprite = SeedIcon(recipe); window.Icon.enabled = window.Icon.sprite != null; }

            int refund = ZoneModeChunkGenerator.WouldRefund(zdo, recipe);
            string description = ZoneModeSeeds.TryGet(recipe.SpawnerPrefab, out var seed) ? seed.Description + "\n\n" : "";
            description += $"One full load of {ZoneModeChunkTiers.BatchFuel} fuel makes {recipe.SeedsPerBatch}. {ZoneModeChunkGeneratorMenu.BurnsLine(recipe)}";
            if (recipe == current) description += "\n\nThe generator is making this now.";
            else if (refund > 0) description += $"\n\n<color=orange>{refund} loaded fuel(s) it cannot burn will be set beside the generator.</color>";
            window.Description.text = description;

            var fuels = new List<ItemDrop>();
            foreach (string name in recipe.RestoresOnly ? ZoneModeChunkTiers.ByLevel(0).Inputs : recipe.Inputs)
                if (ZoneModeChunkGenerator.TryGetFuel(name, out var fuel)) fuels.Add(fuel);
            for (int i = 0; i < window.Requirements.Length; i++)
                ShowRequirement(window.Requirements[i], i < fuels.Count ? fuels[i] : null);

            window.Craft.interactable = recipe != current;
            if (window.CraftLabel != null) window.CraftLabel.text = recipe == current ? InUseLabel : UseLabel;
            ZoneModeChunkGeneratorMenu.SetKeys(recipe != current ? () => Choose(recipe) : (Action)null, ZoneModeChunkGeneratorMenu.Close);
        }

        // A requirement slot showing one fuel the recipe burns, with how many the player carries.
        private static void ShowRequirement(Transform slot, ItemDrop fuel)
        {
            var icon = slot.Find(ResIcon)?.GetComponent<Image>();
            var name = slot.Find(ResName)?.GetComponent<TMP_Text>();
            var amount = slot.Find(ResAmount)?.GetComponent<TMP_Text>();
            var tooltip = slot.GetComponent<UITooltip>();
            bool shown = fuel != null;
            if (icon != null) icon.gameObject.SetActive(shown);
            if (name != null) name.gameObject.SetActive(shown);
            if (amount != null) amount.gameObject.SetActive(shown);
            if (tooltip != null) tooltip.m_text = shown ? Localize(fuel.m_itemData.m_shared.m_name) : "";
            if (!shown) return;
            string shared = fuel.m_itemData.m_shared.m_name;
            int carried = s_player != null ? s_player.GetInventory().CountItems(shared, -1, false) : 0;
            if (icon != null) { icon.sprite = fuel.m_itemData.GetIcon(); icon.color = Color.white; }
            if (name != null) name.text = Localize(shared);
            if (amount != null) { amount.text = carried.ToString(); amount.color = carried > 0 ? Color.white : Unowned; }
        }

        private static void Choose(ChunkTier recipe)
        {
            var machine = s_machine;
            var player = s_player;
            if (machine == null || recipe == null) return;
            ZoneModeChunkGeneratorMenu.Close();
            ZoneModeChunkGenerator.ChooseRecipe(machine, recipe);
            player?.Message(MessageHud.MessageType.Center, $"{machine.m_name} now makes {Localize(recipe.RecipeName)}");
        }

        // ── The door: fuel ───────────────────────────────────────────────────────────────────────────────────────
        internal static bool TryOpenFuel(Smelter smelter, Player player, ItemDrop preselect)
        {
            var gui = InventoryGui.instance;
            var liveGrid = gui != null ? gui.ContainerGrid : null;
            if (gui == null || gui.m_container == null || liveGrid == null || gui.m_splitDialog == null) return Missing("the chest panel or the split dialog");
            var root = ZoneModeChunkGeneratorMenu.OpenCanvas(smelter, player, VanillaScale(gui));
            var panel = CloneUi(gui.m_container, root, true);
            var split = CloneUi((RectTransform)gui.m_splitDialog.transform, root, false);
            s_grid = panel != null ? Twin<InventoryGrid>(gui.m_container, panel, liveGrid) : null;
            s_split = split != null ? split.GetComponent<SplitDialog>() : null;
            if (s_grid == null || s_split == null)
            {
                ZoneModeChunkGeneratorMenu.Close();
                return Missing("the chest grid or split dialog in their clones");
            }
            // The dialog's own OK and Cancel buttons carry our step; its SplitAccepted/SplitCanceled events clash with
            // their backing fields in the publicized game assembly, so they are left alone. It still hides itself.
            if (s_split.m_splitOkButton == null || s_split.m_splitCancelButton == null)
            {
                ZoneModeChunkGeneratorMenu.Close();
                return Missing("the split dialog's OK and Cancel buttons in its clone");
            }
            s_split.m_splitOkButton.onClick.AddListener(AcceptSplit);
            s_split.m_splitCancelButton.onClick.AddListener(CancelSplit);
            s_machine = smelter;
            s_player = player;
            s_split.SetActive(false);
            s_fuelTitle = Twin<TMP_Text>(gui.m_container, panel, gui.m_containerName);
            Hide(Twin<Component>(gui.m_container, panel, gui.m_takeAllButton));
            Hide(Twin<Component>(gui.m_container, panel, gui.m_stackAllButton));
            var weight = Twin<TMP_Text>(gui.m_container, panel, gui.m_containerWeight);
            if (weight != null) Hide(weight.transform.parent != panel ? weight.transform.parent : weight.transform);
            s_grid.m_onSelected = (grid, item, position, modifier) => Picked(item);
            s_grid.m_onRightClick = (grid, item, position) => Picked(item);
            // InventoryGui hands its own grids this drop test at Awake; a clone has none, and UpdateGui calls it for every
            // item it shows, so without it the grid stops after its first item with every square's parts still showing.
            s_grid.CanDropDragOntoItem = item => false;
            if (!RefreshFuel()) return true;
            if (preselect != null) ShowSplit(preselect);
            ZoneModeChunkGeneratorMenu.SetKeys(null, ZoneModeChunkGeneratorMenu.Close);
            return true;
        }

        // The player's own stacks of every fuel the recipe burns, as the grid shows them; closes with a centre message
        // once there is nothing left to add or no room.
        private static bool RefreshFuel()
        {
            if (s_machine == null || s_player == null || s_grid == null) return false;
            var zdo = s_machine.GetComponent<ZNetView>().GetZDO();
            var recipe = ZoneModeChunkGenerator.CurrentRecipe(zdo);
            int loaded = ZoneModeChunkGenerator.Loaded(zdo);
            string refusal = FuelRefusal(recipe, s_player, loaded);
            if (refusal != null)
            {
                var player = s_player;
                ZoneModeChunkGeneratorMenu.Close();
                player.Message(MessageHud.MessageType.Center, refusal);
                return false;
            }
            s_fuelView = new Inventory(FuelViewName, null, FuelColumns, FuelRows);
            int slot = 0;
            foreach (var item in s_player.GetInventory().GetAllItems())
            {
                if (slot >= FuelColumns * FuelRows) break;
                if (item?.m_dropPrefab == null || !ZoneModeChunkGenerator.TryGetFuel(item.m_dropPrefab.name, out _)) continue;
                if (!ZoneModeChunkTiers.Burns(recipe, item.m_dropPrefab.name)) continue;
                var copy = item.Clone();
                copy.m_equipped = false;
                copy.m_gridPos = new Vector2i(slot % FuelColumns, slot / FuelColumns);
                s_fuelView.GetAllItems().Add(copy);
                slot++;
            }
            if (s_fuelTitle != null)
                s_fuelTitle.text = $"{s_machine.m_name}: {Localize(recipe.RecipeName)} ({loaded}/{ZoneModeChunkGenerator.Capacity})";
            s_grid.UpdateInventory(s_fuelView, null, null);
            return true;
        }

        // Why the fuel window does not open, as a centre message, or null when there is something to add.
        internal static string FuelRefusal(ChunkTier recipe, Player player, int loaded)
        {
            if (loaded >= ZoneModeChunkGenerator.Capacity) return $"It is full ({loaded}/{ZoneModeChunkGenerator.Capacity}).";
            if (ZoneModeChunkGenerator.CarriedFuel(recipe, player).Count == 0)
                return $"You carry nothing it burns for {Localize(recipe.RecipeName)}. {ZoneModeChunkGeneratorMenu.BurnsLine(recipe)}";
            return null;
        }

        private static void Picked(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || s_split == null || s_split.IsActive) return;
            if (ZoneModeChunkGenerator.TryGetFuel(item.m_dropPrefab.name, out var fuel)) ShowSplit(fuel);
        }

        // Vanilla's stack-split dialog as the amount step: 1 to what is carried and fits, starting at the most.
        private static void ShowSplit(ItemDrop fuel)
        {
            var zdo = s_machine.GetComponent<ZNetView>().GetZDO();
            string shared = fuel.m_itemData.m_shared.m_name;
            int most = Mathf.Min(s_player.GetInventory().CountItems(shared, -1, false), ZoneModeChunkGenerator.Capacity - ZoneModeChunkGenerator.Loaded(zdo));
            if (most <= 0) return;
            Unhook();
            s_splitFuel = fuel;
            s_split.UpdateLimits(most, false);
            s_split.SliderValue = most;
            s_split.UpdateIcon(fuel.m_itemData.GetIcon(), Localize(shared));
            s_split.SetActive(true);
            ZoneModeChunkGeneratorMenu.SetKeys(() => { AcceptSplit(); s_split?.SetActive(false); }, () => { CancelSplit(); s_split?.SetActive(false); });
        }

        private static void AcceptSplit()
        {
            var fuel = s_splitFuel;
            if (fuel == null) return;
            int amount = s_split != null ? Mathf.RoundToInt(s_split.SliderValue) : 0;
            Unhook();
            if (fuel != null && amount > 0) ZoneModeChunkGenerator.AddFuel(s_machine, s_player, fuel, amount);
            if (RefreshFuel()) ZoneModeChunkGeneratorMenu.SetKeys(null, ZoneModeChunkGeneratorMenu.Close);
        }

        private static void CancelSplit()
        {
            Unhook();
            ZoneModeChunkGeneratorMenu.SetKeys(null, ZoneModeChunkGeneratorMenu.Close);
        }

        private static void Unhook() => s_splitFuel = null;

        // Every frame while the fuel window is open, as InventoryGui does for its grids: hover highlight and tooltips.
        internal static void Tick()
        {
            if (s_grid != null && s_fuelView != null) s_grid.UpdateInventory(s_fuelView, null, null);
        }

        internal static void Clear()
        {
            Unhook();
            s_machine = null;
            s_player = null;
            s_recipes = null;
            s_recipesSelected = null;
            s_grid = null;
            s_fuelView = null;
            s_fuelTitle = null;
            s_split = null;
        }

        // ── Cloning vanilla UI ───────────────────────────────────────────────────────────────────────────────────
        // The clone is built under an inactive holder, so none of its components wakes before the strip; then it moves to
        // `parent`, centred at the live panel's size (or keeping its own anchors, for a full-screen dialog).
        private static RectTransform CloneUi(RectTransform source, Transform parent, bool centre)
        {
            if (source == null) return null;
            var holder = new GameObject("FiresChunkGeneratorClone");
            holder.SetActive(false);
            try
            {
                var copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform, false);
                Strip(copy);
                Silence(copy);
                var rect = (RectTransform)copy.transform;
                rect.SetParent(parent, false);
                if (centre)
                {
                    var size = source.rect.size;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.sizeDelta = size;
                    rect.anchoredPosition = Vector2.zero;
                }
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
                copy.SetActive(true);
                return rect;
            }
            catch (Exception ex)
            {
                if (s_reported.Add("clone " + source.name))
                    FiresLogger.LogWarning($"[FiresZones] Chunk Generator window: cloning vanilla '{source.name}' threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        // Keeps UI components (Unity UI, TextMeshPro, Iron Gate's GUI utilities, the inventory grid, the split dialog);
        // everything else another mod or vanilla hung on the panel goes, and so do gamepad hints.
        private static void Strip(GameObject root)
        {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour is UIBehaviour) continue;
                var type = behaviour.GetType();
                if (type.Assembly.GetName().Name == GuiUtilsAssembly || KeptGameTypes.Contains(type.Name)) continue;
                UnityEngine.Object.DestroyImmediate(behaviour);
            }
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
                if (node != null && node.name == GamepadHint) UnityEngine.Object.DestroyImmediate(node.gameObject);
        }

        private static void Silence(GameObject root)
        {
            foreach (var button in root.GetComponentsInChildren<Button>(true)) Silence(button.onClick);
            foreach (var toggle in root.GetComponentsInChildren<Toggle>(true)) Silence(toggle.onValueChanged);
            foreach (var slider in root.GetComponentsInChildren<Slider>(true)) Silence(slider.onValueChanged);
            foreach (var bar in root.GetComponentsInChildren<Scrollbar>(true)) Silence(bar.onValueChanged);
            foreach (var scroll in root.GetComponentsInChildren<ScrollRect>(true)) Silence(scroll.onValueChanged);
            foreach (var field in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                Silence(field.onValueChanged);
                Silence(field.onEndEdit);
                Silence(field.onSubmit);
                Silence(field.onSelect);
                Silence(field.onDeselect);
            }
        }

        private static void Silence(UnityEventBase raised)
        {
            if (raised == null) return;
            for (int index = 0; index < raised.GetPersistentEventCount(); index++)
                raised.SetPersistentListenerState(index, UnityEventCallState.Off);
        }

        // The component at the same place under the clone as `live` has under `liveRoot`, or null.
        private static T Twin<T>(Transform liveRoot, Transform cloneRoot, Component live) where T : Component
        {
            if (live == null || liveRoot == null || cloneRoot == null) return null;
            if (live.transform != liveRoot && !live.transform.IsChildOf(liveRoot)) return null;
            string path = PathFrom(liveRoot, live.transform);
            var node = path.Length == 0 ? cloneRoot : cloneRoot.Find(path);
            if (node == null) return null;
            if (typeof(T) == typeof(Transform) || typeof(T) == typeof(RectTransform)) return node as T ?? node.GetComponent<T>();
            return node.GetComponent<T>() ?? (typeof(T) == typeof(Component) ? node.GetComponent(live.GetType()) as T : null);
        }

        private static string PathFrom(Transform root, Transform node)
        {
            if (node == root) return "";
            string path = node.name;
            for (var parent = node.parent; parent != null && parent != root; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        private static void Hide(Component part)
        {
            if (part != null) part.gameObject.SetActive(false);
        }

        // The clone copies whatever page the live crafting panel shows. FATI's tab ([seasons], FiresAllTheItems FatiTab) hides
        // vanilla's RecipeList and Decription and draws its own grid (FATI_BaseUI) in that panel, and its tab state outlives the
        // inventory closing, so after using FATI the recipe window opened on a dead copy of FATI's page with no way back (Fire,
        // 10-05). The recipe window shows vanilla's own parts: each part it uses is made visible up to the clone's root, and any
        // other mod's page or tab in the clone (a "FATI" name) is hidden. What was found is logged at Debug.
        private static void ShowRecipePage(RectTransform panel, RecipeWindow window)
        {
            var reopened = new List<string>();
            var parts = new List<Component> { window.List, window.Name, window.Description, window.Icon, window.Craft };
            foreach (var slot in window.Requirements) parts.Add(slot);
            foreach (var part in parts)
            {
                if (part == null) continue;
                for (var node = part.transform; node != null && node != panel.parent; node = node.parent)
                {
                    if (node.gameObject.activeSelf) continue;
                    node.gameObject.SetActive(true);
                    if (!reopened.Contains(node.name)) reopened.Add(node.name);
                }
            }
            var hidden = new List<string>();
            foreach (var node in panel.GetComponentsInChildren<Transform>(true))
            {
                if (node == panel || !node.name.StartsWith("FATI", StringComparison.OrdinalIgnoreCase)) continue;
                if (node.gameObject.activeSelf) hidden.Add(node.name);
                node.gameObject.SetActive(false);
            }
            FiresLogger.LogDebug(reopened.Count == 0 && hidden.Count == 0
                ? "[FiresZones] Chunk Generator recipes: the crafting panel was on its recipe page"
                : $"[FiresZones] Chunk Generator recipes: the crafting panel was on another page; shown again: " +
                  $"{(reopened.Count > 0 ? string.Join(", ", reopened) : "none")}; hidden: {(hidden.Count > 0 ? string.Join(", ", hidden) : "none")}");
        }

        // The game's GUI canvas scale, so the clones come out exactly as big as vanilla draws them.
        private static float VanillaScale(InventoryGui gui)
        {
            var canvas = gui.GetComponentInParent<Canvas>();
            return canvas != null && canvas.rootCanvas != null ? canvas.rootCanvas.scaleFactor : 1f;
        }

        private static Sprite SeedIcon(ChunkTier recipe)
            => ZoneModePrefabs.TryGet(recipe.SpawnerPrefab, out var prefab) ? prefab.GetComponent<ItemDrop>()?.m_itemData?.GetIcon() : null;

        private static string Localize(string text) => Localization.instance != null ? Localization.instance.Localize(text) : text;

        private static bool Missing(string what)
        {
            if (s_reported.Add(what))
                FiresLogger.LogWarning($"[FiresZones] Chunk Generator window: no {what} to borrow; using the plain window instead.");
            return false;
        }
    }
}
