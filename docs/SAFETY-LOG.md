# Safety log

Verification results per docs/03 (verification matrix) and gate criteria from docs/05.
Entries are append-only; never edit a passed gate.

---

## 2026-09-15 — G1: Foundation gate (S8–S13)

**Criterion:** `dotnet run --project tools/ReWorkbench -- sim` executes a 3-move
program in the Simulator and returns to Enabled.

**Result: PASS** (simulator-only, no hardware).

- 3 sequential coordinated moves (base→30°, wrist→−45°, base→10°+wrist→20°) completed
  in 2.91 s wall time; final measured: base 10.01°, wrist 19.81° (converging to targets).
- Suite at gate time: 101 tests green (`dotnet test`), including:
  - architecture dependency rules (S8)
  - JSON contract round-trips + schema export (S9)
  - config store validation/migrations/atomic writes (S10)
  - simulator step-response + fault injection (S11)
  - planner properties: velocity/accel bounds, decel-margin, target reach (S12, FsCheck,
    800 generated cases per property)
  - engine FSM transitions, watchdogs, stop semantics, pause/resume, audit (S13)

Findings fixed during the gate:
- Frame stream starved under Windows `Sleep(1)` granularity (~15.6 ms) — engine now
  sends all due frames per pump (catch-up), completion is delivery-based.
- Simulated clock jumped at connect (telemetry "staleness" of hours) — sim time is now
  based at construction; telemetry gated on connected.

## 2026-09-15 — G2: Fuzz gate (S14–S20)

**Criterion:** random commands × random faults — zero motion-while-faulted, zero
illegal FSM transitions, 100% recoverable.

**Result: PASS** (simulator-only, seeded fuzzer: 30 scenarios × 200 steps,
base seed 20260915, `tests/RoboArm.Tests/Runtime/FuzzTests.cs`).

Action space: move (random in-limit targets), stop (all severities), pause, resume,
connect/disconnect, enable/disable, speed override 10–100%, E-stop injection,
comm loss (50–400 ms), bounded divergence injection, frame delay (0–30 ticks).

Invariants held over the full run:

| Invariant | Result |
|---|---|
| No frame dispatched to the target while engine Faulted | 0 violations |
| FSM walk contains only legal transitions (incl. X→Faulted) | 0 violations |
| Estimated positions stay within soft-limit envelope (±2° tol) | 0 violations |
| Every fault recoverable via ClearFaults → acknowledge → re-home → enable | 254/254 faults recovered |

Findings fixed during the gate:
- **H7 (real):** delayed frames queued in the simulator replayed after a stop,
  cancelling the stop ramp. Fix: any stop clears the delayed-frame queue.
- **H7 (real):** race between concurrent Pump dispatch and StopAsync — a frame built
  under the lock could be *sent* after StopAsync returned, restarting motion. Fix:
  frame dispatch happens under the engine lock; `IMotionTarget` contract now requires
  implementations to never raise events under internal locks.

---

### Pending (hardware gates, not yet run)

G0 (protocol on board), G3 (8 h soak on LED bench), G4 (E-stop drill on the real arm),
G5 (canonical pick-and-place), G6 (external AI agent) — all require bench time per
docs/01 and docs/05 Phase 0+.
