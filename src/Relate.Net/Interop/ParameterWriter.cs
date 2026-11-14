using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using static Relate.Net.Interop.NativeMethods;

namespace Relate.Net.Interop;

/// <summary>Translates .NET parameter objects into a native relate params builder tree.</summary>
internal static unsafe class ParameterWriter
{
    private const int MaxDepth = 64;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)?> s_keyValueTypes = new();

    /// <summary>Builds a params root from a parameter object, or returns 0 for <see langword="null"/>.</summary>
    internal static nint Build(object? parameters)
    {
        if (parameters is null)
        {
            return 0;
        }

        IEnumerable<KeyValuePair<string, object?>> entries = GetMapEntries(parameters)
            ?? GetObjectEntries(parameters)
            ?? throw new ArgumentException(
                $"Parameters of type {parameters.GetType()} cannot be used; pass a dictionary or an object with public properties.",
                nameof(parameters));

        nint root = relate_params_new();
        if (root == 0)
        {
            throw new OutOfMemoryException("relate could not allocate a parameter builder.");
        }

        try
        {
            foreach (KeyValuePair<string, object?> entry in entries)
            {
                WriteValue(root, entry.Key, entry.Value, entry.Key, 0);
            }

            return root;
        }
        catch
        {
            relate_params_free(root);
            throw;
        }
    }

    private static void WriteValue(nint builder, string? name, object? value, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException($"Parameter '{path}' is nested deeper than {MaxDepth} levels (is the object graph cyclic?).");
        }

        nint err = 0;
        RelateStatus status;
        switch (value)
        {
            case null:
            case DBNull:
                status = relate_params_put_null(builder, name, &err);
                break;
            case bool b:
                status = relate_params_put_bool(builder, name, b ? (byte)1 : (byte)0, &err);
                break;
            case string s:
                status = PutString(builder, name, s, &err);
                break;
            case char c:
                status = PutString(builder, name, c.ToString(), &err);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                status = relate_params_put_int(builder, name, Convert.ToInt64(value, null), &err);
                break;
            case ulong ul:
                if (ul > long.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(path, ul, "Cypher integers are 64-bit signed.");
                }

                status = relate_params_put_int(builder, name, (long)ul, &err);
                break;
            case float f:
                status = relate_params_put_float(builder, name, f, &err);
                break;
            case double d:
                status = relate_params_put_float(builder, name, d, &err);
                break;
            case decimal m:
                status = relate_params_put_float(builder, name, (double)m, &err);
                break;
            case Guid g:
                status = PutString(builder, name, g.ToString("D"), &err);
                break;
            case Enum e:
                status = PutString(builder, name, e.ToString(), &err);
                break;
            case DateOnly date:
                status = relate_params_put_date(builder, name, date.Year, date.Month, date.Day, &err);
                break;
            case TimeOnly time:
                status = relate_params_put_local_time(builder, name, time.Hour, time.Minute, time.Second, SubSecondNanos(time.Ticks), &err);
                break;
            case DateTime dt when dt.Kind == DateTimeKind.Utc:
                status = PutDateTime(builder, name, dt, 0, null, &err);
                break;
            case DateTime dt:
                status = relate_params_put_local_datetime(
                    builder, name, dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, SubSecondNanos(dt.Ticks), &err);
                break;
            case DateTimeOffset dto:
                status = PutDateTime(builder, name, dto.DateTime, (long)dto.Offset.TotalSeconds, null, &err);
                break;
            case CypherZonedDateTime zoned:
                status = PutDateTime(builder, name, zoned.DateTimeOffset.DateTime, (long)zoned.DateTimeOffset.Offset.TotalSeconds, zoned.ZoneId, &err);
                break;
            case CypherOffsetTime ot:
                status = relate_params_put_time(
                    builder, name, ot.Time.Hour, ot.Time.Minute, ot.Time.Second, SubSecondNanos(ot.Time.Ticks), (long)ot.Offset.TotalSeconds, null, &err);
                break;
            case TimeSpan ts:
                CypherDuration fromSpan = CypherDuration.FromTimeSpan(ts);
                status = relate_params_put_duration(builder, name, 0, 0, fromSpan.Seconds, fromSpan.Nanoseconds, &err);
                break;
            case CypherDuration dur:
                status = relate_params_put_duration(builder, name, dur.Months, dur.Days, dur.Seconds, dur.Nanoseconds, &err);
                break;
            case CypherPoint p:
                double z = p.Z.GetValueOrDefault();
                status = relate_params_put_point(builder, name, p.Srid, p.X, p.Y, p.Z.HasValue ? &z : null, &err);
                break;
            case CypherVector vec:
                status = PutVector(builder, name, vec, &err);
                break;
            case Node or Relationship or GraphPath:
                throw new ArgumentException(
                    $"Parameter '{path}' is a graph entity; entities cannot be sent as parameters. Pass its ElementId and match on elementId() instead.");
            case byte[] bytes:
                status = WriteList(builder, name, bytes, path, depth, &err);
                break;
            default:
                IEnumerable<KeyValuePair<string, object?>>? mapEntries = GetMapEntries(value);
                if (mapEntries is not null)
                {
                    status = WriteMap(builder, name, mapEntries, path, depth, &err);
                }
                else if (value is IEnumerable enumerable)
                {
                    status = WriteList(builder, name, enumerable, path, depth, &err);
                }
                else
                {
                    IEnumerable<KeyValuePair<string, object?>> props = GetObjectEntries(value)
                        ?? throw new NotSupportedException($"Parameter '{path}' has unsupported type {value.GetType()}.");
                    status = WriteMap(builder, name, props, path, depth, &err);
                }

                break;
        }

        if (status != RelateStatus.Ok)
        {
            RelateException inner = RelateException.FromErrorJson(Native.TakeString(err), "params");
            throw new ArgumentException($"relate rejected parameter '{path}': {inner.Message}", inner);
        }

        Native.TakeString(err);
    }

    private static RelateStatus WriteMap(nint builder, string? name, IEnumerable<KeyValuePair<string, object?>> entries, string path, int depth, nint* err)
    {
        RelateStatus status = relate_params_put_map(builder, name, out nint child, err);
        if (status == RelateStatus.Ok)
        {
            foreach (KeyValuePair<string, object?> entry in entries)
            {
                WriteValue(child, entry.Key, entry.Value, $"{path}.{entry.Key}", depth + 1);
            }
        }

        return status;
    }

    private static RelateStatus WriteList(nint builder, string? name, IEnumerable items, string path, int depth, nint* err)
    {
        RelateStatus status = relate_params_put_list(builder, name, out nint child, err);
        if (status == RelateStatus.Ok)
        {
            int index = 0;
            foreach (object? item in items)
            {
                WriteValue(child, null, item, $"{path}[{index++}]", depth + 1);
            }
        }

        return status;
    }

    private static RelateStatus PutString(nint builder, string? name, string value, nint* err)
    {
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> buffer = max <= 512 ? stackalloc byte[512] : new byte[Encoding.UTF8.GetByteCount(value)];
        int len = Encoding.UTF8.GetBytes(value, buffer);
        fixed (byte* p = buffer)
        {
            return relate_params_put_string(builder, name, len == 0 ? null : p, (nuint)len, err);
        }
    }

    private static RelateStatus PutDateTime(nint builder, string? name, DateTime wallClock, long offsetSeconds, string? zone, nint* err) =>
        relate_params_put_datetime(
            builder, name, wallClock.Year, wallClock.Month, wallClock.Day, wallClock.Hour, wallClock.Minute, wallClock.Second,
            SubSecondNanos(wallClock.Ticks), offsetSeconds, zone, err);

    private static RelateStatus PutVector(nint builder, string? name, CypherVector vector, nint* err)
    {
        string dtype = vector.ElementTypeName;
        if (vector.IsInteger)
        {
            long[] values = vector.ToInt64Array();
            fixed (long* p = values)
            {
                return relate_params_put_vector_int(builder, name, dtype, p, (nuint)values.Length, err);
            }
        }
        else
        {
            double[] values = vector.ToDoubleArray();
            fixed (double* p = values)
            {
                return relate_params_put_vector_float(builder, name, dtype, p, (nuint)values.Length, err);
            }
        }
    }

    private static long SubSecondNanos(long ticks) => (ticks % TimeSpan.TicksPerSecond) * 100;

    /// <summary>Returns the entries of a string-keyed dictionary-like object, or <see langword="null"/>.</summary>
    private static IEnumerable<KeyValuePair<string, object?>>? GetMapEntries(object value)
    {
        switch (value)
        {
            case IEnumerable<KeyValuePair<string, object?>> typed:
                return typed;
            case IDictionary dictionary:
                return EnumerateDictionary(dictionary);
        }

        // Any IEnumerable<KeyValuePair<string, T>> (e.g. an F# Map<string, int>).
        if (value is IEnumerable enumerable && GetKeyValueType(value.GetType()) is { } kv)
        {
            return EnumerateKeyValuePairs(enumerable, kv.Key, kv.Value);
        }

        return null;
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateDictionary(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            yield return new(entry.Key as string ?? throw new ArgumentException("Parameter map keys must be strings."), entry.Value);
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateKeyValuePairs(IEnumerable items, PropertyInfo key, PropertyInfo value)
    {
        foreach (object? item in items)
        {
            yield return new((string)key.GetValue(item)!, value.GetValue(item));
        }
    }

    private static (PropertyInfo Key, PropertyInfo Value)? GetKeyValueType(Type type) => s_keyValueTypes.GetOrAdd(type, static t =>
    {
        foreach (Type iface in t.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type element = iface.GetGenericArguments()[0];
                if (element.IsGenericType
                    && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                    && element.GetGenericArguments()[0] == typeof(string))
                {
                    return (element.GetProperty("Key")!, element.GetProperty("Value")!);
                }
            }
        }

        return null;
    });

    /// <summary>Returns the public readable instance properties of a POCO / anonymous object / record.</summary>
    private static IEnumerable<KeyValuePair<string, object?>>? GetObjectEntries(object value)
    {
        Type type = value.GetType();
        if (type.IsPrimitive || value is IEnumerable)
        {
            return null;
        }

        PropertyInfo[] properties = s_properties.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray());

        return properties.Length == 0 ? null : properties.Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(value)));
    }
}
