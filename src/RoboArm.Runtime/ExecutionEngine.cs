using RoboArm.Safety;

namespace RoboArm.Runtime;

public sealed class ExecutionEngine
{
    public RuntimeState State { get; private set; } = RuntimeState.Offline;

    public IReadOnlyList<(RuntimeState From, RuntimeState To)> LegalTransitions { get; } =
    [
        (RuntimeState.Offline, RuntimeState.Connected),
        (RuntimeState.Connected, RuntimeState.Enabled),
        (RuntimeState.Connected, RuntimeState.Offline),
        (RuntimeState.Enabled, RuntimeState.Executing),
        (RuntimeState.Enabled, RuntimeState.Connected),
        (RuntimeState.Executing, RuntimeState.Paused),
        (RuntimeState.Executing, RuntimeState.Enabled),
        (RuntimeState.Paused, RuntimeState.Executing),
        (RuntimeState.Paused, RuntimeState.Enabled)
    ];

    public bool CanTransition(RuntimeState to) =>
        State == to || LegalTransitions.Contains((State, to));

    public void EnterFault() => State = RuntimeState.Faulted;
}
