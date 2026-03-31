using System.Buffers.Binary;
using System.Net.Sockets;

namespace RagProxyCompat;

internal enum AppPacketType : ushort
{
    Handshake = 0,
    NumApps = 1,
    AppName = 2,
    AppVisibleName = 3,
    AppArgs = 4,
    PipeNameBank = 5,
    PipeNameOutput = 6,
    PipeNameEvents = 7,
    EndOutput = 8,
    WindowHandle = 9,
    PlatformInfo = 10,
    Ps3TargetAddress = 11,
    BuildConfig = 12,
}

internal enum RagProtocolVersion
{
    LegacyV0,
    LegacyVersioned,
    ModernV2,
}

internal enum RagPacketFormat
{
    Legacy8,
    Modern12,
}

internal sealed class RagPacket
{
    public const int LegacyHeaderSize = 8;
    public const int ModernHeaderSize = 12;
    public const int Int32Size = sizeof(int);
    public const int SingleSize = sizeof(float);
    public const float ModernVersion = 2.0f;
    public const float GtaIvVersion = 1.92f;

    public ushort Length { get; init; }
    public ushort Command { get; init; }
    public int Guid { get; init; }
    public uint Id { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public AppPacketType PacketType => (AppPacketType)Command;

    public static async Task<RagPacket?> ReadAsync(NetworkStream stream, RagPacketFormat format, CancellationToken cancellationToken)
    {
        int headerSize = GetHeaderSize(format);
        byte[]? header = await ReadExactOrNullAsync(stream, headerSize, cancellationToken);
        if (header is null)
        {
            return null;
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int guid = format == RagPacketFormat.Modern12
            ? BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4))
            : 0;
        uint id = format == RagPacketFormat.Modern12
            ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));

        byte[] payload = length == 0
            ? Array.Empty<byte>()
            : await ReadExactAsync(stream, length, cancellationToken);

        return new RagPacket
        {
            Length = length,
            Command = command,
            Guid = guid,
            Id = id,
            Payload = payload,
        };
    }

    public async Task WriteAsync(NetworkStream stream, RagPacketFormat format, CancellationToken cancellationToken)
    {
        byte[] buffer = ToByteArray(format);
        await stream.WriteAsync(buffer, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public byte[] ToByteArray(RagPacketFormat format)
    {
        int headerSize = GetHeaderSize(format);
        byte[] buffer = new byte[headerSize + Payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), (ushort)Payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), Command);
        if (format == RagPacketFormat.Modern12)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), Guid);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), Id);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), Id);
        }

        Payload.CopyTo(buffer, headerSize);
        return buffer;
    }

    public float ReadSingle()
    {
        if (Payload.Length < SingleSize)
        {
            return 0.0f;
        }

        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(0, SingleSize)));
    }

    public int ReadInt32()
    {
        if (Payload.Length < Int32Size)
        {
            throw new InvalidOperationException("Packet payload is shorter than 4 bytes.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(0, Int32Size));
    }

    public static RagPacket CreateHandshake(float version)
    {
        byte[] payload = new byte[SingleSize];
        BinaryPrimitives.WriteInt32LittleEndian(payload, BitConverter.SingleToInt32Bits(version));
        return new RagPacket
        {
            Command = (ushort)AppPacketType.Handshake,
            Payload = payload,
        };
    }

    public static RagPacket CreateHandshakeReply(int basePort, float? version)
    {
        byte[] payload = new byte[version.HasValue ? Int32Size + SingleSize : Int32Size];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, Int32Size), basePort);
        if (version.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(Int32Size, SingleSize),
                BitConverter.SingleToInt32Bits(version.Value));
        }

        return new RagPacket
        {
            Command = (ushort)AppPacketType.Handshake,
            Payload = payload,
        };
    }

    public RagPacket RewriteHandshakeVersion(float version)
    {
        byte[] payload = new byte[Math.Max(Payload.Length, SingleSize)];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, SingleSize), BitConverter.SingleToInt32Bits(version));

        if (Payload.Length > SingleSize)
        {
            Payload.AsSpan(SingleSize).CopyTo(payload.AsSpan(SingleSize));
        }

        return CloneWithPayload(payload);
    }

    public RagPacket RewriteHandshakeReply(float? version, int? basePortOverride = null)
    {
        if (Payload.Length < Int32Size)
        {
            throw new InvalidOperationException("Handshake reply payload is shorter than 4 bytes.");
        }

        int basePort = basePortOverride ?? ReadInt32();
        int trailingOffset = Payload.Length >= Int32Size + SingleSize ? Int32Size + SingleSize : Int32Size;
        ReadOnlySpan<byte> trailing = Payload.AsSpan(trailingOffset);
        byte[] payload = new byte[Int32Size + (version.HasValue ? SingleSize : 0) + trailing.Length];

        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, Int32Size), basePort);
        if (version.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(Int32Size, SingleSize),
                BitConverter.SingleToInt32Bits(version.Value));
            trailing.CopyTo(payload.AsSpan(Int32Size + SingleSize));
        }
        else
        {
            trailing.CopyTo(payload.AsSpan(Int32Size));
        }

        return CloneWithPayload(payload);
    }

    public override string ToString() => $"{PacketType} len={Payload.Length} guid=0x{Guid:X8} id={Id}";

    private RagPacket CloneWithPayload(byte[] payload)
    {
        return new RagPacket
        {
            Command = Command,
            Guid = Guid,
            Id = Id,
            Payload = payload,
        };
    }

    private static int GetHeaderSize(RagPacketFormat format)
    {
        return format == RagPacketFormat.Modern12 ? ModernHeaderSize : LegacyHeaderSize;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Socket closed while reading packet.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<byte[]?> ReadExactOrNullAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                return offset == 0 ? null : throw new EndOfStreamException("Socket closed while reading packet header.");
            }

            offset += read;
        }

        return buffer;
    }
}
