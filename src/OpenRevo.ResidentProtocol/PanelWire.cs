using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

/// <summary>Frames on a previously verified local pipe, with one read owner.</summary>
public sealed class PanelWire(Stream pipe) : IDisposable
{
    private const int MaximumFrameBytes = 32 * 1024;
    private const int MaximumLargeFrameBytes = 1024 * 1024;
    private const int MaximumDiagnosticFrameBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 12,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int closed;

    public async Task<T> ReadAsync<T>(CancellationToken cancellationToken) where T : class
    {
        try
        {
            byte[] prefix = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            int limit = typeof(T) == typeof(PanelFrame) ? MaximumDiagnosticFrameBytes :
                typeof(T) == typeof(PanelRequest) ? MaximumLargeFrameBytes : MaximumFrameBytes;
            if (size < 1 || size > limit) throw new InvalidDataException("面板消息长度不合法。");
            byte[] frame = new byte[size];
            await pipe.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
            return DecodeFrame<T>(frame);
        }
        catch { Dispose(); throw; }
    }

    public static int PayloadBytes(PanelRequest request) => request switch
    {
        { Desktop: PanelImportIcc { Valid: true } import } => import.Bytes,
        { Tuning: { Action: PanelTuningAction.ImportPreset, Valid: true, Bytes: int bytes } } => bytes,
        _ => 0
    };

    public async Task WriteAsync<T>(T message, CancellationToken cancellationToken, byte[]? payload = null)
    {
        int bytes = message is PanelRequest request ? PayloadBytes(request) : 0;
        if (bytes == 0 ? payload != null : payload?.Length != bytes)
            throw new InvalidDataException("面板文件上传长度不匹配。");
        byte[] frame = EncodeFrame(message);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, frame.Length);
            await pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            if (payload != null) await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { Dispose(); throw; }
        finally { writeGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) pipe.Dispose();
    }

    public static T DecodeFrame<T>(ReadOnlySpan<byte> frame) where T : class
    {
        T value = JsonSerializer.Deserialize<T>(frame, Json) ?? throw new InvalidDataException("面板消息为空。");
        if (frame.Length > FrameLimit(value)) throw new InvalidDataException("面板消息长度不合法。");
        return value;
    }

    public static byte[] EncodeFrame<T>(T message)
    {
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (frame.Length > FrameLimit(message)) throw new InvalidDataException("面板消息过长。");
        return frame;
    }

    private static int FrameLimit<T>(T message) =>
        message is PanelFrame { Kind: PanelFrameKind.Result, Maintenance.Report.Length: > 0 }
            ? MaximumDiagnosticFrameBytes :
        message is PanelFrame { Kind: PanelFrameKind.Result, Desktop: not null } or
            PanelFrame { Kind: PanelFrameKind.Result, Plugins: not null } or
            PanelFrame { Kind: PanelFrameKind.Result, Tuning: not null } or
            PanelFrame { Kind: PanelFrameKind.Attached, Draft.Tuning.Valid: true } or
            PanelRequest { Operation: PanelOperation.Detach, Draft.Tuning.Valid: true }
            ? MaximumLargeFrameBytes : MaximumFrameBytes;
}
