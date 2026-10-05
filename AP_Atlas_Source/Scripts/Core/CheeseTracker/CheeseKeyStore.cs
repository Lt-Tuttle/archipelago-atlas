namespace AP_Atlas.Core.CheeseTracker
{
    /// <summary>
    /// Keeps the Cheese Tracker API key encrypted with Windows' data protection (DPAPI) for the current Windows account:
    /// settings.json holds only ciphertext that no other account or PC can read. Elsewhere the key isn't stored at all.
    /// </summary>
    public static class CheeseKeyStore
    {
        public static bool Available => Secrets.Available;

        /// <summary>The key encrypted for this Windows account (base64), or null if it couldn't be.</summary>
        public static string Protect(string secret) => Secrets.Protect(secret, Secrets.CheeseApiKey);

        /// <summary>The key, or null if there's none or it can't be decrypted here (another account or PC).</summary>
        public static string Unprotect(string stored) => Secrets.Unprotect(stored, Secrets.CheeseApiKey);
    }
}
