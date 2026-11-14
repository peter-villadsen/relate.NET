using System.Collections;
using System.Text.Json;
using Relate.Net.Interop;
using static Relate.Net.Interop.NativeMethods;

namespace Relate.Net;

/// <summary>
/// The fully materialized result of one statement: its records plus metadata (columns, counters,
/// bookmark, notifications, plan and timings). A result holds no native resources and stays
/// valid after its connection is closed.
/// </summary>
public sealed class QueryResult : IReadOnlyList<RelateRecord>
{
    private readonly RelateRecord[] _records;

    private QueryResult(
        IReadOnlyList<string> columns,
        RelateRecord[] records,
        QueryType queryType,
        string @namespace,
        QueryCounters counters,
        IReadOnlyList<Notification> notifications,
        string? planJson,
        Bookmark? bookmark,
        QueryTimings timings)
    {
        Columns = columns;
        _records = records;
        QueryType = queryType;
        Namespace = @namespace;
        Counters = counters;
        Notifications = notifications;
        PlanJson = planJson;
        Bookmark = bookmark;
        Timings = timings;
    }

    /// <summary>The column names, in order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The number of records.</summary>
    public int Count => _records.Length;

    /// <summary>Gets the record at <paramref name="index"/>.</summary>
    public RelateRecord this[int index] => _records[index];

    /// <summary>The statement type (read, write, read-write or schema).</summary>
    public QueryType QueryType { get; }

    /// <summary>The namespace the statement ran in (its <c>USE</c> target or the session namespace).</summary>
    public string Namespace { get; }

    /// <summary>The mutation counters.</summary>
    public QueryCounters Counters { get; }

    /// <summary>Notifications; populated by <c>EXPLAIN</c> and <c>PROFILE</c>.</summary>
    public IReadOnlyList<Notification> Notifications { get; }

    /// <summary>The structured plan as a <c>relate.plan/v1</c> JSON document; only for <c>EXPLAIN</c>/<c>PROFILE</c>.</summary>
    public string? PlanJson { get; }

    /// <summary>
    /// The bookmark of an auto-commit write; <see langword="null"/> for reads, statements inside an
    /// explicit transaction (see <see cref="RelateTransaction.Commit"/>) and writes that changed nothing.
    /// </summary>
    public Bookmark? Bookmark { get; }

    /// <summary>Engine-reported timings.</summary>
    public QueryTimings Timings { get; }

    /// <summary>
    /// Returns the first column of the first record converted to <typeparamref name="T"/>, e.g.
    /// for <c>MATCH (n) RETURN count(n)</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result has no records or no columns.</exception>
    public T Scalar<T>()
    {
        if (_records.Length == 0 || Columns.Count == 0)
        {
            throw new InvalidOperationException("The result has no records to read a scalar from.");
        }

        return _records[0].Get<T>(0);
    }

    /// <inheritdoc />
    public IEnumerator<RelateRecord> GetEnumerator() => ((IEnumerable<RelateRecord>)_records).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Reads everything from a native result and destroys it. Throws the statement's
    /// <see cref="RelateException"/> for an error result.
    /// </summary>
    internal static QueryResult FromNative(nint result)
    {
        try
        {
            if (relate_result_is_error(result) != 0)
            {
                throw RelateException.FromErrorJson(Native.PtrToString(relate_result_error_json(result)), "query");
            }

            long columnCount = relate_column_count(result);
            var columns = new string[Math.Max(columnCount, 0)];
            for (long c = 0; c < columns.Length; c++)
            {
                columns[c] = Native.PtrToString(relate_column_name(result, c)) ?? string.Empty;
            }

            var schema = new RecordSchema(columns);
            long rowCount = Math.Max(relate_row_count(result), 0);
            var records = new RelateRecord[rowCount];
            for (long r = 0; r < rowCount; r++)
            {
                var values = new object?[columns.Length];
                for (long c = 0; c < columns.Length; c++)
                {
                    values[c] = ValueReader.Read(relate_value_at(result, r, c));
                }

                records[r] = new RelateRecord(schema, values);
            }

            string? bookmark = Native.PtrToString(relate_result_bookmark(result));
            return new QueryResult(
                schema.Columns,
                records,
                ParseQueryType(Native.PtrToString(relate_result_query_type(result))),
                Native.PtrToString(relate_result_namespace(result)) ?? string.Empty,
                ParseCounters(Native.PtrToString(relate_result_stats(result))),
                ParseNotifications(Native.PtrToString(relate_result_notifications(result))),
                Native.PtrToString(relate_result_plan_json(result)),
                bookmark is null ? null : Bookmark.TryParse(bookmark, out Bookmark? b) ? b : null,
                new QueryTimings(
                    Ms(relate_result_elapsed_ms(result)),
                    Ms(relate_result_parse_ms(result)),
                    Ms(relate_result_plan_ms(result)),
                    Ms(relate_result_execute_ms(result))));
        }
        finally
        {
            relate_destroy_result(result);
        }
    }

    private static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)(ms * TimeSpan.TicksPerMillisecond));

    private static QueryType ParseQueryType(string? type) => type switch
    {
        "r" => QueryType.Read,
        "w" => QueryType.Write,
        "rw" => QueryType.ReadWrite,
        "s" => QueryType.Schema,
        _ => QueryType.Unknown,
    };

    private static QueryCounters ParseCounters(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return QueryCounters.Empty;
        }

        using var doc = JsonDocument.Parse(json);
        JsonElement e = doc.RootElement;
        return new QueryCounters
        {
            NodesCreated = Long(e, "nodes_created"),
            NodesDeleted = Long(e, "nodes_deleted"),
            RelationshipsCreated = Long(e, "relationships_created"),
            RelationshipsDeleted = Long(e, "relationships_deleted"),
            PropertiesSet = Long(e, "properties_set"),
            LabelsAdded = Long(e, "labels_added"),
            LabelsRemoved = Long(e, "labels_removed"),
            IndexesAdded = Long(e, "indexes_added"),
            IndexesRemoved = Long(e, "indexes_removed"),
            ConstraintsAdded = Long(e, "constraints_added"),
            ConstraintsRemoved = Long(e, "constraints_removed"),
        };
    }

    private static IReadOnlyList<Notification> ParseNotifications(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<Notification>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Notification>();
        }

        var list = new List<Notification>();
        foreach (JsonElement n in doc.RootElement.EnumerateArray())
        {
            list.Add(new Notification
            {
                Code = Str(n, "code"),
                GqlStatus = Str(n, "gql_status"),
                Severity = Str(n, "severity"),
                Category = Str(n, "category"),
                Title = Str(n, "title"),
                Description = Str(n, "description"),
                Remedy = Str(n, "remedy"),
                OperatorId = n.TryGetProperty("operator_id", out JsonElement op) && op.ValueKind == JsonValueKind.Number ? op.GetInt64() : null,
            });
        }

        return list.AsReadOnly();
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
