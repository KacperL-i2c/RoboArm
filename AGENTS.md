# AGENTS.md — working instructions for AI coding assistants

## Project

RoboArm Studio: Windows WPF app (.NET 8) controlling a 5-axis stepper robotic arm through a
reverse-engineered "nMotion" MACH3 USB board (CH340 serial, 115200 baud assumed until verified).
Author works in Polish sometimes; code and docs are English.

## Commands

- Build: `dotnet build RoboArm.sln`
- Run console tool: `dotnet run --project tools/ReWorkbench` (`sim` = G1 demo, `schemas`, `board`)
- Run app (Windows only): `dotnet run --project src/RoboArm.App`
- Test: `dotnet test` — tests live in `tests/RoboArm.Tests` (xUnit + FsCheck). Targets
  net8.0 with `RollForward: Major` (Directory.Build.props), so machines with only a
  newer runtime work without extra env vars.

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

- M1 + M2 complete (S8–S20): contracts/config store, simulator, planner, execution
  engine (FSM, Guard, watchdogs, stop semantics, divergence tracking, pause/resume,
  audit JSONL). Gates G1 and G2 passed in the Simulator — see docs/SAFETY-LOG.md.
- WPF shell MVP (S25–S27): MachineSession composition root (Runtime owns the target;
  App talks to Runtime only), JogController over the full safety path, safe-close,
  single instance, sleep prevention. Live schematic 3D visualizer (pure WPF 3D,
  dockable/detachable, placeholder geometry until Phase 0). Manual walkthrough:
  docs/UI-CHECKLIST.md.
- Phase 0 (hardware capture) not started. USB plumbing is ready: serial transport
  (CH340 discovery, ring log), NmotionMotionTarget skeleton (port opens, RX logged,
  NO bytes are transmitted until the protocol is decoded — pre-G0 safety rule),
  MachineSession.CreateUsb, app `--usb` flag, and ReWorkbench capture-analysis
  commands (pcapng scan/stream/cadence/diff, tested on synthetic fixtures).
  Bench runbook: docs/02-capture-runbook.md. Next coding milestone needs the
  captures: decode → docs/PROTOCOL.md → codec → replay → G0 (M3/S21+ per
  docs/05-roadmap.md). Hardware tasks need the user physically present.
- **If you are the assistant on the Mach3 PC**: read `docs/HANDOFF.md` first —
  today's job is guiding the user through the Phase-0 capture session and
  analyzing the resulting pcapng corpus.

## Testing philosophy

Simulator-first: any motion feature must be implementable and testable against
`RoboArm.Simulator` before hardware is involved. Hardware-in-loop tests go to a separate
category (`[Trait("Category","HIL")]`) that CI never runs automatically.
