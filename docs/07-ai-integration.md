# 07 — AI integration (local API, MCP, Python)

AI is a **client, never a component**. The app hosts a localhost API; agents (Claude via MCP,
custom scripts via REST/Python) call it. No cloud calls from inside the app, ever.

## REST (localhost-only, key auth, `RoboArm.Server`)

| Method & path | Purpose |
|---|---|
| `GET /state` | joints, positions, executing step, flags (estop/sim/watchdog/fault) |
| `GET /machine` · `PUT /machine` | machine config (validated on write) |
| `GET /poses` · `PUT /poses` | pose library |
| `GET /programs` · `GET /programs/{id}` | program catalog |
| `POST /programs/submit` | external submission → review queue (dry-run mandatory) |
| `POST /jog` | jog command (axis, direction, velocity, hold) |
| `POST /move` | coordinated move to pose / joint targets |
| `POST /home` · `POST /park` | homing / park |
| `POST /programs/{id}/run|pause|stop` | execution control |
| `POST /estop` | emergency stop (also `Kill` via `?severity=kill`) |

- Auth: bearer key generated on first run (`data/api.key`); scopes per token
  (`jog`, `program:write`, `program:run`, `estop`).
- Single-writer arbitration: exactly one control source at a time (UI or one client);
  preemption policy configurable.
- Rate limits + malformed-input hardening tested at S36.

## WebSocket `/telemetry` — 20 Hz

Joint states, step transitions, IO, events (faults, approvals, audit). Enables closed-loop
agent behavior (move → observe → decide).

## MCP server (stdio + SSE) — S39

Tools: `get_state`, `list_poses`, `save_pose`, `jog`, `move_to_pose`, `move_joints`,
`set_gripper`, `run_program`, `submit_program`, `stop`, `estop`.
Resources: machine config, pose library, program catalog, and `docs/06-roboscript.md`
(the reference an agent reads to author programs). Tested with a real MCP client at G6.

## Safety interactions (docs/03 L7)

1. External submissions: schema-validate → **simulate (dry-run)** → human approval
   (default policy: always required) → execute with AI-session speed cap (default 25%).
2. Promotion flow: after N clean runs a program/client can be promoted to auto-approve /
   full speed by explicit human action in the UI.
3. Audit: every API/MCP command logged to JSONL with source, params, outcome; replayable.

## Python client (S40b) — `clients/python/roboarm.py`

xArm-style verbs so existing robot-Python users feel at home (docs/08 finding #3):

```python
from roboarm import RoboArm
arm = RoboArm("127.0.0.1:8787", key="...")
arm.connect()                    # GET /state until ready
arm.move_to_pose("pick", speed=50)
arm.set_joints(base=10, elbow=-5)
arm.set_gripper("close", wait_ms=200)
arm.emergency_stop()
arm.on_telemetry(cb)             # WS subscription
```

Thin wrapper over REST + WS only (~200 lines, stdlib + websocket-client). No SDK lock-in:
the API is the contract, documented in `docs/API.md` + OpenAPI at gate G6.
