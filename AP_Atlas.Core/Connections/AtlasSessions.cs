using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// Where Atlas creates its Archipelago sessions (a guard rail keeps it the only place). Every session keeps the games'
    /// names in Atlas's <see cref="DataPackageStore"/>: left alone, the connection library would read and write
    /// %LocalAppData%\Archipelago\Cache, outside Atlas's folder, on every connection. And every session's connection
    /// gives its thread back once Atlas is done with it (<see cref="Finished"/>, <see cref="LibraryThreads"/>).
    /// </summary>
    /// <remarks>
    /// The library has no setting for this, so Atlas fills in the one part it would otherwise create itself: a session's
    /// file cache, which the library makes only if none is there when the server's room info arrives. Atlas first checks
    /// the library is exactly as expected and refuses to connect if it isn't, because connecting anyway would write
    /// outside Atlas's folder. The unit tests fail before a changed library could ship.
    /// </remarks>
    public static class AtlasSessions
    {
        /// <summary>A new session (not connected yet) for a server address as the user typed it ("host:port", "wss://…").</summary>
        /// <exception cref="NotSupportedException">This version of the connection library can't keep its data in Atlas's folder.</exception>
        public static ArchipelagoSession Create(string server, DataPackageStore store) =>
            Prepared(ArchipelagoSessionFactory.CreateSession(server), store);

        /// <inheritdoc cref="Create(string, DataPackageStore)"/>
        public static ArchipelagoSession Create(Uri server, DataPackageStore store) =>
            Prepared(ArchipelagoSessionFactory.CreateSession(server), store);

        private static ArchipelagoSession Prepared(ArchipelagoSession session, DataPackageStore store)
        {
            LibraryCache.Attach(session, store);
            LibraryThreads.Watch(session);
            return session;
        }

        /// <summary>The store a session keeps its names in, or null if it has none (the library would use its own).</summary>
        public static DataPackageStore? StoreOf(ArchipelagoSession session) => LibraryCache.StoreOf(session);

        /// <summary>
        /// Atlas is done with a session whose connection has closed (closed by Atlas, dropped, or never logged in): its
        /// thread goes back to the pool. Safe to call more than once, and for a session that never connected.
        /// </summary>
        public static void Finished(ArchipelagoSession session) => LibraryThreads.Release(session);
    }

    /// <summary>
    /// The connection library and the thread pool. While its connection is open, each session's send loop waits for its
    /// next packet by blocking a thread pool thread, and closing the connection doesn't wake it (checked in 6.7.1), so
    /// every connection Atlas ever opened would keep a thread for good. Once those use up the pool's minimum (the
    /// processor count), Atlas's background work (logins, logic, map packs) waits while .NET slowly adds threads: on the
    /// fake server, a login took 10 seconds once 40 connections had been opened. So:
    /// <list type="bullet">
    /// <item>the pool's minimum grows by one for each open connection, so those threads are never missed;</item>
    /// <item>a finished connection's loop is woken with one more packet: it finds its connection closed and ends without
    /// sending it (its library reports a closed socket, which <see cref="SessionManager"/> doesn't pass on).</item>
    /// </list>
    /// </summary>
    internal static class LibraryThreads
    {
        private static readonly object Lock = new();
        // Connections whose send loop holds a thread: opened, not finished. Weak, so a session that's dropped without
        // being finished isn't kept in memory (its thread stays counted, the safe way round).
        private static readonly ConditionalWeakTable<ArchipelagoSession, object> Running = new();
        private static int _running;
        private static int _baseline = -1;

        /// <summary>How many connections' send loops hold a thread now (for tests).</summary>
        internal static int Holding
        {
            get { lock (Lock) return _running; }
        }

        /// <summary>The pool's own minimum, before Atlas added to it (-1 until a connection first opened; for tests).</summary>
        internal static int Baseline
        {
            get { lock (Lock) return _baseline; }
        }

        // The library starts the loop just after it raises SocketOpened.
        internal static void Watch(ArchipelagoSession session) => session.Socket.SocketOpened += () => Count(session, opened: true);

        internal static void Release(ArchipelagoSession session)
        {
            if (session.Socket.Connected)
            {
                // Not closed after all: its loop may still send, so its thread stays counted.
                Logger.LogDebug("A connection Atlas was done with was still open; its thread stays in use.");
                return;
            }
            if (!Count(session, opened: false)) return;
            // The packet is never sent (the loop checks its connection first), so the task never finishes: nothing waits on it.
            Async.Fire(() => session.Socket.SendPacketAsync(new BouncePacket()), "ending a closed connection's send loop", tellUser: false);
        }

        private static bool Count(ArchipelagoSession session, bool opened)
        {
            lock (Lock)
            {
                if (opened ? !Running.TryAdd(session, Lock) : !Running.Remove(session)) return false;
                _running += opened ? 1 : -1;
                ThreadPool.GetMinThreads(out int workers, out int io);
                if (_baseline < 0) _baseline = workers;
                ThreadPool.SetMinThreads(_baseline + _running, io);
                return true;
            }
        }
    }

    /// <summary>The connection library's internal file cache, and putting Atlas's store in its place.</summary>
    internal static class LibraryCache
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Assembly Library = typeof(ArchipelagoSession).Assembly;
        private static readonly Type? CacheType = Library.GetType("Archipelago.MultiClient.Net.DataPackage.DataPackageCache");
        private static readonly Type? ProviderInterface = Library.GetType("Archipelago.MultiClient.Net.DataPackage.IFileSystemDataPackageProvider");
        private static readonly FieldInfo? ProviderField = CacheType?.GetFields(InstanceFields).Where(f => f.FieldType == ProviderInterface).ToArray() is [var only] ? only : null;

        /// <summary>Why this version of the library can't be kept inside Atlas's folder, or null when it can.</summary>
        internal static string? Problem { get; } = FindProblem();

        private static string? FindProblem()
        {
            if (CacheType == null || ProviderInterface == null) return "its data cache has moved";
            if (ProviderField == null) return "its data cache is set up differently";
            var methods = ProviderInterface.GetMethods();
            bool expected = methods.Length == 2
                && methods.Any(m => m.Name == "TryGetDataPackage" && m.ReturnType == typeof(bool) && Parameters(m) == "String,String,GameData&")
                && methods.Any(m => m.Name == "SaveDataPackageToFile" && m.ReturnType == typeof(void) && Parameters(m) == "String,GameData");
            return expected ? null : "its data cache works differently";
        }

        private static string Parameters(MethodInfo method) => string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));

        internal static void Attach(ArchipelagoSession session, DataPackageStore store)
        {
            if (Problem != null) throw Unsupported(Problem);
            object cache = TheCache(session);
            if (ProviderField!.GetValue(cache) != null) throw Unsupported("the session's data cache was already set up");
            var provider = (StoreProvider)DispatchProxy.Create(ProviderInterface!, typeof(StoreProvider));
            provider.Store = store;
            ProviderField.SetValue(cache, provider);
        }

        internal static DataPackageStore? StoreOf(ArchipelagoSession session) =>
            Problem == null && ProviderField!.GetValue(TheCache(session)) is StoreProvider provider ? provider.Store : null;

        /// <summary>The session's one data cache, found among the library's own objects the session holds.</summary>
        private static object TheCache(ArchipelagoSession session)
        {
            var found = new List<object>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { session };
            var queue = new Queue<object>();
            queue.Enqueue(session);
            while (queue.Count > 0 && seen.Count < 1000)
            {
                object current = queue.Dequeue();
                if (current.GetType() == CacheType)
                {
                    found.Add(current);
                    continue;
                }
                for (var type = current.GetType(); type != null && type.Assembly == Library; type = type.BaseType)
                    foreach (var field in type.GetFields(InstanceFields))
                    {
                        if (field.FieldType.IsValueType || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        object? value = field.GetValue(current);
                        if (value != null && value is not Delegate && value.GetType().Assembly == Library && seen.Add(value)) queue.Enqueue(value);
                    }
            }
            return found.Count == 1 ? found[0] : throw Unsupported($"a session holds {found.Count} data caches instead of one");
        }

        private static NotSupportedException Unsupported(string why) => new(
            $"This version of the Archipelago connection library can't keep the games' names in Atlas's folder ({why}), " +
            "so Atlas won't connect with it rather than write outside its folder. Please report this.");
    }

    /// <summary>
    /// The library's cache, answered from Atlas's store. Its interface isn't public, so this is a proxy that implements
    /// it at run time; <see cref="LibraryCache"/> checks it has exactly the two methods answered here.
    /// </summary>
    internal class StoreProvider : DispatchProxy
    {
        internal DataPackageStore? Store { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var store = Store;
            switch (targetMethod?.Name)
            {
                case "TryGetDataPackage" when store != null && args is { Length: 3 }:
                    bool found = store.TryGet(args[0] as string ?? "", args[1] as string, out var data);
                    args[2] = data;
                    return found;
                case "SaveDataPackageToFile" when store != null && args is { Length: 2 }:
                    store.Save(args[0] as string ?? "", args[1] as GameData);
                    return null;
                default:
                    // Not reached (the interface was checked). Answering "nothing stored" makes the library ask the server.
                    return targetMethod?.ReturnType == typeof(bool) ? false : null;
            }
        }
    }
}
