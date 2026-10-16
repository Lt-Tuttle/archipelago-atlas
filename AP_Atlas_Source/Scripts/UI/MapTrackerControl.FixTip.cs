#nullable enable
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The map's one tip about fixing where you see it (the owner's 2026-10-15 hand-holding): a quiet card at the map's top
    /// right, beside its notes, on every map until Got it, which is remembered for good (AppSettings.FixTipDismissed).
    /// </summary>
    public partial class MapTrackerControl
    {
        private PanelContainer? _tipBar;
        private Label? _tipNote;

        /// <summary>The tip (it also ends Add games' summary once a map came in).</summary>
        public const string FixTipText = "If a pin or tile is wrong while you play, right-click it and choose a fix; Undo is one click. The Map Tracker's Notices tab lists what Atlas noticed, and Changes what was changed.";

        /// <summary>The tip shown on the map, or null when none shows (for tests).</summary>
        public string? FixTipShown => _tipBar is { Visible: true } && _tipNote != null ? _tipNote.Text : null;

        private void ShowFixTipOnce()
        {
            if (_tipBar == null || _tipNote == null) return;
            _tipNote.Text = Translate(FixTipText);
            _tipBar.Visible = !_appSettings.FixTipDismissed;
        }

        /// <summary>Got it: put away for good (another slot's map hides it when it shows next).</summary>
        public void PressFixTipGotIt()
        {
            _appSettings.FixTipDismissed = true;
            DataManager.SaveSettingsSoon(_appSettings);
            if (_tipBar != null) _tipBar.Visible = false;
        }

        private void HideFixTipIfPutAway()
        {
            if (_tipBar != null && _appSettings.FixTipDismissed) _tipBar.Visible = false;
        }

        private PanelContainer BuildFixTip()
        {
            _tipBar = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, MouseFilter = MouseFilterEnum.Pass, Visible = false };
            _tipBar.SetMeta("fix_tip", true);
            _tipBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = ThemeColors.Info,
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 10,
                ContentMarginRight = 10,
                ContentMarginTop = 6,
                ContentMarginBottom = 6
            });
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 4);
            _tipNote = new Label { MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) };
            _tipNote.SetMeta("font_size_ratio", 0.9f);
            _tipNote.AddThemeColorOverride("font_color", ThemeColors.Text);
            box.AddChild(_tipNote);
            var gotIt = Kit.Button(Translate("Got it"), Translate("Puts this tip away for good; Fix a problem… under the maps works the same any time."), PressFixTipGotIt, small: true);
            gotIt.Name = "FixTipGotIt";
            gotIt.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            box.AddChild(gotIt);
            _tipBar.AddChild(box);
            VisibilityChanged += HideFixTipIfPutAway;
            return _tipBar;
        }
    }
}
