using System;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Starts work that nobody awaits (button handlers, background refreshes, closing connections) without losing its
    /// failures. Every failure is logged with what was being done. Unless the work is routine, it's also raised as
    /// <see cref="Failed"/>, so the window can tell the user. Cancelling isn't a failure.
    /// Use it instead of "async void" and "_ = SomethingAsync()" (the guard rails reject both).
    /// </summary>
    public static class Async
    {
        /// <summary>
        /// Raised (on a worker thread or the main thread) when unawaited work failed and the user should hear about it:
        /// what was being done, in plain words such as "checking map packs for updates", and the exception.
        /// </summary>
        public static event Action<string, Exception> Failed;

        /// <summary>Watches a task that nobody awaits.</summary>
        /// <param name="doing">What the work does, in plain words that complete "Something went wrong while …".</param>
        /// <param name="tellUser">
        /// False for routine work whose failure is expected and handled elsewhere (such as closing a connection that
        /// already dropped): it's only logged.
        /// </param>
        public static void Fire(Task task, string doing, bool tellUser = true)
        {
            if (task == null) return;
            if (task.IsCompleted) Observe(task, doing, tellUser);
            // The continuation can't fail (Observe never throws), so its own task needs no watching.
            else _ = task.ContinueWith(t => Observe(t, doing, tellUser), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>Starts async work that nobody awaits. A throw before its first await is caught too.</summary>
        public static void Fire(Func<Task> work, string doing, bool tellUser = true)
        {
            Task task;
            try { task = work(); }
            catch (Exception ex) { Report(ex, doing, tellUser); return; }
            Fire(task, doing, tellUser);
        }

        /// <summary>
        /// When <paramref name="task"/> finishes, hands its result to <paramref name="then"/> on a worker thread: the default
        /// value (null) when it failed or was cancelled, and a failure is logged. A UI update in <paramref name="then"/>
        /// moves itself to the main thread (CallDeferred). Failures of <paramref name="then"/> itself are logged too.
        /// </summary>
        public static void Then<T>(Task<T> task, Action<T> then, string doing)
        {
            if (task == null) return;
            Fire(task.ContinueWith(finished =>
            {
                T result = default;
                if (finished.IsCompletedSuccessfully) result = finished.Result;
                else if (finished.IsFaulted) Report(finished.Exception, doing, tellUser: false);
                then(result);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default), doing, tellUser: false);
        }

        /// <summary>Logs a failure (the full details go to the log file) and, unless it's routine, raises Failed. Never throws.</summary>
        public static void Report(Exception ex, string doing, bool tellUser = true)
        {
            if (ex is AggregateException aggregate)
            {
                aggregate = aggregate.Flatten();
                if (aggregate.InnerExceptions.Count == 1) ex = aggregate.InnerExceptions[0];
            }
            if (ex is OperationCanceledException) return;
            try
            {
                Logger.LogError($"Something went wrong while {doing}: {ex.GetType().Name}: {ex.Message}");
                Logger.LogDebug($"Details ({doing}): {ex}");
            }
            catch (Exception logFailure) { Godot.GD.PrintErr($"Something went wrong while {doing} ({ex.Message}), and it couldn't be logged: {logFailure.Message}"); }
            if (!tellUser) return;
            try { Failed?.Invoke(doing, ex); }
            catch (Exception handlerFailure) { Godot.GD.PrintErr($"A failure while {doing} couldn't be shown: {handlerFailure.Message}"); }
        }

        private static void Observe(Task task, string doing, bool tellUser)
        {
            // Reading Exception also marks the failure as seen, so it never surfaces later as an unobserved task.
            if (task.IsFaulted) Report(task.Exception, doing, tellUser);
        }
    }
}
