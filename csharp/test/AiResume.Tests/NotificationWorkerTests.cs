using System.Globalization;
using AiResume.Worker.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

public sealed class NotificationWorkerTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(20, 900)]
    public void ComputeRetryDelay_UsesBoundedExponentialBackoff(int failedRounds, int expectedSeconds)
    {
        TimeSpan delay = NotificationWorker.ComputeRetryDelay(TimeSpan.FromSeconds(30), failedRounds);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task StartAsync_SweepsPendingEventsImmediately()
    {
        string root = TestTemp.NewDir("notify-worker");
        try
        {
            string events = Path.Combine(root, "events");
            Directory.CreateDirectory(events);
            File.WriteAllText(
                Path.Combine(events, "event.json"),
                "{\"eventId\":\"1234567890abcdef\",\"source\":\"codex\",\"cwd\":\"C:\\\\work\\\\repo\",\"atUtc\":\"" +
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}");

            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifier = new CompletionNotifier(
                events,
                Path.Combine(root, "seen.json"),
                (_, _, _, _) =>
                {
                    sent.TrySetResult();
                    return Task.FromResult(true);
                });
            var worker = new NotificationWorker(
                NullLogger<NotificationWorker>.Instance,
                notifier,
                () => "ou_test",
                TimeSpan.FromHours(1));

            await worker.StartAsync(CancellationToken.None);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await worker.StopAsync(CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(events, "event.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
