using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class ClipboardSnapshotTests
{
    private static readonly DateTimeOffset BeforeMidnight = new(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(8));

    [TestMethod]
    public void StableTextRead_UsesExactSequenceAndNotificationTime()
    {
        var snapshot = ReadText(21, () => 21, () => "copied before midnight");

        Assert.AreEqual(ClipboardReadState.Captured, snapshot.State);
        Assert.AreEqual(21U, snapshot.Sequence);
        Assert.AreEqual(BeforeMidnight, snapshot.CapturedAt);
        Assert.AreEqual("copied before midnight", snapshot.Value);
    }

    [TestMethod]
    public void ClipboardChangedDuringRead_DoesNotMislabelOldTextWithNewSequence()
    {
        uint sequence = 21;
        var snapshot = ReadText(21, () => sequence, () => { sequence = 22; return "old text"; });

        Assert.AreEqual(ClipboardReadState.Changed, snapshot.State);
        Assert.AreEqual(22U, snapshot.Sequence);
        Assert.IsNull(snapshot.Value);
    }

    [TestMethod]
    public void ClipboardChangedBeforeRead_SkipsStaleRead()
    {
        var attempts = 0;
        var snapshot = ReadText(21, () => 22, () => { attempts++; return "new text"; });

        Assert.AreEqual(ClipboardReadState.Changed, snapshot.State);
        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public void DeliberateRepeatedCopies_HaveSeparateStableSnapshots()
    {
        var first = ReadText(21, () => 21, () => "same text");
        var second = ReadText(22, () => 22, () => "same text");
        var third = ReadText(23, () => 23, () => "same text");

        Assert.IsTrue(new[] { first, second, third }.All(item => item.State == ClipboardReadState.Captured));
        CollectionAssert.AreEqual(new uint[] { 21, 22, 23 }, new[] { first.Sequence, second.Sequence, third.Sequence });
    }

    [TestMethod]
    public void BusyClipboard_ReturnsImmediatelyForCallerRetry()
    {
        var attempts = 0;
        var snapshot = ReadText(21, () => 21, () =>
        {
            attempts++;
            throw new ExternalException("busy", unchecked((int)0x800401D0));
        });

        Assert.AreEqual(ClipboardReadState.Busy, snapshot.State);
        Assert.AreEqual(1, attempts, "The immediate reader must not sleep or retry inside the message callback.");
    }

    [TestMethod]
    public void PermanentReadFailure_IsNotRetriedAsClipboardBusy()
    {
        var snapshot = ReadText(21, () => 21, () => throw new InvalidDataException("unsupported data"));

        Assert.AreEqual(ClipboardReadState.Failed, snapshot.State);
        Assert.IsNull(snapshot.Value);
    }

    [TestMethod]
    public void ImageOrFileOnlyClipboard_IsIgnoredWithoutBusyRetry()
    {
        var reads = 0;
        var snapshot = ReadText(21, () => 21, () => { reads++; return null; });

        Assert.AreEqual(ClipboardReadState.Ignored, snapshot.State);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public void ImageSnapshot_AlsoRejectsChangingSequence()
    {
        uint sequence = 21;
        var snapshot = ClipboardSnapshotReader.Read(21, BeforeMidnight, () => sequence,
            () => { sequence = 22; return new byte[] { 1, 2, 3 }; }, value => value.Length > 0, _ => false);

        Assert.AreEqual(ClipboardReadState.Changed, snapshot.State);
        Assert.IsNull(snapshot.Value);
    }

    [TestMethod]
    public void TextLimit_IsExplicitAndRetainsNormalCopiesUnchanged()
    {
        const string original = "Normal multiline text\n第二行";
        Assert.AreEqual(original, ClipboardTextCapturePolicy.Truncate(original));
        var truncated = ClipboardTextCapturePolicy.Truncate(new string('字', 100_001));
        Assert.AreEqual(100_000, truncated.Length);
        Assert.IsTrue(truncated.EndsWith(ClipboardTextCapturePolicy.TruncatedMarker, StringComparison.Ordinal));
    }

    private static ClipboardReadSnapshot<string> ReadText(uint expectedSequence, Func<uint> sequence, Func<string?> read) =>
        ClipboardSnapshotReader.Read(expectedSequence, BeforeMidnight, sequence, read,
            value => !string.IsNullOrWhiteSpace(value),
            error => error is ExternalException { ErrorCode: unchecked((int)0x800401D0) });
}
