using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Packets;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Connections;

/// <summary>
/// Reads the room's data storage for a map pack's scripts, on one connection: keys to be told about (SetNotify) and keys
/// read once (Get), with the answers (SetReply, Retrieved) passed on as Atlas's own JSON. Read only: nothing here can
/// write the room's data storage, which Atlas never does for a pack (the owner's rule, 2026-10-08).
/// The connection library carries its own copy of Newtonsoft.Json, so the values in its packets are read by reflection
/// and turned into Atlas's JSON (<see cref="FromLibrary"/>), never used as the library's types.
/// </summary>
public sealed class DataStorageReads : IDisposable
{
    private readonly ArchipelagoSession _session;
    private readonly object _lock = new();
    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>A watched key changed: the key, its value now and before (on the connection's thread).</summary>
    public event Action<string, JToken?, JToken?>? Changed;

    /// <summary>A key asked for was read: the key and its value (on the connection's thread).</summary>
    public event Action<string, JToken?>? Read;

    public DataStorageReads(ArchipelagoSession session)
    {
        _session = session;
        _session.Socket.PacketReceived += OnPacket;
    }

    /// <summary>Asks the server to tell Atlas when these keys change (once per key).</summary>
    public Task WatchAsync(IEnumerable<string> keys)
    {
        string[] fresh;
        lock (_lock) fresh = keys.Where(k => !string.IsNullOrEmpty(k) && _watched.Add(k)).ToArray();
        return fresh.Length == 0 || _disposed ? Task.CompletedTask : _session.Socket.SendPacketAsync(new SetNotifyPacket { Keys = fresh });
    }

    /// <summary>Reads these keys once.</summary>
    public Task ReadAsync(IEnumerable<string> keys)
    {
        string[] wanted = keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToArray();
        lock (_lock) _asked.UnionWith(wanted);
        return wanted.Length == 0 || _disposed ? Task.CompletedTask : _session.Socket.SendPacketAsync(new GetPacket { Keys = wanted });
    }

    private void OnPacket(ArchipelagoPacketBase packet)
    {
        if (_disposed) return;
        if (packet is SetReplyPacket reply)
        {
            bool watched;
            lock (_lock) watched = reply.Key != null && _watched.Contains(reply.Key);
            if (!watched) return;
            Changed?.Invoke(reply.Key!, FromLibrary(Member(packet, "Value")), FromLibrary(Member(packet, "OriginalValue")));
        }
        else if (packet is RetrievedPacket && Member(packet, "Data") is IDictionary data)
        {
            foreach (DictionaryEntry entry in data)
            {
                if (entry.Key is not string key) continue;
                bool asked;
                lock (_lock) asked = _asked.Contains(key);
                if (asked) Read?.Invoke(key, FromLibrary(entry.Value));
            }
        }
    }

    private static object? Member(object packet, string name) => packet.GetType().GetProperty(name)?.GetValue(packet);

    /// <summary>
    /// A value from the connection library's JSON as Atlas's: a plain value by its value, anything else by its JSON text.
    /// Null for none.
    /// </summary>
    internal static JToken? FromLibrary(object? token)
    {
        if (token == null) return null;
        var type = token.GetType();
        if (type.Name == "JValue")
        {
            object? value = type.GetProperty("Value")?.GetValue(token);
            return value == null ? JValue.CreateNull() : JToken.FromObject(value);
        }
        try { return JToken.Parse(token.ToString() ?? "null"); }
        catch (Newtonsoft.Json.JsonException) { return null; } // not JSON after all: nothing a script could use
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Socket.PacketReceived -= OnPacket;
    }
}
