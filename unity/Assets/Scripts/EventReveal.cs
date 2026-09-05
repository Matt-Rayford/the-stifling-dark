using System;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The round's Event card, presented the way a player would deal it: full size over a
    /// darkened backdrop for a beat, then slid and shrunk to its resting spot beside the
    /// board while the backdrop lifts on its own. Fire-and-forget — the object destroys
    /// its canvas when the slide lands.
    /// </summary>
    public sealed class EventReveal : MonoBehaviour
    {
        private const float HoldSeconds = 1f;
        private const float SlideSeconds = 0.45f;
        private const float CardHeight = 640f;

        private Func<(Vector2 Center, float Height)?> _target;
        private RectTransform _canvasRect;
        private RectTransform _card;
        private Image _backdrop;
        private float _elapsed;

        public static void Play(Sprite sprite, Func<(Vector2 Center, float Height)?> target)
        {
            if (sprite == null)
            {
                return;
            }
            var canvas = UiKit.CreateCanvas("SdEventRevealCanvas", 300);
            var reveal = canvas.gameObject.AddComponent<EventReveal>();
            reveal._target = target;
            reveal._canvasRect = (RectTransform)canvas.transform;

            var backdropRect = UiKit.CreatePanel(canvas.transform, "Backdrop",
                new Color(0f, 0f, 0f, 0.75f));
            UiKit.Anchor(backdropRect, Vector2.zero, Vector2.one);
            reveal._backdrop = backdropRect.GetComponent<Image>();
            reveal._backdrop.raycastTarget = false; // cosmetic: the game keeps playing under it

            var cardGo = new GameObject("EventCard", typeof(RectTransform), typeof(Image));
            cardGo.transform.SetParent(canvas.transform, false);
            reveal._card = (RectTransform)cardGo.transform;
            float width = CardHeight * sprite.bounds.size.x /
                Mathf.Max(0.0001f, sprite.bounds.size.y);
            reveal._card.sizeDelta = new Vector2(width, CardHeight);
            var image = cardGo.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.material = UiKit.RoundedImageMaterial(width, CardHeight);
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            if (_elapsed <= HoldSeconds)
            {
                return;
            }
            float t = Mathf.Clamp01((_elapsed - HoldSeconds) / SlideSeconds);
            float eased = t * t * (3f - 2f * t);
            _backdrop.color = new Color(0f, 0f, 0f, 0.75f * (1f - eased));

            var spot = _target?.Invoke();
            if (spot.HasValue)
            {
                var (screenCenter, screenHeight) = spot.Value;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenCenter, null, out var local);
                _card.anchoredPosition = Vector2.Lerp(Vector2.zero, local, eased);
                // Screen px -> canvas units via the two projected edges' local distance.
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenCenter + new Vector2(0, screenHeight / 2f), null, out var top);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenCenter - new Vector2(0, screenHeight / 2f), null, out var bottom);
                float endScale = Mathf.Max(0.05f, Mathf.Abs(top.y - bottom.y) / CardHeight);
                float scale = Mathf.Lerp(1f, endScale, eased);
                _card.localScale = new Vector3(scale, scale, 1f);
            }
            else
            {
                // Nowhere to land (no resting card on screen): just fade out in place.
                var image = _card.GetComponent<Image>();
                var color = image.color;
                color.a = 1f - eased;
                image.color = color;
            }
            if (t >= 1f)
            {
                Destroy(gameObject);
            }
        }
    }
}
