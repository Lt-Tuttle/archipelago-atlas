using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AP_Atlas.Core;

/// <summary>
/// A time limit for every regular expression in Atlas that doesn't set its own, libraries' included. Many read text from
/// outside Atlas (a pack's scripts, an apworld's source, a site's pages), and a pattern that backtracks can take hours on
/// one crafted input. With a limit, such a match fails (RegexMatchTimeoutException) in seconds instead. .NET reads the
/// default once, when a regular expression is first made, so it's set as this assembly loads (a module initializer):
/// Atlas's code uses it before making any regular expression, which the self-test checks. Patterns that read large outside
/// text are also written to run in linear time (RegexOptions.NonBacktracking, or a scanner such as <see cref="LuaText"/>):
/// the limit is the backstop.
/// </summary>
public static class RegexDefaults
{
    /// <summary>The limit, far above what any of Atlas's patterns takes on real input (milliseconds).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Atlas's own library: the limit must be set before any code makes a regular expression")]
    internal static void OnLoad() => AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", MatchTimeout);
}
