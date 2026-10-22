using System;
using TMPro;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;
using UnityEngine;

namespace TailsideFurnitureOverlap
{
    public sealed partial class Plugin
    {
        private GameObject orderHintRoot;
        private TMP_Text orderHintText, orderHintTemplate;
        private Transform keyboardHintGroup, controllerHintGroup;
        private float nextHintScan;
        private bool hintFailureLogged;
        private int lastHintRenderFrame = -1;

        private void InstallOrderHints()
        {
            Canvas.willRenderCanvases -= RefreshOrderHints;
            Canvas.willRenderCanvases += RefreshOrderHints;
        }
        private void RemoveOrderHints()
        {
            Canvas.willRenderCanvases -= RefreshOrderHints;
            if (orderHintRoot != null)
            {
                orderHintRoot.SetActive(false);
                Destroy(orderHintRoot);
            }
            orderHintRoot = null;
            orderHintText = orderHintTemplate = null;
            keyboardHintGroup = controllerHintGroup = null;
            lastHintRenderFrame = -1;
        }
        public static string OrderKeyLabel(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                case KeyCode.PageUp: return "PgUp";
                case KeyCode.PageDown: return "PgDn";
                default: return key.ToString();
            }
        }
        public static string OrderHintLabel(bool pad, bool japanese, KeyCode forward, KeyCode backward, KeyCode reset)
        {
            string front = pad ? "↑" : OrderKeyLabel(forward);
            string back = pad ? "↓" : OrderKeyLabel(backward);
            string standard = pad ? "←" : OrderKeyLabel(reset);
            return japanese
                ? $"[{front}] 手前へ    [{back}] 奥へ    [{standard}] 標準に戻す"
                : $"[{front}] Bring forward    [{back}] Send backward    [{standard}] Reset order";
        }
        private void RefreshOrderHints()
        {
            if (instance != this || lastHintRenderFrame == Time.frameCount) return;
            lastHintRenderFrame = Time.frameCount;
            try { UpdateOrderHints(); }
            catch (Exception ex)
            {
                if (orderHintRoot != null) orderHintRoot.SetActive(false);
                if (!hintFailureLogged) { hintFailureLogged = true; Logger.LogWarning("Order hint unavailable: " + ex); }
            }
        }
        private void UpdateOrderHints()
        {
            var placement = ItemPlacement.Instance;
            bool pad = TagTextReplacementManager.instance != null &&
                TagTextReplacementManager.instance.detectedScheme == "Gamepad Controller";
            bool show = overlapEnabled.Value && placement != null && placement.inBuildMode &&
                placement.currentItem != null && !placement.currentItem.isPlaced && !PauseMenu.GameIsPaused &&
                (!pad || gamepadOrderEnabled.Value);
            if (!show) { if (orderHintRoot != null) orderHintRoot.SetActive(false); return; }
            if (orderHintRoot == null)
            {
                if (Time.unscaledTime < nextHintScan) return;
                nextHintScan = Time.unscaledTime + 1f;
                foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                {
                    if (!text.gameObject.scene.IsValid() || text.name != "Description" ||
                        text.transform.parent == null || text.transform.parent.parent == null ||
                        text.transform.parent.parent.name != "RotateButtonInGame") continue;
                    var group = text.transform.parent.parent.parent;
                    if (group == null || group.name != "BuildModeInstructionalButtonsKeyboardGroup") continue;
                    CreateOrderHints(text, group);
                    break;
                }
                if (orderHintRoot == null) return;
            }
            var source = pad ? controllerHintGroup : keyboardHintGroup;
            orderHintRoot.SetActive(source != null && source.gameObject.activeInHierarchy);
            if (!orderHintRoot.activeInHierarchy) return;
            // Follow the game's selected locale and font (including font replacement mods).
            string locale = LocalizationSettings.SelectedLocale?.Identifier.Code ?? "en";
            if (orderHintText.font != orderHintTemplate.font) orderHintText.font = orderHintTemplate.font;
            orderHintText.text = OrderHintLabel(pad, locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase),
                forwardKey.Value, backwardKey.Value, resetKey.Value);
            float width = Mathf.Max(420, orderHintText.GetPreferredValues(orderHintText.text).x + 36);
            var parentRect = orderHintRoot.transform.parent as RectTransform;
            if (parentRect != null && parentRect.rect.width > 64) width = Mathf.Min(width, parentRect.rect.width - 32);
            orderHintRoot.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 36);
        }
        private void CreateOrderHints(TMP_Text template, Transform keyboardGroup)
        {
            orderHintTemplate = template;
            keyboardHintGroup = keyboardGroup;
            controllerHintGroup = keyboardGroup.parent.Find("BuildModeInstructionalButtonsControllerGroup");
            orderHintRoot = new GameObject("FurnitureOverlap_OrderHints", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = orderHintRoot.GetComponent<RectTransform>();
            rect.SetParent(keyboardGroup.parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0);
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, 106);
            var background = orderHintRoot.GetComponent<Image>();
            var original = template.transform.parent.GetComponent<Image>();
            if (original != null)
            {
                background.sprite = original.sprite; background.type = original.type;
                background.color = original.color; background.material = original.material;
            }
            else background.color = new Color(.94f,.84f,.7f,.96f);
            background.raycastTarget = false;
            var child = new GameObject("OrderHintText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            var textRect = child.GetComponent<RectTransform>();
            textRect.SetParent(rect, false); textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16, 1); textRect.offsetMax = new Vector2(-16, -1);
            orderHintText = child.GetComponent<TextMeshProUGUI>();
            orderHintText.font = template.font; orderHintText.fontSize = template.fontSize;
            orderHintText.fontStyle = template.fontStyle; orderHintText.color = template.color;
            orderHintText.alignment = TextAlignmentOptions.Center; orderHintText.enableWordWrapping = false;
            orderHintText.enableAutoSizing = true; orderHintText.fontSizeMax = template.fontSize;
            orderHintText.fontSizeMin = Mathf.Min(12, template.fontSize);
            orderHintText.richText = false; orderHintText.raycastTarget = false;
            Logger.LogInfo("Draw-order operation hints attached to build UI.");
        }
    }
}
