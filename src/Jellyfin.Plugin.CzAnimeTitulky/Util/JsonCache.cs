using System.Collections.Concurrent;
using System.Text.Json;

namespace Jellyfin.Plugin.CzAnimeTitulky.Util;

/// <summary>Small persistent key/value cache stored as JSON in the plugin data folder.</summary>
public sealed class JsonCache<T>
{
    private readonly string _name;
    private readonly object _lock = new();
    private ConcurrentDictionary<string, Entry>? _data;
    private string? _file;

    public JsonCache(string name)
    {
        _name = name;
    }

    public bool TryGet(string key, TimeSpan maxAge, out T? value)
    {
        if (Data.TryGetValue(key, out var e) && e.Stored > DateTime.UtcNow - maxAge)
        {
            value = e.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(string key, T? value)
    {
        Data[key] = new Entry { Value = value, Stored = DateTime.UtcNow };
        lock (_lock)
        {
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
                File.Move(tmp, _file!, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Loaded on first use – the plugin instance (and its data folder) exists by then.</summary>
    private ConcurrentDictionary<string, Entry> Data
    {
        get
        {
            if (_data is not null)
            {
                return _data;
            }

            lock (_lock)
            {
                if (_data is null)
                {
                    var folder = Plugin.Instance?.CacheFolder ?? Path.Combine(Path.GetTempPath(), "cz-anime-titulky");
                    Directory.CreateDirectory(folder);
                    _file = Path.Combine(folder, _name + ".json");
                    try
                    {
                        _data = File.Exists(_file)
                            ? JsonSerializer.Deserialize<ConcurrentDictionary<string, Entry>>(File.ReadAllText(_file)) ?? new()
                            : new();
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        _data = new();
                    }
                }

                return _data;
            }
        }
    }

    public sealed class Entry
    {
        public T? Value { get; set; }

        public DateTime Stored { get; set; }
    }
}
