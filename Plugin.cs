using System.Collections.Generic;
using System.IO;
using System;
using BepInEx.Configuration;
using BepInEx;
using HarmonyLib;
using UnityEngine.Rendering;
using UnityEngine;

namespace TailsideFurnitureOverlap
{
    [BepInPlugin(Id, "Furniture Overlap", "0.6.1")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.tailside.furnitureoverlap";
        private static Plugin instance;
        public ConfigFile Settings { get; private set; }
        private ConfigEntry<bool> overlapEnabled;
        private ConfigEntry<bool> anywherePlacement;
        private Harmony harmony;
        private bool stopped;
        private ConfigEntry<KeyCode> forwardKey, backwardKey, resetKey;
        private ConfigEntry<bool> gamepadOrderEnabled;

        // Lifecycle and rollback: startup failure and normal shutdown share cleanup.
        private void Awake()
        {
            instance = this;
            try
            {
                Settings = new ConfigFile(Path.Combine(Paths.ConfigPath, "FurnitureOverlap.cfg"), false, Info.Metadata);
                overlapEnabled = Settings.Bind("General", "Enabled", true,
                    "Allow furniture overlap during build mode. Use Placement.Anywhere to choose unrestricted or overlap-only placement.");
                anywherePlacement = Settings.Bind("Placement", "Anywhere", true,
                    "Allow carried build items on counters, display cases and other normally forbidden surfaces. False restores overlap-only placement rules.");
                harmony = new Harmony(Id);
                InstallPlacementAndSelection();
                InstallDrawOrder();
                InstallOrderHints();
                Logger.LogInfo("Furniture Overlap 0.6.1 loaded. Carrying furniture: configured keyboard keys; D-pad Up / Down / Left = front / back / reset.");
            }
            catch (Exception ex)
            {
                Shutdown();
                enabled = false;
                Logger.LogError("Overlap disabled; vanilla placement retained: " + ex);
            }
        }

        private void OnDestroy() => Shutdown();

        private void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            try { RemoveOrderHints(); }
            finally
            {
                try { RestoreDrawOrders(); }
                finally
                {
                    try { harmony?.UnpatchSelf(); }
                    finally { if (instance == this) instance = null; }
                }
            }
        }

        // Game integration: all patch targets are registered here.
        private void InstallPlacementAndSelection()
        {
            var colliders = AccessTools.DeclaredMethod(typeof(Item), "CheckColliders", new[] { typeof(RaycastHit2D[]) });
            var layers = AccessTools.DeclaredMethod(typeof(Item), "CheckSameLayer",
                new[] { typeof(RaycastHit2D[]), typeof(RaycastHit2D[]), typeof(RaycastHit2D[]) });
            if (colliders == null || layers == null) throw new MissingMethodException("Unsupported placement methods");
            harmony.Patch(colliders, prefix: new HarmonyMethod(typeof(Plugin), nameof(FilterColliders)));
            harmony.Patch(layers, prefix: new HarmonyMethod(typeof(Plugin), nameof(FilterLayers)));
            var compare = AccessTools.DeclaredMethod(typeof(ItemSpriteHitTester), "CompareTopmost",
                new[] { typeof(Transform), typeof(Transform) });
            if (compare == null) throw new MissingMethodException("Unsupported selection comparison");
            harmony.Patch(compare, postfix: new HarmonyMethod(typeof(Plugin), nameof(CompareVisibleOrder)));
            harmony.Patch(AccessTools.Method(typeof(ItemPlacement), "GetGrabbableItemAtPosition"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TraceSelection)));
            harmony.Patch(AccessTools.Method(typeof(Item), "CanBePlaced"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AllowAnywherePlacement)));
        }

        private void InstallDrawOrder()
        {
            orderFolder = Path.Combine(Paths.ConfigPath, "FurnitureOverlap", "order-data");
            forwardKey = Settings.Bind("DrawOrder", "ForwardKey", KeyCode.PageUp, "While carrying furniture: one step toward the front.");
            backwardKey = Settings.Bind("DrawOrder", "BackwardKey", KeyCode.PageDown, "While carrying furniture: one step toward the back.");
            resetKey = Settings.Bind("DrawOrder", "ResetKey", KeyCode.Home, "While carrying furniture: restore standard draw order.");
            gamepadOrderEnabled = Settings.Bind("DrawOrder", "GamepadEnabled", true,
                "While carrying furniture: D-pad Up = front, Down = back, Left = reset. Normal inventory navigation is retained otherwise.");
            harmony.Patch(AccessTools.Method(typeof(ItemPlacement), "LoadItems"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(LoadDrawOrders)));
            harmony.Patch(AccessTools.Method(typeof(Item), "PlaceItem"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RememberPlacedOrder)));
            harmony.Patch(AccessTools.Method(typeof(Item), "MarkTaken"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RestoreSavedReference)));
            harmony.Patch(AccessTools.Method(typeof(SaveManager), "SaveGame"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SaveDrawOrders)));
            harmony.Patch(AccessTools.Method(typeof(ItemPlacement), "GetGrabbableItemAtPosition"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SynchronizeOrders)));
            harmony.Patch(AccessTools.Method(typeof(ItemPlacement), "Update"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(ProcessOrderInput)));
            harmony.Patch(AccessTools.Method(typeof(StackableItemPlacedSorting), "UpdateSorting"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SynchronizeOrders)));
            harmony.Patch(AccessTools.Method(typeof(BuildModeNavigation), "Update"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(MaskNavigationDpad)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(Plugin), nameof(RestoreNavigationDpad)));
        }

        // Placement adapters. The stateless filtering rule lives below.
        // Preserve vanilla bookkeeping and cache; only override the carried preview's
        // final verdict. No equipment colliders, interactions or save formats change.
        private static void AllowAnywherePlacement(Item __instance, ref bool __result)
        {
            if (!Applies(__instance) || !instance.anywherePlacement.Value) return;
            __result = true;
            __instance.HighlightGreen();
        }
        private static bool Applies(Item item)
        {
            var placement = ItemPlacement.Instance;
            return item != null && instance != null && instance.overlapEnabled.Value && placement != null &&
                placement.inBuildMode && placement.currentItem == item && !item.isPlaced;
        }

        // Keep valid support surfaces (e.g. a shelf's TableCollider) and every world
        // collider. Remove only disallowed hits owned by another placed build item.
        // The vanilla methods still reject empty rays, walls and mixed surfaces.
        public static RaycastHit2D[] Filter(Item item, RaycastHit2D[] hits) => PlacementPolicy.Filter(item, hits);

        private static void FilterColliders(Item __instance, ref RaycastHit2D[] __0)
        {
            if (Applies(__instance)) __0 = Filter(__instance, __0);
        }

        private static void FilterLayers(Item __instance, ref RaycastHit2D[] __0,
            ref RaycastHit2D[] __1, ref RaycastHit2D[] __2)
        {
            if (!Applies(__instance)) return;
            __0 = Filter(__instance, __0);
            __1 = Filter(__instance, __1);
            __2 = Filter(__instance, __2);
        }

        // Selection adapters and bounded diagnostic logging.
        private readonly HashSet<string> selectionTraces = new HashSet<string>();
        private float nextTrace;
        private static readonly System.Reflection.FieldInfo Navigation = AccessTools.Field(typeof(ItemPlacement), "buildModeNavigation");

        private static void CompareVisibleOrder(Transform __0, Transform __1, ref int __result)
        {
            var placement = ItemPlacement.Instance;
            if (instance == null || !instance.overlapEnabled.Value || placement == null || !placement.inBuildMode) return;
            SpriteOrder.CompareVisibleOrder(__0, __1, ref __result);
        }

        private static void TraceSelection(ItemPlacement __instance, Vector2 __0, Item __result)
        {
            if (instance == null || !instance.overlapEnabled.Value || !__instance.inBuildMode ||
                __instance.currentItem != null || instance.selectionTraces.Count >= 12 || Time.unscaledTime < instance.nextTrace) return;
            instance.nextTrace = Time.unscaledTime + .5f;
            var nav = Navigation?.GetValue(__instance) as BuildModeNavigation;
            if (nav == null || nav.placedItemsList == null) return;
            var descriptions = new List<string>();
            foreach (var item in nav.placedItemsList)
            {
                if (item == null || !item.isPlaced) continue;
                var sprite = item.GetComponent<SpriteRenderer>();
                if (sprite == null || !ItemSpriteHitTester.IsPointOverSprite(sprite, __0)) continue;
                var material = sprite.sharedMaterial;
                var path = SpriteOrder.SortPath(sprite);
                var keys = new List<string>();
                foreach (var node in path) keys.Add(node.Component.name + ":" + node.Layer + "/" + node.Order + "/y=" + node.Y.ToString("F4"));
                descriptions.Add(item.name + " itemId=" + item.GetInstanceID() + " rendererId=" + sprite.GetInstanceID() +
                    " savedIndex=" + (__instance.currentPlacedItems?.IndexOf(item.savedItem) ?? -1) +
                    " materialId=" + (material == null ? 0 : material.GetInstanceID()) +
                    " shader=" + (material == null ? "null" : material.shader.name) +
                    " queue=" + (material == null ? 0 : material.renderQueue) + " keys=[" + string.Join(",", keys) + "]");
            }
            if (descriptions.Count < 2) return;
            string trace = "winner=" + (__result == null ? "null" : __result.name) + " candidates=" + string.Join(" | ", descriptions);
            if (instance.selectionTraces.Add(trace)) instance.Logger.LogInfo("OVERLAP_SELECTION " + trace);
        }

    }

    // Placement rules: no configuration, singleton or Harmony dependency.
    internal static class PlacementPolicy
    {
        public static RaycastHit2D[] Filter(Item item, RaycastHit2D[] hits)
        {
            List<RaycastHit2D> kept = null;
            for (int i = 0; i < hits.Length; i++)
            {
                var collider = hits[i].collider;
                bool ignore = false;
                if (collider != null && !collider.transform.IsChildOf(item.transform))
                {
                    int bit = 1 << collider.gameObject.layer;
                    if ((item.placementLayer.value & bit) == 0)
                    {
                        var owner = collider.GetComponentInParent<Item>();
                        ignore = owner != null && owner != item && owner.isPlaced;
                    }
                }
                if (ignore && kept == null)
                {
                    kept = new List<RaycastHit2D>(hits.Length);
                    for (int j = 0; j < i; j++) kept.Add(hits[j]);
                }
                if (!ignore && kept != null) kept.Add(hits[i]);
            }
            return kept == null ? hits : kept.ToArray();
        }
    }

    // Selection rules: compares render keys without changing either item.
    internal static class SpriteOrder
    {
        internal struct SortNode
        {
            public Component Component;
            public int Layer, Order;
            public float Y;
        }

        internal static List<SortNode> SortPath(SpriteRenderer sprite)
        {
            var path = new List<SortNode>();
            for (var parent = sprite.transform; parent != null; parent = parent.parent)
            {
                var group = parent.GetComponent<SortingGroup>();
                if (group != null && group.isActiveAndEnabled)
                    path.Add(new SortNode { Component = group,
                        Layer = SortingLayer.GetLayerValueFromID(group.sortingLayerID),
                        Order = group.sortingOrder, Y = group.transform.position.y });
                if (group != null && group.isActiveAndEnabled && group.sortAtRoot) break;
            }
            path.Reverse();
            path.Add(new SortNode { Component = sprite,
                Layer = SortingLayer.GetLayerValueFromID(sprite.sortingLayerID),
                Order = sprite.sortingOrder,
                Y = sprite.spriteSortPoint == SpriteSortPoint.Pivot ? sprite.transform.position.y : sprite.bounds.center.y });
            return path;
        }

        // Unity sorts a group as one unit against other groups, but sorts the
        // sprites inside a shared group by their own keys. The vanilla helper
        // stops at the outermost group, losing all of those internal keys.
        internal static void CompareVisibleOrder(Transform __0, Transform __1, ref int __result)
        {
            if (__0 == null || __1 == null || __0 == __1) return;
            var itemA = __0.GetComponent<Item>();
            var itemB = __1.GetComponent<Item>();
            if (itemA == null || itemB == null || !itemA.isPlaced || !itemB.isPlaced) return;
            var a = __0.GetComponent<SpriteRenderer>();
            var b = __1.GetComponent<SpriteRenderer>();
            if (a == null || b == null) return;
            var pathA = SortPath(a);
            var pathB = SortPath(b);
            int i = 0;
            while (i < pathA.Count - 1 && i < pathB.Count - 1 &&
                pathA[i].Component == pathB[i].Component) i++;
            var keyA = pathA[i];
            var keyB = pathB[i];
            int order = keyA.Layer.CompareTo(keyB.Layer);
            if (order == 0) order = keyA.Order.CompareTo(keyB.Order);
            // Wall decor uses inconsistent sprite pivots: herbs hang from their
            // top while signs use their centre. Pivot Y cannot resolve which
            // decoration the user intends to grab. For overlapping sprites in
            // the SAME sorting scope and priority, prefer the smaller target.
            // Do not bypass the ordering of distinct SortingGroups.
            if (order == 0 && keyA.Component == a && keyB.Component == b && a.bounds.Intersects(b.bounds))
            {
                float areaA = a.bounds.size.x * a.bounds.size.y;
                float areaB = b.bounds.size.x * b.bounds.size.y;
                if (areaA > 0 && areaB > 0 && Mathf.Min(areaA, areaB) < Mathf.Max(areaA, areaB) * 0.75f)
                    order = areaB.CompareTo(areaA);
            }
            // Inside a common SortingGroup, Unity uses the group's distance for
            // all descendants. Individual sprite/group Y is NOT a render key.
            // See Unity Manual: Sorting Groups / Sorting Renderers within a group.
            // i > 0 means at least one common group was skipped above.
            if (order == 0 && i == 0 && !Mathf.Approximately(keyA.Y, keyB.Y))
                order = keyB.Y.CompareTo(keyA.Y);
            // Instantiated sprite renderers receive decreasing native IDs. With
            // the same material, sort layer, order and distance, the later
            // renderer draws on top (verified by GPU readback). Vanilla compares
            // Item IDs in the opposite direction, selecting the older object.
            if (order == 0 && keyA.Component == a && keyB.Component == b &&
                CompatibleMaterials(a.sharedMaterial, b.sharedMaterial))
                order = b.GetInstanceID().CompareTo(a.GetInstanceID());
            if (order != 0) __result = order;
        }

        private static bool CompatibleMaterials(Material a, Material b)
        {
            // BuildModeNavigation's hover preparation instantiates .material on
            // every item. Reference equality would disable the tie fix as soon
            // as normal build navigation runs. Outline properties do not change
            // draw priority; the shader and render queue still agree.
            return a != null && b != null && a.shader == b.shader && a.renderQueue == b.renderQueue;
        }
    }
}
