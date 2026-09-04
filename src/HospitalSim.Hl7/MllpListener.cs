using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HospitalSim.Hl7;

/// <summary>
/// Minimal MLLP server: accepts connections, reads VT ... FS CR framed messages (possibly several per
/// connection), hands each to a handler, and writes back whatever the handler returns, MLLP-framed.
/// </summary>
public sealed class MllpListener(int port)
{
    private const byte StartBlock = 0x0B;
    private const byte EndBlock = 0x1C;
    private const byte CarriageReturn = 0x0D;

    public async Task RunAsync(Func<string, Task<string>> onMessage, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleClientAsync(client, onMessage, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleClientAsync(TcpClient client, Func<string, Task<string>> onMessage, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var buffer = new List<byte>();
            var readBuf = new byte[8192];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(readBuf, cancellationToken);
                    if (read == 0) break;
                    buffer.AddRange(readBuf.AsSpan(0, read).ToArray());

                    int startIdx;
                    while ((startIdx = buffer.IndexOf(StartBlock)) >= 0)
                    {
                        var endIdx = buffer.IndexOf(EndBlock, startIdx);
                        if (endIdx < 0) break; // frame incomplete, wait for more bytes

                        var body = buffer.GetRange(startIdx + 1, endIdx - startIdx - 1).ToArray();
                        var consumeTo = endIdx + 1 < buffer.Count && buffer[endIdx + 1] == CarriageReturn ? endIdx + 2 : endIdx + 1;
                        buffer.RemoveRange(0, consumeTo);

                        var response = await onMessage(Encoding.UTF8.GetString(body));

                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        var framed = new byte[responseBytes.Length + 3];
                        framed[0] = StartBlock;
                        Array.Copy(responseBytes, 0, framed, 1, responseBytes.Length);
                        framed[^2] = EndBlock;
                        framed[^1] = CarriageReturn;
                        await stream.WriteAsync(framed, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                }
            }
            catch (Exception)
            {
                // connection dropped mid-message - nothing to reconcile, just let it go
            }
        }
    }
}
