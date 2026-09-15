# Phase-0 capture runbook (S1–S3)

Goal: record the Mach3 plugin ↔ board USB traffic for 23 labeled scenarios so the
nMotion protocol can be decoded (docs/02). One bench session, ~1–2 hours.

## Preparation (once, on the PC that runs Mach3)

1. **Safety first (docs/01 S0):** physical E-stop reachable; limit switches wired;
   for the first session keep motors at low current or on LED boards where possible.
   Your single test motor on axis A is acceptable — keep a hand on the power switch.
2. Install **Wireshark** (includes USBPcap during setup — accept the USBPcap driver
   install; allow the unsigned-driver prompt).
3. Verify **Mach3 + vendor plugin move axis A** (baseline oracle). Record:
   - Mach3 version + plugin file name/version (screenshot of plugin config)
   - copy the plugin files (e.g. `usbmove.dll` / `nMotion.zip` contents) to
     `captures/driver/` in this repo
4. Photograph the board both sides → `docs/photos/` (helps identify the main MCU —
   matters for the firmware-replacement fallback).

## Capturing (repeat for every scenario below)

1. Start **Wireshark** → double-click **USBPcap1** → tick only the root hub with your
   board (expand the tree, untick others) → Start. No display filter needed.
2. Perform ONLY the scenario steps (wait ~2 s of idle before and after).
3. Stop capture → **File → Save As** → `captures/<NN>-<name>.pcapng`.
4. Register the file: append a row to `captures/index.csv`
   (`file,scenario,mach3-version,plugin-version,notes`).
5. Verify in Wireshark: you see periodic traffic while Mach3 is open (if you see
   nothing on the board's device, you captured the wrong hub — delete and redo).

## The 23 scenarios (docs/02 table)

| # | File | Do this in Mach3 |
|---|---|---|
| 01 | `01-idle-30s.pcapng` | Board connected, Mach3 open, do nothing for 30 s |
| 02 | `02-connect.pcapng` | Start capture FIRST, then launch Mach3 (plugin init) |
| 03 | `03-enable.pcapng` | Click Reset / enable drives |
| 04 | `04-jog-a-pos-f1.pcapng` | Hold jog A+ for ~3 s at feed F1 (slow) |
| 05 | `05-jog-a-pos-f2.pcapng` | Same at medium feed F2 |
| 06 | `06-jog-a-pos-f3.pcapng` | Same at fast feed F3 |
| 07 | `07-jog-a-neg-f1.pcapng` | Hold jog A− ~3 s at F1 |
| 08 | `08-jog-a-neg-f2.pcapng` | A− at F2 |
| 09 | `09-jog-a-neg-f3.pcapng` | A− at F3 |
| 10 | `10-jog-a-short.pcapng` | Tap A+ briefly (<0.5 s), 3 times with pauses |
| 11 | `11-jog-a-ramp.pcapng` | Continuous A+ while raising feed override slowly |
| 12 | `12-jog-a-fdir.pcapng` | A+ then immediately A− mid-motion |
| 13 | `13-feed-override.pcapng` | A+ hold, change feed override up/down mid-move |
| 14 | `14-mdi-move.pcapng` | MDI: G0 A10 then G1 A0 F100 |
| 15 | `15-coordinated.pcapng` | MDI: G1 A10 (any second axis if wired, else skip) |
| 16 | `17-override-zero.pcapng` | A+ hold, set feed override to 0 %, then back |
| 17 | `18-estop.pcapng` | A+ at F3, press Mach3 E-stop (on-screen) mid-move |
| 18 | `22-usb-unplug.pcapng` | A+ at F3, UNPLUG USB mid-move, wait 5 s |
| 19 | `23-mach3-kill.pcapng` | A+ at F3, kill Mach3 via Task Manager, wait 5 s |
| 20 | `19-disable.pcapng` | Disable drives |
| 21 | `20-output.pcapng` | Toggle an output/spindle relay on/off (skip if unused) |
| 22 | `21-home.pcapng` | Run homing for axis A (skip if no home switch) |
| 23 | `24-reboot-idle.pcapng` | Close Mach3, reopen, idle 10 s |

Notes:
- Numbers 04–15 map to docs/02's per-axis matrix — with only axis A wired we capture
  the A column; remaining axes follow the same procedure once wired.
- 17–19 are the safety probes: what the board does on estop/stream-stop (docs/02 §probes).
- After the session: `git add captures/*.pcapng captures/index.csv` (pcaps are
  gitignored only for *.zip).

## Analysis (automatic, back at the dev machine)

```powershell
dotnet run --project tools/ReWorkbench -- capture scan  captures/04-jog-a-pos-f1.pcapng
dotnet run --project tools/ReWorkbench -- capture stream captures/04-jog-a-pos-f1.pcapng --endpoint 0x02 --max 40
dotnet run --project tools/ReWorkbench -- capture diff    captures/04-jog-a-pos-f1.pcapng captures/05-jog-a-pos-f2.pcapng
```

`diff` between feeds/speeds isolates velocity fields; between directions isolates the
direction bit; `cadence` reveals the stream period. Results land in `docs/PROTOCOL.md`.
