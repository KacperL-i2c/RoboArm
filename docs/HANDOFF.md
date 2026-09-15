# HANDOFF — Phase 0 bench session (Mach3 PC)

Written 2026-09-15 for the AI assistant continuing on the computer where **Mach3 +
the nMotion board plugin are installed**. Read this, then `AGENTS.md`, then
`docs/02-capture-runbook.md` (your click-by-click script for today).

## Repo state (all committed and pushed)

- **M1 + M2 complete** (roadmap S8–S20): contracts/config store, simulator, trapezoid
  planner, execution engine (FSM, Guard, watchdogs, stop semantics, divergence
  tracking, pause/resume, audit JSONL). Gates **G1 and G2 passed** — `docs/SAFETY-LOG.md`.
  **129 tests green** (`dotnet test`).
- **WPF shell MVP** (S25–S27): dashboard, axis cards with hold-to-jog, clamped target
  sliders, F12 E-stop, safe-close, single-instance, sleep prevention, schematic 3D view
  (dockable/detachable). User reports the GUI is **buggy** — fixes are welcome but NOT
  today's task. Manual walkthrough: `docs/UI-CHECKLIST.md`.
- **USB plumbing ready**: `SerialPortChannel` (System.IO.Ports), `Ch340Discovery`
  (registry, VID 1A86:7523), `NmotionMotionTarget` skeleton (opens port, logs RX to a
  ring buffer, **drops all motion calls without transmitting** — pre-G0 safety rule),
  `MachineSession.CreateUsb`, app `--usb` flag, ReWorkbench `usb list` +
  `capture scan/stream/cadence/diff` (pcapng parsing tested on synthetic fixtures).

## TODAY'S MISSION — Phase 0 captures (needs the user physically present)

1. User performs the bench session per `docs/02-capture-runbook.md`:
   23 labeled scenarios recorded with Wireshark+USBPcap into `captures/`, plugin files
   copied to `captures/driver/`, board photos into `docs/photos/`, `captures/index.csv`
   filled in. Axis A has one test motor attached — keep a hand on the power switch.
2. You (the assistant) verify the captures on this machine:
   ```powershell
   dotnet run --project tools/ReWorkbench -- capture scan  captures/04-jog-a-pos-f1.pcapng
   dotnet run --project tools/ReWorkbench -- capture stream captures/04-jog-a-pos-f1.pcapng --endpoint 0x02 --max 40
   dotnet run --project tools/ReWorkbench -- capture diff    captures/04-jog-a-pos-f1.pcapng captures/05-jog-a-pos-f2.pcapng
   dotnet run --project tools/ReWorkbench -- capture cadence captures/04-jog-a-pos-f1.pcapng
   ```
   Expect: OUT endpoint ~0x02 (PC→board) with periodic bulk transfers while jogging;
   IN endpoint ~0x82 for status. `diff` across feeds isolates velocity fields, across
   directions the direction bit, scenario 18/19 show estop/stream-stop behavior.
3. **Commit + push the captures and any analysis notes.**

## AFTER the captures exist (the next coding milestone)

- Infer framing/fields/checksum from the corpus → write **`docs/PROTOCOL.md`**
  (this is the required artifact). Checksum hypotheses: CRC8/CRC16/XOR/sum
  (docs/02 §analysis).
- Implement the codec in `RoboArm.Protocol` (`NmotionCodec` today is constants only),
  with **golden tests against 100% of the corpus** (S21).
- Wire real enable/move/stop into `NmotionMotionTarget` (it already has the ring logs
  and counters; replace the drop-and-count stubs). Replay on the bench per docs/02
  §replay: single-axis jog first, estop replay, then gate **G0**.

## Hard rules (from AGENTS.md — do not violate)

1. **Safety first**: no code path sends motion without the safety state machine; the
   `NmotionMotionTarget` must not transmit ANY byte until the protocol is decoded.
   Faults are sticky; `Kill` closes the port (pre-protocol stop).
2. **Units**: degrees/seconds above `Protocol`; steps only in `Protocol` +
   `AxisConfig.StepsPerDegree`.
3. **Dependencies**: Core ← everything; Runtime owns the live transport; App/Server
   never reference Protocol/Transport (arch tests enforce).
4. No cloud; offline only. JSON files versioned; never break old files silently.
5. C#: NRT on, records for DTOs, no `async void` (except WPF handlers),
   CancellationToken on async public APIs, `TreatWarningsAsErrors`.

## Machine notes

- .NET 8 SDK needed (or newer + `RollForward: Major` already baked into
  `Directory.Build.props` — plain `dotnet build`/`dotnet test`/`dotnet run` work).
- Commands: `dotnet build RoboArm.sln` · `dotnet test` ·
  `dotnet run --project tools/ReWorkbench -- …` · `dotnet run --project src/RoboArm.App [-- --usb]`.
- The app defaults to the Simulator; `--usb` attaches the board (port opens, RX
  logged, no motion — pre-G0).
- Author works in Polish sometimes; code and docs stay English.
