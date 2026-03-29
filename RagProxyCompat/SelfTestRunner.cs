using System.Buffers.Binary;

namespace RagProxyCompat;

internal static class SelfTestRunner
{
    public static void Run()
    {
        VerifyLegacyHeaderParsing();
        VerifyLegacyHeaderSerialization();
        VerifyVersionlessHandshakeUpgrade();
        VerifyLegacyVersionedHandshakeUpgrade();
        VerifyModernHandshakePassthrough();
        VerifyVersionlessReplyDowngrade();
        VerifyLegacyVersionedReplyDowngrade();
        VerifyReplyBasePortRewrite();
        VerifyBootstrapEmptyAppNameGetsSyntheticPath();
        VerifyBankIngressTranslatesCompressedLegacyFrame();
        VerifyBankIngressSkipsGarbageBeforeFrame();
        VerifyBankIngressPreservesSplitFrameHeader();
        VerifyBankIngressTranslatesPartialRawLegacyPacket();
        VerifyBankIngressResyncsRawPacketBuffer();
        VerifyBankIngressAddsButtonFillColor();
        VerifyBankIngressAddsSliderFillColor();
        VerifyBankIngressAddsSliderMemoBeforeFloatData();
        VerifyBankIngressAddsTextFillColor();
        VerifyBankIngressAddsColorFillColor();
        VerifyBankIngressAddsBankCreateFields();
        VerifyBankIngressTranslatesLegacyComboCreate();
        VerifyBankIngressTranslatesLegacyComboItemUpdate();
        VerifyBankIngressTranslatesLegacyGroupOpenUpdate();
        VerifyBankIngressTranslatesLegacyBankManagerInitFinished();
        VerifyBankIngressTranslatesLegacyBankManagerTimeString();
        VerifyBankIngressSynthesizesOutputSetupAfterInitFinished();
        VerifyBankEgressTranslatesModernPacket();
        VerifyBankEgressDropsUnsupportedBankManagerCommands();
        Console.WriteLine("Self-test passed.");
    }

    private static void VerifyLegacyHeaderParsing()
    {
        byte[] legacyHandshake = { 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x8F, 0xC2, 0xF5, 0x3F };
        ushort length = BitConverter.ToUInt16(legacyHandshake, 0);
        ushort command = BitConverter.ToUInt16(legacyHandshake, 2);
        uint id = BitConverter.ToUInt32(legacyHandshake, 4);
        float version = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(legacyHandshake, 8));

        Assert(length == sizeof(float), "Legacy bootstrap header should report a 4-byte payload.");
        Assert(command == 0, "Legacy bootstrap command should be HANDSHAKE.");
        Assert(id == 0, "Legacy bootstrap id should be 0.");
        Assert(Math.Abs(version - RagPacket.GtaIvVersion) < 0.001f, "Legacy bootstrap payload should carry version 1.92.");
    }

    private static void VerifyLegacyHeaderSerialization()
    {
        RagPacket packet = RagPacket.CreateHandshake(RagPacket.GtaIvVersion);
        byte[] serialized = packet.ToByteArray(RagPacketFormat.Legacy8);
        Assert(serialized.Length == 8 + sizeof(float), "Legacy handshake should serialize to 8-byte header plus float.");
        Assert(serialized[0] == 0x04 && serialized[1] == 0x00, "Legacy serialized length should be 4.");
        Assert(serialized[2] == 0x00 && serialized[3] == 0x00, "Legacy serialized command should be HANDSHAKE.");
    }

    private static void VerifyVersionlessHandshakeUpgrade()
    {
        RagPacket legacy = new()
        {
            Command = (ushort)AppPacketType.Handshake,
            Payload = Array.Empty<byte>(),
        };

        RagPacket upgraded = legacy.RewriteHandshakeVersion(RagPacket.ModernVersion);
        float negotiated = upgraded.ReadSingle();
        Assert(Math.Abs(negotiated - RagPacket.ModernVersion) < 0.001f, "Legacy handshake should upgrade to modern version.");
        Assert(legacy.Payload.Length == 0, "Legacy handshake fixture should not carry a version.");
    }

    private static void VerifyLegacyVersionedHandshakeUpgrade()
    {
        RagPacket legacy = RagPacket.CreateHandshake(RagPacket.GtaIvVersion);
        RagPacket upgraded = legacy.RewriteHandshakeVersion(RagPacket.ModernVersion);
        Assert(Math.Abs(legacy.ReadSingle() - RagPacket.GtaIvVersion) < 0.001f, "Legacy handshake fixture should carry the GTA IV version.");
        Assert(Math.Abs(upgraded.ReadSingle() - RagPacket.ModernVersion) < 0.001f, "Legacy versioned handshake should upgrade to modern version.");
    }

    private static void VerifyModernHandshakePassthrough()
    {
        RagPacket modern = RagPacket.CreateHandshake(RagPacket.ModernVersion);
        Assert(modern.Payload.Length == sizeof(float), "Modern handshake should contain only the version float.");
        Assert(Math.Abs(modern.ReadSingle() - RagPacket.ModernVersion) < 0.001f, "Modern handshake version must remain 2.0.");
    }

    private static void VerifyVersionlessReplyDowngrade()
    {
        RagPacket downstreamReply = RagPacket.CreateHandshakeReply(60000, RagPacket.ModernVersion);
        RagPacket downgraded = downstreamReply.RewriteHandshakeReply(RagPacket.GtaIvVersion);
        Assert(downgraded.Payload.Length == sizeof(int) + sizeof(float), "Versionless GTA IV bootstrap still expects a reply version.");
        Assert(downgraded.ReadInt32() == 60000, "Legacy reply must preserve the base port.");
        float version = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(downgraded.Payload, sizeof(int)));
        Assert(Math.Abs(version - RagPacket.GtaIvVersion) < 0.001f, "Versionless GTA IV bootstrap should receive reply version 1.92.");
    }

    private static void VerifyLegacyVersionedReplyDowngrade()
    {
        RagPacket downstreamReply = RagPacket.CreateHandshakeReply(60000, RagPacket.ModernVersion);
        RagPacket downgraded = downstreamReply.RewriteHandshakeReply(RagPacket.GtaIvVersion);
        Assert(downgraded.Payload.Length == sizeof(int) + sizeof(float), "Legacy versioned reply must carry base port and version.");
        Assert(downgraded.ReadInt32() == 60000, "Legacy versioned reply must preserve the base port.");
        float version = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(downgraded.Payload, sizeof(int)));
        Assert(Math.Abs(version - RagPacket.GtaIvVersion) < 0.001f, "Legacy versioned reply must preserve the old version.");
    }

    private static void VerifyReplyBasePortRewrite()
    {
        RagPacket downstreamReply = RagPacket.CreateHandshakeReply(50000, RagPacket.ModernVersion);
        RagPacket rewritten = downstreamReply.RewriteHandshakeReply(RagPacket.GtaIvVersion, basePortOverride: 61000);
        Assert(rewritten.ReadInt32() == 61000, "Reply rewrite must expose the proxy base port.");
        float version = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(rewritten.Payload, sizeof(int)));
        Assert(Math.Abs(version - RagPacket.GtaIvVersion) < 0.001f, "Reply base-port rewrite must preserve the translated version.");
    }

    private static void VerifyBootstrapEmptyAppNameGetsSyntheticPath()
    {
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00,
            0x01, 0x00
        ];
        RagPacket packet = new()
        {
            Command = (ushort)AppPacketType.AppName,
            Payload = payload,
        };

        byte[] rewritten = RewriteBootstrapAppNameForTest(packet).Payload;
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(rewritten.AsSpan(0, 4)) == 0, "Synthetic AppName rewrite should preserve the application index.");
        string text = ReadIndexedCStringForTest(rewritten);
        Assert(text == @"C:\RagProxy\LegacyGame.exe", "Empty AppName should be rewritten to a legal synthetic path.");
    }

    private static void VerifyBankIngressTranslatesCompressedLegacyFrame()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 8, command: 8, guid: FourCc("pmgr"), payload: [0x3D, 0x29, 0x00, 0x00, 1, 2, 3, 4]);
        byte[] frame = WrapNoCompression(legacyPacket);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(frame);

        Assert(forwarded.Count == 1, "Compressed legacy CHD bank traffic should be translated once.");

        byte[] wrapped = forwarded[0];
        Assert(wrapped.Length == 9 + 12 + 4, "Translated ingress packet should be rewrapped in a CHN frame.");
        Assert(wrapped[0] == (byte)'C' && wrapped[1] == (byte)'H' && wrapped[2] == (byte)'N' && wrapped[3] == (byte)':', "Translated ingress packet should use CHN framing.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(wrapped.AsSpan(4, sizeof(uint))) == 12 + 4, "CHN wrapper should report the translated modern packet size.");

        byte[] modernPacket = wrapped.AsSpan(9).ToArray();
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(0, 2)) == 4, "Translated modern packet should drop the legacy id prefix from payload length.");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2)) == 8, "Translated modern packet should preserve command.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(modernPacket.AsSpan(4, 4)) == FourCc("pmgr"), "Translated modern packet should move legacy guid into the modern guid field.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 0x293D, "Translated modern packet should move the first legacy payload dword into the modern id field.");
        Assert(modernPacket.AsSpan(12).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Translated modern packet should preserve payload bytes.");
    }

    private static void VerifyBankIngressTranslatesPartialRawLegacyPacket()
    {
        byte[] packet = BuildLegacyBankPacket(length: 4, command: 2, guid: 0x72676D62, payload: [7, 0, 0, 0]);
        byte[] firstChunk = packet.AsSpan(0, 6).ToArray();
        byte[] secondChunk = packet.AsSpan(6).ToArray();

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> firstForward = normalizer.ProcessChunk(firstChunk);
        IReadOnlyList<byte[]> secondForward = normalizer.ProcessChunk(secondChunk);

        Assert(firstForward.Count == 0, "Partial legacy bank packet should not be forwarded.");
        Assert(secondForward.Count == 1, "Completed legacy bank packet should be forwarded once.");

        byte[] wrapped = secondForward[0];
        byte[] modernPacket = wrapped.AsSpan(9).ToArray();
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2)) == 7, "Legacy bank-manager init packet should map to modern USER_2.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(modernPacket.AsSpan(4, 4)) == 0x72676D62, "Translated modern packet should preserve guid.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 7, "Translated modern packet should preserve the legacy id prefix.");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(0, 2)) == 0, "Translated bank-manager init packet should not carry residual payload bytes.");
    }

    private static void VerifyBankIngressSkipsGarbageBeforeFrame()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 8, command: 8, guid: FourCc("pmgr"), payload: [5, 0, 0, 0, 6, 7, 8, 9]);
        byte[] framed = WrapNoCompression(legacyPacket);
        byte[] chunk = new byte[4 + framed.Length];
        chunk[0] = (byte)'b';
        chunk[1] = (byte)'a';
        chunk[2] = (byte)'n';
        chunk[3] = (byte)'k';
        framed.CopyTo(chunk.AsSpan(4));

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(chunk);

        Assert(forwarded.Count == 1, "Garbage before a valid bank frame should be skipped.");
        Assert(forwarded[0].Length == 9 + 12 + 4, "Bank frame after skipped garbage should still be translated and wrapped.");
        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 5, "Translated wrapped packet should preserve the legacy id prefix.");
    }

    private static void VerifyBankIngressPreservesSplitFrameHeader()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 8, command: 8, guid: FourCc("pmgr"), payload: [9, 0, 0, 0, 10, 11, 12, 13]);
        byte[] framed = WrapNoCompression(legacyPacket);
        byte[] firstChunk = new byte[5];
        firstChunk[0] = 0x01;
        firstChunk[1] = 0x02;
        firstChunk[2] = 0x03;
        framed.AsSpan(0, 2).CopyTo(firstChunk.AsSpan(3));

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> firstForward = normalizer.ProcessChunk(firstChunk);
        IReadOnlyList<byte[]> secondForward = normalizer.ProcessChunk(framed.AsSpan(2).ToArray());

        Assert(firstForward.Count == 0, "Partial frame header should be buffered.");
        Assert(secondForward.Count == 1, "Frame header split across chunks should resync and forward once complete.");
        Assert(secondForward[0].Length == 9 + 12 + 4, "Split frame header should still produce one translated wrapped packet.");
        byte[] modernPacket = secondForward[0].AsSpan(9).ToArray();
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 9, "Split frame header should preserve the legacy id prefix.");
    }

    private static void VerifyBankIngressResyncsRawPacketBuffer()
    {
        byte[] packet = BuildLegacyBankPacket(length: 8, command: 5, guid: FourCc("pmgr"), payload: [10, 0, 0, 0, 13, 14, 15, 16]);
        byte[] noisyPayload = new byte[6 + packet.Length];
        noisyPayload[0] = (byte)'b';
        noisyPayload[1] = (byte)'a';
        noisyPayload[2] = (byte)'n';
        noisyPayload[3] = (byte)'k';
        noisyPayload[4] = 0xFF;
        noisyPayload[5] = 0x00;
        packet.CopyTo(noisyPayload.AsSpan(6));

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(noisyPayload);

        Assert(forwarded.Count == 1, "Packet-buffer junk should be skipped until a plausible legacy packet header is found.");
        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2)) == 5, "Packet-buffer resync should preserve the valid packet command.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 10, "Packet-buffer resync should preserve the legacy id prefix.");
    }

    private static void VerifyBankEgressTranslatesModernPacket()
    {
        byte[] modernPacket = BuildModernBankPacket(length: 4, command: 8, guid: FourCc("pmgr"), id: 0x293D, payload: [9, 8, 7, 6]);

        BankEgressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(modernPacket);

        Assert(forwarded.Count == 1, "Modern bank packet should be downgraded once.");
        byte[] legacyPacket = forwarded[0];
        Assert(legacyPacket.Length == 8 + 8, "Downgraded bank packet should prepend the modern id back into the legacy payload.");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(legacyPacket.AsSpan(2, 2)) == 8, "Downgraded legacy packet should preserve command.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(legacyPacket.AsSpan(4, 4)) == FourCc("pmgr"), "Downgraded legacy packet should preserve guid in the third field.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(legacyPacket.AsSpan(8, 4)) == 0x293D, "Downgraded legacy packet should prefix the modern id.");
        Assert(legacyPacket.AsSpan(12).SequenceEqual(new byte[] { 9, 8, 7, 6 }), "Downgraded legacy packet should preserve payload.");
    }

    private static void VerifyBankEgressDropsUnsupportedBankManagerCommands()
    {
        byte[] modernWindowHandle = BuildModernBankPacket(length: 4, command: 8, guid: FourCc("bmgr"), id: 0, payload: [0x78, 0x56, 0x34, 0x12]);
        byte[] modernPing = BuildModernBankPacket(length: 1, command: 11, guid: FourCc("bmgr"), id: 1, payload: [1]);
        byte[] combined = new byte[modernWindowHandle.Length + modernPing.Length];
        Buffer.BlockCopy(modernWindowHandle, 0, combined, 0, modernWindowHandle.Length);
        Buffer.BlockCopy(modernPing, 0, combined, modernWindowHandle.Length, modernPing.Length);

        BankEgressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(combined);

        Assert(forwarded.Count == 0, "Unsupported modern bank-manager commands should be dropped on the legacy egress path.");
    }

    private static void VerifyBankIngressAddsButtonFillColor()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 16, command: 0, guid: FourCc("butn"), payload: [
            3, 0, 0, 0,
            2, 0, 0, 0,
            6, (byte)'P', (byte)'a', (byte)'u', (byte)'s', (byte)'e', 0,
            0
        ]);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 3, "Button packet should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 2, "Button payload should preserve the parent id.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Button payload should inject a default fill color.");
        Assert(payloadText.Contains("Pause"), "Button payload should preserve the label.");
    }

    private static void VerifyBankIngressAddsTextFillColor()
    {
        byte[] legacyPayload = [
            0x26, 0, 0, 0,
            0x25, 0, 0, 0,
            0x0B, (byte)'S', (byte)'h', (byte)'a', (byte)'d', (byte)'e', (byte)'r', (byte)'N', (byte)'a', (byte)'m', (byte)'e', 0,
            0x00,
            0x0C, (byte)'g', (byte)'t', (byte)'a', (byte)'_', (byte)'d', (byte)'e', (byte)'f', (byte)'a', (byte)'u', (byte)'l', (byte)'t', 0,
            0x0C, 0, 0, 0, 0x01
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 0, guid: FourCc("text"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] wrapped = forwarded[0];
        byte[] packetStream = wrapped.AsSpan(9).ToArray();

        ushort createLen = BinaryPrimitives.ReadUInt16LittleEndian(packetStream.AsSpan(0, 2));
        ushort createCmd = BinaryPrimitives.ReadUInt16LittleEndian(packetStream.AsSpan(2, 2));
        uint createId = BinaryPrimitives.ReadUInt32LittleEndian(packetStream.AsSpan(8, 4));
        byte[] createPayload = packetStream.AsSpan(12, createLen).ToArray();
        string createText = System.Text.Encoding.ASCII.GetString(createPayload);

        int changedOffset = 12 + createLen;
        Assert(packetStream.Length >= changedOffset + 12, "Text translation should emit a second CHANGED packet after CREATE.");
        ushort changedLen = BinaryPrimitives.ReadUInt16LittleEndian(packetStream.AsSpan(changedOffset + 0, 2));
        ushort changedCmd = BinaryPrimitives.ReadUInt16LittleEndian(packetStream.AsSpan(changedOffset + 2, 2));
        uint changedId = BinaryPrimitives.ReadUInt32LittleEndian(packetStream.AsSpan(changedOffset + 8, 4));
        Assert(packetStream.Length >= changedOffset + 12 + changedLen, "Text CHANGED packet should fit within the translated packet stream.");
        byte[] changedPayload = packetStream.AsSpan(changedOffset + 12, changedLen).ToArray();

        Assert(createCmd == 0, "Text create packet should remain a CREATE command.");
        Assert(changedCmd == 2, "Text initial value should be forwarded as a CHANGED command.");
        Assert(createId == 0x26 && changedId == 0x26, "Text create and changed packets should preserve the widget id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(createPayload.AsSpan(0, 4)) == 0x25, "Text create payload should preserve the parent id.");
        Assert(createText.Contains("ShaderName"), "Text create payload should preserve the widget label.");
        Assert(createText.Contains("ARGBColor:0:0:0:0"), "Text create payload should inject a default fill color.");
        Assert(!createText.Contains("gta_default"), "Text initial value should no longer be embedded in the CREATE payload.");

        Assert(BinaryPrimitives.ReadUInt16LittleEndian(changedPayload.AsSpan(0, 2)) == 11, "Text changed payload should send the initial string byte count.");
        Assert(System.Text.Encoding.ASCII.GetString(changedPayload.AsSpan(6)) == "gta_default", "Text changed payload should preserve the original text value.");
    }

    private static void VerifyBankIngressAddsSliderFillColor()
    {
        byte[] legacyPayload = [
            0x06, 0, 0, 0,
            0x02, 0, 0, 0,
            0x0B, (byte)'T', (byte)'i', (byte)'m', (byte)'e', (byte)'S', (byte)'c', (byte)'a', (byte)'l', (byte)'e', (byte)':', 0,
            0x00, 0x00, 0x80, 0x3F,
            0x17, 0xB7, 0xD1, 0x38,
            0x00, 0x00, 0x80, 0x3F,
            0x0A, 0xD7, 0x23, 0x3C
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 0, guid: FourCc("slfl"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);
        int titleExtent = 1 + payload[4];
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 0x06, "Slider packet should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 0x02, "Slider payload should preserve the parent id.");
        Assert(payloadText.Contains("TimeScale:"), "Slider payload should preserve the label.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Slider payload should inject a default fill color.");
        Assert(payload[4 + titleExtent] == 1 && payload[4 + titleExtent + 1] == 0, "Slider payload should inject an empty memo field after the title.");
        Assert(payload[^1] == 0, "Slider payload should append a default exponential flag when the legacy slider tail does not provide one.");
        Assert(payload.Length == 55, "Slider payload should match the modern WidgetSliderFloat CREATE layout.");
    }

    private static void VerifyBankIngressAddsSliderMemoBeforeFloatData()
    {
        byte[] legacyPayload = [
            0x5D, 0x02, 0x00, 0x00,
            0x5C, 0x02, 0x00, 0x00,
            0x13, (byte)'S', (byte)'p', (byte)'e', (byte)'c', (byte)'u', (byte)'l', (byte)'a', (byte)'r', (byte)' ', (byte)'I', (byte)'n', (byte)'t', (byte)'e', (byte)'n', (byte)'s', (byte)'i', (byte)'t', (byte)'y', 0,
            0x0E, (byte)'S', (byte)'p', (byte)'e', (byte)'c', (byte)'u', (byte)'l', (byte)'a', (byte)'r', (byte)'C', (byte)'o', (byte)'l', (byte)'o', (byte)'r', 0,
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x20, 0x41,
            0x0A, 0xD7, 0x23, 0x3C
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 0, guid: FourCc("slfl"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);

        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 0x25D, "Slider-with-memo packet should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 0x25C, "Slider-with-memo payload should preserve the parent id.");
        Assert(payloadText.Contains("Specular Intensity"), "Slider-with-memo payload should preserve the title.");
        Assert(payloadText.Contains("SpecularColor"), "Slider-with-memo payload should preserve the memo/help string.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Slider-with-memo payload should inject a default fill color.");
        Assert(payload.Length > 55, "Slider-with-memo payload should be larger than the memo-less slider layout.");
        Assert(payload[^1] == 0, "Slider-with-memo payload should still append the default exponential flag.");
    }

    private static void VerifyBankIngressAddsColorFillColor()
    {
        byte[] legacyPayload = [
            0xB8, 0x00, 0x00, 0x00,
            0xB7, 0x00, 0x00, 0x00,
            0x11, (byte)'C', (byte)'o', (byte)'l', (byte)'o', (byte)'r', (byte)' ', (byte)'C', (byte)'o', (byte)'r', (byte)'r', (byte)'e', (byte)'c', (byte)'t', (byte)'i', (byte)'o', (byte)'n', 0,
            0x19, (byte)'G', (byte)'l', (byte)'o', (byte)'b', (byte)'a', (byte)'l', (byte)' ', (byte)'c', (byte)'o', (byte)'l', (byte)'o', (byte)'r', (byte)' ', (byte)'c', (byte)'o', (byte)'r', (byte)'r', (byte)'e', (byte)'c', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'.', 0,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x80, 0x3F
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 0, guid: FourCc("vclr"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);

        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 0xB8, "Color packet should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 0xB7, "Color payload should preserve the parent id.");
        Assert(payloadText.Contains("Color Correction"), "Color payload should preserve the widget title.");
        Assert(payloadText.Contains("Global color correction."), "Color payload should preserve the widget memo.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Color payload should inject a default fill color.");
    }

    private static void VerifyBankIngressAddsBankCreateFields()
    {
        byte[] legacyPayload = [
            0x02, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x05, (byte)'T', (byte)'i', (byte)'m', (byte)'e', 0
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 0, guid: FourCc("bank"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);

        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 2, "Bank create should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 0, "Root bank create should preserve the root parent id.");
        Assert(payloadText.Contains("Time"), "Bank create should preserve the title.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Bank create should inject a default fill color.");
    }

    private static void VerifyBankIngressTranslatesLegacyComboCreate()
    {
        byte[] legacyPayload = [
            0x49, 0x08, 0, 0,
            0x3D, 0x08, 0, 0,
            0x0E, (byte)'R', (byte)'e', (byte)'n', (byte)'d', (byte)'e', (byte)'r', (byte)' ', (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 0,
            0x0F, (byte)'r', (byte)'e', (byte)'n', (byte)'d', (byte)'e', (byte)'r', (byte)' ', (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', (byte)'s', 0,
            0x00, 0x00, 0x00, 0x00,
            0x1E, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 4, guid: FourCc("cos3"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        ushort createCmd = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2));
        byte[] payload = modernPacket.AsSpan(12).ToArray();
        string payloadText = System.Text.Encoding.ASCII.GetString(payload);
        int titleExtent = 1 + payload[4];

        Assert(createCmd == 0, "Legacy combo create command 4 should be translated to a modern CREATE command.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4)) == 0x0849, "Combo packet should move the legacy widget id into the modern packet id.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) == 0x083D, "Combo payload should preserve the parent id.");
        Assert(payloadText.Contains("Render target"), "Combo payload should preserve the title.");
        Assert(payloadText.Contains("render targets"), "Combo payload should preserve the combo memo string.");
        Assert(payloadText.Contains("ARGBColor:0:0:0:0"), "Combo payload should inject a default fill color.");
    }

    private static void VerifyBankIngressTranslatesLegacyComboItemUpdate()
    {
        byte[] legacyPayload = [
            0x49, 0x08, 0, 0,
            0x02, 0, 0, 0,
            0x10, (byte)'S', (byte)'o', (byte)'u', (byte)'n', (byte)'d', (byte)'T', (byte)'y', (byte)'p', (byte)'e', (byte)'U', (byte)'p', (byte)'d', (byte)'a', (byte)'t', (byte)'e', 0
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 3, guid: FourCc("cos3"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4));
        byte[] payload = modernPacket.AsSpan(12).ToArray();

        Assert(command == 5, "Legacy combo item updates should map to modern USER_0, not FILL_COLOR.");
        Assert(id == 0x0849, "Combo item update should preserve the widget id.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)) == 2, "Combo item update should preserve the item index.");
        Assert(System.Text.Encoding.ASCII.GetString(payload.AsSpan(4)).Contains("SoundTypeUpdate"), "Combo item update should preserve the item text.");
    }

    private static void VerifyBankIngressTranslatesLegacyGroupOpenUpdate()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 8, command: 2, guid: FourCc("grup"), payload: [0x34, 0x12, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4));
        byte[] payload = modernPacket.AsSpan(12).ToArray();

        Assert(command == 5, "Legacy group open-state updates should map to modern USER_0.");
        Assert(id == 0x1234, "Group open-state update should preserve the widget id.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(payload) == 1, "Group open-state update should preserve the open value.");
    }

    private static void VerifyBankIngressTranslatesLegacyBankManagerInitFinished()
    {
        byte[] legacyPacket = BuildLegacyBankPacket(length: 4, command: 2, guid: FourCc("bmgr"), payload: [0x01, 0x00, 0x00, 0x00]);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(modernPacket.AsSpan(8, 4));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(0, 2));

        Assert(command == 7, "Legacy bank-manager init packets should map to modern USER_2.");
        Assert(id == 1, "Bank-manager init packet should preserve the manager id.");
        Assert(length == 0, "Bank-manager init packet should not carry residual payload after id extraction.");
    }

    private static void VerifyBankIngressTranslatesLegacyBankManagerTimeString()
    {
        byte[] legacyPayload = [
            0x33, 0x46, 0x50, 0x53, 0x3A, 0x20, 0x31, 0x34,
            0x20, 0x55, 0x70, 0x64, 0x61, 0x74, 0x65, 0x3A,
            0x20, 0x30, 0x2E, 0x30, 0x39, 0x20, 0x6D, 0x73,
            0x00
        ];
        byte[] legacyPacket = BuildLegacyBankPacket(length: (ushort)legacyPayload.Length, command: 8, guid: FourCc("bmgr"), payload: legacyPayload);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(WrapNoCompression(legacyPacket));

        byte[] modernPacket = forwarded[0].AsSpan(9).ToArray();
        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(2, 2));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(modernPacket.AsSpan(0, 2));
        string payloadText = System.Text.Encoding.ASCII.GetString(modernPacket.AsSpan(12, length));

        Assert(command == 10, "Legacy bank-manager timing packets should map to modern USER_5.");
        Assert(payloadText.StartsWith("3FPS:", StringComparison.Ordinal), "Bank-manager timing payload should preserve the original text.");
    }

    private static void VerifyBankIngressSynthesizesOutputSetupAfterInitFinished()
    {
        byte[] legacyCreate = BuildLegacyBankPacket(length: 4, command: 0, guid: FourCc("bmgr"), payload: [0x01, 0x00, 0x00, 0x00]);
        byte[] legacyInit = BuildLegacyBankPacket(length: 4, command: 2, guid: FourCc("bmgr"), payload: [0x01, 0x00, 0x00, 0x00]);

        BankIngressNormalizer normalizer = new();
        IReadOnlyList<byte[]> createForwarded = normalizer.ProcessChunk(WrapNoCompression(legacyCreate));
        IReadOnlyList<byte[]> initForwarded = normalizer.ProcessChunk(WrapNoCompression(legacyInit));

        Assert(createForwarded.Count == 1, "Bank-manager CREATE should still forward once.");
        Assert(initForwarded.Count == 1, "Bank-manager init-finished should still forward once.");

        byte[] createPacketStream = createForwarded[0].AsSpan(9).ToArray();
        byte[] initPacketStream = initForwarded[0].AsSpan(9).ToArray();
        List<(ushort Command, uint Id, byte[] Payload)> createPackets = ParseModernPacketStreamForTest(createPacketStream);
        List<(ushort Command, uint Id, byte[] Payload)> initPackets = ParseModernPacketStreamForTest(initPacketStream);

        Assert(createPackets.Count >= 2, "Bank-manager CREATE burst should include startup replay packets.");
        Assert(createPackets[0].Command == 0 && createPackets[0].Id == 1, "First packet in the create burst should remain the bank-manager CREATE.");
        Assert(createPackets.Any(p => p.Command == 7 && p.Id == 1), "Startup replay should synthesize a modern USER_2 init-finished packet.");
        Assert(!createPackets.Any(p => p.Command == 19 || p.Command == 20), "Startup replay should no longer synthesize extra output channel packets.");

        Assert(initPackets.Count == 1, "Legacy bank-manager init packet should still translate to one modern packet.");
        Assert(initPackets[0].Command == 7, "Legacy bank-manager init packet should still map to modern USER_2.");
        Assert(initPackets[0].Id == 1, "Bank-manager init packet should preserve the manager id.");
        Assert(initPackets[0].Payload.Length == 0, "Bank-manager init packet should not carry residual payload after id extraction.");
    }

    private static byte[] BuildLegacyBankPacket(ushort length, ushort command, int guid, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, sizeof(ushort)), length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, sizeof(ushort)), command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, sizeof(int)), guid);
        payload.CopyTo(packet.AsSpan(8));
        return packet;
    }

    private static byte[] BuildModernBankPacket(ushort length, ushort command, int guid, uint id, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, sizeof(ushort)), length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, sizeof(ushort)), command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, sizeof(int)), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, sizeof(uint)), id);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    private static byte[] WrapCompressed(byte kind, ReadOnlySpan<byte> payload, int decompressedSize)
    {
        byte[] wrapped = new byte[14 + payload.Length];
        wrapped[0] = (byte)'C';
        wrapped[1] = (byte)'H';
        wrapped[2] = kind;
        wrapped[3] = (byte)':';
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4, sizeof(uint)), (uint)payload.Length);
        wrapped[8] = (byte)':';
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(9, sizeof(uint)), (uint)decompressedSize);
        wrapped[13] = (byte)':';
        payload.CopyTo(wrapped.AsSpan(14));
        return wrapped;
    }

    private static int FourCc(string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        if (bytes.Length != 4)
        {
            throw new InvalidOperationException("FourCC must be exactly four ASCII characters.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static byte[] WrapNoCompression(ReadOnlySpan<byte> payload)
    {
        byte[] wrapped = new byte[9 + payload.Length];
        wrapped[0] = (byte)'C';
        wrapped[1] = (byte)'H';
        wrapped[2] = (byte)'N';
        wrapped[3] = (byte)':';
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4, sizeof(uint)), (uint)payload.Length);
        wrapped[8] = (byte)':';
        payload.CopyTo(wrapped.AsSpan(9));
        return wrapped;
    }

    private static RagPacket RewriteBootstrapAppNameForTest(RagPacket packet)
    {
        if (packet.PacketType != AppPacketType.AppName)
        {
            return packet;
        }

        if (!TryReadIndexedCStringForTest(packet.Payload, out uint index, out string? value) || !string.IsNullOrEmpty(value))
        {
            return packet;
        }

        return new RagPacket
        {
            Command = packet.Command,
            Guid = packet.Guid,
            Id = packet.Id,
            Payload = BuildIndexedCStringPayloadForTest(index, @"C:\RagProxy\LegacyGame.exe"),
        };
    }

    private static string ReadIndexedCStringForTest(ReadOnlySpan<byte> payload)
    {
        Assert(TryReadIndexedCStringForTest(payload, out _, out string? value), "Indexed C string payload should parse.");
        return value ?? string.Empty;
    }

    private static bool TryReadIndexedCStringForTest(ReadOnlySpan<byte> payload, out uint index, out string? value)
    {
        index = 0;
        value = null;
        if (payload.Length < sizeof(uint) + 1)
        {
            return false;
        }

        index = BinaryPrimitives.ReadUInt32LittleEndian(payload[..sizeof(uint)]);
        int encodedLength = payload[sizeof(uint)];
        if (payload.Length < sizeof(uint) + 1 + encodedLength)
        {
            return false;
        }

        ReadOnlySpan<byte> stringBytes = payload.Slice(sizeof(uint) + 1, encodedLength);
        if (encodedLength > 0 && stringBytes[^1] == 0)
        {
            stringBytes = stringBytes[..^1];
        }

        value = System.Text.Encoding.ASCII.GetString(stringBytes);
        return true;
    }

    private static byte[] BuildIndexedCStringPayloadForTest(uint index, string value)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(value);
        byte[] payload = new byte[sizeof(uint) + 1 + text.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, sizeof(uint)), index);
        payload[sizeof(uint)] = checked((byte)(text.Length + 1));
        text.CopyTo(payload.AsSpan(sizeof(uint) + 1));
        payload[^1] = 0;
        return payload;
    }

    private static List<(ushort Command, uint Id, byte[] Payload)> ParseModernPacketStreamForTest(ReadOnlySpan<byte> stream)
    {
        List<(ushort Command, uint Id, byte[] Payload)> packets = [];
        int offset = 0;
        while (offset + 12 <= stream.Length)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 0, 2));
            ushort command = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2));
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(stream.Slice(offset + 8, 4));
            int total = 12 + length;
            Assert(offset + total <= stream.Length, "Modern packet stream should contain whole packets.");
            packets.Add((command, id, stream.Slice(offset + 12, length).ToArray()));
            offset += total;
        }

        Assert(offset == stream.Length, "Modern packet stream should parse exactly to the end.");
        return packets;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
