using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Agent;

public sealed class LlmResponseCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlMinutes { get; set; } = 1440;
    public int MaxEntries { get; set; } = 200;
}

/// <summary>
/// Caché de respuestas del LLM en memoria. La clave es el hash del prompt completo (reglas + hechos + T-SQL + pregunta),
/// así que si el SP cambia, o cambia la pregunta, la clave cambia sola y no hay respuestas viejas.
/// Misma pregunta sobre el mismo objeto sin cambios = cero tokens.
/// </summary>
public sealed class LlmResponseCache
{
    private readonly ConcurrentDictionary<string, (string Reply, DateTime At)> _items = new();
    private readonly LlmResponseCacheOptions _options;

    public LlmResponseCache(IOptions<LlmResponseCacheOptions> options)
    {
        _options = options.Value;
    }

    public static string KeyFor(string system, string user)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(system + "\u001f" + user));
        return Convert.ToHexString(hash);
    }

    public bool TryGet(string key, out string? reply)
    {
        reply = null;
        if (!_options.Enabled || !_items.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(_options.TtlMinutes))
        {
            _items.TryRemove(key, out _);
            return false;
        }
        reply = entry.Reply;
        return true;
    }

    public void Set(string key, string reply)
    {
        if (!_options.Enabled) return;
        _items[key] = (reply, DateTime.UtcNow);
        if (_items.Count > _options.MaxEntries)
        {
            foreach (string oldest in _items.OrderBy(i => i.Value.At).Take(_items.Count - _options.MaxEntries).Select(i => i.Key))
            {
                _items.TryRemove(oldest, out _);
            }
        }
    }
}
