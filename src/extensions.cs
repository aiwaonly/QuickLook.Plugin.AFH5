using System.Collections.Generic;

namespace QuickLook.Plugin.AFH5;

public static class DictionaryExtensions
{
    /// <summary>
    /// Reads a key without throwing when it is absent. net462 has no
    /// <c>CollectionExtensions.GetValueOrDefault</c>, and the readers rely on a
    /// missing key yielding <c>default</c>.
    /// </summary>
    public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : default;
    }
}
