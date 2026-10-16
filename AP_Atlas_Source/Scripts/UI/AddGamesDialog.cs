#nullable enable
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Add games as a dialog (Home, the Games page, a game's page, the empty map, Add a slot, the Logic banner): the
    /// <see cref="AddGamesPanel"/> with its two actions as the dialog's buttons. It doesn't hold the window, so Atlas
    /// stays usable while games are added, and a dialog that asked (Add a slot) stays under it; closing that dialog hands
    /// this one to the window once it's past its list. Closed while a batch runs, it hides and the batch goes on (Cancel is
    /// what stops it); it comes back with its summary when the batch ends, or when Add games is opened again. One at a
    /// time: opening it again brings it forward with the game ticked.
    /// </summary>
    public sealed partial class AddGamesDialog : AcceptDialog
    {
        /// <summary>The dialog while it's open.</summary>
        public static AddGamesDialog? Showing { get; private set; }

        public AddGamesPanel Panel { get; }

        /// <summary>The second action: Cancel, Back or Try those again.</summary>
        public Button SecondaryButton { get; }

        public static AddGamesDialog Open(Node parent, AddGamesPanel.Hooks hooks, string? game)
        {
            if (Showing is { } open && IsInstanceValid(open) && !open.IsQueuedForDeletion())
            {
                // Still on its list and asked from somewhere else (Add a slot, over Home's): opened again where it was asked.
                if (open.Panel.CurrentStage == AddGamesPanel.Stage.Pick && open.GetParent() != parent) open.QueueFree();
                else
                {
                    if (game != null) open.Panel.Preselect(game);
                    if (!open.Visible) open.ShowAgain(); // closed while its batch runs: shown again (the bar, then the summary)
                    open.GrabFocus();
                    return open;
                }
            }
            var dialog = new AddGamesDialog(hooks, game);
            Showing = dialog;
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 780, 760);
            return dialog;
        }

        private AddGamesDialog(AddGamesPanel.Hooks hooks, string? game)
        {
            Title = hooks.Tr("Add games");
            DialogHideOnOk = false;
            Exclusive = false;
            Unresizable = false;
            Panel = new AddGamesPanel(hooks, game) { CancelOnExit = true };
            AddChild(Panel);
            SecondaryButton = AddButton(hooks.Tr("Cancel"), true, "secondary");
            Panel.ActionsChanged += SyncButtons;
            Panel.CloseRequested += QueueFree;
            // A batch closed out of sight ends with its summary in sight: what was added, and the packs' question.
            Panel.Finished += _ => { if (IsInstanceValid(this) && !IsQueuedForDeletion() && !Visible) ShowAgain(); };
            Confirmed += Panel.PressPrimary;
            CustomAction += action => { if (action == "secondary") Panel.PressSecondary(); };
            Canceled += () =>
            {
                if (Panel.CurrentStage == AddGamesPanel.Stage.Running) _hiddenByUser = true; // hidden; the batch goes on
                else QueueFree();
            };
            SyncButtons();
        }

        public override void _ExitTree()
        {
            if (Showing == this && !_moving) Showing = null;
        }

        private bool _moving, _hiddenByUser;

        /// <summary>Shown again after it was closed while its batch ran.</summary>
        private void ShowAgain()
        {
            _hiddenByUser = false;
            Show();
            GrabFocus();
        }

        /// <summary>
        /// Moves the dialog under another node (the window, when the Add a slot dialog it opened over closes) without
        /// stopping its batch: leaving the tree on the way doesn't count as closing.
        /// </summary>
        public void MoveTo(Node to)
        {
            bool cancel = Panel.CancelOnExit;
            _moving = true;
            Panel.CancelOnExit = false;
            try { Reparent(to); }
            finally
            {
                Panel.CancelOnExit = cancel;
                _moving = false;
            }
            if (!_hiddenByUser && !Visible) Show(); // a dialog closing may have hidden it with itself
        }

        private void SyncButtons()
        {
            if (!IsInstanceValid(this)) return;
            OkButtonText = Panel.PrimaryText;
            GetOkButton().Disabled = !Panel.PrimaryEnabled;
            SecondaryButton.Text = Panel.SecondaryText ?? "";
            SecondaryButton.Visible = Panel.SecondaryText != null;
            WindowFit.RequestShrink(this);
        }
    }
}
