using System.Text.Json;
using Xunit;

namespace CopilotTerminalFeed.Tests;

/// <summary>
/// Tests for the verb extraction logic used in TerminalFeedProvider.OnCustomQueryReceived.
/// Extracted to a static helper so it can be tested without COM activation.
/// </summary>
public class FeedProviderVerbTests
{
    // Mirror the extraction logic from TerminalFeedProvider
    private static string? ExtractVerb(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("verb", out var verbProp))
                return verbProp.GetString();
        }
        catch (JsonException) { }

        var trimmed = data.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    [Fact]
    public void Parses_Json_Verb_Property()
    {
        var result = ExtractVerb("{\"verb\":\"launch_claude\"}");
        Assert.Equal("launch_claude", result);
    }

    [Fact]
    public void Parses_Json_With_Extra_Properties()
    {
        var result = ExtractVerb("{\"verb\":\"launch_copilot\",\"extra\":123}");
        Assert.Equal("launch_copilot", result);
    }

    [Fact]
    public void Falls_Back_To_Raw_String()
    {
        var result = ExtractVerb("launch_claude");
        Assert.Equal("launch_claude", result);
    }

    [Fact]
    public void Trims_Whitespace()
    {
        var result = ExtractVerb("  launch_shell  ");
        Assert.Equal("launch_shell", result);
    }

    [Fact]
    public void Returns_Null_For_Empty()
    {
        Assert.Null(ExtractVerb(""));
        Assert.Null(ExtractVerb("   "));
    }

    [Fact]
    public void Returns_Null_For_Null_Input()
    {
        Assert.Null(ExtractVerb(null!));
    }

    [Fact]
    public void Handles_Json_Without_Verb()
    {
        var result = ExtractVerb("{\"action\":\"something\"}");
        // No verb property -> falls back to raw string which is the whole JSON
        Assert.NotNull(result);
    }
}
