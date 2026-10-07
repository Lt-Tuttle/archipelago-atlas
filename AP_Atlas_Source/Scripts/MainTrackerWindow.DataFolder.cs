using System;
using System.IO;
using Godot;
using AP_Atlas.Core;

/// <summary>
/// The data folder, settled before anything is written: PortableData next to the program, or, when that can't be written
/// to, the folder the user chooses once (<see cref="AP_Atlas.UI.DataFolderDialog"/>). The release build's self-check
/// (ATLAS_FIRSTRUN_SELFCHECK, Tools/build_release.ps1) takes the default choice by itself and quits once the window is up.
/// </summary>
public partial class MainTrackerWindow
{
    private const string FirstRunSelfCheckVariable = "ATLAS_FIRSTRUN_SELFCHECK";
    private static bool FirstRunSelfCheck => System.Environment.GetEnvironmentVariable(FirstRunSelfCheckVariable) is { Length: > 0 };

    /// <summary>Logged when the data went to the fallback folder (the self-check looks for it).</summary>
    public const string FallbackLogLine = "DATA FOLDER FALLBACK";

    /// <summary>Test hook: stands in for the user's local app data folder (the UI test keeps the pointer in its own data folder).</summary>
    internal static string? TestLocalAppData;

    // The one place Atlas looks up a user folder of its own accord, and only when its own folder can't be written to. The
    // LOCALAPPDATA variable comes first: it names the same folder on a user's PC, and the test runs point it at a stand-in
    // (the known-folder lookup ignores the variable, and would reach the real folder from a test).
    private static string LocalAppData => TestLocalAppData
        ?? (System.Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } fromEnvironment ? fromEnvironment : System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Why the data isn't next to the program (for Settings → Data), or null.</summary>
    private string? _dataFolderWhy;

    /// <summary>
    /// Settles the data folder, then runs <paramref name="then"/>: at once when it's the override, the portable folder or
    /// one chosen earlier; after the dialog otherwise (or never, when the user quits).
    /// </summary>
    private void SettleDataFolder(Action then)
    {
        string? overridden = System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR");
        if (OS.HasFeature("editor") || !string.IsNullOrWhiteSpace(overridden))
        {
            then(); // DataManager takes these itself
            return;
        }
        string programDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var resolution = DataFolder.Resolve(null, programDir, LocalAppData, DataFolder.Probe);
        if (resolution.Source != DataFolder.Source.NeedsChoice)
        {
            Settle(resolution.Path!, resolution);
            then();
            return;
        }
        if (FirstRunSelfCheck)
        {
            string fallback = DataFolder.DefaultFallback(LocalAppData);
            string? problem = DataFolder.Probe(fallback);
            if (problem != null)
            {
                GD.PrintErr("First-run self-check: the fallback folder can't be used either: " + problem);
                GetTree().Quit(7);
                return;
            }
            Settle(fallback, resolution);
            then();
            return;
        }
        var dialog = new AP_Atlas.UI.DataFolderDialog(resolution.Problem!, resolution.Portable, DataFolder.DefaultFallback(LocalAppData), DataFolder.PointerPath(LocalAppData),
            DataFolder.Probe, (folder, chosen) =>
            {
                if (chosen)
                {
                    try { DataFolder.WritePointer(LocalAppData, folder); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        GD.PrintErr("The chosen folder couldn't be remembered: " + ex.Message);
                    }
                }
                Settle(folder, resolution);
                then();
            }, () => GetTree().Quit(0), text => Tr(text));
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(660, 0));
    }

    private void Settle(string folder, DataFolder.Resolution resolution)
    {
        DataManager.SetDataDirectory(folder);
        if (resolution.Source is DataFolder.Source.Portable or DataFolder.Source.Override) return;
        _dataFolderWhy = $"Atlas's own folder ({resolution.Portable}) can't be written to ({resolution.Problem}), so your data is kept here instead.";
    }

    /// <summary>After the log file is open: where the data went, when it isn't next to the program.</summary>
    private void LogDataFolder()
    {
        if (_dataFolderWhy == null) return;
        AP_Atlas.Core.Logger.LogInfo($"{FallbackLogLine}: {DataManager.GetDataDirectory()}. {_dataFolderWhy}");
    }
}
