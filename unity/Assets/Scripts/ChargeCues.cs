using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Floating "-N {flashlight}" cues over the board, in the charge track's amber, spawned
    /// whenever an Investigator's Charge drops. World-space (TextMeshPro + a sprite glyph,
    /// sized off the map's own space radius) so a cue sits on its space at any zoom, and
    /// animated from <see cref="BoardView.Tick"/> rather than a MonoBehaviour — nothing on the
    /// board is a component.
    ///
    /// Cues live under their own parent, NOT BoardView's _dynamic: a cue outlives the render
    /// that spawned it and _dynamic is cleared on every render.
    /// </summary>
    public sealed class ChargeCues
    {
        private const float LifetimeSeconds = 1.2f;
        private const float FadeInSeconds = 0.12f;
        /// <summary>Seconds into a cue's life at which it starts fading out.</summary>
        private const float FadeStartSeconds = 0.5f;
        /// <summary>Start offset between cues stacked on one space, so they read as separate.</summary>
        private const float StackDelaySeconds = 0.14f;

        private readonly Transform _parent;
        private readonly TokenArt _art;
        private readonly float _spaceRadius;
        private readonly List<Cue> _live = new List<Cue>();

        public ChargeCues(Transform parent, TokenArt art, double spaceRadius)
        {
            _parent = parent;
            _art = art;
            _spaceRadius = (float)spaceRadius;
        }

        /// <summary>One cue for a loss of <paramref name="amount"/> Charge at a board position.</summary>
        public void Spawn(Vector3 world, int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            var glyph = _art.Token(TokenArt.ChargeGlyph);
            int stacked = StackedAt(world);

            var go = new GameObject("ChargeCue");
            go.transform.SetParent(_parent, false);

            var label = new GameObject("Amount", typeof(TextMeshPro));
            label.transform.SetParent(go.transform, false);
            var text = label.GetComponent<TextMeshPro>();
            text.font = UiKit.Font;
            text.text = glyph == null ? "-" + amount + " Charge" : "-" + amount;
            text.fontSize = _spaceRadius * 0.85f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = UiKit.AccentColor;
            text.GetComponent<MeshRenderer>().sortingOrder = 41;
            label.transform.localPosition =
                new Vector3(glyph == null ? 0f : -_spaceRadius * 0.34f, 0f, 0f);

            SpriteRenderer icon = null;
            if (glyph != null)
            {
                var iconGo = new GameObject("Glyph", typeof(SpriteRenderer));
                iconGo.transform.SetParent(go.transform, false);
                icon = iconGo.GetComponent<SpriteRenderer>();
                icon.sprite = glyph;
                icon.color = UiKit.AccentColor;
                icon.sortingOrder = 41;
                float scale = _spaceRadius * 0.9f / Mathf.Max(0.0001f, glyph.bounds.size.x);
                iconGo.transform.localScale = new Vector3(scale, scale, 1f);
                iconGo.transform.localPosition = new Vector3(_spaceRadius * 0.42f, 0f, 0f);
            }

            // Cues on one space fan out sideways and start staggered instead of overlapping:
            // Flare-Up can drain two Investigators standing together in the same update.
            var from = world + new Vector3(
                stacked % 2 == 0 ? stacked * _spaceRadius * 0.28f : -(stacked + 1) * _spaceRadius * 0.28f,
                _spaceRadius * 0.5f, 0f);
            go.transform.position = from;
            _live.Add(new Cue
            {
                Root = go,
                Text = text,
                Glyph = icon,
                From = from,
                Delay = stacked * StackDelaySeconds,
            });
        }

        /// <summary>Rise half a space, fade, self-destroy.</summary>
        public void Tick(float deltaTime)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var cue = _live[i];
                cue.Elapsed += deltaTime;
                float age = cue.Elapsed - cue.Delay;
                if (age < 0f)
                {
                    continue;
                }
                if (age >= LifetimeSeconds)
                {
                    UnityEngine.Object.Destroy(cue.Root);
                    _live.RemoveAt(i);
                    continue;
                }
                float eased = 1f - (1f - age / LifetimeSeconds) * (1f - age / LifetimeSeconds);
                cue.Root.transform.position = cue.From + new Vector3(0f, _spaceRadius * eased, 0f);
                float alpha = age < FadeInSeconds
                    ? age / FadeInSeconds
                    : 1f - Mathf.Clamp01(
                        (age - FadeStartSeconds) / (LifetimeSeconds - FadeStartSeconds));
                Fade(cue, alpha);
            }
        }

        public void Clear()
        {
            foreach (var cue in _live)
            {
                UnityEngine.Object.Destroy(cue.Root);
            }
            _live.Clear();
        }

        private static void Fade(Cue cue, float alpha)
        {
            var color = UiKit.AccentColor;
            color.a = alpha;
            cue.Text.color = color;
            if (cue.Glyph != null)
            {
                cue.Glyph.color = color;
            }
        }

        private int StackedAt(Vector3 world)
        {
            int count = 0;
            foreach (var cue in _live)
            {
                if (Vector3.Distance(cue.From, world) < _spaceRadius)
                {
                    count++;
                }
            }
            return count;
        }

        private sealed class Cue
        {
            public GameObject Root;
            public TMP_Text Text;
            public SpriteRenderer Glyph;
            public Vector3 From;
            public float Delay;
            public float Elapsed;
        }
    }
}
