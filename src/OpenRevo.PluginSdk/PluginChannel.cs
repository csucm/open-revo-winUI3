using System.Buffers.Binary;
using System.Text.Json;

namespace OpenRevo.PluginSdk;

/// <summary>
/// Bounded, length-prefixed JSON over an already authenticated duplex stream.
/// Owns the stream. It does not authenticate peers, launch processes or grant permissions.
/// Any interrupted/invalid frame poisons the connection; never resume a partial frame.
/// </summary>
public sealed class PluginChannel(Stream stream) : IAsyncDisposable
{
    private readonly Stream stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly SemaphoreSlim readGate = new(1, 1), writeGate = new(1, 1);
    private int closed;

    public async ValueTask SendAsync<T>(PluginMessageKind kind, long requestId, string method,
        T payload, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        var message = new PluginMessage(PluginProtocol.Major, kind, requestId, method,
            JsonSerializer.SerializeToElement(payload, PluginProtocol.Json));
        Validate(message);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, PluginProtocol.Json);
        if (bytes.Length > PluginProtocol.MaximumFrameBytes)
            throw new InvalidDataException("Plugin frame exceeds its byte limit.");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { Close(); throw; }
        finally { writeGate.Release(); }
    }

    public async ValueTask<PluginMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            byte[] prefix = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is < 1 or > PluginProtocol.MaximumFrameBytes)
                throw new InvalidDataException("Invalid plugin frame length.");
            byte[] bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var message = JsonSerializer.Deserialize<PluginMessage>(bytes, PluginProtocol.Json)
                ?? throw new InvalidDataException("Missing plugin message.");
            Validate(message);
            return message;
        }
        catch { Close(); throw; }
        finally { readGate.Release(); }
    }

    private static void Validate(PluginMessage message)
    {
        if (message.ApiMajor != PluginProtocol.Major)
            throw new InvalidDataException("Unsupported plugin API major.");
        if (!Enum.IsDefined(message.Kind) ||
            (message.Kind == PluginMessageKind.Event ? message.RequestId != 0 : message.RequestId <= 0))
            throw new InvalidDataException("Invalid plugin message kind or request ID.");
        if (message.Method is not { Length: >= 1 and <= 96 } ||
            message.Method.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("Invalid plugin method name.");
        if (message.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Plugin payload must be an object.");
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) stream.Dispose();
    }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
