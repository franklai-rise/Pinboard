using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class TextReadingSceneTests
{
    [TestMethod]
    public void Parse_PreservesOriginalClipAndSkipsDeletedLabelsAndCardMetadata()
    {
        var scene = JsonSerializer.Serialize(new
        {
            elements = new object[]
            {
                new { id = "pinboard-inbox-title", type = "text", text = "Text Clips" },
                new { id = "clip-card-1", type = "rectangle", groupIds = new[] { "g1" }, customData = new { pinboard = new { role = "text-clip-card" } } },
                new { id = "old-meta-label", type = "text", text = "Copied 2026-10-01", groupIds = new[] { "g1" } },
                new { id = "content", type = "text", text = "wrapped\ntext", originalText = "原始完整文字\nOriginal text", groupIds = new[] { "g1" }, customData = new { pinboard = new { role = "text-clip", capturedAt = "2026-10-01T23:58:59+08:00", source = "clipboard" } } },
                new { id = "deleted", type = "text", text = "should not be read", isDeleted = true },
                new { id = "manual", type = "text", text = "A manually added note", updated = 1_700_000_000_000L }
            }
        });

        var clips = TextReadingScene.Parse(scene);

        Assert.AreEqual(2, clips.Count);
        Assert.AreEqual("content", clips[0].ElementId);
        Assert.AreEqual("原始完整文字\nOriginal text", clips[0].Text);
        Assert.AreEqual(TimeSpan.FromHours(8), clips[0].CapturedAt!.Value.Offset);
        Assert.AreEqual("clipboard", clips[0].Source);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L), clips[1].CapturedAt);
    }

    [TestMethod]
    public void Parse_MissingElementsAndInvalidTimestampDoNotCreateFalseEntries()
    {
        Assert.AreEqual(0, TextReadingScene.Parse("{}").Count);
        var clips = TextReadingScene.Parse("""
            {"elements":[{"id":"note","type":"text","text":"content","updated":9223372036854775807,
            "customData":{"pinboard":{"capturedAt":"invalid"}}}]}
            """);
        Assert.AreEqual(1, clips.Count);
        Assert.IsNull(clips[0].CapturedAt);
    }

    [TestMethod]
    public void Project_CollapsesOnlyConsecutiveDuplicatesWithoutLosingRecordIds()
    {
        var clips = new[] { Clip("a", "same", 0), Clip("b", "same", 1), Clip("c", "other", 2), Clip("d", "same", 3) };

        var collapsed = TextReadingScene.Project(clips, "same", newestFirst: true, collapseConsecutiveDuplicates: true);

        Assert.AreEqual(2, collapsed.Count);
        Assert.AreEqual("d", collapsed[0].Clip.ElementId);
        Assert.AreEqual(1, collapsed[0].DuplicateCount);
        CollectionAssert.AreEqual(new[] { "a", "b" }, collapsed[1].ElementIds.ToArray());
        Assert.AreEqual("b", collapsed[1].Clip.ElementId);
        Assert.AreEqual(4, clips.Length);
        Assert.AreEqual(3, TextReadingScene.Project(clips, "SAME", false, false).Count);
    }

    [TestMethod]
    public void Project_OrdersByCaptureTimeRatherThanSceneLayoutAndPreservesUndatedNotes()
    {
        var clips = new[] { Clip("later", "two", 5), Clip("earlier", "one", 0), new TextReadingClip("undated", "three", null, "", 9) };
        CollectionAssert.AreEqual(new[] { "later", "earlier", "undated" },
            TextReadingScene.Project(clips, null, true, false).Select(group => group.Clip.ElementId).ToArray());
    }

    [TestMethod]
    public void Excerpt_ShowsTheKeywordNearTheEndOfALongText()
    {
        var text = new string('前', 2_000) + " EnglishKeyword 中文检索 " + new string('后', 500);
        var excerpt = TextReadingScene.Excerpt(text, "englishkeyword");
        StringAssert.Contains(excerpt, "EnglishKeyword");
        Assert.IsTrue(excerpt.StartsWith("… ", StringComparison.Ordinal));
        Assert.IsTrue(excerpt.Length <= 264);
    }

    [TestMethod]
    public void Project_ThousandsOfRepeatedClipsKeepEveryRecordInOneGroup()
    {
        var clips = Enumerable.Range(0, 10_000).Select(index => Clip($"clip-{index}", "repeated copied content", index)).ToArray();
        var groups = TextReadingScene.Project(clips, null, true, true);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(10_000, groups[0].DuplicateCount);
        Assert.AreEqual("clip-9999", groups[0].Clip.ElementId);
        CollectionAssert.AreEqual(clips.Select(clip => clip.ElementId).ToArray(), groups[0].ElementIds.ToArray());
    }

    private static TextReadingClip Clip(string id, string text, int seconds) =>
        new(id, text, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)).AddSeconds(seconds), "clipboard", seconds);
}
