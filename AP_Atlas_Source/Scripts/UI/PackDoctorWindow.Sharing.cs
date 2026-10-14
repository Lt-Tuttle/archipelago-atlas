#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using AP_Atlas.Core.Reports;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Sharing what the user fixed: the author's report with what fixed it (to copy into an issue), the user's own fixes
    /// suggested to Atlas's developer (<see cref="FixSuggestionService"/>), and a pack's fixes as a file to export and import
    /// (<see cref="PackFixShare"/>).
    /// </summary>
    public partial class PackDoctorWindow
    {
        // ---- What fixed it, for the author ----

        /// <summary>The author's report's section on what the user fixed by hand (their own and their rules' fixes), or "" when none.</summary>
        public string WhatFixedIt()
        {
            var f = PackFixes.Get(_key);
            static bool Mine(PackFixBase fix) => fix.Source is null or "example";
            var lines = new List<string>();
            foreach (var l in f.Links.Where(Mine))
            {
                string where = $"pin `{l.PinPath}`" + (string.IsNullOrEmpty(l.SectionName) ? "" : $", section `{l.SectionName}`");
                lines.Add(l.ApLocationId == null ? $"- The {where} isn't a check." : $"- The {where} is **{l.ApLocationName}**.");
            }
            foreach (var t in f.Tiles.Where(Mine))
            {
                if (t.ApItemId != null) lines.Add($"- The tile `{t.Code}` is the item **{t.ApItemName}**.");
                if (t.Hidden) lines.Add($"- The tile `{t.Code}` isn't an item (hidden).");
                if (!string.IsNullOrEmpty(t.ImageFile)) lines.Add($"- The tile `{t.Code}` needed another picture.");
            }
            foreach (var a in f.AddedTiles.Where(Mine)) lines.Add($"- Added a tile for **{a.ApItemName}** (the pack has none).");
            foreach (var p in f.Pins.Where(Mine)) lines.Add(p.Removed ? $"- Removed the pin `{p.PinPath}` from map `{p.MapId}`." : $"- Moved the pin `{p.PinPath}` on map `{p.MapId}` to {p.X:0}, {p.Y:0}.");
            foreach (var p in f.AddedPins.Where(Mine)) lines.Add($"- Placed **{p.Name}** on map `{p.MapId}` at {p.X:0}, {p.Y:0} (no pin had it).");
            foreach (var m in f.MapImages.Where(Mine)) lines.Add($"- The map `{m.MapId}` needed another picture.");
            if (lines.Count == 0) return "";
            var text = new StringBuilder();
            text.AppendLine("### What fixed it");
            text.AppendLine();
            text.AppendLine("Fixed by hand in The Archipelago Atlas, against the names above:");
            text.AppendLine();
            foreach (string line in lines.Take(200)) text.AppendLine(line);
            if (lines.Count > 200) text.AppendLine($"- … and {lines.Count - 200} more.");
            return text.ToString();
        }

        // ---- Suggestions to Atlas ----

        /// <summary>The user's own fixes as suggestions see them: what the pack wrote, what they chose, what Atlas had guessed.</summary>
        public List<FixPair> UserFixPairs()
        {
            var pairs = new List<FixPair>();
            if (_report?.Index == null) return pairs;
            var f = PackFixes.Get(_key);
            var names = LocationNames;
            var context = PackDoctor.SuggestContext.For(_report.Index, names, learnFromMapping: true);
            var pins = _report.Pack.Locations.GroupBy(p => p.FullPath).ToDictionary(g => g.Key, g => g.First());
            foreach (var l in f.Links.Where(l => l.Source == null))
            {
                Suggestion? guess = null;
                if (pins.TryGetValue(l.PinPath, out var pin))
                {
                    var section = pin.Sections?.FirstOrDefault(s => (s.Name ?? "") == (l.SectionName ?? ""));
                    guess = PackDoctor.SuggestFor(pin, section, names, 1, context).FirstOrDefault();
                }
                string wrote = string.IsNullOrEmpty(l.SectionName) ? l.PinPath : $"{l.PinPath} / {l.SectionName}";
                pairs.Add(new FixPair("location", wrote, l.ApLocationId == null ? "(not a check)" : l.ApLocationName ?? "", guess?.Label, guess?.Score, "loc:unmatched"));
            }
            foreach (var t in f.Tiles.Where(t => t.Source == null && (t.ApItemId != null || t.Hidden)))
            {
                string itemName = _report.Pack.ItemsByCode.TryGetValue(t.Code, out var item) && !string.IsNullOrEmpty(item.Name) ? item.Name : t.Code;
                var guess = PackDoctor.Suggest(itemName, ItemNames, 1).FirstOrDefault();
                pairs.Add(new FixPair("item", $"tile {t.Code} ({itemName})", t.ApItemId == null ? "(hidden: not an item)" : t.ApItemName ?? "", guess?.Label, guess?.Score, "tile:unlinked"));
            }
            return pairs;
        }

        private FixSuggestion SuggestionOf(IReadOnlyList<FixPair> pairs, string? note) => new(
            _report?.Names?.Game ?? _original.Manifest?.GameName ?? "",
            _report?.Names?.Version,
            _report?.Names?.Source ?? "",
            _original.Manifest?.Name ?? _key,
            _original.Manifest?.GetActualVersion() ?? "",
            ProjectOf(_original.Manifest?.VersionsUrl),
            HashOf(_original.SourcePath),
            pairs,
            note);

        private static string? HashOf(string? path)
        {
            try { return string.IsNullOrEmpty(path) ? null : AP_Atlas.Core.EngineSetup.ApworldSources.Sha256OfPack(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>"owner/name" from the pack's versions URL on GitHub, when it is one.</summary>
        private static string? ProjectOf(string? versionsUrl)
        {
            if (string.IsNullOrEmpty(versionsUrl) || !Uri.TryCreate(versionsUrl, UriKind.Absolute, out var uri)) return null;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            bool github = uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
            return github && parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
        }

        /// <summary>Opens "Suggest your fixes to Atlas" (for tests too): the user's own fixes, ticked, and what would be sent.</summary>
        public void SuggestFixes()
        {
            if (!FixSuggestionService.Available) { SetStatus("This build of Atlas can't send suggestions."); return; }
            var pairs = UserFixPairs();
            if (pairs.Count == 0) { SetStatus("You haven't fixed anything by hand in this pack yet: nothing to suggest."); return; }
            string game = _report?.Names?.Game ?? _original.Manifest?.GameName ?? "the game";
            var dialog = new SuggestFixesDialog(pairs, game, (ticked, note) => FixSuggestionService.Preview(SuggestionOf(ticked, note)), (decision, ticked, note) =>
            {
                if (decision == ReportDecision.DontSend || ticked.Count == 0) { SetStatus("Not sent."); return; }
                SetStatus("Sending your fixes…");
                AP_Atlas.Core.Async.Fire(FixSuggestionService.AnswerAsync(decision, SuggestionOf(ticked, note)), "sending pack fixes to Atlas's developer", tellUser: true);
            });
            AddChild(dialog);
            WindowFit.Pop(dialog, 700);
        }

        // ---- A pack's fixes as a file ----

        /// <summary>Writes the pack's fixes (and the pictures they use) to a zip (for tests; the button asks where first).</summary>
        public void ExportFixesTo(string path)
        {
            try
            {
                SafeFile.WriteAllBytes(path, PackFixShare.Export(_original));
                SetStatus($"Your fixes are in {Path.GetFileName(path)}: anyone with this pack can import them.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AP_Atlas.Core.Logger.LogWarning("The pack's fixes couldn't be exported: " + ex.Message);
                SetStatus("Couldn't export: " + ex.Message);
            }
        }

        /// <summary>Reads a fixes file and shows what importing it does (for tests; the button asks which file first).</summary>
        public void ImportFixesFrom(string path)
        {
            SharedFixes shared;
            ImportPlan plan;
            try
            {
                shared = PackFixShare.Read(path);
                plan = PackFixShare.Plan(_original, shared);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Newtonsoft.Json.JsonException)
            {
                SetStatus("Couldn't import that file: " + ex.Message);
                return;
            }
            if (plan.Nothing) { SetStatus("You have every fix that file holds already."); return; }
            var dialog = new ImportFixesDialog(plan, Path.GetFileName(path), theirs =>
            {
                try
                {
                    PackFixShare.Apply(_original, shared, plan, theirs);
                    SetStatus($"Imported {plan.Adds.Count + theirs.Count} fix{(plan.Adds.Count + theirs.Count == 1 ? "" : "es")}. Undo reverses the import.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SetStatus("Couldn't import: " + ex.Message);
                }
            });
            AddChild(dialog);
            WindowFit.Pop(dialog, 660);
        }

        private void PickFixesFile(bool save)
        {
            var dialog = new FileDialog
            {
                FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.zip ; Atlas pack fixes" },
                UseNativeDialog = true,
                Title = save ? "Export this pack's fixes" : "Import fixes for this pack",
                CurrentFile = save ? $"{string.Concat((_original.Manifest?.Name ?? "pack").Split(Path.GetInvalidFileNameChars()))} fixes.zip" : ""
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                if (save) ExportFixesTo(path);
                else ImportFixesFrom(path);
            };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        /// <summary>The Your fixes tab's sharing row: export, import, suggest.</summary>
        private Control SharingRow()
        {
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 8);
            row.AddThemeConstantOverride("v_separation", 6);
            row.AddChild(Kit.Button("Export these fixes…", "Saves this pack's fixes (and their pictures) as one file to share; whoever has the pack can import it", () => PickFixesFile(save: true), PackFixes.Get(_key).Count > 0));
            row.AddChild(Kit.Button("Import fixes…", "Adds someone's fixes for this pack: you see what changes first, and yours stay where you both fixed the same thing unless you choose theirs", () => PickFixesFile(save: false)));
            var suggest = Kit.Button("Suggest them to Atlas…", FixSuggestionService.Available
                ? "Sends the fixes you made by hand (you tick which, and see exactly what goes) to Atlas's developer, who turns patterns into rules for every pack"
                : "This build of Atlas can't send suggestions", SuggestFixes, FixSuggestionService.Available);
            suggest.SetMeta("suggest_fixes", true);
            row.AddChild(suggest);
            return row;
        }
    }
}
