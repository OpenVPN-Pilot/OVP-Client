using System.Text;
using System.Threading.Channels;

namespace OpenVpnPilot.OpenVpn.Tests.Management;

/// <summary>
/// An in memory stand in for the management socket. The test drives the OpenVPN side of the
/// conversation, so the client can be exercised without a running process.
/// </summary>
internal sealed class FakeManagementStream : Stream
{
    private readonly Channel<byte[]> toClient =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<string> fromClient =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });

    private readonly StringBuilder clientBuffer = new();
    private ReadOnlyMemory<byte> remainder = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Queues raw bytes for the client to read. No newline is appended, so prompts can be simulated.
    /// </summary>
    public void SendRaw(string text) => toClient.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Queues a complete line for the client to read.
    /// </summary>
    public void SendLine(string line) => SendRaw(line + "\n");

    /// <summary>
    /// Waits for the next line the client wrote.
    /// </summary>
    public async Task<string> ReceiveLineAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(5));
        return await fromClient.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// True when the client has written a line that has not been consumed yet.
    /// </summary>
    public bool HasPendingClientLine => fromClient.Reader.Count > 0;

    public void CloseFromServer() => toClient.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (remainder.IsEmpty)
        {
            try
            {
                remainder = await toClient.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        int count = Math.Min(buffer.Length, remainder.Length);
        remainder[..count].CopyTo(buffer);
        remainder = remainder[count..];
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        clientBuffer.Append(Encoding.UTF8.GetString(buffer.Span));

        while (true)
        {
            string text = clientBuffer.ToString();
            int index = text.IndexOf('\n', StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            fromClient.Writer.TryWrite(text[..index].TrimEnd('\r'));
            clientBuffer.Remove(0, index + 1);
        }

        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // Nothing is buffered on the way out.
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
