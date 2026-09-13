# 01 — Hardware

## The controller board

| Property | Value | Confidence |
|---|---|---|
| Product | 5-axis CNC MACH3 USB interface board (AliExpress item `1005003912398322`) | confirmed by owner |
| Board family | "nMotion" (Mach3 plugin family; see docs/08 research notes) | high |
| PC connection | Single USB cable (PC has no LPT port) | confirmed |
| USB chip | CH340 USB-serial, expected COM port, **115200 baud assumed until Phase 0 verifies** | to verify |
| USB VID/PID | `1A86:7523` (CH340 typical) | to verify on Windows |
| Motion role | True USB motion card: plugin streams buffered motion, board MCU generates STEP/DIR | confirmed behaviorally |
| Stepper drivers | External (TB6600 / DM542 class), wired to board outputs | confirmed by owner |
| Baseline | Board works with Mach3 + vendor plugin (owner verified motors turning) | confirmed |

**Action item when board is next on the bench:** photograph both sides, record main MCU part
number (near the CH340). If it is an STM32/GD32, firmware replacement becomes a viable fallback.

## The machine

5-axis stepper robotic arm (joint names/geometry to be defined in `MachineConfig` during
first-run wizard, Phase 4). Open-loop: no encoders. Gravity-loaded joints must respect
docs/03 §L0b (brake/counterweight or documented risk).

## Bench setup required before Phase 0 (S0 checklist)

- [ ] Physical **E-stop button** wired to cut driver ENABLE (and ideally PSU) — independent of all software
- [ ] **Limit switches** per axis, wired **normally-closed** (break = fault) into the enable chain
- [ ] **LED test boards** in place of motors (or drivers with motors disconnected) for safe capture/replay
- [ ] Logic analyzer (cheap USB one + PulseView is fine) for STEP/DIR/ENABLE verification
- [ ] Mach3 + vendor plugin installed and working (baseline oracle)
- [ ] Optional: USB 3-position **deadman/enabling switch** for jog mode (`JogRequiresEnablingSwitch`)

## Fallback ladder (if protocol decoding fails — decided at gate G0)

1. **Ghidra static analysis** of the Mach3 plugin DLL from the driver CD.
2. **MCU firmware replacement**: if the main MCU is STM32/GD32 and reflashing is possible,
   write our own firmware speaking our protocol — board becomes fully ours.
3. **Controller swap**: keep drivers/PSU/wiring; replace board with a cheap GRBL-based
   controller (e.g. ESP32). `IMotionTarget` abstraction makes this a single new adapter class.

None of these change the app architecture — only the `Protocol`/`Transport` layer.
