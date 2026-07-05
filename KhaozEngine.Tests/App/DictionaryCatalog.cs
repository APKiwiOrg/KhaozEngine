using System.Collections.Generic;
using KhaozEngine.App;

namespace KhaozEngine.Tests.App
{
    /// <summary>A minimal in-memory <see cref="IStringCatalog"/> for localization tests.</summary>
    internal sealed class DictionaryCatalog : IStringCatalog
    {
        private readonly Dictionary<string, string> _map = new();

        public DictionaryCatalog Add(string key, string value) { _map[key] = value; return this; }

        public string Get(string key) => _map.TryGetValue(key, out var v) ? v : key;

        public string Format(string key, params object?[] args) => string.Format(Get(key), args);

        public bool TryGet(string key, out string value) { value = Get(key); return _map.ContainsKey(key); }
    }
}
