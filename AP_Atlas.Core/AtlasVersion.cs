using System.Reflection;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Atlas's version. It is set in one place, &lt;Version&gt; in Directory.Build.props, and the build adds the commit after a '+'
    /// (e.g. "0.1.0-beta.1+3f2a9c1…").
    /// </summary>
    public static class AtlasVersion
    {
        /// <summary>The full version with the commit, for logs and bug reports.</summary>
        public static readonly string Full = Read();

        /// <summary>The version people see, e.g. "0.1.0-beta.1".</summary>
        public static string Display => Full.Split('+')[0];

        /// <summary>The short commit this build came from, or "" when unknown.</summary>
        public static string Commit
        {
            get
            {
                int plus = Full.IndexOf('+');
                if (plus < 0) return "";
                string sha = Full[(plus + 1)..];
                return sha.Length > 7 ? sha[..7] : sha;
            }
        }

        /// <summary>Sent with every web request, so site owners can see who is calling and where the project lives.</summary>
        /// <summary>Where Atlas lives: its code, releases and issues.</summary>
        public const string RepoUrl = "https://github.com/Lt-Tuttle/archipelago-atlas";

        public static string UserAgent => $"TheArchipelagoAtlas/{Display} (Archipelago tracker; +{RepoUrl})";

        private static string Read()
        {
            var attr = typeof(AtlasVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return string.IsNullOrWhiteSpace(attr?.InformationalVersion) ? "0.0.0-dev" : attr.InformationalVersion.Trim();
        }
    }
}
