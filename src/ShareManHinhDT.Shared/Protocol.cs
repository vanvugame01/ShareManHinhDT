using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShareManHinhDT.Shared;

public enum MessageType : ushort
{
    Authenticate = 1,
    Accepted = 2,
    VideoConfiguration = 3,
    VideoFrame = 4,
    RequestKeyFrame = 5,
    Stop = 6,
    Ping = 7,
    Pong = 8,
    Error = 9
}

public sealed record WireMessage(MessageType Type, byte[] Payload, long TimestampUs = 0, uint Flags = 0)
{
    public const uint KeyFrameFlag = 1;
    public static WireMessage Json<T>(MessageType type, T value) => new(type, JsonSerializer.SerializeToUtf8Bytes(value, JsonType<T>()));
    public T ReadJson<T>() => JsonSerializer.Deserialize(Payload, JsonType<T>()) ?? throw new InvalidDataException("Dữ liệu JSON rỗng.");
    private static JsonTypeInfo<T> JsonType<T>() => (JsonTypeInfo<T>)(ProtocolJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException("Kiểu dữ liệu không thuộc giao thức v1."));
}

public sealed record Authentication(string Code, string DeviceName);
public sealed record SessionAccepted(string SessionId);
public sealed record SessionError(string Message);
public sealed record VideoConfiguration(int Width, int Height, int FrameRate, int BitRate, byte[] CodecData)
{
    public void Validate()
    {
        if (Width < 16 || Height < 16 || Width > 1280 || Height > 1280 ||
            Width % 2 != 0 || Height % 2 != 0 || (long)Width * Height > 1280 * 720 ||
            FrameRate is < 1 or > 30 || BitRate is < 100_000 or > 8_000_000 ||
            CodecData is not { Length: > 0 and <= 65536 } || !H264.IsAnnexB(CodecData))
            throw new InvalidDataException("Cấu hình video không hợp lệ.");
    }
}

public sealed class WireConnection(Stream stream) : IAsyncDisposable
{
    public const int HeaderSize = 24;
    public const int MaximumFrameSize = 4 * 1024 * 1024;
    public const int MaximumControlSize = 128 * 1024;
    private const uint Magic = 0x534D4454;
    private const ushort Version = 1;
    private readonly SemaphoreSlim writeLock = new(1);

    public async ValueTask SendAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        ValidateHeader(message.Type, message.Payload.Length, message.TimestampUs, message.Flags);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)message.Type);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), message.Payload.Length);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12), message.TimestampUs);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), message.Flags);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(message.Payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally { writeLock.Release(); }
    }

    public async ValueTask<WireMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) != Version)
            throw new InvalidDataException("Không tương thích giao thức ShareManHinhDT v1.");
        var type = (MessageType)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        long timestamp = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12));
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        ValidateHeader(type, length, timestamp, flags);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new(type, payload, timestamp, flags);
    }

    private static void ValidateHeader(MessageType type, int length, long timestamp, uint flags)
    {
        if (!Enum.IsDefined(type) || length < 0 || length > (type == MessageType.VideoFrame ? MaximumFrameSize : MaximumControlSize) ||
            timestamp < 0 || timestamp > long.MaxValue / 10 || (flags & ~WireMessage.KeyFrameFlag) != 0 ||
            (type != MessageType.VideoFrame && (timestamp != 0 || flags != 0)) ||
            (type == MessageType.VideoFrame && length == 0) ||
            (type is MessageType.RequestKeyFrame or MessageType.Stop or MessageType.Ping or MessageType.Pong && length != 0))
            throw new InvalidDataException("Thông điệp giao thức không hợp lệ.");
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        writeLock.Dispose();
    }
}

[JsonSerializable(typeof(Authentication))]
[JsonSerializable(typeof(SessionAccepted))]
[JsonSerializable(typeof(SessionError))]
[JsonSerializable(typeof(VideoConfiguration))]
internal partial class ProtocolJsonContext : JsonSerializerContext;

public static class H264
{
    public static bool IsAnnexB(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0 && data[1] == 0 &&
        (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    public static byte[] ToAnnexB(ReadOnlySpan<byte> data)
    {
        if (IsAnnexB(data)) return data.ToArray();
        using var output = new MemoryStream();
        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 4) throw new InvalidDataException("NAL H.264 bị thiếu.");
            int length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            if (length <= 0 || length > data.Length - offset) throw new InvalidDataException("Độ dài NAL H.264 không hợp lệ.");
            output.Write([0, 0, 0, 1]);
            output.Write(data.Slice(offset, length));
            offset += length;
        }
        return output.ToArray();
    }

    public static (int Width, int Height) Fit720p(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double scale = Math.Min(1d, Math.Min(1280d / Math.Max(width, height), 720d / Math.Min(width, height)));
        return (Math.Max(16, (int)(width * scale) / 2 * 2), Math.Max(16, (int)(height * scale) / 2 * 2));
    }
}
