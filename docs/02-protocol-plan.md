# 02 — Protocol plan (Phase 0)

The nMotion serial protocol is undocumented and closed. Strategy: **capture Mach3-plugin ↔ board
traffic, decode it, replay it** from our own code. Mach3 is the oracle; our app replaces it at
runtime. Tools: USBPcap + Wireshark (free), ReWorkbench for analysis.

## Capture corpus (S3) — every scenario, labeled

Save each as `captures/<nn>-<name>.pcapng` and register it in `captures/index.csv`.

| # | Scenario | Purpose |
|---|---|---|
| 01 | Idle 30 s after connect | keepalive/handshake cadence |
| 02 | Connect sequence (plug-in → plugin start) | init/handshake, version query |
| 03 | Enable drives | enable packet |
| 04–15 | Single-axis jog, each axis, both directions, 3 different feeds | per-axis step/dir encoding, velocity fields |
| 16 | 2-axis coordinated move (diagonal in Mach3) | multi-axis encoding |
| 17 | Feed override during move | velocity update semantics |
| 18 | E-stop during move | stop packet + board reaction |
| 19 | Disable drives | disable packet |
| 20 | Spindle/relay on/off | IO encoding |
| 21 | Homing routine | homing packet semantics |
| 22 | **USB unplug mid-move** | board-side watchdog behavior |
| 23 | **Mach3 process kill mid-move** | board reaction to stream stop |

## Analysis plan (S4–S6, ReWorkbench)

1. **Frame extraction**: USBPcap captures of CDC-serial data → byte streams per direction.
2. **Cadence/timing histograms**: plugin update period (expected 10–25 ms), burst sizes.
3. **Field diffing**: compare frames across scenarios 04–15 to isolate axis index, direction,
   velocity, position/step-count fields.
4. **Checksum inference**: trailing bytes; test CRC8/CRC16/XOR/sum hypotheses against corpus.
5. **Replay**: send captured/reconstructed frames to the real board on the LED bench until a
   single-axis jog reproduces motion exactly. Then e-stop replay.
6. Document everything in `docs/PROTOCOL.md` (to be created at G0).

## Board safety probes (S7 — results go into docs/03 and PROTOCOL.md)

- Stop the stream mid-motion: does the board halt outputs by itself? After how long?
  (Determines whether L2 watchdog has hardware backing.)
- Enable polarity on power-up and after USB reset (must be "disabled" on ambiguity).
- Behavior when E-stop input triggers (if the board has one).
- Behavior on malformed frames (garbage burst) — reject, reset, or execute?

## Exit gate G0

We can reliably, from our own code (no Mach3): connect, enable, move one axis at a chosen
feed, stop. Board auto-stop behavior known. If not achievable → fallback ladder
(docs/01). Golden traces from the corpus become protocol unit-test fixtures forever.
