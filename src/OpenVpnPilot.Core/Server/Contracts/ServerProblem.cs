using System.Text.Json;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// A refusal from the server, read from its RFC 9457 problem details.
/// </summary>
/// <param name="Code">
/// One of <see cref="ServerErrorCodes"/>. Null when the answer was not the server's problem
/// details at all, such as a proxy's error page in front of it.
/// </param>
/// <param name="Status">The HTTP status of the answer.</param>
/// <param name="Detail">The server's explanation. For display and logs only, never for branching.</param>
/// <param name="RequestId">The server's id of the request, from the body or else the header.</param>
/// <param name="Errors">Only for <see cref="ServerErrorCodes.ValidationFailed"/>: what was wrong per field.</param>
public sealed record ServerProblem(
    string? Code,
    int Status,
    string? Detail,
    string? RequestId,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors)
{
    /// <summary>
    /// The media type problem details travel as.
    /// </summary>
    public const string MediaType = "application/problem+json";

    /// <summary>
    /// Reads problem details as tolerantly as possible, because a refusal must never become an
    /// exception of its own.
    /// </summary>
    /// <param name="body">The answer's body, possibly empty or not JSON at all.</param>
    /// <param name="status">The answer's status.</param>
    /// <param name="headerRequestId">The request id the answer's header carried.</param>
    public static ServerProblem Read(string? body, int status, string? headerRequestId)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ServerProblem(null, status, null, headerRequestId, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ServerProblem(null, status, null, headerRequestId, null);
            }

            return new ServerProblem(
                StringOf(root, "code"),
                status,
                StringOf(root, "detail"),
                StringOf(root, "requestId") ?? headerRequestId,
                ErrorsOf(root));
        }
        catch (JsonException)
        {
            // Not the server's own shape, such as a proxy's page. Kept as a problem without a code.
            return new ServerProblem(null, status, null, headerRequestId, null);
        }
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, IReadOnlyList<string>>? ErrorsOf(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out JsonElement errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.Ordinal);

        foreach (JsonProperty field in errors.EnumerateObject())
        {
            List<string> messages = [];

            if (field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement message in field.Value.EnumerateArray())
                {
                    if (message.ValueKind == JsonValueKind.String && message.GetString() is { } text)
                    {
                        messages.Add(text);
                    }
                }
            }

            result[field.Name] = messages;
        }

        return result;
    }
}
