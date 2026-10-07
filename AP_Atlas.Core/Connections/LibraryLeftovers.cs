using System;
using System.Linq;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// Failures the connection library leaves behind unchecked. Its session's ConnectAsync waits 4 seconds for the socket's
    /// connection and then stops waiting without ever checking how it ended (checked in 6.7.1). A server that's down takes
    /// longer than that to refuse on Windows when the address has no scheme (the library tries wss, then ws, about 2 seconds
    /// each), so the failure surfaces later from the finalizer as an "unobserved task exception". The connection attempt
    /// itself was already reported (it failed or timed out), so this isn't a crash.
    /// </summary>
    public static class LibraryLeftovers
    {
        private const string LibraryConnect = "Archipelago.MultiClient.Net.Helpers.ArchipelagoSocketHelper.ConnectToProvidedUri";

        /// <summary>Whether an unchecked failure is the library's abandoned socket connection.</summary>
        public static bool IsAbandonedConnect(Exception? failure)
        {
            if (failure is not AggregateException all) return false;
            var inner = all.Flatten().InnerExceptions;
            return inner.Count > 0 && inner.All(ex => ex is System.Net.WebSockets.WebSocketException && (ex.StackTrace ?? "").Contains(LibraryConnect, StringComparison.Ordinal));
        }
    }
}
