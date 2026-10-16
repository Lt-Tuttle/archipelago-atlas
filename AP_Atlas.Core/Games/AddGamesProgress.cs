using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>Where an Add games batch is, for its panel: one line ("Game 3 of 6: …") and one bar (0 to 1).</summary>
public sealed record AddGamesStatus(string Text, float Fraction);

/// <summary>
/// One bar for a whole Add games batch. Its parts are known from the plan before anything starts: each game's apworld
/// download, the engine's one step (Archipelago's worlds unpacked, the apworlds installed, ONE health check), reading the
/// added games' names, each game's map pack, and the Pack Doctor's checks. A download weighs its size in megabytes (one
/// when the list doesn't say), the engine's step about what an 8 MB download takes. The bar never goes down: a part that
/// reports nothing of its own holds at its start, and a download moves within its share.
/// </summary>
public sealed class AddGamesProgress
{
    /// <summary>What one game of the plan brings: an apworld download and a pack, with their sizes (0 or less: not listed).</summary>
    public sealed record GameWork(bool Apworld, long ApworldBytes, bool Pack, long PackBytes);

    /// <summary>A part's place: its game (1-based, in the plan's order; 0 for a part for every game), how many games, and the bar.</summary>
    public readonly record struct Step(int Game, int Games, float Fraction);

    public const string EnginePart = "engine", NamesPart = "names", DoctorPart = "doctor";
    public static string ApworldPart(int index) => "apworld:" + index.ToString(CultureInfo.InvariantCulture);
    public static string PackPart(int index) => "pack:" + index.ToString(CultureInfo.InvariantCulture);

    public const float EngineWeight = 8f, NamesWeight = 2f, DoctorWeightPerPack = 1f;

    private readonly List<(string Id, int Game, float Weight)> _parts = new();
    private readonly int _games;
    private readonly float _total;
    private int _current = -1;
    private float _last;

    private AddGamesProgress(IReadOnlyList<GameWork> games, bool engine, long worldsDownloadBytes)
    {
        _games = games.Count;
        for (int i = 0; i < games.Count; i++)
            if (games[i].Apworld) _parts.Add((ApworldPart(i), i + 1, Weight(games[i].ApworldBytes)));
        if (engine)
        {
            _parts.Add((EnginePart, 0, EngineWeight + (worldsDownloadBytes > 0 ? Weight(worldsDownloadBytes) : 0f)));
            _parts.Add((NamesPart, 0, NamesWeight));
        }
        int packs = 0;
        for (int i = 0; i < games.Count; i++)
            if (games[i].Pack)
            {
                _parts.Add((PackPart(i), i + 1, Weight(games[i].PackBytes)));
                packs++;
            }
        if (packs > 0) _parts.Add((DoctorPart, 0, DoctorWeightPerPack * packs));
        _total = Math.Max(0.001f, _parts.Sum(p => p.Weight));
    }

    /// <summary>The bar for a plan: its games' work, whether the engine changes, and Archipelago's release to download (0: none).</summary>
    public static AddGamesProgress For(IReadOnlyList<GameWork> games, bool engine, long worldsDownloadBytes) => new(games, engine, worldsDownloadBytes);

    private static float Weight(long bytes) => bytes > 0 ? Math.Max(0.2f, bytes / (1024f * 1024f)) : 1f;

    public int Parts => _parts.Count;

    /// <summary>The fraction reported last; never goes down.</summary>
    public float Fraction => _last;

    /// <summary>A part begins: the bar moves to its start. A part the plan lacks leaves the bar where it is (its step names no game).</summary>
    public Step Enter(string id)
    {
        int index = _parts.FindIndex(p => p.Id == id);
        if (index < 0) return new Step(0, _games, _last);
        _current = index;
        _last = Math.Max(_last, StartOf(index));
        return new Step(_parts[index].Game, _games, _last);
    }

    /// <summary>Progress within the running part (0 to 1 of its share; negative: unknown, nothing moves).</summary>
    public float Within(float part)
    {
        if (_current < 0 || part < 0 || float.IsNaN(part)) return _last;
        float start = StartOf(_current), share = _parts[_current].Weight / _total;
        _last = Math.Max(_last, Math.Min(1f, start + Math.Clamp(part, 0f, 1f) * share));
        return _last;
    }

    /// <summary>The batch ended (done, stopped or failed): the bar is full.</summary>
    public float Complete() => _last = 1f;

    private float StartOf(int index) => _parts.Take(index).Sum(p => p.Weight) / _total;
}
