using System.Text.Json;
using System.Text.Json.Nodes;
using TypeLay.Protocol;
using Xunit;

namespace TypeLay.Protocol.Tests;

public sealed class EditorDraftTextTests
{
    [Fact]
    public async Task NativeJsonRelayKeepsEveryDraftChangeIncludingAnIncompleteEmoji()
    {
        string[] frames = ["\"Draft \"", "{\"utf16\":[68,114,97,102,116,32,55357]}",
            "\"Draft \\uD83D\\uDE42\"", "{\"utf16\":[68,114,97,102,116,32,56898]}", "\"Recovered\""];
        string[] expected = ["Draft ", "Draft \ud83d", "Draft 🙂", "Draft \ude42", "Recovered"];
        using var native = new MemoryStream();
        foreach (string frame in frames)
            await Wire.WriteAsync(native, JsonNode.Parse(frame), TestContext.Current.CancellationToken);
        native.Position = 0;
        foreach (string draft in expected)
        {
            var value = await Wire.ReadAsync<JsonElement>(native, TestContext.Current.CancellationToken);
            Assert.True(EditorDraftText.TryRead(value, out string text));
            Assert.Equal(draft, text);
        }
        Assert.Equal(native.Length, native.Position);
    }

    [Fact]
    public void EveryUtf16UnitSurvivesTheDraftProjectionWithoutRepairOrReplacement()
    {
        const int batchSize = 4096;
        for (int first = 0; first <= ushort.MaxValue; first += batchSize)
        {
            int[] units = Enumerable.Range(first, batchSize).ToArray();
            var value = JsonSerializer.SerializeToElement(new
            {
                utf16 = units
            });
            Assert.True(EditorDraftText.TryRead(value, out string text));
            Assert.Equal(new string(units.Select(unit => (char)unit).ToArray()), text);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"utf16\":null}")]
    [InlineData("{\"utf16\":[-1]}")]
    [InlineData("{\"utf16\":[65536]}")]
    [InlineData("{\"utf16\":[1.5]}")]
    [InlineData("{\"utf16\":[true]}")]
    [InlineData("{\"utf16\":[\"55357\"]}")]
    [InlineData("{\"utf16\":[],\"text\":\"conflicting\"}")]
    [InlineData("{\"utf16\":[],\"utf16\":[65]}")]
    [InlineData("\"\\uD83D\"")]
    public void MalformedOrAmbiguousDraftPayloadCannotBecomeText(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(EditorDraftText.TryRead(document.RootElement, out _));
    }

    [Fact]
    public void EmptyAndOverWriteLimitDraftsRemainObservableButFrameSizedArraysAreBounded()
    {
        foreach (int length in new[] { 0, 8192, 8193 })
        {
            var value = JsonSerializer.SerializeToElement(new
            {
                utf16 = Enumerable.Repeat(65, length)
            });
            Assert.True(EditorDraftText.TryRead(value, out string text));
            Assert.Equal(new string('A', length), text);
        }
        var impossibleFrame = JsonSerializer.SerializeToElement(new
        {
            utf16 = new int[Wire.MaxFrameBytes / 2]
        });
        Assert.False(EditorDraftText.TryRead(impossibleFrame, out _));
    }
}
