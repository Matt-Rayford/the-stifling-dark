using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>One choice on a modal prompt.</summary>
    public sealed class PromptOption
    {
        public string Label;
        public string Detail;
        public Action Chosen;

        public PromptOption(string label, Action chosen, string detail = null)
        {
            Label = label;
            Chosen = chosen;
            Detail = detail;
        }
    }

    /// <summary>
    /// The modal layer: a titled panel with a scrolling list of options, and a variant with a
    /// free-text field for the argument lists the engine's item and ability commands take.
    ///
    /// Everything that needs an answer before a command can be sent goes through here —
    /// window crossings, Escape-card selection, Spirit adoption, evidence reward pickers,
    /// target pickers. It never decides legality; the option it builds sends a command and
    /// the server rules on it.
    /// </summary>
    public sealed class Prompt
    {
        private readonly RectTransform _root;
        private readonly RectTransform _panel;
        private readonly TMP_Text _title;
        private readonly TMP_Text _body;
        private readonly RectTransform _list;
        private readonly RectTransform _footer;

        /// <summary>Identifies what is currently on screen, so a re-render does not flicker it.</summary>
        public string Signature { get; private set; } = "";
        public bool Open => _root.gameObject.activeSelf;

        public Prompt(Transform canvas)
        {
            _root = UiKit.CreatePanel(canvas, "Modal", new Color(0f, 0f, 0f, 0.72f));
            UiKit.Anchor(_root, Vector2.zero, Vector2.one);

            _panel = UiKit.CreatePanel(_root, "Panel", UiKit.PanelColor);
            UiKit.Anchor(_panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _panel.sizeDelta = new Vector2(720, 620);

            _title = UiKit.CreateText(_panel, "", 24, TextAnchor.MiddleLeft, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_title.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -58), new Vector2(-20, -14));

            _body = UiKit.CreateText(_panel, "", 16, TextAnchor.UpperLeft, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_body.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -120), new Vector2(-20, -60));

            var listHost = UiKit.CreateGroup(_panel, "ListHost");
            UiKit.Anchor(listHost, Vector2.zero, Vector2.one, new Vector2(14, 62), new Vector2(-14, -122));
            _list = UiKit.CreateScrollList(listHost);

            _footer = UiKit.CreateRow(_panel, "Footer", 8f, 40f);
            UiKit.Anchor(_footer, Vector2.zero, new Vector2(1, 0), new Vector2(14, 12), new Vector2(-14, 52));

            Hide();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
            Signature = "";
        }

        /// <summary>
        /// Show a list of options. <paramref name="signature"/> lets the caller re-issue the
        /// same prompt every frame without rebuilding it; pass something that changes when the
        /// question changes.
        /// </summary>
        public void Show(string signature, string title, string body, List<PromptOption> options,
            Action cancel = null)
        {
            if (Signature == signature && Open)
            {
                return;
            }
            Signature = signature;
            _root.gameObject.SetActive(true);
            _title.text = title;
            _body.text = body ?? "";
            UiKit.Clear(_list);
            UiKit.Clear(_footer);

            foreach (var option in options)
            {
                var captured = option;
                string label = option.Detail == null
                    ? option.Label
                    : option.Label + "   —   " + option.Detail;
                UiKit.CreateButton(_list, label, 17, () =>
                {
                    Hide();
                    captured.Chosen?.Invoke();
                });
            }
            if (options.Count == 0)
            {
                UiKit.CreateText(_list, "(nothing legal here)", 16, TextAnchor.MiddleLeft,
                    UiKit.MutedColor).gameObject.AddComponent<LayoutElement>().minHeight = 30;
            }
            if (cancel != null)
            {
                UiKit.CreateButton(_footer, "Cancel", 16, () =>
                {
                    Hide();
                    cancel();
                });
            }
        }

        /// <summary>
        /// Ask for a comma-separated argument list. The engine's UseItem / ability / event
        /// commands take <c>List&lt;string&gt;</c> whose contents differ per card, and the
        /// engine's refusal message is the documentation — so v1 offers a text field plus
        /// quick-fill chips for the ids that are plausibly wanted right now.
        /// </summary>
        public void ShowArgs(string signature, string title, string body,
            IEnumerable<KeyValuePair<string, string>> quickFill, Action<List<string>> submit,
            Action cancel = null)
        {
            Signature = signature;
            _root.gameObject.SetActive(true);
            _title.text = title;
            _body.text = body ?? "";
            UiKit.Clear(_list);
            UiKit.Clear(_footer);

            var input = UiKit.CreateInput(_list, "argument, argument, …");
            input.GetComponent<LayoutElement>().minHeight = 40;

            foreach (var pair in quickFill)
            {
                var captured = pair;
                UiKit.CreateButton(_list, "+  " + captured.Value, 16, () =>
                {
                    input.text = string.IsNullOrEmpty(input.text)
                        ? captured.Key
                        : input.text + ", " + captured.Key;
                });
            }

            UiKit.CreateButton(_footer, "Send", 17, () =>
            {
                var args = new List<string>();
                foreach (string part in (input.text ?? "").Split(','))
                {
                    string trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        args.Add(trimmed);
                    }
                }
                Hide();
                submit(args.Count == 0 ? null : args);
            });
            UiKit.CreateButton(_footer, "No arguments", 17, () =>
            {
                Hide();
                submit(null);
            });
            if (cancel != null)
            {
                UiKit.CreateButton(_footer, "Cancel", 16, () =>
                {
                    Hide();
                    cancel();
                });
            }
        }
    }
}
