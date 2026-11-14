using System.Collections.ObjectModel;
using static Relate.Net.Interop.NativeMethods;

namespace Relate.Net.Interop;

/// <summary>Materializes relate value handles into .NET objects (see the type map in the user guide).</summary>
internal static class ValueReader
{
    private static readonly IReadOnlyDictionary<string, object?> s_emptyMap =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    internal static object? Read(nint v)
    {
        if (v == 0)
        {
            return null;
        }

        int kind = relate_value_kind(v);
        switch ((NativeValueKind)kind)
        {
            case NativeValueKind.Null:
                return null;
            case NativeValueKind.Bool:
                Ok(relate_value_bool(v, out byte b), "bool");
                return b != 0;
            case NativeValueKind.Int:
                Ok(relate_value_int(v, out long l), "int");
                return l;
            case NativeValueKind.Float:
                Ok(relate_value_float(v, out double d), "float");
                return d;
            case NativeValueKind.String:
                return ReadString(v);
            case NativeValueKind.Node:
                return ReadNode(v);
            case NativeValueKind.Relationship:
                return ReadRelationship(v);
            case NativeValueKind.Path:
                return ReadPath(v);
            case NativeValueKind.List:
                return ReadList(v);
            case NativeValueKind.Map:
                return ReadMap(v);
            case NativeValueKind.Point:
                Ok(relate_value_point(v, out int srid, out int dim, out double x, out double y, out double z), "point");
                return new CypherPoint(srid, x, y, dim == 3 ? z : null);
            case NativeValueKind.Date:
                return ToDate(v);
            case NativeValueKind.LocalTime:
                return ToTimeOnly(v);
            case NativeValueKind.Time:
                return new CypherOffsetTime(ToTimeOnly(v), TimeSpan.FromSeconds(Int(v, relate_value_offset_seconds, "offset")));
            case NativeValueKind.LocalDateTime:
                return ToLocalDateTime(v);
            case NativeValueKind.DateTime:
                return ToDateTime(v);
            case NativeValueKind.Duration:
                Ok(relate_value_duration(v, out long months, out long days, out long seconds, out long nanos), "duration");
                return new CypherDuration(months, days, seconds, nanos);
            case NativeValueKind.Vector:
                return ReadVector(v);
            default:
                return new UnsupportedValue(kind);
        }
    }

    private static string ReadString(nint v) => Native.PtrToString(relate_value_string(v, out nuint len), len);

    private static IReadOnlyList<object?> ReadList(nint v)
    {
        long count = relate_value_list_len(v);
        var items = new object?[Math.Max(count, 0)];
        for (long i = 0; i < items.Length; i++)
        {
            items[i] = Read(relate_value_list_get(v, i));
        }

        return Array.AsReadOnly(items);
    }

    private static IReadOnlyDictionary<string, object?> ReadMap(nint v)
    {
        long count = relate_value_map_len(v);
        if (count <= 0)
        {
            return s_emptyMap;
        }

        var map = new Dictionary<string, object?>((int)count, StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            nint key = relate_value_map_key(v, i, out nuint len);
            if (key == 0)
            {
                break;
            }

            // The key pointer is NUL-terminated, so it can be handed straight back to map_get.
            map[Native.PtrToString(key, len)] = Read(relate_value_map_get(v, key));
        }

        return new ReadOnlyDictionary<string, object?>(map);
    }

    private static Node ReadNode(nint v)
    {
        long labelCount = relate_value_list_len(v);
        var labels = new string[Math.Max(labelCount, 0)];
        for (long i = 0; i < labels.Length; i++)
        {
            labels[i] = ReadString(relate_value_list_get(v, i));
        }

        return new Node(
            Native.PtrToString(relate_value_element_id(v)) ?? string.Empty,
            Int(v, relate_value_id, "id"),
            Array.AsReadOnly(labels),
            ReadMap(v));
    }

    private static Relationship ReadRelationship(nint v) => new(
        Native.PtrToString(relate_value_element_id(v)) ?? string.Empty,
        Int(v, relate_value_id, "id"),
        ReadString(v),
        Native.PtrToString(relate_value_start_element_id(v)) ?? string.Empty,
        Int(v, relate_value_start_id, "start id"),
        Native.PtrToString(relate_value_end_element_id(v)) ?? string.Empty,
        Int(v, relate_value_end_id, "end id"),
        ReadMap(v));

    private static GraphPath ReadPath(nint v)
    {
        long count = relate_value_list_len(v);
        var nodes = new List<Node>((int)(count / 2) + 1);
        var rels = new List<Relationship>((int)(count / 2));
        for (long i = 0; i < count; i++)
        {
            switch (Read(relate_value_list_get(v, i)))
            {
                case Node n:
                    nodes.Add(n);
                    break;
                case Relationship r:
                    rels.Add(r);
                    break;
                default:
                    throw new RelateException("A path element was neither a node nor a relationship.");
            }
        }

        return new GraphPath(nodes.AsReadOnly(), rels.AsReadOnly());
    }

    private static CypherVector ReadVector(nint v)
    {
        string? dtype = Native.PtrToString(relate_value_vector_dtype(v));
        VectorElementType type = CypherVector.FromDType(dtype)
            ?? throw new RelateException($"Unknown vector coordinate type '{dtype}'.");
        long dim = Math.Max(relate_value_vector_dim(v), 0);
        if (CypherVector.IsIntegerType(type))
        {
            var values = new long[dim];
            for (long i = 0; i < dim; i++)
            {
                Ok(relate_value_vector_int(v, i, out values[i]), "vector");
            }

            return CypherVector.FromNative(type, null, values);
        }
        else
        {
            var values = new double[dim];
            for (long i = 0; i < dim; i++)
            {
                Ok(relate_value_vector_float(v, i, out values[i]), "vector");
            }

            return CypherVector.FromNative(type, values, null);
        }
    }

    private static DateOnly ToDate(nint v)
    {
        long y = Int(v, relate_value_year, "year");
        long m = Int(v, relate_value_month, "month");
        long d = Int(v, relate_value_day, "day");
        EnsureYearInRange(y);
        return new DateOnly((int)y, (int)m, (int)d);
    }

    private static TimeOnly ToTimeOnly(nint v) => new(TimeTicks(v));

    private static long TimeTicks(nint v)
    {
        long h = Int(v, relate_value_hour, "hour");
        long mi = Int(v, relate_value_minute, "minute");
        long s = Int(v, relate_value_second, "second");
        long ns = Int(v, relate_value_nanosecond, "nanosecond");
        return ((h * 60 + mi) * 60 + s) * TimeSpan.TicksPerSecond + ns / 100;
    }

    private static DateTime ToLocalDateTime(nint v) =>
        ToDate(v).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddTicks(TimeTicks(v));

    private static object ToDateTime(nint v)
    {
        DateTime wallClock = ToLocalDateTime(v);
        long offsetSeconds = Int(v, relate_value_offset_seconds, "offset");
        if (offsetSeconds % 60 != 0)
        {
            throw new OverflowException(
                $"The datetime offset of {offsetSeconds} seconds is not a whole number of minutes and cannot be represented by DateTimeOffset.");
        }

        var dto = new DateTimeOffset(wallClock, TimeSpan.FromSeconds(offsetSeconds));
        string? zone = Native.PtrToString(relate_value_zone(v));
        // Note: no ?: here - CypherZonedDateTime converts implicitly to DateTimeOffset and would lose its zone.
        if (string.IsNullOrEmpty(zone))
        {
            return dto;
        }

        return new CypherZonedDateTime(dto, zone);
    }

    private static void EnsureYearInRange(long year)
    {
        if (year is < 1 or > 9999)
        {
            throw new OverflowException($"The year {year} is outside the range supported by .NET date types (1-9999).");
        }
    }

    private delegate RelateStatus IntAccessor(nint v, out long value);

    private static long Int(nint v, IntAccessor accessor, string what)
    {
        Ok(accessor(v, out long value), what);
        return value;
    }

    private static void Ok(RelateStatus status, string what)
    {
        if (status != RelateStatus.Ok)
        {
            throw new RelateException($"relate refused to read the {what} component of a value.");
        }
    }
}
