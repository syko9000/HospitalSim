using System.Net.Sockets;
using System.Text;

namespace HospitalSim.Hl7;

/// <summary>
/// Minimal MLLP (Minimal Lower Layer Protocol) client: frames a message with VT ... FS CR and waits
/// for an ACK on the same connection. Keeps the TCP connection open across calls instead of
/// reconnecting for every message - connects lazily on first use, and reconnects only if the
/// connection turns out to be dead (write/read failure, or the remote side closing it, including an
/// idle timeout on the receiver's end), retrying that one send once on the fresh connection.
/// </summary>
public sealed class MllpClient(string host, int port) : IDisposable
{
    private const byte StartBlock = 0x0B;
    private const byte EndBlock = 0x1C;
    private const byte CarriageReturn = 0x0D;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public async Task<string> SendAsync(string hl7Message, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendOnCurrentConnectionAsync(hl7Message, cancellationToken);
        }
        catch (Exception) when (_client is not null)
        {
            Reset();
            return await SendOnCurrentConnectionAsync(hl7Message, cancellationToken);
        }
    }

    private async Task<string> SendOnCurrentConnectionAsync(string hl7Message, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            _client = client;
            _stream = client.GetStream();
        }

        var stream = _stream!;
        var body = Encoding.UTF8.GetBytes(hl7Message);
        var framed = new byte[body.Length + 3];
        framed[0] = StartBlock;
        Array.Copy(body, 0, framed, 1, body.Length);
        framed[^2] = EndBlock;
        framed[^1] = CarriageReturn;

        await stream.WriteAsync(framed, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
            throw new IOException("MLLP connection closed by remote host.");
        }
        return Encoding.UTF8.GetString(buffer, 0, read).Trim([(char)StartBlock, (char)EndBlock, (char)CarriageReturn]);
    }

    private void Reset()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose() => Reset();
}
