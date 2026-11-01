using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public class WorkspacePresentationTests
{
    [TestMethod]
    public void DailyLabels_SeparateFilenameFromTodayAndMonthPresentation()
    {
        var today = new DateOnly(2026, 11, 1);
        Assert.AreEqual("今日 · 2026-11-01", WorkspacePresentation.DailyTitle("2026-11-01.pinboard", "2026-11-01", today, true));
        Assert.AreEqual("Today · 2026-11-01", WorkspacePresentation.DailyTitle("2026-11-01.pinboard", "2026-11-01", today, false));
        Assert.AreEqual("2026-11-01", WorkspacePresentation.DailyTitle("2026-11-01.pinboard", "2026-11-01", today.AddDays(1), true));
        Assert.AreEqual("本月 · 2026-11", WorkspacePresentation.MonthTitle(today, today, true));
        Assert.AreEqual("2026-10", WorkspacePresentation.MonthTitle(today.AddDays(-1), today, true));
        Assert.AreEqual("Notes", WorkspacePresentation.DailyTitle("Notes.pinboard", "Notes", today, false));
    }

    [TestMethod]
    public void LayoutDefaultsAndBounds_KeepNarrowReadersUsable()
    {
        Assert.AreEqual(272d, WorkspacePresentation.SidebarWidth(double.NaN, 1200));
        Assert.AreEqual(220d, WorkspacePresentation.SidebarWidth(420, 778));
        Assert.AreEqual(420d, WorkspacePresentation.SidebarWidth(900, 1500));
        Assert.AreEqual(0.43, WorkspacePresentation.ReaderRatio(double.PositiveInfinity));
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SidebarCollapsed\":true,\"TextCaptureEnabled\":true,\"TextPrivacyModeEnabled\":false}")!;
        Assert.AreEqual(272d, settings.SidebarWidth);
        Assert.AreEqual(0.43, settings.TextReaderListRatio);
        Assert.IsTrue(settings.TextCaptureEnabled);
        Assert.IsFalse(settings.TextPrivacyModeEnabled);
    }

    [TestMethod]
    public void DrawingClipboard_RejectsInternalPayloadBeforeTextTruncation()
    {
        Assert.IsTrue(ClipboardTextCapturePolicy.IsDrawingPayload("{\"type\":\"excalidraw/clipboard\",\"elements\":[],\"files\":{\"data\":\"" + new string('x', 200_000) + "\"}}"));
        Assert.IsTrue(ClipboardTextCapturePolicy.IsDrawingPayload(" {\"elements\":[],\"type\":\"excalidraw/clipboard\"}"));
        Assert.IsFalse(ClipboardTextCapturePolicy.IsDrawingPayload("{\"type\":\"ordinary\",\"elements\":[]}"));
        Assert.IsFalse(ClipboardTextCapturePolicy.IsDrawingPayload("{\"example\":{\"type\":\"excalidraw/clipboard\",\"elements\":[]}}"));
        Assert.IsFalse(ClipboardTextCapturePolicy.IsDrawingPayload("{\"type\":\"excalidraw/clipboard\",\"notes\":\"keep me\"}"));
        Assert.IsFalse(ClipboardTextCapturePolicy.IsDrawingPayload("A note about excalidraw/clipboard and data:image/png."));
        Assert.IsFalse(ClipboardTextCapturePolicy.IsDrawingPayload("{\"broken\":"));
    }
}
