using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>Keeps the card preview beside the mouse, flipping sides near screen edges.</summary>
    public sealed class PreviewFollower : MonoBehaviour
    {
        private const float CursorGap = 30f;

        public RectTransform CanvasRect;

        private RectTransform _rect;

        private void Awake()
        {
            _rect = (RectTransform)transform;
        }

        private void LateUpdate()
        {
            Reposition();
        }

        public void Reposition()
        {
            if (CanvasRect == null)
            {
                return;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                CanvasRect, Input.mousePosition, null, out var local);

            var half = _rect.sizeDelta * 0.5f;
            var canvasHalf = CanvasRect.rect.size * 0.5f;

            // Sit to the right of the cursor; flip left when there is no room.
            float x = local.x + CursorGap + half.x;
            if (x + half.x > canvasHalf.x)
            {
                x = local.x - CursorGap - half.x;
            }
            float y = Mathf.Clamp(local.y, -canvasHalf.y + half.y, canvasHalf.y - half.y);

            _rect.anchoredPosition = new Vector2(x, y);
        }
    }
}
