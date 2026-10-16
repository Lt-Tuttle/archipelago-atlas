#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>
    /// What slots' logic checks found about each game, kept per game and per apworld (its SHA-256, or
    /// "archipelago-&lt;version&gt;" for a world that ships with Archipelago) in <c>engine\game_trust.json</c>, beside
    /// GameSweep's results. The Games page and Add a slot say the newest, else what Check this game found.
    /// </summary>
    public static class GameTrustRecords
    {
        private static readonly object Gate = new();
        private static GameTrustStore? _store;
        private static GameTrustStore Store => _store ??= GameTrustStore.Load(GameTrustStore.PathFor(AtlasEngine.EngineDir));

        /// <summary>Keeps what a slot's check found (run off the main thread: a seed's own apworld is hashed here). Never throws.</summary>
        public static void Remember(EngineInstall install, string game, string? seedApworld, string? worldVersion, string? archipelago, string? yamlSource,
            bool same, bool logicDiffers, IReadOnlyList<string> examples)
        {
            try
            {
                var (key, version) = ApworldOf(install, game, seedApworld, worldVersion, archipelago);
                var record = new TrustRecord
                {
                    Game = game,
                    Apworld = key,
                    Version = version,
                    Same = same,
                    LogicDiffers = logicDiffers,
                    Examples = examples.Take(6).ToList(),
                    YamlSource = yamlSource,
                    Checked = DateTime.Now // wall clock: shown as a date
                };
                lock (Gate) Store.Put(record);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogWarning($"Couldn't keep what {game}'s logic check found: {ex.Message}");
            }
        }

        /// <summary>The game's newest kept record, or null.</summary>
        public static TrustRecord? Newest(string game)
        {
            lock (Gate) return Store.Newest(game);
        }

        /// <summary>Forgets a game's records (for tests: what the page says without a slot's check).</summary>
        public static void ForgetForTests(string game)
        {
            lock (Gate) Store.Remove(game);
        }

        /// <summary>
        /// The apworld a game's logic runs on, as its key and the version people know it by: the seed's own copy when the
        /// engine used it, else the installed copy, else the world Archipelago ships (by the version the engine runs).
        /// </summary>
        public static (string Key, string? Version) ApworldOf(EngineInstall? install, string game, string? seedApworld = null, string? worldVersion = null, string? archipelago = null)
        {
            if (seedApworld != null && File.Exists(seedApworld))
            {
                string sha = EngineDownloader.Sha256Of(seedApworld).ToLowerInvariant();
                return (sha, ApworldSources.KnownSourceOf(sha).Tag ?? worldVersion);
            }
            var copy = ApworldSources.InstalledCopies(install, game).FirstOrDefault();
            if (copy.Sha256 != null) return (copy.Sha256.ToLowerInvariant(), ApworldSources.KnownSourceOf(copy.Sha256).Tag ?? worldVersion);
            string ap = archipelago ?? (install != null ? AtlasEngine.LastCheck(install)?.Archipelago : null) ?? AtlasEngine.ArchipelagoVersion;
            return (GameTrustStore.ArchipelagoKey(ap), "Archipelago " + ap);
        }

        /// <summary>
        /// Whether a slot's logic for the game is the seed's, as the game's page and Add a slot say it: the kept record for
        /// the installed apworld, else the newest (saying it isn't that version), else what Check this game found; null
        /// when nothing is known.
        /// </summary>
        public static string? Line(EngineInstall? install, string game, Func<string, string> tr)
        {
            string current = ApworldOf(install, game).Key;
            TrustRecord? record;
            lock (Gate) record = Store.Find(game, current) ?? Store.Newest(game);
            if (record != null)
            {
                string summary = new GameTrust { Same = record.Same, LogicDiffers = record.LogicDiffers, Examples = record.Examples }.Summary + ".";
                string from = tr("Found by a slot's logic on {0}, with {1}.").Replace("{0}", Date(record.Checked)).Replace("{1}", record.Version ?? tr("an apworld Atlas can't name"));
                if (!string.Equals(record.Apworld, current, StringComparison.OrdinalIgnoreCase))
                    from += " " + (install == null ? tr("The Atlas Engine isn't set up now.")
                        : GameTrustStore.IsArchipelagoKey(current) && !GameTrustStore.IsArchipelagoKey(record.Apworld) ? tr("Its apworld isn't installed now.")
                        : tr("That isn't the version installed now."));
                return summary + " " + from;
            }
            if (install != null && GameSweep.LastFor(install, game) is { Trust: { Error: null } trust } result)
            {
                // The game's own check time (results kept before Atlas recorded it say no date rather than another game's).
                bool versioned = result.WorldVersion is { Length: > 0 };
                string from = result.Checked is { } at
                    ? (versioned ? tr("Found by Check this game on {0}, with version {1}.") : tr("Found by Check this game on {0}.")).Replace("{0}", Date(at))
                    : versioned ? tr("Found by Check this game, with version {1}.") : tr("Found by Check this game.");
                return trust.Summary + ". " + from.Replace("{1}", result.WorldVersion);
            }
            return null;
        }

        private static string Date(DateTime when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
