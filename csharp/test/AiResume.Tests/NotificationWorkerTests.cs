using System.Globalization;
using AiResume.Worker.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

public sealed class NotificationWorkerTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(20, 900)]
    public void 单条重试按指数退避且有上限和抖动(int attempts, int expectedSeconds)
    {
        TimeSpan delay = CompletionNotifier.ItemBackoff(attempts);

        Assert.InRange(delay.TotalSeconds, expectedSeconds * 0.8 - 0.001, expectedSeconds * 1.2 + 0.001);
    }

    [Theory]
    [InlineData("keychain_unavailable", 1, null, 60)]
    [InlineData("keychain_unavailable", 9, null, 600)]
    [InlineData("rate_limit", 1, 7, 7)]
    [InlineData("network", 3, null, 120)]
    [InlineData("auth_config", 1, null, 1800)]
    [InlineData("permission_or_recipient", 5, null, 1800)]
    public void 通道熔断按错误类别退避(string @class, int consecutive, int? retryAfter, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CompletionNotifier.ChannelBackoff(@class, consecutive, retryAfter));

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
