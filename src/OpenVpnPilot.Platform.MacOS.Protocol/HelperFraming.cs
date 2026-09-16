using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenVpnPilot.Platform.MacOS.Protocol;

/// <summary>
/// Reads and writes helper messages: a four byte length in network order, then that many bytes of
/// UTF-8 JSON.
/// </summary>
/// <remarks>
/// A length prefix rather than a line per message, because a configuration is text with line breaks
/// of its own and escaping it into one line would be a second format to get wrong. The length is
/// checked before anything is allocated, so a caller cannot make the helper reserve memory by
/// announcing a large message it never sends.
/// </remarks>
public static class HelperFraming
{
    private const int HeaderBytes = 4;

    public static Task WriteRequestAsync(Stream stream, HelperRequest request, CancellationToken cancellationToken) =>
        WriteAsync(stream, request, HelperJsonContext.Default.HelperRequest, cancellationToken);

    public static Task WriteResponseAsync(Stream stream, HelperResponse response, CancellationToken cancellationToken) =>
        WriteAsync(stream, response, HelperJsonContext.Default.HelperResponse, cancellationToken);

    /// <returns>The request, or null when the other side closed the connection between messages.</returns>
    public static Task<HelperRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, HelperJsonContext.Default.HelperRequest, cancellationToken);

    /// <returns>The response, or null when the other side closed the connection between messages.</returns>
    public static Task<HelperResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, HelperJsonContext.Default.HelperResponse, cancellationToken);

    private static async Task WriteAsync<T>(
        Stream stream,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

        if (payload.Length > HelperProtocol.MaximumMessageBytes)
        {
            throw new HelperProtocolException(
                $"A message of {payload.Length} bytes exceeds the limit of {HelperProtocol.MaximumMessageBytes}.");
        }

        byte[] frame = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, HeaderBytes);

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderBytes];

        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length <= 0 || length > HelperProtocol.MaximumMessageBytes)
        {
            throw new HelperProtocolException($"A message announced {length} bytes, which is not a valid length.");
        }

        byte[] payload = new byte[length];

        if (!await ReadExactlyOrEndAsync(stream, payload, cancellationToken))
        {
            throw new HelperProtocolException("The connection closed in the middle of a message.");
        }

        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo)
                ?? throw new HelperProtocolException("A message carried nothing.");
        }
        catch (JsonException exception)
        {
            throw new HelperProtocolException("A message could not be read.", exception);
        }
    }

    /// <summary>
    /// Fills the buffer, or reports that the stream ended before the first byte.
    /// </summary>
    /// <remarks>
    /// An end after the first byte is a truncated message rather than a closed connection, and is
    /// reported as such by the caller.
    /// </remarks>
    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);

            if (read == 0)
            {
                if (filled == 0)
                {
                    return false;
                }

                throw new HelperProtocolException("The connection closed in the middle of a message.");
            }

            filled += read;
        }

        return true;
    }
}

/// <summary>
/// A message that does not follow the helper protocol.
/// </summary>
public sealed class HelperProtocolException : Exception
{
    public HelperProtocolException()
    {
    }

    public HelperProtocolException(string message)
        : base(message)
    {
    }

    public HelperProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
