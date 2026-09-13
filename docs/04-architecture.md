# 04 — Architecture

## Projects & dependency rules

```
                    ┌─────────────┐
                    │ RoboArm.Core │  domain: axes, config, poses, programs,
                    └──────┬──────┘  IMotionTarget, safety enums, planner (added M1)
                           ▲
        ┌──────────┬───────┼────────┬─────────────┐
        │          │       │        │             │
  RoboArm.Protocol │ RoboArm.Simulator  RoboArm.RoboScript
  (nMotion codec   │ (digital twin)    (text/JSON program
   + transport)    │                   serialization)
        │          │       │        │             │
        └────┬─────┘       │        │             │
             │       ┌─────┴────────┴──────┐      │
             └──────►│    RoboArm.Runtime   │◄────┘
                     │ engine, FSM, watch-  │
                     │ dogs, interpreter    │
                     └──────────┬───────────┘
                                │
                    ┌───────────┴───────────┐
                    │     RoboArm.Server     │  REST + WS + MCP (hosted in-proc)
                    └───────────┬───────────┘
                                │
                        ┌───────┴───────┐
                        │  RoboArm.App   │  WPF (also hosts Server in-proc)
                        └───────────────┘
```

Rules (future architecture tests will enforce):

1. `Core` depends on nothing. `Protocol`/`Simulator`/`RoboScript` depend only on `Core`.
2. `Runtime` is the ONLY project allowed to own a live transport instance.
3. `App`/`Server` talk to `Runtime`'s public API only — never to `Protocol`/transport directly.
4. One `Runtime` instance per process; `App` and `Server` are interchangeable frontends over it.

## Threading model

```
UI (WPF) ──Channel<Command>──► ExecutionEngine loop ──► MotionPlanner
                                    │                      │ setpoint frames
                                    │                      ▼
                                    │              serial writer thread
                                    │              (fixed cadence, timeBeginPeriod(1))
                                    ▼
                          Channel<TelemetryFrame> ──► UI + WebSocket subscribers
```

- Motion/serial threads: no UI dependencies, allocation-free in steady state, no blocking IO
  except the serial write itself.
- Watchdogs: stream cadence monitor, motion-loop heartbeat, RX staleness → severity-mapped
  stop actions (docs/03).

## Units policy

Degrees & seconds everywhere above `Protocol`. Steps/microsteps exist only inside `Protocol`
and in `AxisConfig.StepsPerDegree` conversion. UI, scripts, API, poses, programs: degrees only.

## Persistence (all JSON, all versioned)

| What | Where | Version field |
|---|---|---|
| Machine config | `%PROGRAMDATA%\RoboArm\machine.json` (editor: `data/machine.json` in dev) | `configVersion` |
| Pose library | `data/poses.json` | `version` |
| Programs | `data/programs/*.json` (+ `.rscript` export) | `programVersion` |
| Audit log | `data/audit/*.jsonl` (append-only) | per-record |

Never break old files silently: unknown fields preserved, missing fields defaulted with a
migration note in the release notes.

## Error & fault flow

Every fault source (watchdog, input poll, decoder, planner) → `FaultReason` → FSM latches
`Faulted` → stop per severity → UI banner + API event → human ack + documented recovery.
No code path may clear a fault without explicit `acknowledgeFault()` from a human action.
