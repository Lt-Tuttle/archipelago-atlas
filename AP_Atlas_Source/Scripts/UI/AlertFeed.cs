using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The alert feed: what Atlas tells the user, as cards stacked at the bottom right of the window, the newest at the
    /// bottom, never on top of each other and at most <see cref="MostShown"/> at once (the oldest goes first). A card goes by
    /// itself after its hold (longer when it has a button), or when its × is pressed; every card is kept in the
    /// <see cref="Log"/> for the Notifications window. In the visual check the cards stay, so pictures don't depend on timing.
    /// </summary>
    public sealed partial class AlertFeed : CanvasLayer
    {
        public const int MostShown = 5;
        public const float Hold = 3f;
        public const float HoldWithAction = 10f;
        private const float FadeIn = 0.3f;
        private const float FadeOut = 0.5f;

        private readonly Func<string, string> _tr;
        private readonly Func<int> _fontSize;
        private readonly bool _stay;
        private readonly VBoxContainer _stack = new();
        private readonly Dictionary<Control, (Button? Action, Button Close)> _buttons = new();
        private readonly HashSet<Control> _going = new();

        /// <summary>Everything shown, newest first.</summary>
        public AP_Atlas.Core.AlertLog Log { get; }

        /// <param name="fontSize">The size the cards' text takes (the menu and tab size).</param>
        /// <param name="stay">Cards never go by themselves (the visual check).</param>
        public AlertFeed(AP_Atlas.Core.AlertLog log, Func<string, string> tr, Func<int> fontSize, bool stay)
        {
            Log = log;
            _tr = tr;
            _fontSize = fontSize;
            _stay = stay;
            Layer = 100;
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(root);
            _stack.AddThemeConstantOverride("separation", 8);
            _stack.Alignment = BoxContainer.AlignmentMode.End;
            _stack.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_stack);
            _stack.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            _stack.GrowHorizontal = Control.GrowDirection.Begin;
            _stack.GrowVertical = Control.GrowDirection.Begin;
            _stack.Position = Vector2.Zero;
            _stack.OffsetLeft = -20;
            _stack.OffsetTop = -20;
            _stack.OffsetRight = -20;
            _stack.OffsetBottom = -20;
        }

        /// <summary>The cards showing, top to bottom (the oldest first).</summary>
        public IReadOnlyList<Control> Cards => _stack.GetChildren().OfType<Control>().Where(c => !_going.Contains(c)).ToList();

        public Button? ActionButtonOf(Control card) => _buttons.TryGetValue(card, out var b) ? b.Action : null;

        public Button CloseButtonOf(Control card) => _buttons[card].Close;

        /// <summary>Shows a card (and keeps it in the log). With an action it has a button and stays longer.</summary>
        public Control Show(string message, Color color, string? actionText, Action? action)
        {
            Log.Add(KindOf(color), message, AP_Atlas.Core.Logger.DisplayClock());
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(AP_Atlas.Core.ThemeColors.SurfaceDeep, 0.95f),
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderColor = color,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 20,
                ContentMarginRight = 20,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new ColorRect { CustomMinimumSize = new Vector2(10, 10), Color = color, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            var text = new Label { Text = AP_Atlas.Core.Logger.Shown(message) };
            text.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Text);
            row.AddChild(text);
            Button? actionButton = null;
            if (action != null)
            {
                actionButton = new Button { Text = actionText ?? _tr("Open"), FocusMode = Control.FocusModeEnum.None };
                var run = action;
                actionButton.Pressed += () =>
                {
                    run();
                    Dismiss(card);
                };
                row.AddChild(actionButton);
            }
            var close = new Button { Text = "×", Flat = true, FocusMode = Control.FocusModeEnum.None, TooltipText = _tr("Dismiss") };
            close.Pressed += () => Dismiss(card);
            row.AddChild(close);
            card.AddChild(row);
            card.Modulate = new Color(1, 1, 1, 0);
            _stack.AddChild(card);
            _buttons[card] = (actionButton, close);
            MainTrackerWindow.SetFontSizeRecursive(card, _fontSize());
            var tween = card.CreateTween();
            tween.TweenProperty(card, "modulate", new Color(1, 1, 1, 1), FadeIn).SetTrans(Tween.TransitionType.Cubic);
            foreach (var oldest in Cards.Take(Math.Max(0, Cards.Count - MostShown)).ToList()) Dismiss(oldest);
            if (!_stay)
            {
                var timer = GetTree().CreateTimer(action != null ? HoldWithAction : Hold);
                timer.Timeout += () =>
                {
                    if (GodotObject.IsInstanceValid(card)) Dismiss(card);
                };
            }
            return card;
        }

        /// <summary>Takes a card away: it fades, then goes; it stops counting at once so the next card takes its place.</summary>
        public void Dismiss(Control card)
        {
            if (!GodotObject.IsInstanceValid(card) || !_going.Add(card)) return;
            _buttons.Remove(card);
            var tween = card.CreateTween();
            tween.TweenProperty(card, "modulate", new Color(1, 1, 1, 0), FadeOut).SetTrans(Tween.TransitionType.Cubic);
            tween.TweenCallback(Callable.From(() =>
            {
                _going.Remove(card);
                if (!GodotObject.IsInstanceValid(card)) return;
                _stack.RemoveChild(card);
                card.QueueFree();
            }));
        }

        /// <summary>What a card's colour means, for the history: the palette's error, danger, warning and success colours.</summary>
        public static AP_Atlas.Core.AlertKind KindOf(Color color)
        {
            if (color == AP_Atlas.Core.ThemeColors.Error || color == AP_Atlas.Core.ThemeColors.Danger) return AP_Atlas.Core.AlertKind.Error;
            if (color == AP_Atlas.Core.ThemeColors.Warning) return AP_Atlas.Core.AlertKind.Warning;
            if (color == AP_Atlas.Core.ThemeColors.Success) return AP_Atlas.Core.AlertKind.Success;
            return AP_Atlas.Core.AlertKind.Info;
        }
    }
}
