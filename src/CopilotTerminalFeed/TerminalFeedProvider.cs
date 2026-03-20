using System.Text.Json;
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

    private static readonly Dictionary<string, string> VerbToCommand = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launch_claude"] = "claude",
        ["launch_copilot"] = "gh copilot",
        ["launch_shell"] = "cmd.exe",
    };

    public TerminalFeedProvider(TerminalServer server)
    {
        _server = server;
    }

    public void OnFeedProviderEnabled(FeedProviderEnabledArgs args)
    {
        _providerInfo = args.FeedProviderInfo;
        _enabled = true;
        Log.Info($"Feed provider enabled: {_providerInfo.Id}");
    }

    public void OnFeedProviderDisabled(FeedProviderDisabledArgs args)
    {
        _enabled = false;
        Log.Info("Feed provider disabled");
    }

    public void OnFeedEnabled(FeedEnabledArgs args)
    {
        Log.Info("Feed enabled, preparing terminal card");
        SendFeedUpdate();
    }

    public void OnFeedDisabled(FeedDisabledArgs args)
    {
        Log.Info("Feed disabled");
    }

    public void OnCustomQueryReceived(CustomQueryReceivedArgs args)
    {
        var queryData = args.CustomQueryData;
        Log.Info($"Custom query received: {queryData}");

        try
        {
            // Extract verb from the Adaptive Card Action.Execute payload
            var verb = ExtractVerb(queryData);
            if (verb is not null && VerbToCommand.TryGetValue(verb, out var command))
            {
                _server.CreateSession(command);
            }
            else
            {
                Log.Warn($"Unknown verb: {verb ?? "(null)"}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to handle custom query", ex);
        }
    }

    /// <summary>
    /// Extracts the Action.Execute verb from the custom query data.
    /// The Widget Board sends JSON like {"verb":"launch_claude"} or the raw verb string.
    /// </summary>
    private static string? ExtractVerb(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;

        // Try JSON first
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("verb", out var verbProp))
                return verbProp.GetString();
        }
        catch (JsonException) { }

        // Fall back to treating the whole string as the verb
        var trimmed = data.Trim();
        return trimmed.Length > 0 ? trimmed : null;
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
