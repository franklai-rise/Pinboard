using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;
using System.Text.Json;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class CaptureInboxTests
{
    [TestMethod]
    public async Task PendingEvents_PreserveRepeatedTextTargetAndOriginalTimestampAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PinboardCaptureInboxTests", Guid.NewGuid().ToString("N"));
        try
        {
            var midnight = new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.FromHours(8));
            var target = Path.Combine(directory, "library", "2026-10-02.pinboard");
            await using (var inbox = new CaptureInbox(directory))
            {
                for (uint index = 1; index <= 3; index++)
                    await inbox.EnqueueTextAsync(target, "重复文字", midnight, index);
            }
            await using var restored = new CaptureInbox(directory);
            var jobs = restored.ReadPending();
            Assert.AreEqual(3, jobs.Count);
            Assert.AreEqual(3, jobs.Select(job => job.Id).Distinct().Count());
            foreach (var job in jobs)
            {
                Assert.AreEqual(midnight, job.CapturedAt);
                Assert.AreEqual(target, job.TargetPath);
                Assert.AreEqual("重复文字", await restored.ReadTextAsync(job));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task Worker_IsSerialAndAcknowledgesOnlySuccessfulEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PinboardCaptureInboxTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var inbox = new CaptureInbox(directory);
            var failed = await inbox.EnqueueTextAsync(Path.Combine(directory, "board.pinboard"), "retry", DateTimeOffset.Now);
            await inbox.EnqueueTextAsync(Path.Combine(directory, "board.pinboard"), "saved", DateTimeOffset.Now);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = 0;
            var peak = 0;
            inbox.Start(async (job, token) =>
            {
                var concurrent = Interlocked.Increment(ref running);
                peak = Math.Max(peak, concurrent);
                try
                {
                    await Task.Delay(10, token);
                    if (job.Id == failed.Id) throw new IOException("Synthetic write failure");
                    completion.TrySetResult();
                }
                finally { Interlocked.Decrement(ref running); }
            });
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await inbox.StopAsync();
            Assert.AreEqual(1, peak);
            var pending = inbox.ReadPending();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(failed.Id, pending[0].Id);
            Assert.IsTrue(pending[0].Attempts >= 1);
            Assert.IsTrue(pending[0].RetryAfter > DateTimeOffset.UtcNow);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task Worker_SurvivesRetryMetadataWriteFailureAndThrowingNotificationHandler()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "PinboardCaptureInboxTests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(basePath, "journal");
        var temporarilyOffline = Path.Combine(basePath, "offline");
        try
        {
            await using var inbox = new CaptureInbox(directory);
            var first = await inbox.EnqueueTextAsync(Path.Combine(basePath, "board.pinboard"), "saved after a temporary failure", DateTimeOffset.Now);
            var second = await inbox.EnqueueTextAsync(Path.Combine(basePath, "board.pinboard"), "later capture", DateTimeOffset.Now);
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = 0;
            var firstAttempt = true;
            inbox.ProcessingFailed += (_, _) =>
            {
                Interlocked.Increment(ref failures);
                Directory.Move(temporarilyOffline, directory);
                throw new InvalidOperationException("Synthetic notification handler failure");
            };
            inbox.Start((job, _) =>
            {
                if (job.Id == first.Id && firstAttempt)
                {
                    firstAttempt = false;
                    Directory.Move(directory, temporarilyOffline);
                    throw new IOException("Synthetic offline journal");
                }
                if (job.Id == second.Id) processed.TrySetResult();
                return Task.CompletedTask;
            });
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await inbox.StopAsync();
            Assert.AreEqual(1, failures);
            Assert.IsTrue(Directory.Exists(directory));
            Assert.IsFalse(inbox.ReadPending().Any(job => job.Id == second.Id));
            // The first committed payload remains available unless the worker has
            // already retried and acknowledged it during the restored scan.
            foreach (var remaining in inbox.ReadPending()) Assert.AreEqual(first.Id, remaining.Id);
        }
        finally { Directory.Delete(basePath, recursive: true); }
    }

    [TestMethod]
    public async Task DatabaseCommitBeforeJournalAcknowledgement_ReplaysWithoutDuplicatingText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PinboardCaptureInboxTests", Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(directory, "library", "2026-10-02.pinboard");
            var document = new PinboardDocument(target);
            PendingCapture capture;
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var inbox = new CaptureInbox(directory))
            {
                capture = await inbox.EnqueueTextAsync(target, "durable distinctive clip", DateTimeOffset.Now);
                inbox.ProcessingFailed += (_, _) => failed.TrySetResult();
                inbox.Start(async (job, token) =>
                {
                    await document.InsertCapturedTextAsync(await inbox.ReadTextAsync(job, token), job.Source,
                        token, job.CapturedAt, job.Id);
                    throw new IOException("Simulated interruption after database commit");
                });
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await inbox.StopAsync();
                var retry = inbox.ReadPending().Single() with { RetryAfter = null };
                await File.WriteAllTextAsync(Path.Combine(directory, retry.Id + ".json"), JsonSerializer.Serialize(retry));
            }
            var committedRevision = await document.GetCurrentRevisionAsync();
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var restored = new CaptureInbox(directory);
            restored.Start(async (job, token) =>
            {
                await new PinboardDocument(job.TargetPath).InsertCapturedTextAsync(await restored.ReadTextAsync(job, token),
                    job.Source, token, job.CapturedAt, job.Id);
                acknowledged.TrySetResult();
            });
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await restored.StopAsync();
            Assert.AreEqual(0, restored.ReadPending().Count);
            Assert.AreEqual(committedRevision, await document.GetCurrentRevisionAsync());
            Assert.AreEqual(1, (await document.SearchAsync("distinctive")).Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
