using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RagProxyCompat;

internal sealed class BankIngressNormalizer
{
    private readonly BankPayloadNormalizer _normalizer;

    public BankIngressNormalizer(Action<string>? alert = null)
    {
        _normalizer = new BankPayloadNormalizer(BankPayloadDirection.GameToRag, alert);
    }

    public IReadOnlyList<byte[]> ProcessChunk(ReadOnlySpan<byte> chunk) => _normalizer.ProcessChunk(chunk);
}

internal sealed class BankEgressNormalizer
{
    private readonly BankPayloadNormalizer _normalizer;

    public BankEgressNormalizer(Action<string>? alert = null)
    {
        _normalizer = new BankPayloadNormalizer(BankPayloadDirection.RagToGame, alert);
    }

    public IReadOnlyList<byte[]> ProcessChunk(ReadOnlySpan<byte> chunk) => _normalizer.ProcessChunk(chunk);
}

internal sealed class BankPayloadNormalizer
{
    private const int CompressionHeaderShortSize = 9;
    private const int CompressionHeaderLongSize = 14;
    private const int LegacyPacketHeaderSize = 8;
    private const int ModernPacketHeaderSize = 12;
    private const ushort MaxRemotePacketCommand = 16 * 1024;
    private const ushort BankCommandCreate = 0;
    private const ushort BankCommandChanged = 2;
    private const ushort BankCommandLegacyComboCreate = 4;
    private const ushort BankCommandUser0 = 5;
    private const ushort BankCommandUser2 = 7;
    private const ushort BankCommandUser5 = 10;
    private const int WidgetTextStringType = 4;
    private static readonly byte[] DefaultWidgetColorField = EncodeLegacyString("ARGBColor:0:0:0:0");
    private static readonly Dictionary<string, WidgetGroup> WidgetGroupByPrefix = new(StringComparer.Ordinal)
    {
        ["bmgr"] = WidgetGroup.BankManager,
        ["bank"] = WidgetGroup.BankOrGroup,
        ["grup"] = WidgetGroup.BankOrGroup,
        ["text"] = WidgetGroup.Text,
        ["vclr"] = WidgetGroup.Color,
        ["butn"] = WidgetGroup.Button,
        ["tgbo"] = WidgetGroup.Button,
        ["tgs3"] = WidgetGroup.Button,
        ["tgu8"] = WidgetGroup.Button,
        ["tgu1"] = WidgetGroup.Button,
        ["tgu3"] = WidgetGroup.Button,
        ["tgbs"] = WidgetGroup.Button,
        ["tgfb"] = WidgetGroup.Button,
        ["tgfl"] = WidgetGroup.Button,
        ["slfl"] = WidgetGroup.Slider,
        ["slu8"] = WidgetGroup.Slider,
        ["sls8"] = WidgetGroup.Slider,
        ["slu1"] = WidgetGroup.Slider,
        ["sls1"] = WidgetGroup.Slider,
        ["slu3"] = WidgetGroup.Slider,
        ["sls3"] = WidgetGroup.Slider,
        ["cou8"] = WidgetGroup.Combo,
        ["cos8"] = WidgetGroup.Combo,
        ["cou1"] = WidgetGroup.Combo,
        ["cos1"] = WidgetGroup.Combo,
        ["cou3"] = WidgetGroup.Combo,
        ["cos3"] = WidgetGroup.Combo,
    };

    private readonly BankPayloadDirection _direction;
    private readonly Action<string>? _alert;
    private byte[] _streamBuffer = Array.Empty<byte>();
    private int _streamCount;
    private BankIngressMode _mode;
    private uint _bankManagerId;
    private bool _startupCompatPacketsInjected;
    private readonly Queue<byte[]> _pendingOutputs = new();
    private readonly HashSet<string> _unknownWidgetGuids = new(StringComparer.Ordinal);

    public BankPayloadNormalizer(BankPayloadDirection direction, Action<string>? alert)
    {
        _direction = direction;
        _alert = alert;
    }

    public IReadOnlyList<byte[]> ProcessChunk(ReadOnlySpan<byte> chunk)
    {
        if (!chunk.IsEmpty)
        {
            Append(ref _streamBuffer, ref _streamCount, chunk);
        }

        List<byte[]> forwarded = [];
        while (TryReadNextOutput(out byte[] output))
        {
            if (output.Length > 0)
            {
                forwarded.Add(output);
            }
        }

        return forwarded;
    }

    private bool TryReadNextOutput(out byte[] output)
    {
        if (_pendingOutputs.Count > 0)
        {
            output = _pendingOutputs.Dequeue();
            return true;
        }

        output = Array.Empty<byte>();

    Restart:
        if (_streamCount == 0)
        {
            return false;
        }

        if (_mode == BankIngressMode.Unknown && !TryDetermineMode())
        {
            return false;
        }

        switch (_mode)
        {
            case BankIngressMode.Framed:
                {
                    CompressionParseStatus status = TryParseFramedSegment(_streamBuffer.AsSpan(0, _streamCount), out int consumed);
                    switch (status)
                    {
                        case CompressionParseStatus.Success:
                            byte[] frame = Consume(ref _streamBuffer, ref _streamCount, consumed);
                            IReadOnlyList<byte[]> translatedFrames = TranslateFramedSegment(frame);
                            EnqueueOutputs(translatedFrames);
                            if (_pendingOutputs.Count > 0)
                            {
                                output = _pendingOutputs.Dequeue();
                            }
                            return true;

                        case CompressionParseStatus.NeedMoreData:
                            return false;

                        case CompressionParseStatus.InvalidData:
                            if (!TryResyncFrameBuffer())
                            {
                                return false;
                            }

                            goto Restart;

                        default:
                            throw new UnreachableException();
                    }
                }

            case BankIngressMode.RawLegacyPackets:
            case BankIngressMode.RawModernPackets:
                if (!TryDrainCompletePackets(ref _streamBuffer, ref _streamCount, GetPacketStreamFormat(_mode), out byte[] completePackets))
                {
                    return false;
                }

                IReadOnlyList<byte[]> translatedPackets = TranslatePacketStreamToOutputs(completePackets, GetPacketStreamFormat(_mode));
                EnqueueOutputs(translatedPackets);
                if (_pendingOutputs.Count > 0)
                {
                    output = _pendingOutputs.Dequeue();
                }
                return true;

            default:
                throw new UnreachableException();
        }
    }

    private void EnqueueOutputs(IReadOnlyList<byte[]> outputs)
    {
        for (int i = 0; i < outputs.Count; i++)
        {
            byte[] output = outputs[i];
            if (output.Length > 0)
            {
                _pendingOutputs.Enqueue(output);
            }
        }
    }

    private bool TryDetermineMode()
    {
        while (_streamCount > 0)
        {
            ReadOnlySpan<byte> source = _streamBuffer.AsSpan(0, _streamCount);
            if (_streamCount >= 4 && IsFrameMarker(source[..4]))
            {
                _mode = BankIngressMode.Framed;
                return true;
            }

            PacketStreamFormat packetFormat = DetectPacketStreamFormat(source);
            if (packetFormat != PacketStreamFormat.Unknown)
            {
                _mode = packetFormat == PacketStreamFormat.Legacy8
                    ? BankIngressMode.RawLegacyPackets
                    : BankIngressMode.RawModernPackets;
                return true;
            }

            int nextFrameStart = FindNextFrameStart(source.Slice(1));
            if (nextFrameStart >= 0)
            {
                ConsumeInPlace(ref _streamBuffer, ref _streamCount, nextFrameStart + 1);
                _mode = BankIngressMode.Framed;
                return true;
            }

            int trailingFramePrefix = GetTrailingFramePrefixLength(source);
            if (trailingFramePrefix > 0)
            {
                int discard = _streamCount - trailingFramePrefix;
                if (discard > 0)
                {
                    ConsumeInPlace(ref _streamBuffer, ref _streamCount, discard);
                }

                return false;
            }

            if (_streamCount < LegacyPacketHeaderSize)
            {
                return false;
            }

            ConsumeInPlace(ref _streamBuffer, ref _streamCount, 1);
        }

        return false;
    }

    private static PacketStreamFormat GetPacketStreamFormat(BankIngressMode mode)
    {
        return mode switch
        {
            BankIngressMode.RawLegacyPackets => PacketStreamFormat.Legacy8,
            BankIngressMode.RawModernPackets => PacketStreamFormat.Modern12,
            _ => PacketStreamFormat.Unknown,
        };
    }

    private IReadOnlyList<byte[]> TranslateFramedSegment(byte[] frame)
    {
        ReadOnlySpan<byte> source = frame;
        CompressionKind kind = DetectCompressionKind(source[..4]);
        int compressedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, sizeof(uint))));
        int headerSize = kind == CompressionKind.None ? CompressionHeaderShortSize : CompressionHeaderLongSize;

        byte[] payload = kind switch
        {
            CompressionKind.None => source.Slice(headerSize, compressedSize).ToArray(),
            CompressionKind.Dat => DecompressDat(
                source.Slice(headerSize, compressedSize).ToArray(),
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(9, sizeof(uint))))),
            CompressionKind.Fi => DecompressFi(
                source.Slice(headerSize, compressedSize).ToArray(),
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(9, sizeof(uint))))),
            _ => throw new UnreachableException(),
        };

        PacketStreamFormat packetFormat = DetectPacketStreamFormat(payload);
        return packetFormat == PacketStreamFormat.Unknown
            ? [frame]
            : TranslatePacketStreamToOutputs(payload, packetFormat);
    }

    private IReadOnlyList<byte[]> TranslatePacketStreamToOutputs(ReadOnlySpan<byte> rawPackets, PacketStreamFormat inputFormat)
    {
        byte[] translatedStream = TranslatePacketStream(rawPackets, inputFormat);
        if (translatedStream.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        PacketStreamFormat outputFormat = _direction == BankPayloadDirection.GameToRag
            ? PacketStreamFormat.Modern12
            : PacketStreamFormat.Legacy8;

        List<byte[]> packets = SplitPacketStream(translatedStream, outputFormat);
        if (_direction != BankPayloadDirection.GameToRag)
        {
            return packets;
        }

        for (int i = 0; i < packets.Count; i++)
        {
            packets[i] = WrapNoCompression(packets[i]);
        }

        return packets;
    }

    private byte[] TranslatePacketStream(ReadOnlySpan<byte> rawPackets, PacketStreamFormat inputFormat)
    {
        PacketStreamFormat outputFormat = _direction == BankPayloadDirection.GameToRag
            ? PacketStreamFormat.Modern12
            : PacketStreamFormat.Legacy8;

        List<byte> translated = new(rawPackets.Length + 256);
        Span<byte> legacyIdPrefix = stackalloc byte[sizeof(uint)];
        int offset = 0;
        int headerSize = inputFormat == PacketStreamFormat.Legacy8 ? LegacyPacketHeaderSize : ModernPacketHeaderSize;
        while (offset + headerSize <= rawPackets.Length)
        {
            if (!TryReadPacketHeader(rawPackets.Slice(offset), inputFormat, out BankPacketHeader header))
            {
                throw new InvalidOperationException($"Invalid bank packet stream while translating {_direction}.");
            }

            ReadOnlySpan<byte> payload = rawPackets.Slice(offset + headerSize, header.PayloadLength);
            if (outputFormat == PacketStreamFormat.Modern12)
            {
                ushort modernCommand = header.Command;
                uint id = 0;
                ReadOnlySpan<byte> modernPayload = payload;
                if (inputFormat == PacketStreamFormat.Legacy8)
                {
                    modernCommand = TranslateLegacyCommand(header.Command, header.Guid);
                    if (LegacyPacketHasIdPrefix(header.Command, header.Guid, payload))
                    {
                        id = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
                        ReadOnlySpan<byte> legacyPayload = payload.Slice(sizeof(uint));
                        if (TryAppendTranslatedLegacyCreatePackets(translated, modernCommand, header.Guid, id, legacyPayload))
                        {
                            offset += header.TotalLength;
                            continue;
                        }

                        modernPayload = TransformLegacyCreatePayload(modernCommand, header.Guid, legacyPayload);
                    }
                }

                AppendModernPacket(translated, modernCommand, header.Guid, id, modernPayload);
                MaybeAppendCompatStartupPackets(translated, modernCommand, header.Guid, id);
            }
            else
            {
                ReadOnlySpan<byte> legacyPayload = payload;
                if (inputFormat == PacketStreamFormat.Modern12)
                {
                    if (ShouldDropModernToLegacyPacket(header.Command, header.Guid, header.Id, payload))
                    {
                        offset += header.TotalLength;
                        continue;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(legacyIdPrefix, header.Id);
                    AppendLegacyPacket(translated, header.Command, header.Guid, legacyIdPrefix, legacyPayload);
                }
                else
                {
                    AppendLegacyPacket(translated, header.Command, header.Guid, ReadOnlySpan<byte>.Empty, legacyPayload);
                }
            }

            offset += header.TotalLength;
        }

        return translated.ToArray();
    }

    private void MaybeAppendCompatStartupPackets(List<byte> destination, ushort modernCommand, int guid, uint id)
    {
        if (_direction != BankPayloadDirection.GameToRag || guid != ComputeGuid("bmgr"))
        {
            return;
        }

        if (modernCommand == BankCommandCreate && id != 0)
        {
            _bankManagerId = id;
            if (!_startupCompatPacketsInjected)
            {
                AppendModernPacket(destination, BankCommandUser2, guid, _bankManagerId, ReadOnlySpan<byte>.Empty);
                _startupCompatPacketsInjected = true;
            }

            return;
        }

        if (_startupCompatPacketsInjected)
        {
            return;
        }

        if (modernCommand == BankCommandUser2)
        {
            if (id != 0)
            {
                _bankManagerId = id;
            }

            if (_bankManagerId != 0)
            {
                _startupCompatPacketsInjected = true;
            }

            return;
        }

        if (modernCommand == BankCommandUser5 && _bankManagerId != 0)
        {
            AppendModernPacket(destination, BankCommandUser2, guid, _bankManagerId, ReadOnlySpan<byte>.Empty);
            _startupCompatPacketsInjected = true;
        }
    }

    private bool TryAppendTranslatedLegacyCreatePackets(
        List<byte> destination,
        ushort command,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        WidgetGroup group = ResolveWidgetGroup(guid, command, "legacy->modern create");
        switch (group)
        {
            case WidgetGroup.Text:
                if (command != BankCommandCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacyTextCreate(destination, command, guid, id, payload);

            case WidgetGroup.Color:
                if (command != BankCommandCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacyColorCreate(destination, guid, id, payload);

            case WidgetGroup.BankOrGroup:
                if (command != BankCommandCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacyBankOrGroupCreate(destination, guid, id, payload);

            case WidgetGroup.Slider:
                if (command != BankCommandCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacySimpleCreate(destination, command, guid, id, payload, appendExponentialFlag: true);

            case WidgetGroup.Combo:
                if (command != BankCommandCreate && command != BankCommandLegacyComboCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacyComboCreate(destination, guid, id, payload);

            case WidgetGroup.Button:
                if (command != BankCommandCreate)
                {
                    return false;
                }
                return TryAppendTranslatedLegacySimpleCreate(destination, command, guid, id, payload, appendExponentialFlag: false);

            default:
                return false;
        }
    }

    private static bool TryAppendTranslatedLegacyTextCreate(
        List<byte> destination,
        ushort command,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload)
    {
        if (guid != ComputeGuid("text") || payload.Length < sizeof(uint))
        {
            return false;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return false;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return false;
        }

        int valueOffset = memoOffset + memoExtent;
        if (!TryGetLegacyString(payload.Slice(valueOffset), out string? value, out int valueExtent))
        {
            return false;
        }

        int stringSizeOffset = valueOffset + valueExtent;
        if (payload.Length < stringSizeOffset + sizeof(uint))
        {
            return false;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        uint stringSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(stringSizeOffset, sizeof(uint)));

        List<byte> createPayload = new(payload.Length + 32);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        BinaryPrimitives.WriteInt32LittleEndian(word, WidgetTextStringType);
        AddBytes(createPayload, word);
        BinaryPrimitives.WriteUInt32LittleEndian(word, stringSize);
        AddBytes(createPayload, word);
        AppendModernPacket(destination, BankCommandCreate, guid, id, createPayload.ToArray());

        if (!string.IsNullOrEmpty(value))
        {
            byte[] valueBytes = System.Text.Encoding.ASCII.GetBytes(value);
            int count = Math.Min(valueBytes.Length, checked((int)stringSize));
            if (count > 0)
            {
                List<byte> changedPayload = new(6 + count);
                Span<byte> shortWord = stackalloc byte[sizeof(ushort)];
                BinaryPrimitives.WriteUInt16LittleEndian(shortWord, (ushort)count);
                AddBytes(changedPayload, shortWord);
                BinaryPrimitives.WriteUInt16LittleEndian(shortWord, 0);
                AddBytes(changedPayload, shortWord);
                BinaryPrimitives.WriteInt16LittleEndian(shortWord, 0);
                AddBytes(changedPayload, shortWord);
                AddBytes(changedPayload, valueBytes.AsSpan(0, count));
                AppendModernPacket(destination, BankCommandChanged, guid, id, changedPayload.ToArray());
            }
        }

        return true;
    }

    private static bool TryAppendTranslatedLegacyColorCreate(
        List<byte> destination,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return false;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return false;
        }

        int typeOffset = memoOffset + memoExtent;
        if (payload.Length < typeOffset + sizeof(uint) + (sizeof(float) * 4))
        {
            return false;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> colorTail = payload.Slice(typeOffset);

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        AddBytes(createPayload, colorTail);
        AppendModernPacket(destination, BankCommandCreate, guid, id, createPayload.ToArray());
        return true;
    }

    private static bool TryAppendTranslatedLegacyBankOrGroupCreate(
        List<byte> destination,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return false;
        }

        int openOffset = sizeof(uint) + titleExtent;
        int openValue = 0;
        if (payload.Length >= openOffset + sizeof(int))
        {
            openValue = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(openOffset, sizeof(int)));
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        BinaryPrimitives.WriteInt32LittleEndian(word, openValue);
        AddBytes(createPayload, word);
        AppendModernPacket(destination, BankCommandCreate, guid, id, createPayload.ToArray());
        return true;
    }

    private bool TryAppendTranslatedLegacySimpleCreate(
        List<byte> destination,
        ushort command,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload,
        bool appendExponentialFlag)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return false;
        }

        int tailOffset = sizeof(uint) + titleExtent;
        if (tailOffset > payload.Length)
        {
            return false;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> legacyTail = payload.Slice(tailOffset);
        string memo = string.Empty;
        if (TrySplitOptionalLegacyMemo(legacyTail, out string? parsedMemo, out ReadOnlySpan<byte> memoTrimmedTail))
        {
            memo = parsedMemo ?? string.Empty;
            legacyTail = memoTrimmedTail;
        }

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        ReadOnlySpan<byte> numericTail = legacyTail;
        byte? exponentialFlag = null;
        if (appendExponentialFlag)
        {
            int expectedNumericLength = GetLegacySliderNumericLength(guid);
            numericTail = ExtractLegacySliderExponentialFlag(legacyTail, expectedNumericLength, out exponentialFlag);
            LogSliderTranslation("legacy->modern create", guid, legacyTail, numericTail, exponentialFlag, expectedNumericLength);
        }

        AddBytes(createPayload, numericTail);
        if (appendExponentialFlag)
        {
            if (exponentialFlag.HasValue)
            {
                createPayload.Add(exponentialFlag.Value);
            }
            else if (RequiresAppendedLegacySliderExponentialFlag(legacyTail))
            {
                createPayload.Add(0); // exponential = false
            }
        }

        AppendModernPacket(destination, BankCommandCreate, guid, id, createPayload.ToArray());
        return true;
    }

    private static bool TryAppendTranslatedLegacyComboCreate(
        List<byte> destination,
        int guid,
        uint id,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return false;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return false;
        }

        int numericOffset = memoOffset + memoExtent;
        if (payload.Length < numericOffset + (sizeof(int) * 3))
        {
            return false;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> numericTail = payload.Slice(numericOffset);

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        AddBytes(createPayload, numericTail);
        AppendModernPacket(destination, BankCommandCreate, guid, id, createPayload.ToArray());
        return true;
    }

    private bool TryDrainCompletePackets(ref byte[] buffer, ref int count, PacketStreamFormat format, out byte[] completePackets)
    {
        completePackets = Array.Empty<byte>();
        int headerSize = format == PacketStreamFormat.Legacy8 ? LegacyPacketHeaderSize : ModernPacketHeaderSize;

    Restart:
        if (count < headerSize)
        {
            return false;
        }

        int offset = 0;
        while (offset + headerSize <= count)
        {
            if (!TryReadPacketHeader(buffer.AsSpan(offset, count - offset), format, out BankPacketHeader header))
            {
                if (offset == 0)
                {
                    ConsumeInPlace(ref buffer, ref count, 1);
                    goto Restart;
                }

                break;
            }

            if (offset + header.TotalLength > count)
            {
                break;
            }

            offset += header.TotalLength;
        }

        if (offset == 0)
        {
            return false;
        }

        completePackets = Consume(ref buffer, ref count, offset);
        return true;
    }

    private bool TryResyncFrameBuffer()
    {
        if (_streamCount == 0)
        {
            return false;
        }

        int nextStart = FindNextFrameStart(_streamBuffer.AsSpan(1, _streamCount - 1));
        if (nextStart >= 0)
        {
            ConsumeInPlace(ref _streamBuffer, ref _streamCount, nextStart + 1);
            return true;
        }

        int preserve = GetTrailingFramePrefixLength(_streamBuffer.AsSpan(0, _streamCount));
        int discard = _streamCount - preserve;
        if (discard <= 0)
        {
            return false;
        }

        ConsumeInPlace(ref _streamBuffer, ref _streamCount, discard);
        return true;
    }

    private static CompressionParseStatus TryParseFramedSegment(ReadOnlySpan<byte> source, out int consumed)
    {
        consumed = 0;
        if (source.Length < 4)
        {
            return CompressionParseStatus.NeedMoreData;
        }

        CompressionKind kind = DetectCompressionKind(source[..4]);
        if (kind == CompressionKind.Unknown)
        {
            return CompressionParseStatus.InvalidData;
        }

        int headerSize = kind == CompressionKind.None ? CompressionHeaderShortSize : CompressionHeaderLongSize;
        if (source.Length < headerSize)
        {
            return CompressionParseStatus.NeedMoreData;
        }

        uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, sizeof(uint)));
        if (source[8] != (byte)':')
        {
            return CompressionParseStatus.InvalidData;
        }

        if (kind != CompressionKind.None && source[13] != (byte)':')
        {
            return CompressionParseStatus.InvalidData;
        }

        long totalLength = headerSize + compressedSize;
        if (totalLength > int.MaxValue)
        {
            return CompressionParseStatus.InvalidData;
        }

        if (source.Length < totalLength)
        {
            return CompressionParseStatus.NeedMoreData;
        }

        consumed = (int)totalLength;
        return CompressionParseStatus.Success;
    }

    private static PacketStreamFormat DetectPacketStreamFormat(ReadOnlySpan<byte> source)
    {
        if (CanParseEntirePacketStream(source, PacketStreamFormat.Legacy8))
        {
            return PacketStreamFormat.Legacy8;
        }

        if (CanParseEntirePacketStream(source, PacketStreamFormat.Modern12))
        {
            return PacketStreamFormat.Modern12;
        }

        return PacketStreamFormat.Unknown;
    }

    private static bool CanParseEntirePacketStream(ReadOnlySpan<byte> source, PacketStreamFormat format)
    {
        int offset = 0;
        int headerSize = format == PacketStreamFormat.Legacy8 ? LegacyPacketHeaderSize : ModernPacketHeaderSize;
        if (source.Length < headerSize)
        {
            return false;
        }

        while (offset < source.Length)
        {
            if (offset + headerSize > source.Length || !TryReadPacketHeader(source.Slice(offset), format, out BankPacketHeader header))
            {
                return false;
            }

            offset += header.TotalLength;
        }

        return offset == source.Length;
    }

    private static bool TryReadPacketHeader(ReadOnlySpan<byte> source, PacketStreamFormat format, out BankPacketHeader header, bool requireFourCc = true)
    {
        header = default;
        int headerSize = format == PacketStreamFormat.Legacy8 ? LegacyPacketHeaderSize : ModernPacketHeaderSize;
        if (source.Length < headerSize)
        {
            return false;
        }

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(0, sizeof(ushort)));
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2, sizeof(ushort)));
        if (command > MaxRemotePacketCommand)
        {
            return false;
        }

        int totalLength = headerSize + payloadLength;
        if (totalLength > source.Length)
        {
            return false;
        }

        int guid = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, sizeof(int)));
        uint id = format == PacketStreamFormat.Modern12
            ? BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, sizeof(uint)))
            : 0;

        if (requireFourCc && format == PacketStreamFormat.Modern12 && !LooksLikeFourCc(source.Slice(4, sizeof(int))))
        {
            return false;
        }

        header = new BankPacketHeader(payloadLength, command, guid, id, totalLength);
        return true;
    }

    private static List<byte[]> SplitPacketStream(ReadOnlySpan<byte> stream, PacketStreamFormat format)
    {
        List<byte[]> packets = [];
        if (stream.IsEmpty)
        {
            return packets;
        }

        int offset = 0;
        int headerSize = format == PacketStreamFormat.Legacy8 ? LegacyPacketHeaderSize : ModernPacketHeaderSize;
        while (offset + headerSize <= stream.Length)
        {
            if (!TryReadPacketHeader(stream.Slice(offset), format, out BankPacketHeader header, requireFourCc: false))
            {
                throw new InvalidOperationException("Translated bank packet stream is invalid.");
            }

            int totalLength = header.TotalLength;
            packets.Add(stream.Slice(offset, totalLength).ToArray());
            offset += totalLength;
        }

        if (offset != stream.Length)
        {
            throw new InvalidOperationException("Translated bank packet stream is truncated.");
        }

        return packets;
    }

    private WidgetGroup ResolveWidgetGroup(int guid, ushort command, string context)
    {
        if (TryGetWidgetGroup(guid, out WidgetGroup group, out _))
        {
            return group;
        }

        MaybeAlertUnknownWidget(guid, command, context);
        return WidgetGroup.Unknown;
    }

    private void MaybeAlertUnknownWidget(int guid, ushort command, string context)
    {
        string label = FormatGuidForLog(guid);
        if (_unknownWidgetGuids.Add(label))
        {
            _alert?.Invoke($"Unrecognized bank widget prefix {label} in {DescribeDirection()} {context} cmd={command}. Add translation if needed.");
        }
    }

    private string DescribeDirection()
    {
        return _direction == BankPayloadDirection.GameToRag ? "legacy->modern" : "modern->legacy";
    }

    private static bool TryGetWidgetGroup(int guid, out WidgetGroup group, out string label)
    {
        group = WidgetGroup.Unknown;
        if (!TryGetFourCcLabel(guid, out label))
        {
            return false;
        }

        return WidgetGroupByPrefix.TryGetValue(label, out group);
    }

    private static bool TryGetFourCcLabel(int guid, out string label)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, guid);
        if (!LooksLikeFourCc(bytes))
        {
            label = string.Empty;
            return false;
        }

        label = Encoding.ASCII.GetString(bytes);
        return true;
    }

    private static string FormatGuidForLog(int guid)
    {
        return TryGetFourCcLabel(guid, out string label) ? $"'{label}'" : $"0x{guid:X8}";
    }

    private static int GetLegacySliderNumericLength(int guid)
    {
        int size = GetLegacySliderNumericSize(guid);
        return size == 0 ? 0 : size * 4;
    }

    private static int GetLegacySliderNumericSize(int guid)
    {
        if (!TryGetFourCcLabel(guid, out string label))
        {
            return 0;
        }

        return label switch
        {
            "slfl" => 4,
            "slu8" or "sls8" => 1,
            "slu1" or "sls1" => 2,
            "slu3" or "sls3" => 4,
            _ => 0
        };
    }

    private void LogSliderTranslation(
        string context,
        int guid,
        ReadOnlySpan<byte> legacyTail,
        ReadOnlySpan<byte> numericTail,
        byte? exponentialFlag,
        int expectedNumericLength)
    {
        if (_alert is null)
        {
            return;
        }

        string label = FormatGuidForLog(guid);
        string legacyHex = Convert.ToHexString(legacyTail.ToArray());
        string numericHex = Convert.ToHexString(numericTail.ToArray());
        string values = DescribeSliderValues(guid, numericTail);
        string flag = exponentialFlag.HasValue ? exponentialFlag.Value.ToString(CultureInfo.InvariantCulture) : "none";

        _alert($"slider {label} {context}: tailLen={legacyTail.Length} numericLen={numericTail.Length} expectedLen={expectedNumericLength} expFlag={flag} {values} tailHex={legacyHex} numericHex={numericHex}");
    }

    private static string DescribeSliderValues(int guid, ReadOnlySpan<byte> numericTail)
    {
        if (TryGetFourCcLabel(guid, out string label) && label == "slfl")
        {
            return DescribeSliderFloats(numericTail);
        }

        int size = GetLegacySliderNumericSize(guid);
        return size switch
        {
            1 => DescribeSliderInts(numericTail, 1),
            2 => DescribeSliderInts(numericTail, 2),
            4 => DescribeSliderInts(numericTail, 4),
            _ => "values=unavailable"
        };
    }

    private static string DescribeSliderFloats(ReadOnlySpan<byte> numericTail)
    {
        const int floatBytes = sizeof(float) * 4;
        if (numericTail.Length < floatBytes)
        {
            return $"floats=unavailable";
        }

        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(numericTail.Slice(0, sizeof(float))));
        float min = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(numericTail.Slice(sizeof(float), sizeof(float))));
        float max = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(numericTail.Slice(sizeof(float) * 2, sizeof(float))));
        float step = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(numericTail.Slice(sizeof(float) * 3, sizeof(float))));

        return $"floats=value={value.ToString("G9", CultureInfo.InvariantCulture)} min={min.ToString("G9", CultureInfo.InvariantCulture)} max={max.ToString("G9", CultureInfo.InvariantCulture)} step={step.ToString("G9", CultureInfo.InvariantCulture)}";
    }

    private static string DescribeSliderInts(ReadOnlySpan<byte> numericTail, int size)
    {
        int needed = size * 4;
        if (numericTail.Length < needed)
        {
            return "ints=unavailable";
        }

        long value = ReadSignedInteger(numericTail.Slice(0, size), size);
        long min = ReadSignedInteger(numericTail.Slice(size, size), size);
        long max = ReadSignedInteger(numericTail.Slice(size * 2, size), size);
        long step = ReadSignedInteger(numericTail.Slice(size * 3, size), size);

        return $"ints=value={value} min={min} max={max} step={step}";
    }

    private static long ReadSignedInteger(ReadOnlySpan<byte> bytes, int size)
    {
        return size switch
        {
            1 => (sbyte)bytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            _ => 0
        };
    }

    private ushort TranslateLegacyCommand(ushort command, int guid)
    {
        WidgetGroup group = ResolveWidgetGroup(guid, command, "legacy->modern command");
        return group switch
        {
            WidgetGroup.BankManager when command == 2 => 7, // USER_2 / init finished on modern RAG
            WidgetGroup.BankManager when command == 8 => 10, // USER_5 / time string on modern RAG
            WidgetGroup.BankOrGroup when command == 2 => 5, // USER_0 / open-state update on modern RAG
            WidgetGroup.Combo when command == 3 => 5, // USER_0 in modern RAG
            WidgetGroup.Combo when command == 4 => 0, // CREATE in modern RAG
            _ => command
        };
    }

    private bool LegacyPacketHasIdPrefix(ushort command, int guid, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        WidgetGroup group = ResolveWidgetGroup(guid, command, "legacy->modern id prefix");
        return group switch
        {
            WidgetGroup.BankManager when command == 8 => false, // timing/status string
            _ => true
        };
    }

    private bool ShouldDropModernToLegacyPacket(ushort command, int guid, uint id, ReadOnlySpan<byte> payload)
    {
        WidgetGroup group = ResolveWidgetGroup(guid, command, "modern->legacy drop");
        return group switch
        {
            WidgetGroup.BankManager when command == 5 => true,  // modern USER_0/file-dialog path not supported by GTA IV bank manager
            WidgetGroup.BankManager when command == 8 => true,  // modern USER_3 window-handle packet is proxy-only; GTA IV treats legacy cmd 8 as a time-string path
            WidgetGroup.BankManager when command == 11 => true, // modern USER_6 ping path currently rejected by GTA IV runtime
            _ => false
        };
    }

    private enum WidgetGroup
    {
        Unknown,
        BankManager,
        BankOrGroup,
        Combo,
        Button,
        Slider,
        Text,
        Color,
    }

    private static void AppendLegacyPacket(List<byte> destination, ushort command, int guid, ReadOnlySpan<byte> idPrefix, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[LegacyPacketHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0, sizeof(ushort)), (ushort)(idPrefix.Length + payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, sizeof(ushort)), command);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, sizeof(int)), guid);
        AddBytes(destination, header);
        AddBytes(destination, idPrefix);
        AddBytes(destination, payload);
    }

    private static void AppendModernPacket(List<byte> destination, ushort command, int guid, uint id, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[ModernPacketHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0, sizeof(ushort)), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, sizeof(ushort)), command);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, sizeof(int)), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, sizeof(uint)), id);
        AddBytes(destination, header);
        AddBytes(destination, payload);
    }

    private static void AddBytes(List<byte> destination, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            destination.Add(bytes[i]);
        }
    }

    private static CompressionKind DetectCompressionKind(ReadOnlySpan<byte> prefix)
    {
        return prefix switch
        {
            [(byte)'C', (byte)'H', (byte)'N', (byte)':'] => CompressionKind.None,
            [(byte)'C', (byte)'H', (byte)'D', (byte)':'] => CompressionKind.Dat,
            [(byte)'C', (byte)'H', (byte)'F', (byte)':'] => CompressionKind.Fi,
            _ => CompressionKind.Unknown,
        };
    }

    private static bool LooksLikeFourCc(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindNextFrameStart(ReadOnlySpan<byte> source)
    {
        for (int i = 0; i <= source.Length - 4; i++)
        {
            if (IsFrameMarker(source.Slice(i, 4)))
            {
                return i;
            }
        }

        return -1;
    }

    private static int GetTrailingFramePrefixLength(ReadOnlySpan<byte> source)
    {
        int maxLength = Math.Min(3, source.Length);
        for (int length = maxLength; length > 0; length--)
        {
            ReadOnlySpan<byte> suffix = source.Slice(source.Length - length, length);
            if (IsFrameMarkerPrefix(suffix))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool IsFrameMarker(ReadOnlySpan<byte> value)
    {
        return value is
        [
            (byte)'C',
            (byte)'H',
            (byte)'N' or (byte)'D' or (byte)'F',
            (byte)':'
        ];
    }

    private static bool IsFrameMarkerPrefix(ReadOnlySpan<byte> value)
    {
        return value switch
        {
            [(byte)'C'] => true,
            [(byte)'C', (byte)'H'] => true,
            [(byte)'C', (byte)'H', (byte)'N' or (byte)'D' or (byte)'F'] => true,
            _ => false,
        };
    }

    private static byte[] DecompressDat(byte[] compressedData, int decompressedSize)
    {
        byte[] decompressed = new byte[decompressedSize];
        uint actualSize = DatCompression.Decompress(decompressed, compressedData, (uint)compressedData.Length);
        if (actualSize == 0 || actualSize != decompressedSize)
        {
            throw new InvalidOperationException("Failed to decompress CHD bank data.");
        }

        return decompressed;
    }

    private static byte[] DecompressFi(byte[] compressedData, int decompressedSize)
    {
        byte[] decompressed = new byte[decompressedSize];
        if (!FiCompression.Decompress(decompressed, (uint)decompressedSize, compressedData))
        {
            throw new InvalidOperationException("Failed to decompress CHF bank data.");
        }

        return decompressed;
    }

    private static byte[] WrapNoCompression(ReadOnlySpan<byte> payload)
    {
        byte[] wrapped = new byte[CompressionHeaderShortSize + payload.Length];
        wrapped[0] = (byte)'C';
        wrapped[1] = (byte)'H';
        wrapped[2] = (byte)'N';
        wrapped[3] = (byte)':';
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4, sizeof(uint)), (uint)payload.Length);
        wrapped[8] = (byte)':';
        payload.CopyTo(wrapped.AsSpan(CompressionHeaderShortSize));
        return wrapped;
    }

    private ReadOnlySpan<byte> TransformLegacyCreatePayload(ushort command, int guid, ReadOnlySpan<byte> payload)
    {
        if (command != 0 || payload.Length < sizeof(uint))
        {
            return payload;
        }

        WidgetGroup group = ResolveWidgetGroup(guid, command, "legacy->modern payload");
        return group switch
        {
            WidgetGroup.Button => RewriteLegacySimpleCreatePayload(payload, guid, appendExponentialFlag: false),
            WidgetGroup.Combo => RewriteLegacyComboCreatePayload(payload),
            WidgetGroup.Slider => RewriteLegacySimpleCreatePayload(payload, guid, appendExponentialFlag: true),
            WidgetGroup.BankOrGroup => RewriteLegacyBankOrGroupCreatePayload(payload),
            WidgetGroup.Text => RewriteLegacyTextCreatePayload(payload),
            WidgetGroup.Color => RewriteLegacyColorCreatePayload(payload),
            WidgetGroup.Unknown when TryGetLegacyString(payload.Slice(sizeof(uint)), out _, out _)
                => InsertFieldAfterFirstString(payload, DefaultWidgetColorField),
            _ => payload
        };
    }

    private static ReadOnlySpan<byte> RewriteLegacyTextCreatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return payload;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return payload;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return payload;
        }

        int valueOffset = memoOffset + memoExtent;
        if (!TryGetLegacyString(payload.Slice(valueOffset), out _, out int valueExtent))
        {
            return payload;
        }

        int stringSizeOffset = valueOffset + valueExtent;
        if (payload.Length < stringSizeOffset + sizeof(uint))
        {
            return payload;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        uint stringSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(stringSizeOffset, sizeof(uint)));

        List<byte> createPayload = new(payload.Length + 32);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        BinaryPrimitives.WriteInt32LittleEndian(word, WidgetTextStringType);
        AddBytes(createPayload, word);
        BinaryPrimitives.WriteUInt32LittleEndian(word, stringSize);
        AddBytes(createPayload, word);
        return createPayload.ToArray();
    }

    private static ReadOnlySpan<byte> RewriteLegacyComboCreatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return payload;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return payload;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return payload;
        }

        int numericOffset = memoOffset + memoExtent;
        if (payload.Length < numericOffset + (sizeof(int) * 3))
        {
            return payload;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> numericTail = payload.Slice(numericOffset);

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        AddBytes(createPayload, numericTail);
        return createPayload.ToArray();
    }

    private static ReadOnlySpan<byte> RewriteLegacyColorCreatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return payload;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return payload;
        }

        int memoOffset = sizeof(uint) + titleExtent;
        if (!TryGetLegacyString(payload.Slice(memoOffset), out string? memo, out int memoExtent))
        {
            return payload;
        }

        int typeOffset = memoOffset + memoExtent;
        if (payload.Length < typeOffset + sizeof(uint) + (sizeof(float) * 4))
        {
            return payload;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> colorTail = payload.Slice(typeOffset);

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo ?? string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        AddBytes(createPayload, colorTail);
        return createPayload.ToArray();
    }

    private static ReadOnlySpan<byte> RewriteLegacyBankOrGroupCreatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint))
        {
            return payload;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return payload;
        }

        int openOffset = sizeof(uint) + titleExtent;
        int openValue = 0;
        if (payload.Length >= openOffset + sizeof(int))
        {
            openValue = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(openOffset, sizeof(int)));
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(string.Empty));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        BinaryPrimitives.WriteInt32LittleEndian(word, openValue);
        AddBytes(createPayload, word);
        return createPayload.ToArray();
    }

    private ReadOnlySpan<byte> RewriteLegacySimpleCreatePayload(ReadOnlySpan<byte> payload, int guid, bool appendExponentialFlag)
    {
        if (payload.Length < sizeof(uint))
        {
            return payload;
        }

        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out string? title, out int titleExtent))
        {
            return payload;
        }

        int tailOffset = sizeof(uint) + titleExtent;
        if (tailOffset > payload.Length)
        {
            return payload;
        }

        uint remoteParent = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, sizeof(uint)));
        ReadOnlySpan<byte> legacyTail = payload.Slice(tailOffset);
        string memo = string.Empty;
        if (TrySplitOptionalLegacyMemo(legacyTail, out string? parsedMemo, out ReadOnlySpan<byte> memoTrimmedTail))
        {
            memo = parsedMemo ?? string.Empty;
            legacyTail = memoTrimmedTail;
        }

        List<byte> createPayload = new(payload.Length + 24);
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(word, remoteParent);
        AddBytes(createPayload, word);
        AddBytes(createPayload, EncodeLegacyString(title ?? string.Empty));
        AddBytes(createPayload, EncodeLegacyString(memo));
        AddBytes(createPayload, DefaultWidgetColorField);
        createPayload.Add(0); // readOnly = false
        ReadOnlySpan<byte> numericTail = legacyTail;
        byte? exponentialFlag = null;
        if (appendExponentialFlag)
        {
            int expectedNumericLength = GetLegacySliderNumericLength(guid);
            numericTail = ExtractLegacySliderExponentialFlag(legacyTail, expectedNumericLength, out exponentialFlag);
            LogSliderTranslation("legacy->modern payload", guid, legacyTail, numericTail, exponentialFlag, expectedNumericLength);
        }

        AddBytes(createPayload, numericTail);
        if (appendExponentialFlag)
        {
            if (exponentialFlag.HasValue)
            {
                createPayload.Add(exponentialFlag.Value);
            }
            else if (RequiresAppendedLegacySliderExponentialFlag(legacyTail))
            {
                createPayload.Add(0); // exponential = false
            }
        }

        return createPayload.ToArray();
    }

    private static byte[] InsertFieldAfterParent(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> field)
    {
        if (TryGetLegacyString(payload.Slice(sizeof(uint)), out string? existingField, out _)
            && IsColorField(existingField))
        {
            return payload.ToArray();
        }

        byte[] output = new byte[payload.Length + field.Length];
        payload.Slice(0, sizeof(uint)).CopyTo(output);
        field.CopyTo(output.AsSpan(sizeof(uint)));
        payload.Slice(sizeof(uint)).CopyTo(output.AsSpan(sizeof(uint) + field.Length));
        return output;
    }

    private static byte[] InsertFieldAfterFirstString(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> field)
    {
        if (!TryGetLegacyString(payload.Slice(sizeof(uint)), out _, out int firstStringExtent))
        {
            return payload.ToArray();
        }

        int secondFieldOffset = sizeof(uint) + firstStringExtent;
        if (secondFieldOffset < payload.Length
            && TryGetLegacyString(payload.Slice(secondFieldOffset), out string? existingField, out _)
            && IsColorField(existingField))
        {
            return payload.ToArray();
        }

        int insertOffset = secondFieldOffset;
        byte[] output = new byte[payload.Length + field.Length];
        payload.Slice(0, insertOffset).CopyTo(output);
        field.CopyTo(output.AsSpan(insertOffset));
        payload.Slice(insertOffset).CopyTo(output.AsSpan(insertOffset + field.Length));
        return output;
    }

    private static bool TryGetLegacyStringExtent(ReadOnlySpan<byte> payload, out int extent)
    {
        extent = 0;
        if (payload.IsEmpty)
        {
            return false;
        }

        int length = payload[0];
        if (length < 0 || payload.Length < 1 + length)
        {
            return false;
        }

        extent = 1 + length;
        return true;
    }

    private static bool TryGetLegacyString(ReadOnlySpan<byte> payload, out string? value, out int extent)
    {
        value = null;
        if (!TryGetLegacyStringExtent(payload, out extent))
        {
            return false;
        }

        int stringLength = payload[0];
        ReadOnlySpan<byte> bytes = payload.Slice(1, stringLength);
        int terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            bytes = bytes.Slice(0, terminator);
        }

        value = System.Text.Encoding.ASCII.GetString(bytes);
        return true;
    }

    private static bool TrySplitOptionalLegacyMemo(
        ReadOnlySpan<byte> payload,
        out string? memo,
        out ReadOnlySpan<byte> remaining)
    {
        memo = null;
        remaining = payload;
        if (payload.IsEmpty || payload[0] == 0)
        {
            return false;
        }

        if (!TryGetLegacyString(payload, out string? parsedMemo, out int memoExtent))
        {
            return false;
        }

        if (memoExtent <= 0 || memoExtent >= payload.Length)
        {
            return false;
        }

        memo = parsedMemo ?? string.Empty;
        remaining = payload.Slice(memoExtent);
        return true;
    }

    private static bool RequiresAppendedLegacySliderExponentialFlag(ReadOnlySpan<byte> legacyTail)
    {
        return !legacyTail.IsEmpty && (legacyTail.Length % sizeof(uint)) == 0;
    }

    private static ReadOnlySpan<byte> ExtractLegacySliderExponentialFlag(ReadOnlySpan<byte> legacyTail, int expectedNumericLength, out byte? exponentialFlag)
    {
        exponentialFlag = null;
        if (expectedNumericLength <= 0)
        {
            return legacyTail;
        }

        if (legacyTail.Length == expectedNumericLength)
        {
            return legacyTail;
        }

        if (legacyTail.Length == expectedNumericLength + 1)
        {
            byte leading = legacyTail[0];
            if (leading <= 1)
            {
                exponentialFlag = leading;
                return legacyTail.Slice(1);
            }

            byte trailing = legacyTail[^1];
            if (trailing <= 1)
            {
                exponentialFlag = trailing;
                return legacyTail.Slice(0, expectedNumericLength);
            }
        }

        return legacyTail;
    }

    private static bool IsColorField(string? value)
    {
        return value is not null
            && value.StartsWith("ARGBColor:", StringComparison.Ordinal);
    }

    private static byte[] EncodeLegacyString(string value)
    {
        byte[] textBytes = System.Text.Encoding.ASCII.GetBytes(value + '\0');
        if (textBytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException("Widget color field is too long to encode.");
        }

        byte[] encoded = new byte[1 + textBytes.Length];
        encoded[0] = (byte)textBytes.Length;
        textBytes.CopyTo(encoded.AsSpan(1));
        return encoded;
    }

    private static string FourCcToString(int guid)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, guid);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static int ComputeGuid(string value)
    {
        ReadOnlySpan<byte> bytes = System.Text.Encoding.ASCII.GetBytes(value);
        if (bytes.Length != sizeof(int))
        {
            throw new InvalidOperationException("Widget guid must be exactly four ASCII characters.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void Append(ref byte[] buffer, ref int count, ReadOnlySpan<byte> data)
    {
        EnsureCapacity(ref buffer, count + data.Length);
        data.CopyTo(buffer.AsSpan(count));
        count += data.Length;
    }

    private static byte[] Consume(ref byte[] buffer, ref int count, int amount)
    {
        byte[] result = new byte[amount];
        buffer.AsSpan(0, amount).CopyTo(result);
        ConsumeInPlace(ref buffer, ref count, amount);
        return result;
    }

    private static void ConsumeInPlace(ref byte[] buffer, ref int count, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        int remaining = count - amount;
        if (remaining > 0)
        {
            Buffer.BlockCopy(buffer, amount, buffer, 0, remaining);
        }

        count = remaining;
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required)
        {
            return;
        }

        int newSize = Math.Max(required, Math.Max(buffer.Length * 2, 256));
        Array.Resize(ref buffer, newSize);
    }

    private readonly record struct BankPacketHeader(int PayloadLength, ushort Command, int Guid, uint Id, int TotalLength);

    private enum CompressionKind
    {
        Unknown,
        None,
        Dat,
        Fi,
    }

    private enum CompressionParseStatus
    {
        NeedMoreData,
        InvalidData,
        Success,
    }

    private enum BankIngressMode
    {
        Unknown,
        Framed,
        RawLegacyPackets,
        RawModernPackets,
    }
}

internal enum PacketStreamFormat
{
    Unknown,
    Legacy8,
    Modern12,
}

internal enum BankPayloadDirection
{
    GameToRag,
    RagToGame,
}

internal static class DatCompression
{
    private const int MinMatchLength = 3;
    private const int MatchWidth = 4;
    private const int HeaderSize = 2;

    public static uint Decompress(byte[] destination, byte[] source, uint compressedSize)
    {
        int encodedLength = source[0] | (source[1] << 8);
        return encodedLength <= compressedSize ? Decompress(destination, source) : 0;
    }

    private static uint Decompress(byte[] destination, byte[] source)
    {
        int sourceIndex = 0;
        int destinationIndex = 0;
        int encodedLength = source[sourceIndex++];
        encodedLength += source[sourceIndex++] << 8;

        do
        {
            byte control = source[sourceIndex++];
            --encodedLength;
            for (int i = 0; encodedLength != 0 && i < 8; i++, control <<= 1)
            {
                if ((control & 0x80) != 0)
                {
                    destination[destinationIndex++] = source[sourceIndex++];
                    --encodedLength;
                }
                else
                {
                    byte count = source[sourceIndex];
                    int offset = (source[sourceIndex + 1] << MatchWidth) | (count >> MatchWidth);
                    int pointerIndex = destinationIndex - offset - 1;

                    sourceIndex += HeaderSize;
                    encodedLength -= HeaderSize;
                    count = (byte)((count & 15) + MinMatchLength);
                    do
                    {
                        destination[destinationIndex++] = destination[pointerIndex++];
                    } while (--count != 0);
                }
            }
        } while (encodedLength != 0);

        return (uint)destinationIndex;
    }
}

internal static class FiCompression
{
    private const int InitialWidth = 2;
    private const int WidthStep = 2;
    private const byte Magic1 = 0xDC;
    private const byte Magic2 = 0xE0;

    public static bool Decompress(byte[] destination, uint destinationSize, byte[] source)
    {
        int accumulator = 0x10000;
        uint outputOffset = 0;
        uint sourceIndex = 0;

        if (source[0] != Magic1 || source[1] != Magic2)
        {
            return false;
        }

        sourceIndex += 2;
        while (outputOffset < destinationSize)
        {
            uint length;
            if (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
            {
                length = 1;
            }
            else if (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
            {
                if (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
                {
                    uint width = InitialWidth + WidthStep;
                    uint bias = 1U + (1U << InitialWidth);
                    while (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
                    {
                        bias += 1U << (int)width;
                        width += WidthStep;
                    }

                    length = bias + GetBits(ref accumulator, source, ref sourceIndex, (int)width);
                }
                else
                {
                    length = 4;
                }
            }
            else if (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
            {
                length = 3;
            }
            else
            {
                length = 2;
            }

            if (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
            {
                ++length;
                uint width = InitialWidth;
                uint bias = 1;
                while (GetBits(ref accumulator, source, ref sourceIndex, 1) != 0)
                {
                    bias += 1U << (int)width;
                    width += WidthStep;
                }

                uint backup = bias + GetBits(ref accumulator, source, ref sourceIndex, (int)width);
                if (backup > outputOffset)
                {
                    return false;
                }

                do
                {
                    if (outputOffset >= destinationSize)
                    {
                        return false;
                    }

                    destination[outputOffset] = destination[outputOffset - backup];
                    ++outputOffset;
                } while (--length != 0);
            }
            else
            {
                do
                {
                    if (outputOffset >= destinationSize)
                    {
                        return false;
                    }

                    destination[outputOffset++] = source[sourceIndex++];
                } while (--length != 0);
            }
        }

        return outputOffset == destinationSize;
    }

    private static uint GetBits(ref int accumulator, byte[] source, ref uint sourceIndex, int count)
    {
        uint result = 0;
        do
        {
            if ((accumulator & 0x10000) != 0)
            {
                accumulator = 0x100 | source[sourceIndex++];
            }

            result = (result << 1) | (uint)((accumulator & 0x80) >> 7);
            accumulator <<= 1;
        } while (--count != 0);

        return result;
    }
}
