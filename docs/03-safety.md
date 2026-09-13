# 03 — Safety architecture

**Rule zero: the hardware E-stop chain (L0) must work with the PC unplugged.** Software layers
protect the process; hardware protects the human. Steppers have no holding torque without
power — a gravity-loaded arm CANNOT be caught by software (see L0b).

## Hazard register

| # | Hazard | Cause |
|---|---|---|
| H1 | Uncommanded motion | codec bug, corrupted frame, stale packet replay |
| H2 | Runaway axis | wrong direction bit, missed limit, bad stepsPerDeg |
| H3 | Comm loss mid-move | USB unplug, driver crash, app hang/kill, PC sleep/BSOD |
| H4 | Mechanical crash | soft limit bypass, wrong pose, programming error |
| H5 | Arm falls on power/enable loss | gravity joints, no holding torque |
| H6 | AI/remote client issues dangerous command | hallucinated params, no rate limit |
| H7 | Queue replays old motion after pause/stop | stale buffered setpoints |
| H8 | App crash mid-stream | unhandled exception while enabled |
| H9 | Windows timing corruption | USB selective suspend, thread starvation, jitter |

## Eight layers (every hazard covered by ≥2 independent layers)

| Layer | Mechanism | Covers | Implemented in |
|---|---|---|---|
| L0 Electrical | Hardware E-stop chain (button cuts driver EN + PSU); N.C. limit switches in the same chain | H1,H2,H4,H8,H9 | wiring only |
| L0b Mechanical | Counterweights / spring brakes on gravity joints, or documented risk acceptance; park pose when idle | H5 | mechanical |
| L1 Board I/O | Enable output polarity = disabled-on-ambiguity; E-stop & limit inputs read every stream cycle | H1–H4 | Protocol |
| L2 Stream watchdog | No valid frame for N ms ⇒ controlled stop + enable drop (board-side if S7 confirms, else PC-side + relay) | H3,H8,H9 | Transport/Runtime |
| L3 State machine | Single authoritative FSM; every command passes the Guard (interlocks); faults sticky, human ack required; stop flushes queues atomically | H1,H2,H7,H8 | Safety/Runtime |
| L4 Config safety | Hard ceilings above user values (see `AxisConfig.AbsoluteMax*`); cross-checks; wizard-only first calibration; live edits limited to speed overrides | H2,H4 | Core |
| L5 Process resilience | Isolated motion thread (no UI deps, steady-state allocation-free); unhandled exception ⇒ safe-close (stop→disable→close port); sleep/USB-suspend prevention while Enabled; single-instance app | H3,H8,H9 | Runtime/App |
| L6 Motion bounds | Soft limits with **decel margin** (limit − stopping distance at current velocity, recomputed every frame); global speed override 0–100%; lower cap for AI sessions (default 25%) | H2,H4,H6 | Motion/Safety |
| L7 Authorization & audit | localhost-only API, key auth + scopes; approval queue + mandatory dry-run for external programs; single-writer arbitration; JSONL audit log of every command (source, params, outcome) | H6 | Server/Safety |

## Runtime state machine (normative)

```
                 connect()                enable request
  Offline ─────────────► Connected ────────────────► Enabled
                          │  ▲   ▲                     │
             disable ◄────┘   │   │ ack()         jog | program run
                          Faulted◄┐                     ▼
                            ▲   │ │               Executing ──pause──► Paused
        any state ─────────┘   │ └── recovery ──► (Enabled, re-home first)
             on: EStop,        │
             LimitSwitch,   ack + diagnose
             SoftLimit,         │
             Watchdog,       human
             CommLoss,       action
             Divergence
```

Reference implementation seed: `src/RoboArm.Runtime/ExecutionEngine.cs` (transition table).

### Fault taxonomy (sticky, latched)

`EStop`, `HardLimit`, `SoftLimit`, `StreamWatchdog`, `CommLoss`, `PositionDivergence`,
`ConfigInvalid`, `InternalError` — each with a documented recovery procedure in
`docs/RECOVERY.md` (create with M2).

### Stop vocabulary (severity-escalating, callable from every layer incl. API)

1. **SoftStop** — max-decel ramp to zero, stay enabled.
2. **ControlledStop** — decel + disable drives.
3. **Kill** — drop enable immediately (E-stop button, watchdog).

**Pause/resume rule:** resume always re-plans from current measured state; buffered frames are
never replayed (H7).

**Latency budgets:** `Kill` ≤ 10 ms software path; `SoftStop` ≤ physics (v/a). Measured at G3.

## Verification matrix (every gate, results into docs/SAFETY-LOG.md)

| Test | Method | Pass criterion |
|---|---|---|
| Estop latency | logic analyzer on STEP & ENABLE, trigger mid-move | within budget, no steps after |
| USB yank mid-move | unplug during full-speed move | stop within watchdog window, enable dropped |
| App kill mid-move | `taskkill /F` during program | same, via port-close path |
| UI hang under load | freeze UI thread artificially | motion-loop heartbeat absence ⇒ stop |
| Fuzz commands | random commands × random faults vs Simulator | no motion while Faulted; no illegal FSM transition; all faults recoverable |
| Soak | 8 h random programs (LED bench → arm) | zero uncommanded steps (LA capture) |
| AI abuse | over-speed / out-of-limit / malformed programs via API | all rejected pre-motion, caps enforced |

## Bench discipline

Energize in stages, never skip: LEDs instead of motors → single axis at low current → full arm.
Drivers' motor connectors are NEVER hot-plugged. Full procedure lives in `docs/BENCH.md`
(create at Phase 3).
