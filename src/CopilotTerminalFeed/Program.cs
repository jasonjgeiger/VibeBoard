using CopilotTerminalFeed.Server;
using Microsoft.Windows.Widgets;
using System.Runtime.InteropServices;

namespace CopilotTerminalFeed;

/// <summary>
/// Entry point. Handles COM activation for the feed provider and starts the local terminal server.
/// </summary>
public static class Program
{
    private const string FeedProviderClsid = "BF483014-A3C5-4D64-9553-F13714B2D196";

    [STAThread]
    public static async Task Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        // Start the local HTTP + WebSocket terminal server
        var server = new TerminalServer();
        var serverTask = server.StartAsync();

        // Register the feed provider with the Widget Board
        var provider = new TerminalFeedProvider(server);
        FeedManager.GetDefault().Register(provider);

        Console.WriteLine($"CopilotTerminalFeed running. Terminal server on port {server.Port}");

        // Keep the process alive until signalled to exit
        var exitEvent = new ManualResetEventSlim(false);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => exitEvent.Set();

        exitEvent.Wait();

        // Cleanup
        FeedManager.GetDefault().Revoke();
        await server.StopAsync();
    }
}
