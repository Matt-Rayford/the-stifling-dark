using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Full-screen "Your Turn" interstitial, shown between Investigator turns when the table
    /// is waiting on this seat. BEGIN sends the begin-turn command itself — the banner IS the
    /// old bar button.
    /// </summary>
    public sealed class TurnBanner
    {
        private readonly RectTransform _root;
        private System.Action _begin;

        /// <summary>What is on screen, so a re-render does not rebuild and flicker it.</summary>
        public string Signature { get; private set; } = "";

        public TurnBanner(Transform parent)
        {
            // The backdrop Image swallows clicks, keeping the board and bar beneath quiet.
            _root = UiKit.CreatePanel(parent, "TurnBanner", new Color(0f, 0f, 0f, 0.82f));
            UiKit.Anchor(_root, Vector2.zero, Vector2.one);

            var title = UiKit.CreateText(_root, "Your Turn", 120, TextAnchor.MiddleCenter,
                UiKit.TitleColor);
            title.font = UiKit.TitleFont;
            UiKit.Anchor((RectTransform)title.transform,
                new Vector2(0.05f, 0.46f), new Vector2(0.95f, 0.70f));

            UiKit.CreateButton(CenteredRow("BeginRow", 0.34f, 0.42f), "BEGIN", 26,
                () =>
                {
                    var begin = _begin;
                    Hide();
                    begin?.Invoke();
                }, fixedWidth: 240f, labelFont: UiKit.MenuFont);

            _root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Offer the turn. <paramref name="signature"/> identifies this opportunity, so the
        /// same value on every re-render keeps the open banner untouched.
        /// </summary>
        public void Show(string signature, System.Action begin)
        {
            _begin = begin;
            if (Signature == signature && _root.gameObject.activeSelf)
            {
                return;
            }
            Signature = signature;
            _root.gameObject.SetActive(true);
        }

        public void Hide()
        {
            Signature = "";
            _root.gameObject.SetActive(false);
        }

        /// <summary>A layout host that centres its lone button at mid-screen.</summary>
        private RectTransform CenteredRow(string name, float yMin, float yMax)
        {
            var row = UiKit.CreateRow(_root, name, 0f, 34f);
            UiKit.Anchor(row, new Vector2(0.3f, yMin), new Vector2(0.7f, yMax));
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            return row;
        }
    }
}
