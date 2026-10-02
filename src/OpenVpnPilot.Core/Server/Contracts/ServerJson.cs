using System.Text.Json;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// How the contract's JSON is read and written.
/// </summary>
/// <remarks>
/// camelCase both ways, as the server writes it. Reading is strict about what the records promise:
/// a member the record declares as not nullable that arrives null, or a constructor parameter that
/// does not arrive at all, is refused instead of becoming a null nobody expects. A refused answer is
/// reported as one the client could not read, which is honest; a list that silently became null is
/// a crash somewhere else later.
/// </remarks>
public static class ServerJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
