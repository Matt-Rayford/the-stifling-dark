using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>Forwards pointer enter/exit/click on any UI object to plain delegates.</summary>
    public sealed class PointerRelay : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IPointerMoveHandler
    {
        public System.Action Entered;
        public System.Action Exited;
        public System.Action Clicked;
        public System.Action<PointerEventData> RightClicked;
        public System.Action<Vector2> Moved;

        public void OnPointerEnter(PointerEventData eventData) => Entered?.Invoke();
        public void OnPointerExit(PointerEventData eventData) => Exited?.Invoke();
        public void OnPointerMove(PointerEventData eventData) => Moved?.Invoke(eventData.position);

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                RightClicked?.Invoke(eventData);
                return;
            }
            Clicked?.Invoke();
        }
    }

    /// <summary>
    /// One shared tooltip that follows the cursor. Used for the reason a button is greyed
    /// out and for space / token identification on the board — the fastest way to answer
    /// "what is that and why can't I click it?" while bug-hunting.
    /// </summary>
    public static class Tooltip
    {
        private static RectTransform _panel;
        private static TMP_Text _text;
        private static Canvas _canvas;

        public static void Show(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                Hide();
                return;
            }
            Ensure();
            _text.text = content;
            _panel.gameObject.SetActive(true);
            Follow();
        }

        public static void Hide()
        {
            if (_panel != null)
            {
                _panel.gameObject.SetActive(false);
            }
        }

        /// <summary>Called every frame by the app while a tooltip is up.</summary>
        public static void Follow()
        {
            if (_panel == null || !_panel.gameObject.activeSelf)
            {
                return;
            }
            var canvasRect = (RectTransform)_canvas.transform;
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect, Input.mousePosition, null, out local);
            // Flip to the left / below the cursor near the screen edges so the panel stays on.
            float width = _panel.sizeDelta.x;
            float height = _panel.sizeDelta.y;
            float x = local.x + 16f;
            float y = local.y - 16f;
            if (x + width > canvasRect.rect.xMax)
            {
                x = local.x - 16f - width;
            }
            if (y - height < canvasRect.rect.yMin)
            {
                y = local.y + 16f + height;
            }
            _panel.anchoredPosition = new Vector2(x, y);
        }

        private static void Ensure()
        {
            if (_panel != null)
            {
                return;
            }
            _canvas = UiKit.CreateCanvas("TooltipCanvas", 400);
            _panel = UiKit.CreatePanel(_canvas.transform, "Tooltip", new Color(0.02f, 0.02f, 0.03f, 0.96f));
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0, 1);
            _panel.sizeDelta = new Vector2(340, 64);
            _panel.GetComponent<Image>().raycastTarget = false;

            var fitter = _panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var layout = _panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            _text = UiKit.CreateText(_panel, "", 15, TextAnchor.UpperLeft);
            _text.gameObject.AddComponent<LayoutElement>().preferredWidth = 320;
            _panel.gameObject.SetActive(false);
        }
    }
}
