using RagProxyCompat;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            ProxyOptions options = ProxyOptions.Parse(args);

            if (options.RunSelfTest)
            {
                SelfTestRunner.Run();
                return 0;
            }

            using CompatibilityProxy proxy = new(options);
            await proxy.RunAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
