# AGENTS.md — working instructions for AI coding assistants

## Project

RoboArm Studio: Windows WPF app (.NET 8) controlling a 5-axis stepper robotic arm through a
reverse-engineered "nMotion" MACH3 USB board (CH340 serial, 115200 baud assumed until verified).
Author works in Polish sometimes; code and docs are English.

## Commands

- Build: `dotnet build RoboArm.sln`
- Run console tool: `dotnet run --project tools/ReWorkbench`
- Run app (Windows only): `dotnet run --project src/RoboArm.App`
- Test: `dotnet test` (no test project yet — create `tests/RoboArm.Tests` when starting M1)

## Hard rules (do not violate)

1. **Safety first.** Read `docs/03-safety.md` before touching `Runtime`, `Protocol`, or anything
   that can move hardware. Never add a code path that sends motion without passing the safety
   state machine. Fault states are sticky and require human acknowledgment.
2. **Units.** Degrees and seconds above the `Protocol` layer. Steps/microsteps live only in
   `Protocol` and in `AxisConfig.StepsPerDegree` conversion.
3. **Dependency direction.** Core ← everything; Runtime is the only owner of a live transport;
   App/Server never reference Protocol/Transport directly. Architecture tests will enforce this.
4. **No cloud dependencies.** The app must work fully offline. AI integration is server-side
   clients calling our local API (docs/07).
5. Config/pose/program files are JSON with explicit schema version — never break old files silently.
6. C# conventions: nullable reference types on, records for DTOs, no `async void` (except WPF
   event handlers), `CancellationToken` on all async public APIs.

## Current status & next actions

- Phase 0 (hardware capture) not started. Code is scaffolding only.
- Next coding tasks (when user says go): M1 items from `docs/05-roadmap.md` — config store,
  simulator, planner, property tests; all runnable without hardware.
- Hardware tasks need the user physically present (bench work per `docs/01-hardware.md`).

## Testing philosophy

Simulator-first: any motion feature must be implementable and testable against
`RoboArm.Simulator` before hardware is involved. Hardware-in-loop tests go to a separate
category (`[Trait("Category","HIL")]`) that CI never runs automatically.
