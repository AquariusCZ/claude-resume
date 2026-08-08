using AiResume.Core;
using Xunit;

namespace AiResume.Tests;

public sealed class RunStateMachineTests
{
    private static readonly HashSet<(RunState, RunState)> Legal = new()
    {
        (RunState.Queued, RunState.Starting),
        (RunState.Queued, RunState.Cancelled),
        (RunState.Starting, RunState.Running),
        (RunState.Starting, RunState.FailedProvider),
        (RunState.Starting, RunState.FailedLocal),
        (RunState.Starting, RunState.Cancelled),
        (RunState.Running, RunState.Succeeded),
        (RunState.Running, RunState.FailedProvider),
        (RunState.Running, RunState.FailedLocal),
        (RunState.Running, RunState.Cancelled),
    };

    [Fact]
    public void Only_contract_transitions_are_legal()
    {
        RunState[] states = Enum.GetValues<RunState>();
        foreach (RunState from in states)
        {
            foreach (RunState to in states)
            {
                Assert.Equal(Legal.Contains((from, to)), RunStateMachine.CanTransition(from, to));
            }
        }
    }

    [Fact]
    public void Terminal_states_accept_no_outgoing_transition()
    {
        foreach (RunState terminal in new[] { RunState.Succeeded, RunState.FailedProvider, RunState.FailedLocal, RunState.Cancelled })
        {
            foreach (RunState to in Enum.GetValues<RunState>())
            {
                Assert.False(RunStateMachine.CanTransition(terminal, to));
            }
        }
    }

    [Fact]
    public void Terminal_set_is_exactly_the_contract_terminals()
    {
        RunState[] expected = { RunState.Succeeded, RunState.FailedProvider, RunState.FailedLocal, RunState.Cancelled };
        foreach (RunState state in Enum.GetValues<RunState>())
        {
            Assert.Equal(expected.Contains(state), RunStateMachine.IsTerminal(state));
        }
    }

    [Fact]
    public void TryTransition_only_mutates_on_legal_transition()
    {
        RunState current = RunState.Queued;

        Assert.False(RunStateMachine.TryTransition(ref current, RunState.Running));
        Assert.Equal(RunState.Queued, current);

        Assert.True(RunStateMachine.TryTransition(ref current, RunState.Starting));
        Assert.Equal(RunState.Starting, current);

        Assert.False(RunStateMachine.TryTransition(ref current, RunState.Succeeded));
        Assert.Equal(RunState.Starting, current);

        Assert.True(RunStateMachine.TryTransition(ref current, RunState.Cancelled));
        Assert.Equal(RunState.Cancelled, current);

        Assert.False(RunStateMachine.TryTransition(ref current, RunState.Running));
        Assert.Equal(RunState.Cancelled, current);
    }

    [Fact]
    public void Wire_codes_round_trip_for_all_states()
    {
        var expected = new Dictionary<RunState, string>
        {
            [RunState.Queued] = "queued",
            [RunState.Starting] = "starting",
            [RunState.Running] = "running",
            [RunState.Succeeded] = "succeeded",
            [RunState.FailedProvider] = "failed_provider",
            [RunState.FailedLocal] = "failed_local",
            [RunState.Cancelled] = "cancelled",
        };

        foreach ((RunState state, string code) in expected)
        {
            Assert.Equal(code, state.ToWireCode());
            Assert.True(RunStateMachine.TryFromWireCode(code, out RunState parsed));
            Assert.Equal(state, parsed);
        }

        Assert.False(RunStateMachine.TryFromWireCode("bogus", out _));
        Assert.False(RunStateMachine.TryFromWireCode(null, out _));
    }
}
