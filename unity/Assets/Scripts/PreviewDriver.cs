using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Gates the card preview (Lemonade Wars behaviour): hovering a card shows it after a
    /// short dwell — long enough that sweeping the cursor across the hand doesn't strobe —
    /// and Alt/Cmd skips the wait. Once up it stays up until the hover ends, so releasing
    /// the key while still on the card doesn't snatch it away.
    /// </summary>
    public sealed class PreviewDriver : MonoBehaviour
    {
        private const float DwellSeconds = 0.75f;

        public System.Action ShowReady;
        public System.Action HideRequested;
        public Sprite PendingSprite { get; private set; }

        private GameObject _source;
        private bool _hovering;
        private bool _shown;
        private float _hoverTime;

        public void BeginHover(GameObject source, Sprite sprite)
        {
            _source = source;
            PendingSprite = sprite;
            _hovering = true;
            _shown = false;
            _hoverTime = 0f;
        }

        public void EndHover()
        {
            _source = null;
            _hovering = false;
            _shown = false;
            PendingSprite = null;
        }

        private void Update()
        {
            if (!_hovering)
            {
                return;
            }
            // The hovered card can be DESTROYED by a re-render (bot turns re-render
            // constantly); destroyed objects never send pointer-exit, so self-dismiss
            // instead of leaving the preview stranded on screen.
            if (_source == null)
            {
                EndHover();
                HideRequested?.Invoke();
                return;
            }
            bool modifier = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                            Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);

            _hoverTime += Time.unscaledDeltaTime;
            if (!_shown && (modifier || _hoverTime >= DwellSeconds))
            {
                _shown = true;
                ShowReady?.Invoke();
            }
        }
    }
}
