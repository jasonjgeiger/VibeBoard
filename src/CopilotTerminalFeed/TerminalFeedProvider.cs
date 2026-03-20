using CopilotTerminalFeed.Server;
using Microsoft.Windows.Widgets.Feeds;
using Microsoft.Windows.Widgets.Feeds.Providers;

namespace CopilotTerminalFeed;

/// <summary>
/// Implements IFeedProvider to supply a terminal feed card to the Windows Widget Board.
/// The feed content is an iframe pointing to the local terminal server's xterm.js UI.
/// </summary>
public sealed class TerminalFeedProvider : IFeedProvider
{
    private readonly TerminalServer _server;
    private FeedProviderInfo? _providerInfo;
    private bool _enabled;

    public TerminalFeedProvider(TerminalServer server)
    {
        _server = server;
    }

    /// <summary>
    /// Called when the Widget Board activates this feed provider.
    /// </summary>
    public void OnFeedProviderEnabled(FeedProviderEnabledArgs args)
    {
        _providerInfo = args.FeedProviderInfo;
        _enabled = true;
        Console.WriteLine($"Feed provider enabled: {_providerInfo.Id}");
    }

    /// <summary>
    /// Called when the Widget Board deactivates this feed provider.
    /// </summary>
    public void OnFeedProviderDisabled(FeedProviderDisabledArgs args)
    {
        _enabled = false;
        Console.WriteLine("Feed provider disabled");
    }

    /// <summary>
    /// Called when the Widget Board requests feed content.
    /// Returns an Adaptive Card with a ContentUri pointing to the local terminal page.
    /// </summary>
    public void OnFeedEnabled(FeedEnabledArgs args)
    {
        Console.WriteLine("Feed enabled, preparing terminal card");
        SendFeedUpdate();
    }

    public void OnFeedDisabled(FeedDisabledArgs args)
    {
        Console.WriteLine("Feed disabled");
    }

    /// <summary>
    /// Handles custom actions from the feed card (e.g., button clicks).
    /// </summary>
    public void OnCustomQueryReceived(CustomQueryReceivedArgs args)
    {
        var query = args.CustomQueryData;
        Console.WriteLine($"Custom query received: {query}");

        // Handle terminal commands like launching specific shells
        if (query.Contains("launch_claude"))
        {
            _server.CreateSession("claude");
        }
        else if (query.Contains("launch_copilot"))
        {
            _server.CreateSession("gh copilot");
        }
    }

    private void SendFeedUpdate()
    {
        if (!_enabled || _providerInfo is null) return;

        // Build the Adaptive Card JSON that embeds the terminal via ContentUri
        var terminalUrl = $"http://localhost:{_server.Port}/terminal";
        var adaptiveCardJson = BuildAdaptiveCard(terminalUrl);

        var update = new FeedUpdateRequestOptions(_providerInfo.Id)
        {
            Template = adaptiveCardJson,
            Data = "{}"
        };

        FeedManager.GetDefault().SetFeedContent(update);
    }

    private static string BuildAdaptiveCard(string terminalUrl)
    {
        return $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [
                {
                    "type": "ColumnSet",
                    "columns": [
                        {
                            "type": "Column",
                            "width": "auto",
                            "items": [
                                {
                                    "type": "TextBlock",
                                    "text": "⌨ Copilot Terminal",
                                    "weight": "Bolder",
                                    "size": "Medium"
                                }
                            ]
                        },
                        {
                            "type": "Column",
                            "width": "stretch",
                            "items": [
                                {
                                    "type": "ActionSet",
                                    "actions": [
                                        {
                                            "type": "Action.Execute",
                                            "title": "Claude",
                                            "verb": "launch_claude"
                                        },
                                        {
                                            "type": "Action.Execute",
                                            "title": "Copilot",
                                            "verb": "launch_copilot"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                },
                {
                    "type": "Container",
                    "minHeight": "400px",
                    "items": [
                        {
                            "type": "Media",
                            "contentUri": "{{terminalUrl}}"
                        }
                    ]
                }
            ]
        }
        """;
    }
}
