# RoboArm Studio

Windows control software (C# / .NET 8 / WPF) for a stepper-motor robotic arm driven by the
**5-axis "nMotion" MACH3 USB interface board** (AliExpress item `1005003912398322`).

Goal: independently control any axis with any settings, program multi-move sequences visually,
and let external AI agents drive the arm safely through a local API.

**Status: M1 + M2 complete (S8–S20, gates G1 & G2 passed in the Simulator); WPF shell MVP
(S25–S27) running against the Simulator.** Phase 0 (protocol capture on hardware) not yet
started. Read `docs/05-roadmap.md`, `docs/SAFETY-LOG.md`, and `docs/UI-CHECKLIST.md`.

## Repository layout

```
src/
  RoboArm.Core       domain model: axes, config, poses, programs, IMotionTarget, safety enums
  RoboArm.Protocol   nMotion serial protocol (to be reverse-engineered, see docs/02)
  RoboArm.Simulator  digital twin (test without hardware)
  RoboArm.RoboScript text/JSON serialization of programs
  RoboArm.Runtime    execution engine, state machine, watchdogs
  RoboArm.Server     local REST + WebSocket + MCP server for AI integration
  RoboArm.App        WPF application (dashboard, jog, program editor)
tools/
  ReWorkbench        Phase-0 capture/replay/diff tooling
docs/                ALL project knowledge — read this
examples/            example programs
captures/            USB capture corpus (pcaps stay out of git)
```

## Getting started (Windows)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `dotnet build RoboArm.sln`
3. `dotnet test` — 100+ unit/property/fuzz tests (no hardware needed). Targets net8.0
   with `RollForward: Major`, so it also runs where only a newer runtime is installed.
4. `dotnet run --project tools/ReWorkbench -- sim` — G1 gate: 3-move program in the Simulator.
5. `dotnet run --project tools/ReWorkbench -- schemas` — export JSON schemas to `docs/schemas/`.
6. `dotnet run --project tools/ReWorkbench -- usb list` — find the CH340 (nMotion) board COM port.
7. `dotnet run --project tools/ReWorkbench -- capture scan|stream|cadence|diff <file.pcapng>` —
   Phase-0 USBPcap analysis (see docs/02-capture-runbook.md).
8. `dotnet run --project src/RoboArm.App` — WPF dashboard: connect/enable, axis cards,
   hold-to-jog, clamped target sliders, F12 E-stop, and a live schematic 3D view
   (dockable or floating window) — walkthrough: `docs/UI-CHECKLIST.md`.
   Add `--usb` to attach the real board instead of the simulator (pre-G0 skeleton:
   the port opens and traffic is logged, but the board cannot move until the protocol
   is decoded — docs/02).

## Documentation index

| Doc | Contents |
|---|---|
| [docs/01-hardware.md](docs/01-hardware.md) | The board, bench setup, fallback ladder |
| [docs/02-protocol-plan.md](docs/02-protocol-plan.md) | Phase 0: USB capture corpus & protocol decoding |
| [docs/03-safety.md](docs/03-safety.md) | Safety architecture (8 layers), state machine, verification |
| [docs/04-architecture.md](docs/04-architecture.md) | Software architecture, dependency rules, threading |
| [docs/05-roadmap.md](docs/05-roadmap.md) | Step-by-step plan S0–S41 with gates G0–G6 |
| [docs/06-roboscript.md](docs/06-roboscript.md) | The program language spec |
| [docs/07-ai-integration.md](docs/07-ai-integration.md) | REST / WebSocket / MCP / Python client |
| [docs/08-research-notes.md](docs/08-research-notes.md) | How the industry programs arms (research findings) |

## Hardware summary

- 5-axis MACH3 **USB motion card** (nMotion family), single USB cable, CH340 serial.
- Works with Mach3 via vendor plugin (verified on the author's setup) — that plugin is our
  protocol oracle: we sniff its traffic and re-implement it (docs/02).
- External stepper drivers (TB6600 / DM542 class) wired to the board's outputs.
- Open-loop steppers, no encoders. Safety constraints in docs/03 apply.

## Core design decisions

1. One execution engine, many frontends (WPF GUI, API, MCP) — all pass the same safety gate.
2. Everything is data: machine config, poses, programs = versioned JSON.
3. Simulator-first: every feature testable without hardware.
4. AI is a client, never a component: no cloud dependency inside the app.
5. Units: degrees everywhere above the Protocol layer; raw steps never leak upward.
