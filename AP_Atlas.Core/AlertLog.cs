using System;
using System.Collections.Generic;

namespace AP_Atlas.Core;

/// <summary>What kind of thing an alert tells: it colours the card and marks the history.</summary>
public enum AlertKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>One thing Atlas told the user, as the history keeps it.</summary>
public sealed record AlertEntry(int Id, DateTime At, AlertKind Kind, string Message);

/// <summary>
/// Everything Atlas told the user through the alert feed, newest first, kept to the last <see cref="MaxEntries"/>: the feed
/// adds as it shows a card, the Notifications window reads. Unseen counts what was added since the window last opened.
/// Main thread only.
/// </summary>
public sealed class AlertLog
{
    public const int MaxEntries = 200;

    private readonly List<AlertEntry> _entries = new();
    private int _nextId;

    /// <summary>The alerts, newest first.</summary>
    public IReadOnlyList<AlertEntry> Entries => _entries;

    /// <summary>How many were added since <see cref="MarkSeen"/>.</summary>
    public int Unseen { get; private set; }

    /// <summary>Something was added, cleared or seen.</summary>
    public event Action? Changed;

    /// <summary>Keeps an alert (its message cut to what a line may show), newest first; the oldest goes once the log is full.</summary>
    public AlertEntry Add(AlertKind kind, string message, DateTime at)
    {
        var entry = new AlertEntry(++_nextId, at, kind, Logger.Shown(message ?? ""));
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Unseen++;
        Changed?.Invoke();
        return entry;
    }

    public void MarkSeen()
    {
        if (Unseen == 0) return;
        Unseen = 0;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_entries.Count == 0 && Unseen == 0) return;
        _entries.Clear();
        Unseen = 0;
        Changed?.Invoke();
    }
}
