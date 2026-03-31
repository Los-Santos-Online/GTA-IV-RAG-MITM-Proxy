using System.Net;

namespace RagProxyCompat;

internal sealed record ProxyOptions(
    IPAddress ListenAddress,
    int ListenPort,
    IPAddress TargetAddress,
    int TargetPort,
    int ProxyBasePortStart,
    bool Verbose,
    bool ViewerEnabled,
    int ViewerProxyBasePort,
    int ViewerTargetBasePort)
{
    public static ProxyOptions Parse(string[] args)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown argument '{arg}'.");
            }

            string key = arg;
            string? value = null;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            values[key] = value;
        }

        if (values.ContainsKey("--help"))
        {
            PrintHelp();
            Environment.Exit(0);
        }

        IPAddress listenAddress = ParseAddress(values, "--listen-address", IPAddress.Any);
        int listenPort = ParsePort(values, "--listen-port", 2000);
        IPAddress targetAddress = ParseAddress(values, "--target-address", IPAddress.Loopback);
        int targetPort = ParsePort(values, "--target-port", 2001);
        int proxyBasePortStart = ParsePort(values, "--proxy-base-port", 61000);
        bool verbose = values.ContainsKey("--verbose");

        bool viewerEnabled = values.ContainsKey("--viewer")
            || values.ContainsKey("--viewer-proxy-base-port")
            || values.ContainsKey("--viewer-target-base-port");
        int viewerProxyBasePort = ParsePort(values, "--viewer-proxy-base-port", proxyBasePortStart);
        int viewerTargetBasePort = ParsePort(values, "--viewer-target-base-port", 60000);

        return new ProxyOptions(
            listenAddress,
            listenPort,
            targetAddress,
            targetPort,
            proxyBasePortStart,
            verbose,
            viewerEnabled,
            viewerProxyBasePort,
            viewerTargetBasePort);
    }

    private static IPAddress ParseAddress(IReadOnlyDictionary<string, string?> values, string key, IPAddress fallback)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new ArgumentException($"Invalid IP address for {key}: '{value}'.");
        }

        return address;
    }

    private static int ParsePort(IReadOnlyDictionary<string, string?> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"Invalid port for {key}: '{value}'.");
        }

        return port;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("RagProxyCompat");
        Console.WriteLine("Bridges legacy GTA IV-style RAG traffic to newer GTA V-era RAG, including bootstrap and base-port relays.");
        Console.WriteLine("Known compatibility path: GTA IV-style handshake version 1.92 <-> modern RAG version 2.0.");
        Console.WriteLine("  --listen-address <ip>   Address exposed to the legacy game. Default: 0.0.0.0");
        Console.WriteLine("  --listen-port <port>    Handshake port exposed to the legacy game. Default: 2000");
        Console.WriteLine("  --target-address <ip>   Modern RAG endpoint. Default: 127.0.0.1");
        Console.WriteLine("  --target-port <port>    Modern RAG handshake port. Default: 2001");
        Console.WriteLine("  --proxy-base-port <p>   First local port for proxied bank/output/events triplets. Default: 61000");
        Console.WriteLine("  --viewer               Enable ragviewer relay (no handshake).");
        Console.WriteLine("  --viewer-proxy-base-port <p>  Base port exposed to legacy ragviewer. Default: --proxy-base-port");
        Console.WriteLine("  --viewer-target-base-port <p> Base port on modern RAG viewer. Default: 60000");
        Console.WriteLine("  --verbose               Print packet flow.");
        Console.WriteLine("  --help                  Show this help.");
    }
}
