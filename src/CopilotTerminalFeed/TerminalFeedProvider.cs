using CopilotTerminalFeed.Server;
using Microsoft.Windows.Widgets.Feeds;
using Microsoft.Windows.Widgets.Feeds.Providers;

namespace CopilotTerminalFeed;

/// <summary>
/// Implements IFeedProvider to supply a terminal feed card to the Windows Widget Board.
/// The feed content is an iframe pointing to the local terminal server's xterm.js UI,
/// authenticated with a per-session token.
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

    public void OnFeedProviderEnabled(FeedProviderEnabledArgs args)
    {
        _providerInfo = args.FeedProviderInfo;
        _enabled = true;
        Console.WriteLine($"Feed provider enabled: {_providerInfo.Id}");
    }

    public void OnFeedProviderDisabled(FeedProviderDisabledArgs args)
    {
        _enabled = false;
        Console.WriteLine("Feed provider disabled");
    }

    public void OnFeedEnabled(FeedEnabledArgs args)
    {
        Console.WriteLine("Feed enabled, preparing terminal card");
        SendFeedUpdate();
    }

    public void OnFeedDisabled(FeedDisabledArgs args)
    {
        Console.WriteLine("Feed disabled");
    }

    public void OnCustomQueryReceived(CustomQueryReceivedArgs args)
    {
        var query = args.CustomQueryData;
        Console.WriteLine($"Custom query received: {query}");

        try
        {
            if (query.Contains("launch_claude"))
            {
                _server.CreateSession("claude");
            }
            else if (query.Contains("launch_copilot"))
            {
                _server.CreateSession("gh copilot");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to handle custom query: {ex.Message}");
        }
    }

    private void SendFeedUpdate()
    {
        if (!_enabled || _providerInfo is null) return;

        var terminalUrl = _server.TerminalUrl;
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
                                    "text": "Copilot Terminal",
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
