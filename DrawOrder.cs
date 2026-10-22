using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System;
using BepInEx.Configuration;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine.Rendering;
using UnityEngine;

namespace TailsideFurnitureOverlap
{
    public sealed partial class Plugin
    {
        // Order orchestration; actual renderer state belongs to DrawOrderState below.
        private readonly Dictionary<Item, DrawOrderState> orderStates = new Dictionary<Item, DrawOrderState>();
        private readonly List<Item> retiredOrders = new List<Item>();
        public static bool ChangeOrder(Item item, int direction, bool reset = false)
        {
            var placement = ItemPlacement.Instance;
            if (instance == null || !instance.overlapEnabled.Value || placement == null ||
                !placement.inBuildMode || placement.currentItem != item || item == null || item.isPlaced) return false;
            var sprite = item.GetComponent<SpriteRenderer>();
            if (sprite == null) return false;
            if (!instance.orderStates.TryGetValue(item, out var state))
            {
                state = new DrawOrderState(sprite);
                instance.orderStates.Add(item, state);
            }
            state.ObserveStandard();
            state.Offset = reset ? 0 : Mathf.Clamp(state.Offset + Math.Sign(direction), -1000, 1000);
            state.Apply();
            if (item.savedItem != null) instance.savedOrders[item.savedItem] = state.Offset;
            instance.Logger.LogInfo("DRAW_ORDER item=" + item.name + " offset=" + state.Offset + " effective=" + sprite.sortingOrder + (reset ? " (reset)" : ""));
            return true;
        }

        private void LateUpdate()
        {
            if (instance != this || overlapEnabled == null) return;
            if (!overlapEnabled.Value) { RestoreDrawOrders(false); return; }
            SynchronizeOrders();
        }
        private static void SynchronizeOrders()
        {
            if (instance == null || !instance.overlapEnabled.Value) return;
            instance.retiredOrders.Clear();
            foreach (var pair in instance.orderStates)
            {
                if (pair.Key == null || pair.Value.Renderer == null)
                {
                    pair.Value.Restore(true);
                    instance.retiredOrders.Add(pair.Key);
                    continue;
                }
                pair.Value.ObserveStandard();
                pair.Value.Apply();
            }
            foreach (var item in instance.retiredOrders) instance.orderStates.Remove(item);
            instance.retiredOrders.Clear();
        }
        private void RestoreDrawOrders(bool clear = true)
        {
            foreach (var state in orderStates.Values) state.Restore(clear);
            if (clear) { orderStates.Clear(); savedOrders.Clear(); retiredOrders.Clear(); }
        }
        private void TrackOrder(Item item, int offset)
        {
            var renderer = item.GetComponent<SpriteRenderer>();
            if (renderer == null) return;
            var state = new DrawOrderState(renderer) { Offset = offset };
            orderStates[item] = state;
            if (overlapEnabled.Value) state.Apply();
        }

        // Input routing: keyboard compatibility and temporary inventory D-pad mask.
        private int lastInputFrame = -1;
        private readonly HashSet<int> inputReadyItems = new HashSet<int>();
        private static bool legacyInputUnavailable;
        public static bool TryOrderInputKey(KeyCode key, out UnityEngine.InputSystem.Key mapped)
        {
            string name = key.ToString();
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) name = "Digit" + ((int)key - (int)KeyCode.Alpha0);
            else if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) name = "Numpad" + ((int)key - (int)KeyCode.Keypad0);
            else switch (key)
            {
                case KeyCode.Return: name = "Enter"; break;
                case KeyCode.KeypadEnter: name = "NumpadEnter"; break;
                case KeyCode.KeypadPeriod: name = "NumpadPeriod"; break;
                case KeyCode.KeypadDivide: name = "NumpadDivide"; break;
                case KeyCode.KeypadMultiply: name = "NumpadMultiply"; break;
                case KeyCode.KeypadMinus: name = "NumpadMinus"; break;
                case KeyCode.KeypadPlus: name = "NumpadPlus"; break;
                case KeyCode.KeypadEquals: name = "NumpadEquals"; break;
                case KeyCode.LeftControl: name = "LeftCtrl"; break;
                case KeyCode.RightControl: name = "RightCtrl"; break;
                case KeyCode.LeftCommand: case KeyCode.LeftWindows: name = "LeftMeta"; break;
                case KeyCode.RightCommand: case KeyCode.RightWindows: name = "RightMeta"; break;
                case KeyCode.Numlock: name = "NumLock"; break;
                case KeyCode.BackQuote: name = "Backquote"; break;
            }
            return Enum.TryParse(name, out mapped) && mapped != UnityEngine.InputSystem.Key.None &&
                Enum.IsDefined(typeof(UnityEngine.InputSystem.Key), mapped);
        }
        private static bool OrderKeyPressed(KeyCode key)
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && TryOrderInputKey(key, out var newKey) && keyboard[newKey].wasPressedThisFrame) return true;
            if (legacyInputUnavailable || key == KeyCode.None) return false;
            try { return Input.GetKeyDown(key); }
            catch (InvalidOperationException) { legacyInputUnavailable = true; return false; }
            catch (NotSupportedException) { legacyInputUnavailable = true; return false; }
        }
        private static void ProcessOrderInput(ItemPlacement __instance)
        {
            if (instance == null || !instance.overlapEnabled.Value || __instance == null ||
                !__instance.inBuildMode || __instance.currentItem == null || PauseMenu.GameIsPaused) return;
            if (instance.lastInputFrame == Time.frameCount) return;
            instance.lastInputFrame = Time.frameCount;
            var pad = instance.gamepadOrderEnabled.Value ? UnityEngine.InputSystem.Gamepad.current : null;
            if (instance.inputReadyItems.Count < 8 && instance.inputReadyItems.Add(__instance.currentItem.GetInstanceID()))
                instance.Logger.LogInfo("DRAW_ORDER_INPUT_READY item=" + __instance.currentItem.name +
                    " pluginEnabled=" + instance.enabled + " keyboard=" + (UnityEngine.InputSystem.Keyboard.current != null));
            if (OrderKeyPressed(instance.resetKey.Value) || (pad != null && pad.dpad.left.wasPressedThisFrame))
            {
                instance.Logger.LogInfo("DRAW_ORDER_KEY reset");
                ChangeOrder(__instance.currentItem, 0, true);
            }
            else if (OrderKeyPressed(instance.forwardKey.Value) || (pad != null && pad.dpad.up.wasPressedThisFrame))
            {
                instance.Logger.LogInfo("DRAW_ORDER_KEY forward");
                ChangeOrder(__instance.currentItem, 1);
            }
            else if (OrderKeyPressed(instance.backwardKey.Value) || (pad != null && pad.dpad.down.wasPressedThisFrame))
            {
                instance.Logger.LogInfo("DRAW_ORDER_KEY backward");
                ChangeOrder(__instance.currentItem, -1);
            }
            SynchronizeOrders();
        }
        private sealed class NavigationMask
        {
            public UserControl Controls;
            public Vector2 Original;
        }
        private static void MaskNavigationDpad(out NavigationMask __state)
        {
            __state = null;
            var placement = ItemPlacement.Instance;
            if (instance != null && instance.overlapEnabled.Value && instance.gamepadOrderEnabled.Value &&
                placement != null && placement.inBuildMode && placement.currentItem != null &&
                !placement.currentItem.isPlaced && !PauseMenu.GameIsPaused && UnityEngine.InputSystem.Gamepad.current != null && UserControl.instance != null)
            {
                __state = new NavigationMask { Controls = UserControl.instance, Original = UserControl.instance.dPadMovement };
                UserControl.instance.dPadMovement = Vector2.zero;
            }
        }
        private static Exception RestoreNavigationDpad(Exception __exception, NavigationMask __state)
        {
            if (__state != null && __state.Controls != null) __state.Controls.dPadMovement = __state.Original;
            return __exception;
        }

        // Save integration. Data schema and file I/O belong to OrderStorage below.
        private Dictionary<SavedItem, int> savedOrders = new Dictionary<SavedItem, int>();
        private string orderFolder;
        private static readonly System.Reflection.FieldInfo CurrentSave = AccessTools.Field(typeof(SaveManager), "CurrentSaveFile");
        private static readonly System.Reflection.FieldInfo SaveSingleton = AccessTools.Field(typeof(SaveManager), "<_instance>k__BackingField");
        private static void RememberPlacedOrder(Item __instance)
        {
            if (instance == null || __instance.savedItem == null) return;
            if (instance.orderStates.TryGetValue(__instance, out var state))
                instance.savedOrders[__instance.savedItem] = state.Offset;
        }
        private static void RestoreSavedReference(Item __instance)
        {
            if (instance == null || __instance.savedItem == null || instance.orderStates.ContainsKey(__instance)) return;
            if (instance.savedOrders.TryGetValue(__instance.savedItem, out int offset) && offset != 0)
                instance.TrackOrder(__instance, offset);
        }
        private static string SavePath(SaveManager manager, SaveFile save)
        {
            return Path.Combine(manager.GetSaveFolderPathByID(), (save.isSandbox ? "Sandbox" : "SaveFile") + manager.currentSlotId + ".sav");
        }
        private static void LoadDrawOrders(ItemPlacement __instance)
        {
            if (instance == null) return;
            instance.RestoreDrawOrders();
            try
            {
                var manager = SaveSingleton.GetValue(null) as SaveManager;
                var save = manager == null ? null : CurrentSave.GetValue(manager) as SaveFile;
                if (save == null || __instance.currentPlacedItems == null) return;
                var file = SavePath(manager, save);
                if (!File.Exists(file)) return;
                var doc = OrderStorage.Read(instance.orderFolder, File.ReadAllBytes(file));
                instance.savedOrders = OrderStorage.Match(doc, __instance.currentPlacedItems);
                foreach (var item in UnityEngine.Object.FindObjectsOfType<Item>())
                    if (item.isPlaced && item.savedItem != null && instance.savedOrders.TryGetValue(item.savedItem, out int offset) && offset != 0)
                        instance.TrackOrder(item, offset);
                instance.Logger.LogInfo("DRAW_ORDER loaded overrides=" + instance.savedOrders.Count);
            }
            catch (Exception ex)
            {
                instance.RestoreDrawOrders();
                instance.Logger.LogWarning("Draw-order data skipped; standard order retained: " + ex.Message);
            }
        }
        private static void SaveDrawOrders(SaveManager __instance, ref Task<bool> __result)
        {
            if (instance == null || __result == null) return;
            try
            {
                var save = CurrentSave.GetValue(__instance) as SaveFile;
                if (save?.allSavedItems == null) return;
                var doc = OrderStorage.Capture(save.allSavedItems, instance.savedOrders);
                string file = SavePath(__instance, save);
                string folder = instance.orderFolder;
                var logger = instance.Logger;
                __result = StoreAfterSave(__result, file, folder, doc, message => logger.LogWarning(message));
            }
            catch (Exception ex) { instance.Logger.LogWarning("Draw-order save skipped: " + ex.Message); }
        }
        private static async Task<bool> StoreAfterSave(Task<bool> gameSave, string file, string folder, OrderStorage.Document doc, Action<string> warning)
        {
            bool success = await gameSave;
            if (success)
            {
                try { OrderStorage.Write(folder, File.ReadAllBytes(file), doc); }
                catch (Exception ex) { warning("Game saved; draw-order data could not be saved: " + ex.Message); }
            }
            return success;
        }

    }

    // Owns one furniture renderer and restores only the changes made by this Mod.
    internal sealed class DrawOrderState
    {
        internal SpriteRenderer Renderer { get; }
        internal int Offset { get; set; }
        private int Standard, Applied;
        private bool HasApplied;
        private SortingGroup Group;
        private bool OwnGroup, GroupWasEnabled, GroupWasRoot, GroupApplied;
        private int GroupLayer, GroupOrder;

        internal DrawOrderState(SpriteRenderer renderer)
        {
            Renderer = renderer;
            Standard = renderer.sortingOrder;
        }

        internal void ObserveStandard()
        {
            if (Renderer != null && (!HasApplied || Renderer.sortingOrder != Applied))
                Standard = Renderer.sortingOrder;
        }
        internal void Apply()
        {
            if (Renderer == null) return;
            Applied = Mathf.Clamp(Standard + Offset, short.MinValue, short.MaxValue);
            Renderer.sortingOrder = Applied;
            HasApplied = true;
            ApplyFrontGroup();
        }
        // Parent groups are indivisible sorting units. A positive user adjustment
        // opts this furniture into its own root group, above world equipment.
        // The hierarchy, colliders and original component settings are preserved.
        private void ApplyFrontGroup()
        {
            if (Offset <= 0) { RestoreFrontGroup(); return; }
            if (Group == null)
            {
                Group = Renderer.GetComponent<SortingGroup>();
                OwnGroup = Group == null;
                if (OwnGroup) Group = Renderer.gameObject.AddComponent<SortingGroup>();
                GroupWasEnabled = !OwnGroup && Group.enabled;
                GroupWasRoot = Group.sortAtRoot;
                GroupLayer = Group.sortingLayerID;
                GroupOrder = Group.sortingOrder;
            }
            int foreground = SortingLayer.NameToID("Foreground");
            // Never lower a furniture group that already lives above Foreground.
            if (SortingLayer.GetLayerValueFromID(GroupLayer) > SortingLayer.GetLayerValueFromID(foreground))
                foreground = GroupLayer;
            if (SortingLayer.GetLayerValueFromID(Renderer.sortingLayerID) > SortingLayer.GetLayerValueFromID(foreground))
                foreground = Renderer.sortingLayerID;
            Group.sortingLayerID = foreground;
            Group.sortingOrder = Mathf.Clamp(Math.Max(0, GroupOrder) + Offset, short.MinValue, short.MaxValue);
            Group.sortAtRoot = true;
            Group.enabled = true;
            GroupApplied = true;
        }
        private void RestoreFrontGroup()
        {
            if (Group == null || !GroupApplied) return;
            Group.sortingLayerID = GroupLayer;
            Group.sortingOrder = GroupOrder;
            Group.sortAtRoot = GroupWasRoot;
            Group.enabled = GroupWasEnabled;
            GroupApplied = false;
        }
        internal void Restore(bool dispose)
        {
            RestoreFrontGroup();
            if (dispose && OwnGroup && Group != null) UnityEngine.Object.Destroy(Group);
            if (Renderer != null && HasApplied)
            {
                ObserveStandard();
                Renderer.sortingOrder = Standard;
                HasApplied = false;
            }
        }
    }

    // Only mod-owned files are written. The exact vanilla save bytes are hashed
    // so a different layout, account or old backup cannot inherit stale indices.
    public static class OrderStorage
    {
        public sealed class Entry { public int Index; public string Item; public int Offset; }
        public sealed class Document
        {
            public int Version = 1;
            public string Layout;
            public List<Entry> Entries = new List<Entry>();
        }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string ItemKey(SavedItem item)
        {
            if (item == null || item.SavedPosition == null) return "null";
            var p = item.SavedPosition;
            return ((int)item.Type) + ":" + item.prefabListIndex + ":" +
                p.x.ToString("R", CultureInfo.InvariantCulture) + ":" + p.y.ToString("R", CultureInfo.InvariantCulture) + ":" +
                p.z.ToString("R", CultureInfo.InvariantCulture) + ":" + item.isFlipped;
        }
        public static string LayoutKey(IList<SavedItem> items)
        {
            return Hash(Encoding.UTF8.GetBytes(string.Join("\n", items.Select(ItemKey))));
        }
        public static Document Capture(IList<SavedItem> items, IDictionary<SavedItem, int> offsets)
        {
            var doc = new Document { Layout = LayoutKey(items) };
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && offsets.TryGetValue(items[i], out int offset) && offset != 0)
                    doc.Entries.Add(new Entry { Index = i, Item = ItemKey(items[i]), Offset = offset });
            return doc;
        }
        public static Dictionary<SavedItem, int> Match(Document doc, IList<SavedItem> items)
        {
            var result = new Dictionary<SavedItem, int>();
            if (doc == null || doc.Version != 1 || doc.Layout != LayoutKey(items) || doc.Entries == null || doc.Entries.Count > items.Count) return result;
            foreach (var e in doc.Entries)
            {
                if (e == null || e.Index < 0 || e.Index >= items.Count || e.Offset < -1000 || e.Offset > 1000 ||
                    items[e.Index] == null || e.Item != ItemKey(items[e.Index]) || result.ContainsKey(items[e.Index]))
                    return new Dictionary<SavedItem, int>();
                result.Add(items[e.Index], e.Offset);
            }
            return result;
        }
        public static void Write(string folder, byte[] saveBytes, Document doc)
        {
            // A save may have completed asynchronously after the player moved
            // more furniture. Validate against the bytes actually saved first.
            var saved = JsonConvert.DeserializeObject<SaveFile>(Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(saveBytes))));
            if (saved?.allSavedItems == null || doc.Layout != LayoutKey(saved.allSavedItems))
                throw new InvalidDataException("Layout changed while saving; draw-order snapshot was not written.");
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, Hash(saveBytes) + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(doc, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static Document Read(string folder, byte[] saveBytes)
        {
            string file = Path.Combine(folder, Hash(saveBytes) + ".json");
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Oversized draw-order data.");
            return JsonConvert.DeserializeObject<Document>(File.ReadAllText(file));
        }
    }
}
