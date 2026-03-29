using System.Net;

namespace RagProxyCompat;

internal sealed record ProxyOptions(
    IPAddress ListenAddress,
    int ListenPort,
    IPAddress TargetAddress,
    int TargetPort,
    int ProxyBasePortStart,
    bool Verbose,
    string? DumpFilePath,
    bool RunSelfTest)
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

        if (values.ContainsKey("--self-test"))
        {
            return new ProxyOptions(IPAddress.Loopback, 2000, IPAddress.Loopback, 2001, 61000, true, null, true);
        }

        IPAddress listenAddress = ParseAddress(values, "--listen-address", IPAddress.Any);
        int listenPort = ParsePort(values, "--listen-port", 2000);
        IPAddress targetAddress = ParseAddress(values, "--target-address", IPAddress.Loopback);
        int targetPort = ParsePort(values, "--target-port", 2001);
        int proxyBasePortStart = ParsePort(values, "--proxy-base-port", 61000);
        bool verbose = values.ContainsKey("--verbose");
        string? dumpFilePath = ParseOptionalString(values, "--dump-file");

        return new ProxyOptions(listenAddress, listenPort, targetAddress, targetPort, proxyBasePortStart, verbose, dumpFilePath, false);
    }

    private static string? ParseOptionalString(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
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
        Console.WriteLine("  --verbose               Print packet flow.");
        Console.WriteLine("  --dump-file <path>      Append raw packet traces to a log file.");
        Console.WriteLine("  --self-test             Run local protocol checks and exit.");
        Console.WriteLine("  --help                  Show this help.");
    }
}
