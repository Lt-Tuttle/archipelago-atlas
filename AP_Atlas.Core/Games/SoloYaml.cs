#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games
{
    /// <summary>What a solo test's YAML holds: the player's name, the game, how many options its section has, and anything wrong with it.</summary>
    public sealed record SoloYamlCheck(string? Name, string? Game, int Options, IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0;
    }

    /// <summary>Checks the YAML the engine made for a solo test before it's generated from: one document, the player named as asked, the game and its section present.</summary>
    public static class SoloYaml
    {
        public const string PlayerName = "AtlasTest";

        public static SoloYamlCheck Inspect(string? text, string playerName = PlayerName)
        {
            var problems = new List<string>();
            var docs = YamlExclusions.Documents(text ?? "").ToList();
            if (docs.Count == 0)
            {
                problems.Add("the YAML holds no document");
                return new SoloYamlCheck(null, null, 0, problems);
            }
            if (docs.Count > 1) problems.Add("the YAML holds " + docs.Count + " documents; a solo test wants one player");
            var doc = docs[0];
            string? name = YamlExclusions.Scalar(doc, "name");
            string? game = YamlExclusions.Scalar(doc, "game");
            if (name != playerName) problems.Add("the player's name is \"" + (name ?? "") + "\", not " + playerName);
            else if (name.Contains('{')) problems.Add("the player's name holds a placeholder");
            if (string.IsNullOrWhiteSpace(game)) problems.Add("no game is named");
            int options = 0;
            if (game != null && doc.TryGetValue(game, out var section) && section is Dictionary<string, object> map) options = map.Count;
            else if (game != null) problems.Add("the YAML has no section for " + game);
            return new SoloYamlCheck(name, game, options, problems);
        }
    }
}
