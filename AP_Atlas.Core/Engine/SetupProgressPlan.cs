using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.EngineSetup;

/// <summary>
/// One number for the whole engine setup. Each phase of the setup has a weight (seconds measured on a typical PC), the
/// phases the setup will run are known before it starts, and the fraction never goes down: a phase that reports no
/// progress of its own (pip, the health check) holds at its start until the next begins, and a download moves within
/// its phase's share. The setup panel shows "Step 3 of 9: …" and one bar from it.
/// </summary>
public sealed class SetupProgressPlan
{
    /// <summary>A phase of the setup: its id, its share of the time (seconds on a typical PC), and what it is in plain words.</summary>
    public sealed record Phase(string Id, float Weight, string Title);

    /// <summary>Every phase a portable setup can run, in order.</summary>
    public static readonly IReadOnlyList<Phase> All = new[]
    {
        new Phase("runtime", 4f, "Downloading Python"),
        new Phase("pip", 3f, "Installing pip"),
        new Phase("archipelago", 6f, "Downloading Archipelago"),
        new Phase("packages", 9f, "Installing Archipelago's packages"),
        new Phase("tracker", 0.5f, "Downloading Universal Tracker"),
        new Phase("bridge", 0.5f, "Installing Atlas's bridge"),
        new Phase("check", 9f, "Running the health check"),
        new Phase("world-packages", 8f, "Installing the packages some games need"),
        new Phase("check-2", 3f, "Checking again"),
    };

    private readonly List<Phase> _phases;
    private readonly float _total;
    private readonly HashSet<string> _entered = new(StringComparer.Ordinal);
    private string? _current;
    private float _last;

    private SetupProgressPlan(List<Phase> phases)
    {
        _phases = phases;
        _total = Math.Max(0.001f, phases.Sum(p => p.Weight));
    }

    /// <summary>A plan of the phases the setup will run, in <see cref="All"/>'s order; ids it doesn't know are ignored.</summary>
    public static SetupProgressPlan For(IEnumerable<string> neededIds)
    {
        var needed = new HashSet<string>(neededIds, StringComparer.Ordinal);
        return new SetupProgressPlan(All.Where(p => needed.Contains(p.Id)).ToList());
    }

    /// <summary>How many phases the plan runs.</summary>
    public int Steps => _phases.Count;

    /// <summary>The phase running now (1-based), or 0 before the first.</summary>
    public int Step => _current == null ? 0 : _phases.FindIndex(p => p.Id == _current) + 1;

    /// <summary>The fraction reported last (0 to 1); never goes down.</summary>
    public float Fraction => _last;

    /// <summary>The phases, in order.</summary>
    public IReadOnlyList<Phase> Phases => _phases;

    /// <summary>
    /// A phase begins: the fraction moves to its start. A phase entered a second time (the health check after the world
    /// packages) counts as its "-2" phase when the plan has one. Unknown ids leave the plan where it is. Returns the step
    /// number (0 for an unknown id).
    /// </summary>
    public int Enter(string id)
    {
        if (_entered.Contains(id) && _phases.Any(p => p.Id == id + "-2")) id += "-2";
        int index = _phases.FindIndex(p => p.Id == id);
        if (index < 0) return 0;
        _entered.Add(id);
        _current = id;
        _last = Math.Max(_last, StartOf(index));
        return index + 1;
    }

    /// <summary>Progress within the running phase (0 to 1 of its share; a negative value means unknown and changes nothing).</summary>
    public float Within(float part)
    {
        if (_current == null || part < 0) return _last;
        int index = _phases.FindIndex(p => p.Id == _current);
        float start = StartOf(index), share = _phases[index].Weight / _total;
        _last = Math.Max(_last, Math.Min(1f, start + Math.Clamp(part, 0f, 1f) * share));
        return _last;
    }

    /// <summary>The setup finished: the bar is full.</summary>
    public float Complete()
    {
        _last = 1f;
        return _last;
    }

    private float StartOf(int index) => _phases.Take(index).Sum(p => p.Weight) / _total;
}
