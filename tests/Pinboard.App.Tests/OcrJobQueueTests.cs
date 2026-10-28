using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class OcrJobQueueTests
{
    [TestMethod]
    public async Task CaptureBurst_IsPersistedAndProcessedOneOriginalImageAtATime()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "burst.pinboard"));
        var recognizer = new TestRecognizer(async (bytes, chinese, english, cancellationToken) =>
        {
            await Task.Delay(25, cancellationToken);
            return Results(chinese, english, $"recognized original {bytes[0]}");
        });
        await using var queue = new OcrJobQueueService(recognizer, path => new PinboardDocument(path), temp.QueuePath);
        for (byte marker = 1; marker <= 5; marker++)
        {
            var asset = CreateAsset(marker);
            await document.InsertCapturedImageAsync(asset, "test");
            await queue.EnqueueAsync(document.FilePath, asset.Hash, [marker, 42, 99], 80, marker * 200, true, true);
        }
        Assert.AreEqual(5, Directory.GetFiles(temp.QueuePath, "*.image").Length);
        Assert.AreEqual(5, Directory.GetFiles(temp.QueuePath, "*.json").Length);
        queue.Start();
        await WaitUntilAsync(() => Directory.GetFiles(temp.QueuePath, "*.json").Length == 0);

        Assert.AreEqual(5, recognizer.Calls);
        Assert.AreEqual(1, recognizer.MaxConcurrent);
        Assert.AreEqual(0, Directory.GetFiles(temp.QueuePath, "*.image").Length);
        for (byte marker = 1; marker <= 5; marker++)
        {
            var results = await document.LoadOcrResultsAsync(CreateAsset(marker).Hash);
            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(result => result.Status == "complete" && result.Text == $"recognized original {marker}"));
        }
    }

    [TestMethod]
    public async Task InterruptedOcr_ResumesFromOriginalBytesAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "restart.pinboard"));
        var asset = CreateAsset(9);
        await document.InsertCapturedImageAsync(asset, "test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TestRecognizer(async (_, _, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        });
        await using (var first = new OcrJobQueueService(interrupted, path => new PinboardDocument(path), temp.QueuePath))
        {
            await first.EnqueueAsync(document.FilePath, asset.Hash, [9, 8, 7], 80, 80, false, true);
            first.Start();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await first.StopAsync();
            Assert.AreEqual(1, Directory.GetFiles(temp.QueuePath, "*.json").Length);
            Assert.AreEqual(0, (await document.LoadOcrResultsAsync(asset.Hash)).Count);
        }
        var resumed = new TestRecognizer((bytes, chinese, english, _) =>
        {
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, bytes);
            return Task.FromResult(Results(chinese, english, "resumed original screenshot"));
        });
        await using var second = new OcrJobQueueService(resumed, path => new PinboardDocument(path), temp.QueuePath);
        second.Start();
        await WaitUntilAsync(() => Directory.GetFiles(temp.QueuePath, "*.json").Length == 0);
        Assert.AreEqual(1, resumed.Calls);
        Assert.AreEqual("resumed original screenshot", (await document.LoadOcrResultsAsync(asset.Hash)).Single().Text);
    }

    [TestMethod]
    public async Task FailedOcr_PersistsBackoffInsteadOfRetryingInAHotLoop()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "retry.pinboard"));
        var asset = CreateAsset(3);
        await document.InsertCapturedImageAsync(asset, "test");
        var recognizer = new TestRecognizer((_, _, _, _) => Task.FromResult<IReadOnlyList<OcrTextResult>>(
            [new OcrTextResult("en-US", "", "failed", "simulated OCR failure")]));
        await using var queue = new OcrJobQueueService(recognizer, path => new PinboardDocument(path), temp.QueuePath);
        var failed = new TaskCompletionSource<OcrQueueFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobFailed += (_, failure) => failed.TrySetResult(failure);
        await queue.EnqueueAsync(document.FilePath, asset.Hash, [3, 2, 1], 80, 80, false, true);
        queue.Start();
        var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, failure.Attempts);
        Assert.IsTrue(failure.RetryAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(45));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(temp.QueuePath, "*.json").Single()));
        Assert.AreEqual(1, metadata.RootElement.GetProperty("Attempts").GetInt32());
        await Task.Delay(120);
        Assert.AreEqual(1, recognizer.Calls);
        Assert.AreEqual(1, Directory.GetFiles(temp.QueuePath, "*.image").Length);
        Assert.AreEqual("failed", (await document.LoadOcrResultsAsync(asset.Hash)).Single().Status);
    }

    [TestMethod]
    public async Task ExistingCompletedLanguage_IsReusedAndOnlyMissingLanguageRuns()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "reuse.pinboard"));
        var asset = CreateAsset(5);
        await document.InsertCapturedImageAsync(asset, "test");
        await document.SaveOcrAsync(asset.Hash, "zh-Hans-CN", "existing Chinese recognition", "complete", null, 80, 80);
        var recognizer = new TestRecognizer((_, chinese, english, _) =>
        {
            Assert.IsFalse(chinese);
            Assert.IsTrue(english);
            return Task.FromResult(Results(chinese, english, "new English recognition"));
        });
        await using var queue = new OcrJobQueueService(recognizer, path => new PinboardDocument(path), temp.QueuePath);
        await queue.EnqueueAsync(document.FilePath, asset.Hash, [5, 0], 80, 80, true, true);
        await queue.EnqueueAsync(document.FilePath, asset.Hash, [5, 0], 80, 80, true, true);
        Assert.AreEqual(1, Directory.GetFiles(temp.QueuePath, "*.json").Length);
        queue.Start();
        await WaitUntilAsync(() => Directory.GetFiles(temp.QueuePath, "*.json").Length == 0);
        var results = await document.LoadOcrResultsAsync(asset.Hash);
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("existing Chinese recognition", results.Single(result => result.Language.StartsWith("zh")).Text);
        Assert.AreEqual(1, recognizer.Calls);

        await queue.EnqueueAsync(document.FilePath, asset.Hash, [5, 0], 80, 80, true, true);
        await WaitUntilAsync(() => Directory.GetFiles(temp.QueuePath, "*.json").Length == 0);
        Assert.AreEqual(1, recognizer.Calls);
    }

    [TestMethod]
    public async Task MissingDocument_DoesNotRecreateADeletedBoardAndKeepsTheJob()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "deleted.pinboard");
        var recognizer = new TestRecognizer((_, chinese, english, _) => Task.FromResult(Results(chinese, english, "unused")));
        await using var queue = new OcrJobQueueService(recognizer, documentPath => new PinboardDocument(documentPath), temp.QueuePath);
        var failed = new TaskCompletionSource<OcrQueueFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobFailed += (_, failure) => failed.TrySetResult(failure);
        await queue.EnqueueAsync(path, CreateAsset(4).Hash, [4, 3, 2], 80, 80, false, true);
        queue.Start();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, recognizer.Calls);
        Assert.AreEqual(1, Directory.GetFiles(temp.QueuePath, "*.json").Length);
    }

    [TestMethod]
    public void RetryDelay_GrowsAndRemainsBounded()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), OcrJobQueueService.GetRetryDelay(1));
        Assert.AreEqual(TimeSpan.FromMinutes(8), OcrJobQueueService.GetRetryDelay(4));
        Assert.AreEqual(TimeSpan.FromHours(1), OcrJobQueueService.GetRetryDelay(1000));
    }

    private static IReadOnlyList<OcrTextResult> Results(bool chinese, bool english, string text)
    {
        var results = new List<OcrTextResult>();
        if (chinese) results.Add(new OcrTextResult("zh-Hans-CN", text, "complete", null));
        if (english) results.Add(new OcrTextResult("en-US", text, "complete", null));
        return results;
    }

    private static AssetRecord CreateAsset(byte marker)
    {
        byte[] bytes = [137, 80, 78, 71, marker];
        return new AssetRecord(Convert.ToHexString(SHA256.HashData(bytes)), "image/png", 100, 80, bytes, DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("The OCR worker did not reach the expected durable state.");
            await Task.Delay(20);
        }
    }

    private sealed class TestRecognizer(Func<byte[], bool, bool, CancellationToken, Task<IReadOnlyList<OcrTextResult>>> recognize) : IOcrRecognizer
    {
        private int _calls;
        private int _active;
        public int Calls => _calls;
        public int MaxConcurrent { get; private set; }
        public async Task<IReadOnlyList<OcrTextResult>> RecognizeAsync(byte[] sourceBytes, bool chinese, bool english, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, active);
            try { return await recognize(sourceBytes, chinese, english, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardOcrQueueTests", Guid.NewGuid().ToString("N"));
        public string QueuePath => System.IO.Path.Combine(Path, "OcrQueue");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
