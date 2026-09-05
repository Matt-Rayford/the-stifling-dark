using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Hover-to-enlarge card preview (dwell to show, Alt/Cmd for instant — see
    /// <see cref="PreviewDriver"/>). Follows the mouse offset to the side, clamped to the
    /// screen, on its own top-sorted canvas so it renders above everything. Never
    /// intercepts input.
    /// </summary>
    public sealed class CardPreview
    {
        // The print card aspect (~0.655) at a comfortable reading height.
        private const float Width = 400f;
        private const float Height = 610f;

        private readonly RectTransform _root;
        private readonly Image _image;
        private readonly PreviewFollower _follower;
        private readonly PreviewDriver _driver;

        public CardPreview()
        {
            var canvas = UiKit.CreateCanvas("SdPreviewCanvas", 5000);

            // The dwell/modifier gate lives on the always-active canvas object.
            _driver = canvas.gameObject.AddComponent<PreviewDriver>();
            _driver.ShowReady = () => Show(_driver.PendingSprite);
            _driver.HideRequested = () => _root.gameObject.SetActive(false);

            var rootGo = new GameObject("Preview", typeof(RectTransform), typeof(PreviewFollower));
            rootGo.transform.SetParent(canvas.transform, false);
            _root = (RectTransform)rootGo.transform;
            _root.sizeDelta = new Vector2(Width, Height);
            _follower = rootGo.GetComponent<PreviewFollower>();
            _follower.CanvasRect = (RectTransform)canvas.transform;

            var go = new GameObject("PreviewImage", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_root, false);
            UiKit.Anchor((RectTransform)go.transform, Vector2.zero, Vector2.one);
            _image = go.GetComponent<Image>();
            _image.raycastTarget = false;
            _image.preserveAspect = true;
            _image.material = UiKit.RoundedImageMaterial(Width, Height);
            _root.gameObject.SetActive(false);
        }

        public void Show(Sprite sprite)
        {
            if (sprite == null)
            {
                return;
            }
            _image.sprite = sprite;
            _root.gameObject.SetActive(true);
            _follower.Reposition(); // snap to the cursor immediately, no one-frame lag
        }

        public void Hide()
        {
            _driver.EndHover();
            _root.gameObject.SetActive(false);
        }

        /// <summary>Wire a card to preview on hover: dwell to show, Alt/Cmd for instant.</summary>
        public void Attach(GameObject cardGo, Sprite sprite)
        {
            if (sprite == null)
            {
                return; // nothing to enlarge — the card already shows its text fallback
            }
            UiKit.AddHover(cardGo, () => _driver.BeginHover(cardGo, sprite), Hide);
        }
    }
}
