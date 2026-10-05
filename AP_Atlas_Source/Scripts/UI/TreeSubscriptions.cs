using System;
using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A view's event subscriptions, made while the view is in the scene tree and removed while it isn't. Add one as a
    /// child of the view: children enter and leave the tree with their parent, so a view moved to another parent (a dock,
    /// a pop-out) is subscribed again, and a closed or detached one is never called back.
    /// </summary>
    /// <remarks>
    /// Subscribing in _Ready and unsubscribing in _ExitTree loses the subscriptions on the first move, because _Ready
    /// runs only once. This keeps both sides on the tree's own enter and exit.
    /// </remarks>
    public partial class TreeSubscriptions : Node
    {
        private readonly List<(Action Subscribe, Action Unsubscribe)> _pairs = new();

        public TreeSubscriptions() => Name = "TreeSubscriptions";

        /// <summary>Whether the subscriptions are made (the view is in the tree).</summary>
        public bool Active { get; private set; }

        /// <summary>How many subscriptions this holds.</summary>
        public int Count => _pairs.Count;

        /// <summary>Adds a subscription, made at once if the view is already in the tree.</summary>
        public TreeSubscriptions On(Action subscribe, Action unsubscribe)
        {
            _pairs.Add((subscribe, unsubscribe));
            if (Active) subscribe();
            return this;
        }

        public override void _EnterTree()
        {
            if (Active) return;
            Active = true;
            foreach (var (subscribe, _) in _pairs) subscribe();
        }

        public override void _ExitTree()
        {
            if (!Active) return;
            Active = false;
            foreach (var (_, unsubscribe) in _pairs) unsubscribe();
        }
    }
}
