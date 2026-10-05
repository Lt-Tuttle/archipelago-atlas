using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// Where Atlas creates its Archipelago sessions (a guard rail keeps it the only place). Every session keeps the games'
    /// names in Atlas's <see cref="DataPackageStore"/>: left alone, the connection library would read and write
    /// %LocalAppData%\Archipelago\Cache, outside Atlas's folder, on every connection.
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
            return session;
        }

        /// <summary>The store a session keeps its names in, or null if it has none (the library would use its own).</summary>
        public static DataPackageStore? StoreOf(ArchipelagoSession session) => LibraryCache.StoreOf(session);
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
