# 08 — Research notes: how others program robotic arms

Researched 2026-09-14 (web sources listed at bottom). Question: what makes arm programming
"easy and fast" — used to validate/refine our design.

## The universal pattern — four layers, everyone converges

| Layer | Who | Shape |
|---|---|---|
| 1. Teach & replay (poses) | ALL industrial pendants (KUKA/FANUC/ABB); Annin AR4 (closest DIY analog) | jog → save named point P1..Pn → program = ordered moves + gripper/IO + waits |
| 2. Visual/blocks | Niryo Ned (Blockly+Python+ROS levels); xArm/Dobot Studios | blocks frontend **generates** the text backend (Blockly→Python export) |
| 3. Text/Python SDK | xArm Python SDK, PyNiryo, AR4 (Python + G-code + Modbus) | ~10 verbs suffice for pick-and-place |
| 4. ROS/MoveIt | researchers (AR4 offers it) | heavy; out of scope for us |

## Key findings adopted into this project

1. **Two things get taught, never one** (Wikipedia, industrial robot): *positional data*
   (pose library) and *procedure* (program). Kept separate in our model. ✔ already our design
2. **Record & playback** (xArm examples 3002/3003): sample joints while user jogs → dense
   waypoint path → replay at any speed. "Lead-through teaching" adapted to steppers.
   → added as S33b. Biggest easy-and-fast multiplier.
3. **Visual layer generates text layer** (xArm Blockly→Python): one model, multiple views.
   → validates our GUI ⇄ RoboScript ⇄ JSON round-trip requirement.
4. **Deadman/enabling switch** (all pendants, 3-position): motion only while held.
   → optional USB enabling switch, `JogRequiresEnablingSwitch` (docs/01 S0).
5. **Simulation before hardware** (offline programming norm): → our dry-run mode.
6. **Canonical acceptance scenario** (Wikipedia typical programming, P1–P5 pick-and-place):
   → `examples/01-pick-and-place.rscript`, gates G5/G6.
7. **Python client is the industry norm for AI/scripting** → S40b, docs/07.

## Explicit non-goals (parked in M7)

Full ROS/MoveIt stack, Blockly web view, G-code compatibility, Cartesian/IK beyond optional
kinematics module. Our step-cards + RoboScript + REST/MCP/Python already cover all four
industry layers.

## Sources

- Wikipedia — *Industrial robot* (programming & interfaces, teach pendant, typical
  pick-and-place program): https://en.wikipedia.org/wiki/Industrial_robot#Robot_programming_and_interfaces
- xArm Python SDK (API shape, Blockly→Python, record/playback examples):
  https://github.com/xArm-Developer/xArm-Python-SDK
- Annin Robotics AR4 (DIY 6-axis stepper arm; Python/ROS/G-code/Modbus ecosystem):
  https://anninrobotics.com/
- Niryo docs (Blockly/Python/ROS levels — page JS-rendered, pattern corroborated by xArm):
  https://niryo.com/docs/ned-2/programming/
- MoveIt (redirected to moveit.ai; research-grade, not "easy and fast"): https://moveit.ros.org/
- nMotion board identification lead (empty repo, confirms family name):
  https://github.com/nMotionMach3/nMotionV3.22upgrade
- AliExpress item 1005003912398322 (the board; JS-rendered, identified by owner + variants)
