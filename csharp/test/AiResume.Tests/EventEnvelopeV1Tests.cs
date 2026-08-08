using AiResume.Core.Events;
using Xunit;

namespace AiResume.Tests;

public sealed class EventEnvelopeV1Tests
{
    [Fact]
    public void Envelope_version_is_v1()
    {
        EventEnvelopeV1 envelope = new()
        {
            EventId = Guid.NewGuid(),
            Type = "run.state_changed",
            Source = "worker",
            IdempotencyKey = "run:00000000-0000-0000-0000-000000000001:3",
        };

        Assert.Equal("1", envelope.EnvelopeVersion);
        Assert.Equal("1", EventEnvelopeV1.EnvelopeVersionValue);
    }

    [Fact]
    public void Deadline_ms_is_always_zero()
    {
        EventEnvelopeV1 envelope = new()
        {
            EventId = Guid.NewGuid(),
            Type = "run.start.requested",
            Source = "gui",
            IdempotencyKey = "start:req-1",
            Ts = 1234567890,
        };

        Assert.Equal(0, envelope.DeadlineMs);

        EventEnvelopeV1 empty = new();
        Assert.Equal(0, empty.DeadlineMs);
    }

    [Fact]
    public void Identity_fields_are_preserved()
    {
        Guid eventId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        EventEnvelopeV1 envelope = new()
        {
            EventId = eventId,
            Type = "run.state_changed",
            Source = "worker",
            Ts = 42,
            IdempotencyKey = "k",
            RunId = runId,
            Seq = 7,
            Actor = "ou_1",
            CorrelationId = "c-1",
            CausationId = "ca-1",
            Attempt = 2,
        };

        Assert.Equal(eventId, envelope.EventId);
        Assert.Equal(runId, envelope.RunId);
        Assert.Equal(7, envelope.Seq);
        Assert.Equal("ou_1", envelope.Actor);
        Assert.Equal("c-1", envelope.CorrelationId);
        Assert.Equal("ca-1", envelope.CausationId);
        Assert.Equal(2, envelope.Attempt);
    }
}
