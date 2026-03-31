using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RagProxyCompat;

internal sealed class CompatibilityProxy : IDisposable
{
    private static readonly string[] ChannelNames = ["bank", "output", "events"];
    private const string SyntheticLegacyAppPath = @"C:\RagProxy\LegacyGame.exe";

    private readonly ProxyOptions _options;
    private readonly TcpListener _listener;
    private readonly object _logLock = new();

    public CompatibilityProxy(ProxyOptions options)
    {
        _options = options;
        _listener = new TcpListener(options.ListenAddress, options.ListenPort);
    }

    public async Task RunAsync()
    {
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Task? viewerTask = null;
        if (_options.ViewerEnabled)
        {
            if (RangesOverlap(
                _options.ProxyBasePortStart,
                _options.ProxyBasePortStart + ChannelNames.Length - 1,
                _options.ViewerProxyBasePort,
                _options.ViewerProxyBasePort + ChannelNames.Length - 1))
            {
                Log($"Warning: viewer proxy base port {_options.ViewerProxyBasePort} overlaps handshake proxy base {_options.ProxyBasePortStart}. Use --viewer-proxy-base-port to avoid collisions.");
            }

            viewerTask = Task.Run(() => RunViewerRelayAsync(cts.Token), cts.Token);
        }

        _listener.Start();
        Log($"Listening on {_options.ListenAddress}:{_options.ListenPort}, forwarding to {_options.TargetAddress}:{_options.TargetPort}");

        while (!cts.Token.IsCancellationRequested)
        {
            TcpClient upstream = await _listener.AcceptTcpClientAsync(cts.Token);
            _ = Task.Run(() => HandleConnectionAsync(upstream, cts.Token), cts.Token);
        }

        if (viewerTask is not null)
        {
            await viewerTask;
        }
    }

    public void Dispose()
    {
        _listener.Stop();
    }

    private async Task HandleConnectionAsync(TcpClient upstreamClient, CancellationToken cancellationToken)
    {
        using TcpClient upstream = upstreamClient;
        using TcpClient downstream = new();
        using CancellationTokenSource connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            Log($"Upstream connected from {upstream.Client.RemoteEndPoint}");

            await downstream.ConnectAsync(_options.TargetAddress, _options.TargetPort, connectionCts.Token);
            Log($"Downstream connected to {_options.TargetAddress}:{_options.TargetPort}");

            using NetworkStream upstreamStream = upstream.GetStream();
            using NetworkStream downstreamStream = downstream.GetStream();

            HandshakeBridgeResult handshake = await BridgeHandshakeAsync(upstreamStream, downstreamStream, connectionCts.Token);
            Log($"Negotiated compatibility mode: {handshake.Translation}");

            using (handshake.ChannelSession)
            {
                Task upstreamToDownstream = PumpAsync(
                    source: upstreamStream,
                    destination: downstreamStream,
                    directionName: "bootstrap game->rag",
                    sourceFormat: RagPacketFormat.Legacy8,
                    destinationFormat: RagPacketFormat.Modern12,
                    transform: static (packet, _) => RewriteBootstrapGamePacket(packet),
                    handshake.Translation.Protocol,
                    connectionCts.Token);

                Task downstreamToUpstream = PumpAsync(
                    source: downstreamStream,
                    destination: upstreamStream,
                    directionName: "bootstrap rag->game",
                    sourceFormat: RagPacketFormat.Modern12,
                    destinationFormat: RagPacketFormat.Legacy8,
                    transform: static (packet, _) => packet,
                    handshake.Translation.Protocol,
                    connectionCts.Token);

                await handshake.ChannelSession.Completion;
                connectionCts.Cancel();
                await Task.WhenAll(upstreamToDownstream, downstreamToUpstream);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
        }
    }

    private async Task<HandshakeBridgeResult> BridgeHandshakeAsync(
        NetworkStream upstream,
        NetworkStream downstream,
        CancellationToken cancellationToken)
    {
        RagPacket? gameHandshake = await RagPacket.ReadAsync(upstream, RagPacketFormat.Legacy8, cancellationToken);
        if (gameHandshake is null)
        {
            throw new EndOfStreamException("Legacy game disconnected before sending handshake.");
        }

        if (gameHandshake.PacketType != AppPacketType.Handshake)
        {
            throw new InvalidOperationException($"Expected HANDSHAKE from legacy game but received {gameHandshake.PacketType}.");
        }

        HandshakeTranslation translation = DetectHandshakeTranslation(gameHandshake);
        Log($"Upstream handshake packet: {gameHandshake}");

        RagPacket forwardedHandshake = translation.RequiresRewrite
            ? gameHandshake.RewriteHandshakeVersion(RagPacket.ModernVersion)
            : gameHandshake;

        await forwardedHandshake.WriteAsync(downstream, RagPacketFormat.Modern12, cancellationToken);
        Log($"Forwarded handshake to downstream as {DescribeHandshake(forwardedHandshake)}");

        RagPacket? ragReply = await RagPacket.ReadAsync(downstream, RagPacketFormat.Modern12, cancellationToken);
        if (ragReply is null)
        {
            throw new EndOfStreamException("Modern RAG disconnected before handshake reply.");
        }

        if (ragReply.PacketType != AppPacketType.Handshake)
        {
            throw new InvalidOperationException($"Expected HANDSHAKE reply from modern RAG but received {ragReply.PacketType}.");
        }

        Log($"Downstream handshake reply: {ragReply}");

        int downstreamBasePort = ragReply.ReadInt32();
        ChannelProxySession channelSession = CreateChannelSession(downstreamBasePort, translation.Protocol, cancellationToken);
        Log($"Reserved proxy base port triplet {channelSession.ProxyBasePort}-{channelSession.ProxyBasePort + ChannelNames.Length - 1} for downstream {downstreamBasePort}-{downstreamBasePort + ChannelNames.Length - 1}");

        RagPacket replyToGame = ragReply.RewriteHandshakeReply(
            translation.RequiresRewrite ? translation.LegacyReplyVersion : ExtractReplyVersion(ragReply),
            basePortOverride: channelSession.ProxyBasePort);

        await replyToGame.WriteAsync(upstream, RagPacketFormat.Legacy8, cancellationToken);
        Log($"Forwarded handshake reply upstream as {DescribeHandshake(replyToGame)}");

        return new HandshakeBridgeResult(translation, channelSession);
    }

    private ChannelProxySession CreateChannelSession(int downstreamBasePort, RagProtocolVersion protocol, CancellationToken cancellationToken)
    {
        for (int candidate = _options.ProxyBasePortStart; candidate <= 65535 - ChannelNames.Length; candidate++)
        {
            TcpListener[] listeners = new TcpListener[ChannelNames.Length];
            try
            {
                for (int i = 0; i < ChannelNames.Length; i++)
                {
                    TcpListener listener = new(_options.ListenAddress, candidate + i);
                    listener.Start();
                    listeners[i] = listener;
                }

                return new ChannelProxySession(
                    proxy: this,
                    listeners: listeners,
                    proxyBasePort: candidate,
                    downstreamBasePort: downstreamBasePort,
                    protocol: protocol,
                    downstreamConnectTimeout: TimeSpan.FromSeconds(5),
                    cancellationToken: cancellationToken);
            }
            catch (SocketException)
            {
                foreach (TcpListener? listener in listeners)
                {
                    listener?.Stop();
                }
            }
        }

        throw new InvalidOperationException($"Failed to reserve three consecutive proxy ports starting at {_options.ProxyBasePortStart}.");
    }

    private async Task RunViewerRelayAsync(CancellationToken cancellationToken)
    {
        int proxyBasePort = _options.ViewerProxyBasePort;
        int downstreamBasePort = _options.ViewerTargetBasePort;

        Log($"Viewer relay enabled: {_options.ListenAddress}:{proxyBasePort}-{proxyBasePort + ChannelNames.Length - 1} -> {_options.TargetAddress}:{downstreamBasePort}-{downstreamBasePort + ChannelNames.Length - 1}");

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpListener[] listeners = new TcpListener[ChannelNames.Length];
            try
            {
                for (int i = 0; i < ChannelNames.Length; i++)
                {
                    TcpListener listener = new(_options.ListenAddress, proxyBasePort + i);
                    listener.Start();
                    listeners[i] = listener;
                }

                using ChannelProxySession session = new(
                    proxy: this,
                    listeners: listeners,
                    proxyBasePort: proxyBasePort,
                    downstreamBasePort: downstreamBasePort,
                    protocol: RagProtocolVersion.LegacyVersioned,
                    downstreamConnectTimeout: TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken);

                await session.Completion;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                foreach (TcpListener? listener in listeners)
                {
                    listener?.Stop();
                }

                Log($"Viewer relay failed to bind to {proxyBasePort}-{proxyBasePort + ChannelNames.Length - 1}: {ex.Message}");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task PumpAsync(
        NetworkStream source,
        NetworkStream destination,
        string directionName,
        RagPacketFormat sourceFormat,
        RagPacketFormat destinationFormat,
        Func<RagPacket, RagProtocolVersion, RagPacket> transform,
        RagProtocolVersion protocol,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RagPacket? packet = await RagPacket.ReadAsync(source, sourceFormat, cancellationToken);
            if (packet is null)
            {
                break;
            }

            RagPacket forwarded = transform(packet, protocol);
            if (_options.Verbose)
            {
                Log($"{directionName}: {forwarded}");
            }

            await forwarded.WriteAsync(destination, destinationFormat, cancellationToken);
        }
    }

    private static RagPacket RewriteBootstrapGamePacket(RagPacket packet)
    {
        if (packet.PacketType != AppPacketType.AppName)
        {
            return packet;
        }

        if (!TryReadIndexedCString(packet.Payload, out uint index, out string? value) || !string.IsNullOrEmpty(value))
        {
            return packet;
        }

        return new RagPacket
        {
            Command = packet.Command,
            Guid = packet.Guid,
            Id = packet.Id,
            Payload = BuildIndexedCStringPayload(index, SyntheticLegacyAppPath),
        };
    }

    private async Task CopyRawAsync(
        NetworkStream source,
        NetworkStream destination,
        string directionName,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    private async Task CopyBankIngressAsync(
        NetworkStream source,
        NetworkStream destination,
        string directionName,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        BankIngressNormalizer normalizer = new(Log);

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, bytesRead);

            IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(chunk);
            foreach (byte[] output in forwarded)
            {
                await destination.WriteAsync(output, cancellationToken);
                if (_options.Verbose)
                {
                    Log($"{directionName} packet len={output.Length}");
                }
            }

            if (forwarded.Count > 0)
            {
                await destination.FlushAsync(cancellationToken);
            }
        }
    }

    private async Task CopyBankEgressAsync(
        NetworkStream source,
        NetworkStream destination,
        string directionName,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        BankEgressNormalizer normalizer = new(Log);

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, bytesRead);

            IReadOnlyList<byte[]> forwarded = normalizer.ProcessChunk(chunk);
            foreach (byte[] output in forwarded)
            {
                await destination.WriteAsync(output, cancellationToken);
                if (_options.Verbose)
                {
                    Log($"{directionName} packet len={output.Length}");
                }
            }

            if (forwarded.Count > 0)
            {
                await destination.FlushAsync(cancellationToken);
            }
        }
    }

    private static HandshakeTranslation DetectHandshakeTranslation(RagPacket packet)
    {
        if (packet.Payload.Length < RagPacket.SingleSize)
        {
            return new HandshakeTranslation(
                RagProtocolVersion.LegacyV0,
                RequiresRewrite: true,
                LegacyReplyVersion: RagPacket.GtaIvVersion,
                "LegacyV0ReplyV1.92");
        }

        float version = packet.ReadSingle();
        if (Math.Abs(version - RagPacket.ModernVersion) < 0.001f)
        {
            return new HandshakeTranslation(RagProtocolVersion.ModernV2, RequiresRewrite: false, LegacyReplyVersion: RagPacket.ModernVersion, "ModernV2");
        }

        return new HandshakeTranslation(
            RagProtocolVersion.LegacyVersioned,
            RequiresRewrite: true,
            LegacyReplyVersion: version,
            $"LegacyV{version:0.###}");
    }

    private static float? ExtractReplyVersion(RagPacket packet)
    {
        if (packet.Payload.Length < RagPacket.Int32Size + RagPacket.SingleSize)
        {
            return null;
        }

        return BitConverter.Int32BitsToSingle(BitConverter.ToInt32(packet.Payload, RagPacket.Int32Size));
    }

    private static string DescribeHandshake(RagPacket packet)
    {
        if (packet.Payload.Length == RagPacket.Int32Size)
        {
            return $"HANDSHAKE reply port={packet.ReadInt32()}";
        }

        if (packet.Payload.Length == RagPacket.SingleSize)
        {
            return $"HANDSHAKE version={packet.ReadSingle():0.###}";
        }

        if (packet.Payload.Length >= RagPacket.Int32Size + RagPacket.SingleSize)
        {
            int port = packet.ReadInt32();
            float version = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(packet.Payload, RagPacket.Int32Size));
            return $"HANDSHAKE reply port={port} version={version:0.###}";
        }

        return packet.ToString();
    }

    private static bool TryReadIndexedCString(ReadOnlySpan<byte> payload, out uint index, out string? value)
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

        value = Encoding.ASCII.GetString(stringBytes);
        return true;
    }

    private static byte[] BuildIndexedCStringPayload(uint index, string value)
    {
        byte[] text = Encoding.ASCII.GetBytes(value);
        byte[] payload = new byte[sizeof(uint) + 1 + text.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, sizeof(uint)), index);
        payload[sizeof(uint)] = checked((byte)(text.Length + 1));
        text.CopyTo(payload.AsSpan(sizeof(uint) + 1));
        payload[^1] = 0;
        return payload;
    }

    private static bool RangesOverlap(int startA, int endA, int startB, int endB)
        => startA <= endB && startB <= endA;

    private async Task<TcpClient?> ConnectWithRetryAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime start = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = new();
            try
            {
                await client.ConnectAsync(address, port, cancellationToken);
                return client;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch (SocketException)
            {
                client.Dispose();
            }

            if (timeout != Timeout.InfiniteTimeSpan && DateTime.UtcNow - start >= timeout)
            {
                break;
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_logLock)
        {
            Console.WriteLine(line);
        }
    }

    private readonly record struct HandshakeTranslation(
        RagProtocolVersion Protocol,
        bool RequiresRewrite,
        float? LegacyReplyVersion,
        string Label)
    {
        public override string ToString() => Label;
    }

    private readonly record struct HandshakeBridgeResult(
        HandshakeTranslation Translation,
        ChannelProxySession ChannelSession);

    private sealed class ChannelProxySession : IDisposable
    {
        private readonly CompatibilityProxy _proxy;
        private readonly TcpListener[] _listeners;
        private readonly int _downstreamBasePort;
        private readonly TimeSpan _downstreamConnectTimeout;
        private readonly CancellationToken _cancellationToken;

        public ChannelProxySession(
            CompatibilityProxy proxy,
            TcpListener[] listeners,
            int proxyBasePort,
            int downstreamBasePort,
            RagProtocolVersion protocol,
            TimeSpan downstreamConnectTimeout,
            CancellationToken cancellationToken)
        {
            _proxy = proxy;
            _listeners = listeners;
            ProxyBasePort = proxyBasePort;
            _downstreamBasePort = downstreamBasePort;
            _downstreamConnectTimeout = downstreamConnectTimeout;
            _cancellationToken = cancellationToken;
            Completion = Task.WhenAll(RunChannelAsync(0), RunChannelAsync(1), RunChannelAsync(2));
        }

        public int ProxyBasePort { get; }

        public Task Completion { get; }

        public void Dispose()
        {
            foreach (TcpListener listener in _listeners)
            {
                listener.Stop();
            }
        }

        private async Task RunChannelAsync(int channelIndex)
        {
            string channelName = ChannelNames[channelIndex];
            TcpListener listener = _listeners[channelIndex];
            int proxyPort = ProxyBasePort + channelIndex;
            int downstreamPort = _downstreamBasePort + channelIndex;

            try
            {
                _proxy.Log($"Waiting for {channelName} connection on {_proxy._options.ListenAddress}:{proxyPort} -> {_proxy._options.TargetAddress}:{downstreamPort}");

                using TcpClient upstream = await listener.AcceptTcpClientAsync(_cancellationToken);
                TcpClient? downstream = await _proxy.ConnectWithRetryAsync(
                    _proxy._options.TargetAddress,
                    downstreamPort,
                    _downstreamConnectTimeout,
                    _cancellationToken);
                if (downstream is null)
                {
                    _proxy.Log($"{channelName} relay failed: unable to connect to {_proxy._options.TargetAddress}:{downstreamPort} within {_downstreamConnectTimeout.TotalSeconds:0.#}s");
                    return;
                }

                using (downstream)
                {
                    _proxy.Log($"{channelName} connected: game {upstream.Client.RemoteEndPoint} -> rag {_proxy._options.TargetAddress}:{downstreamPort}");

                    using NetworkStream upstreamStream = upstream.GetStream();
                    using NetworkStream downstreamStream = downstream.GetStream();

                    Task upstreamToDownstream = channelName == "bank"
                        ? _proxy.CopyBankIngressAsync(
                            source: upstreamStream,
                            destination: downstreamStream,
                            directionName: $"{channelName} game->rag",
                            cancellationToken: _cancellationToken)
                        : _proxy.CopyRawAsync(
                            source: upstreamStream,
                            destination: downstreamStream,
                            directionName: $"{channelName} game->rag",
                            cancellationToken: _cancellationToken);

                    Task downstreamToUpstream = channelName == "bank"
                        ? _proxy.CopyBankEgressAsync(
                            source: downstreamStream,
                            destination: upstreamStream,
                            directionName: $"{channelName} rag->game",
                            cancellationToken: _cancellationToken)
                        : _proxy.CopyRawAsync(
                            source: downstreamStream,
                            destination: upstreamStream,
                            directionName: $"{channelName} rag->game",
                            cancellationToken: _cancellationToken);

                    await Task.WhenAny(upstreamToDownstream, downstreamToUpstream);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _proxy.Log($"{channelName} relay failed: {ex.Message}");
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
