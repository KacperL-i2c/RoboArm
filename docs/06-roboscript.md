# 06 — RoboScript (program language spec)

RoboScript is the plain-text serialization of a `RobotProgram` (see `RoboArm.Core/Programs`).
Design goals: hand-writable, trivially LLM-generable, strictly validatable, lossless GUI round-trip.
One model, three views: **GUI step cards ⇄ RoboScript text ⇄ JSON** (property-tested).

## Grammar (v1 draft — finalize in S34)

```
program     := line*
line        := comment | directive | step
comment     := ';' text
directive   := 'program' 'v' NUMBER
step        := stmt | block
block       := 'repeat' NUMBER '{' step* '}' | 'while' cond '{' step* '}'

stmt        := 'pose' STRING [speed=N]
             | 'joints' axisSpec+ [speed=N]
             | 'axis' AXIS ('+'|'-')? NUMBER [speed=N] [mode=(rel|abs)]
             | 'jog' AXIS ('+'|'-') 'hold' NUMBER UNIT
             | 'gripper' ('open'|'close') [wait=NUMBER UNIT]
             | 'output' NUMBER ('on'|'off')
             | 'wait' NUMBER UNIT
             | 'wait' 'input' NUMBER ('=='|'!=') ('on'|'off') [timeout=NUMBER UNIT]
             | 'home' [AXIS]
             | 'park'
             | 'speed' PERCENT
             | 'call' STRING
axisSpec    := AXIS ':' ('+'|'-')? NUMBER
cond        := 'input' NUMBER ('=='|'!=') ('on'|'off')
AXIS        := identifier from machine config (e.g. base, elbow, wrist)
UNIT        := ms | s
```

## Semantics

- `pose "name"` — coordinated multi-axis move: all joints arrive together; per-axis
  vmax/amax/jerk respected by time-scaling on the slowest axis (docs/04 planner).
- `speed N%` — global multiplicative override for subsequent steps (clamped 0–100; further
  capped by AI-session cap when source is external, docs/03 L6).
- `repeat N { }` — bounded loops only (N ≤ 1000; `while` max 60 s wall-clock, else fault).
- `call "name"` — subroutine by program name, depth ≤ 8, recursion rejected at validation.
- Every step accepts `enabled=false` (skipped) and a `comment` — both round-trip.

## JSON twin

`{ "name": "...", "programVersion": "1", "steps": [ { "type": "MovePose", "parameters": { "pose": "home", "speed": "60" }, "enabled": true, "comment": null }, ... ] }`
A JSON Schema is exported in CI (`schema/program.schema.json`) and is the contract for API/MCP
submissions. Enums are camelCase strings mapping to `StepType`.

## Validation rules (enforced pre-execution, non-negotiable)

1. Schema-valid (JSON path) / parse-valid (text path).
2. All referenced poses, axes, outputs, inputs exist in machine config.
3. All targets inside soft limits (with decel margin, docs/03 L6).
4. Loop bounds, call depth, wait timeouts within limits.
5. No step may bypass the Guard — execution itself re-checks interlocks per step.

## Canonical example

See `examples/01-pick-and-place.rscript` (the industrial-standard P1–P5 scenario from
docs/08 — also the G5/G6 acceptance test).
