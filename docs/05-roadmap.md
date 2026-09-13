# 05 — Roadmap (S0–S41, gates G0–G6)

Phases M0–M2 need no hardware beyond Phase-0 bench days. Each step: build → verify → artifact.
Gates are stop/go checkpoints. Research context: docs/08. Safety: docs/03.

## Phase 0 — Hardware desk & protocol capture (needs the board)

| # | Step | Verify / artifact |
|---|---|---|
| S0 | Bench safety rig: E-stop chain, N.C. limits, LED boards, logic analyzer (docs/01 checklist) | wiring photos in docs |
| S1 | Photograph board both sides; identify CH340 + main MCU; snapshot driver CD (plugin DLL + versions) | docs/HARDWARE.md update |
| S2 | Mach3 + plugin working baseline; record exact config | known-good baseline zip |
| S3 | Capture corpus: 23 labeled scenarios (docs/02 table) | captures/ + index.csv |
| S4 | ReWorkbench v0: pcap→frames, cadence histograms, field diffing | frame tables |
| S5 | Framing/checksum inference; draft docs/PROTOCOL.md | decoder passes on 100% of corpus |
| S6 | Replay harness on LED bench: our code reproduces jog + estop | live demo, LA trace |
| S7 | Board safety probes (docs/02 §probes) | findings in PROTOCOL.md + docs/03 |
| **G0** | **Gate:** connect/enable/move/stop from our code; board auto-stop known → protocol path or fallback ladder (docs/01) | decision record |

## Phase 1 — Foundation (no hardware)

| # | Step | Verify |
|---|---|---|
| S8 | CI + architecture tests (dependency rules, docs/04) | CI green |
| S9 | Contracts/JSON round-trip + schema export tests | unit tests |
| S10 | Config store + validation (hard ceilings, cross-checks) + migrations | unit tests |
| S11 | Simulator: axis physics + fault injection | step-response tests |
| S12 | Planner: trapezoid profiles, coordinated multi-axis time-scaling, decel-margin soft limits | property tests |
| S13 | `IMotionTarget` + `SimulatedMotionTarget` + telemetry stream | integration smoke |
| **G1** | `dotnet run --project tools/ReWorkbench` executes a 3-move program in Simulator | recorded demo |

## Phase 2 — Safety core (no hardware)

| # | Step | Verify |
|---|---|---|
| S14 | Guard/interlocks consulted before every dispatch | each interlock trips independently |
| S15 | FSM: full transition table, fault taxonomy, ack/recovery | exhaustive transition tests |
| S16 | Watchdogs (stream, heartbeat, RX staleness) → severity actions | fault injection tests |
| S17 | Stop semantics + atomic queue flush | stop-vs-dispatch race tests |
| S18 | Position tracker: commanded vs issued divergence → fault | threshold tests |
| S19 | Pause/resume re-plan-from-measured-state | no-stale-frame property test |
| S20 | Audit JSONL sink + replay tooling | audit round-trip test |
| **G2** | **Fuzz gate:** random commands × random faults — zero motion-while-faulted, zero illegal transitions, 100% recoverable | fuzz report committed |

## Phase 3 — Protocol on hardware

| # | Step | Verify |
|---|---|---|
| S21 | Protocol codec per PROTOCOL.md | golden tests vs full corpus |
| S22 | Transport: CH340 discovery, auto-reconnect, ring-log, crash-safe | com0com fake-serial tests |
| S23 | Writer loop at captured cadence; jitter stats in telemetry | jitter budget met |
| S24 | HIL bench (LEDs): connect/enable/jog/estop/watchdog full loop | HIL suite green |
| **G3** | **Soak gate:** 8 h random programs on LEDs; zero uncommanded steps; estop ≤ budget | docs/SAFETY-LOG.md |

## Phase 4 — WPF MVP on the real arm

| # | Step | Verify |
|---|---|---|
| S25 | Shell: dashboard, state indicator, always-visible E-stop (F12), connection panel | walkthrough |
| S26 | Axis cards: live position, hold-to-jog, per-axis enable, clamped sliders, homing, limit display | manual matrix |
| S27 | Process resilience: safe-close, sleep/suspend prevention, single-instance | app-kill & sleep tests |
| S28 | First-run calibration wizard (capped v/a, direction check, stepsPerDeg, limits, park pose) | one axis end-to-end |
| S29 | Energize arm (LEDs → low current → full); home; physical E-stop drill at full speed | signed SAFETY-LOG |
| **G4** | Arm jogs safely, E-stop physically proven, valid machine config exists | demo + log |

## Phase 5 — Programming layer

| # | Step | Verify |
|---|---|---|
| S30 | Pose library UI (teach/name/edit/protect) | UI tests |
| S31 | Visual editor: 13 step types (docs/06), drag-reorder, undo/redo, params editors | integration tests |
| S32 | Timeline preview (per-joint graphs from Simulator) | golden snapshots |
| S33 | Interpreter/sequencer: loops, call, IO, waits; step-over, pause, live highlight | mid-program stop/resume tests |
| S33b | **Record & playback**: sample joints @20 Hz during free jog → dense path program → replay with speed scaling (docs/08 finding #2) | recorded-path round-trip |
| S34 | RoboScript parser/writer + round-trip property tests + JSON schema export | property suite |
| S35 | Dry-run mode end-to-end | examples/ all dry-run |
| **G5** | **Canonical scenario:** teach → build the P1–P5 pick-and-place (examples/01) → dry-run → run on arm → pause/resume → estop mid-program → clean recovery | recorded run |

## Phase 6 — AI / API layer

| # | Step | Verify |
|---|---|---|
| S36 | REST endpoints (docs/07), key auth + scopes, localhost bind, rate limits, single-writer arbitration | integration + hostile-input suite |
| S37 | WebSocket telemetry 20 Hz + events | 3-client load test |
| S38 | Review queue UI (diff, sim preview, approve/reject); policy default = approve-always-required | submission→review→run E2E |
| S39 | MCP server (stdio + SSE): tools per docs/07; resources include ROBOSCRIPT.md | real MCP client completes a task |
| S40 | Docs pack: ROBOSCRIPT.md, API.md + OpenAPI, RECOVERY.md, SAFETY.md final | review; examples validate |
| S40b | Thin Python client `clients/python/roboarm.py` (~200 lines over REST+WS, xArm-style verbs) | notebook demo |
| S41 | AI session speed cap + promotion flow | cap-enforcement tests |
| **G6** | External agent authors & runs a pick-and-place via approval; API estop works; USB-yank & app-kill mid-move pass | final SAFETY-LOG |

## M7 — Optional backlog

Blending, kinematics/IK + Cartesian moves, 3D preview, Blockly web view, G-code compat,
self-collision checks, voice. None block G6.

## Deferred decisions (asked, unanswered — defaults chosen)

- Hardware E-stop + limit switches: treated as **required Phase-0 items** (S0).
- AI approval default: **human approves every external submission; dry-run mandatory**.
