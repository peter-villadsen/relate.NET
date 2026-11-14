using System.Text.Json;

namespace Relate.Net;

/// <summary>The engine stage at which a relate operation failed.</summary>
public enum ErrorStage
{
    /// <summary>The stage was not reported.</summary>
    Unknown = 0,

    /// <summary>The statement failed to parse, bind or plan (for example a syntax error).</summary>
    Compile = 1,

    /// <summary>The statement failed while executing (for example a division by zero).</summary>
    Runtime = 2,

    /// <summary>
    /// The failure happened outside the query engine: an invalid argument or handle, an IO or
    /// storage failure, or an internal engine error.
    /// </summary>
    Internal = 3,
}

/// <summary>
/// The exception thrown when relate reports a failure. It carries the Neo4j-compatible status
/// <see cref="Code"/>, the ISO GQL <see cref="GqlStatus"/>, the failing <see cref="Stage"/> and
/// whether the operation <see cref="IsRetryable">may be retried</see>.
/// </summary>
public class RelateException : Exception
{
    /// <summary>Creates an exception with a default message.</summary>
    public RelateException()
        : this("A relate operation failed.")
    {
    }

    /// <summary>Creates an exception with the given message.</summary>
    public RelateException(string message)
        : this(message, innerException: null)
    {
    }

    /// <summary>Creates an exception with the given message and inner exception.</summary>
    public RelateException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = string.Empty;
        GqlStatus = string.Empty;
    }

    /// <summary>Creates an exception with full relate error details.</summary>
    public RelateException(string message, string code, string gqlStatus, ErrorStage stage, bool isRetryable)
        : base(message)
    {
        Code = code ?? string.Empty;
        GqlStatus = gqlStatus ?? string.Empty;
        Stage = stage;
        IsRetryable = isRetryable;
    }

    /// <summary>The Neo4j-compatible status code, e.g. <c>Neo.ClientError.Statement.SyntaxError</c>.</summary>
    public string Code { get; }

    /// <summary>The ISO GQL status code, e.g. <c>42001</c>.</summary>
    public string GqlStatus { get; }

    /// <summary>The engine stage at which the failure happened.</summary>
    public ErrorStage Stage { get; }

    /// <summary><see langword="true"/> when retrying the same operation may succeed (for example a transient conflict).</summary>
    public bool IsRetryable { get; }

    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Code) ? base.ToString() : $"{GetType().FullName} [{Code} / {GqlStatus}]: {base.ToString()}";

    internal static RelateException FromErrorJson(string? json, string operation)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RelateException($"The relate operation '{operation}' failed without further detail.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string message = GetString(root, "message") ?? $"The relate operation '{operation}' failed.";
            string code = GetString(root, "code") ?? string.Empty;
            string gql = GetString(root, "gql_status") ?? string.Empty;
            ErrorStage stage = (GetString(root, "stage") ?? string.Empty) switch
            {
                "compile" => ErrorStage.Compile,
                "runtime" => ErrorStage.Runtime,
                "internal" => ErrorStage.Internal,
                _ => ErrorStage.Unknown,
            };
            bool retryable = root.TryGetProperty("retryable", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            return new RelateException(message, code, gql, stage, retryable);
        }
        catch (JsonException)
        {
            return new RelateException(json);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
