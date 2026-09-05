using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The carried Items as a hand of cards along the bottom edge, Lemonade Wars style:
    /// cards peek two-thirds up at rest, rise fully on hover, and a dwell (or Alt/Cmd for
    /// instant) opens the big readable preview beside the cursor. Clicking a card plays it
    /// (see <see cref="UseRequested"/>); the side panel's buttons remain as a fallback.
    /// </summary>
    public sealed class HandView
    {
        /// <summary>Raised with the card id when a hand card is clicked.</summary>
        public System.Action<string> UseRequested;

        // The print card aspect (~0.655), sized to read as a hand rather than a wall.
        private const float CardWidth = 170f;
        private const float CardHeight = 260f;
        private const float RaisedY = 8f;
        /// <summary>How much of a resting card is on screen.</summary>
        private const float PeekFraction = 0.62f;

        private readonly TokenArt _art;
        private readonly Describe _describe;
        private readonly RectTransform _host;
        private readonly CardPreview _preview;

        /// <summary>Last-rendered fingerprint: rebuilding an unchanged fan on every server
        /// tick would drop the hover raise and kill an open preview.</summary>
        private string _signature;

        public HandView(RectTransform parent, TokenArt art, Describe describe)
        {
            _art = art;
            _describe = describe;
            _preview = new CardPreview();
            _host = UiKit.CreateGroup(parent, "Hand");
            // The band between the side columns, tall enough for a fully risen card.
            UiKit.Anchor(_host, Vector2.zero, new Vector2(1, 0),
                new Vector2(368, 0), new Vector2(-438, CardHeight + RaisedY + 4f));
        }

        /// <summary>
        /// <paramref name="me"/> null (an Adversary seat) hides the hand, as does
        /// <paramref name="standDown"/> — while the flashlight is being aimed or spaces are
        /// being picked, the map's bottom edge belongs to the mouse.
        /// </summary>
        public void Render(PlayerView.InvestigatorPanel me, bool standDown)
        {
            // The engine keeps its Supply counters and standing markers in the same list as
            // the real cards ("supply:…", "marker:…" — never a card id); those are not cards.
            var items = me == null || standDown
                ? new List<string>()
                : (me.Items ?? new List<string>()).Where(id => !id.Contains(":")).ToList();
            string signature = string.Join(",", items) + "|" +
                Mathf.RoundToInt(_host.rect.width);
            if (signature == _signature)
            {
                return;
            }
            _signature = signature;

            UiKit.Clear(_host);
            _preview.Hide();
            if (items.Count == 0)
            {
                return;
            }

            float hostWidth = _host.rect.width;
            if (hostWidth < 10f)
            {
                hostWidth = 1100f; // first-frame fallback before canvas layout settles
                _signature = null; // re-measure next render
            }
            // Constant overlap while it fits; a big hand compresses instead of scrolling —
            // item counts stay small.
            float spacing = items.Count > 1
                ? Mathf.Min(CardWidth * 0.72f,
                    (hostWidth - 40f - CardWidth) / (items.Count - 1))
                : 0f;
            float span = CardWidth + spacing * (items.Count - 1);
            float startX = -span / 2f + CardWidth / 2f;
            float restY = PeekFraction * CardHeight - CardHeight;

            for (int i = 0; i < items.Count; i++)
            {
                BuildCard(items[i], startX + i * spacing, restY, i);
            }
        }

        private void BuildCard(string cardId, float x, float restY, int sibling)
        {
            var sprite = _art.ItemCard(cardId);
            var frameGo = new GameObject("Card-" + cardId, typeof(RectTransform), typeof(Image));
            frameGo.transform.SetParent(_host, false);
            var frame = (RectTransform)frameGo.transform;
            frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 0f);
            frame.pivot = new Vector2(0.5f, 0f);
            frame.sizeDelta = new Vector2(CardWidth, CardHeight);
            frame.anchoredPosition = new Vector2(x, restY);

            var image = frameGo.GetComponent<Image>();
            if (sprite != null)
            {
                image.sprite = sprite;
                image.material = UiKit.RoundedImageMaterial(CardWidth, CardHeight);
            }
            else
            {
                BuildTextFallback(frame, cardId);
                image.color = new Color(0.84f, 0.78f, 0.66f); // parchment stand-in
                image.sprite = UiSprites.RoundedRect;
                image.type = Image.Type.Sliced;
            }

            var motion = frameGo.AddComponent<HandCardMotion>();
            motion.TargetY = restY;
            UiKit.AddClick(frameGo, () =>
            {
                _preview.Hide();
                UseRequested?.Invoke(cardId);
            });
            UiKit.AddHover(frameGo,
                () =>
                {
                    frame.SetAsLastSibling();
                    motion.TargetY = RaisedY;
                },
                () =>
                {
                    frame.SetSiblingIndex(sibling);
                    motion.TargetY = restY;
                });
            _preview.Attach(frameGo, sprite);
        }

        /// <summary>No rendered face for this id — name and rules text on the stand-in.</summary>
        private void BuildTextFallback(RectTransform frame, string cardId)
        {
            var ink = new Color(0.13f, 0.10f, 0.08f);
            var name = UiKit.CreateText(frame, _describe.Card(cardId), 17,
                TextAnchor.UpperCenter, ink);
            name.fontStyle = FontStyles.Bold;
            UiKit.Anchor((RectTransform)name.transform, new Vector2(0, 0.78f), Vector2.one,
                new Vector2(8, 0), new Vector2(-8, -10));
            var body = UiKit.CreateText(frame, _describe.CardText(cardId), 13,
                TextAnchor.UpperCenter, ink);
            UiKit.Anchor((RectTransform)body.transform, Vector2.zero, new Vector2(1, 0.78f),
                new Vector2(8, 8), new Vector2(-8, 0));
        }
    }
}
