using System.Collections;
using System.Globalization;

namespace Relate.Net;

/// <summary>Converts materialized relate values to a requested .NET type for <c>Get&lt;T&gt;</c>.</summary>
internal static class ValueConverter
{
    internal static T Convert<T>(object? value) => (T)Convert(value, typeof(T))!;

    internal static object? Convert(object? value, Type target)
    {
        if (value is null)
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) is not null)
            {
                return null;
            }

            throw new InvalidCastException($"Cannot convert a Cypher null to the non-nullable type {target}.");
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        Type type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        switch (value)
        {
            case CypherZonedDateTime zoned when type == typeof(DateTimeOffset):
                return zoned.DateTimeOffset;
            case CypherDuration duration when type == typeof(TimeSpan):
                return duration.ToTimeSpan();
            case CypherVector vector when type == typeof(double[]):
                return vector.ToDoubleArray();
            case CypherVector vector when type == typeof(float[]):
                return vector.ToSingleArray();
            case CypherVector vector when type == typeof(long[]):
                return vector.ToInt64Array();
            case string s when type == typeof(Guid):
                return Guid.Parse(s);
            case string s when type.IsEnum:
                return Enum.Parse(type, s, ignoreCase: true);
            case long l when type.IsEnum:
                return Enum.ToObject(type, l);
            case string s when type == typeof(char) && s.Length == 1:
                return s[0];
        }

        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type) && type != typeof(string) && value is not string)
        {
            return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        if (type == typeof(string))
        {
            return System.Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        if (value is IReadOnlyDictionary<string, object?> map && TryConvertMap(map, type, out object? convertedMap))
        {
            return convertedMap;
        }

        if (value is IReadOnlyList<object?> list && TryConvertList(list, type, out object? convertedList))
        {
            return convertedList;
        }

        throw new InvalidCastException($"Cannot convert a value of type {value.GetType()} to {target}.");
    }

    private static bool TryConvertList(IReadOnlyList<object?> list, Type type, out object? result)
    {
        Type? element = null;
        if (type.IsArray)
        {
            element = type.GetElementType()!;
            Array array = Array.CreateInstance(element, list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                array.SetValue(Convert(list[i], element), i);
            }

            result = array;
            return true;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>)
                || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            {
                element = type.GetGenericArguments()[0];
            }
        }

        if (element is null)
        {
            result = null;
            return false;
        }

        var typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element), list.Count)!;
        foreach (object? item in list)
        {
            typed.Add(Convert(item, element));
        }

        result = typed;
        return true;
    }

    private static bool TryConvertMap(IReadOnlyDictionary<string, object?> map, Type type, out object? result)
    {
        result = null;
        if (!type.IsGenericType)
        {
            return false;
        }

        Type definition = type.GetGenericTypeDefinition();
        Type[] args = type.GetGenericArguments();
        if ((definition != typeof(Dictionary<,>) && definition != typeof(IDictionary<,>) && definition != typeof(IReadOnlyDictionary<,>))
            || args[0] != typeof(string))
        {
            return false;
        }

        var typed = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), args[1]))!;
        foreach (KeyValuePair<string, object?> entry in map)
        {
            typed.Add(entry.Key, Convert(entry.Value, args[1]));
        }

        result = typed;
        return true;
    }
}

/// <summary>Formats values Cypher-style for <c>ToString()</c> diagnostics.</summary>
internal static class ValueFormatter
{
    internal static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"'{s.Replace("'", "\\'", StringComparison.Ordinal)}'",
        bool b => b ? "true" : "false",
        IReadOnlyDictionary<string, object?> map => "{" + string.Join(", ", map.Select(e => $"{e.Key}: {Format(e.Value)}")) + "}",
        IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Format)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
