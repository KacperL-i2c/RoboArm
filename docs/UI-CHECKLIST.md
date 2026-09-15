# UI walkthrough checklist (S25/S26/S27 — simulator MVP)

Run `dotnet run --project src/RoboArm.App` and tick each box.
Anything failing → note it and fix before Phase 4 hardware gates.

## S25 — Shell

- [ ] Window opens titled "RoboArm Studio — Simulator", state chip is gray **Offline**.
- [ ] **Connect** → chip turns orange **Connected**; Connect/Enable become disabled appropriately.
- [ ] **Enable drives** → chip green **Enabled**; axis-card LEDs turn green.
- [ ] **Disable** → back to **Connected**, LEDs gray.
- [ ] **Disconnect** → **Offline**.
- [ ] Illegal buttons are always disabled (e.g. no Enable while Offline, no Move while not Enabled).
- [ ] Speed slider clamps 0–100 %; a move at 25 % is visibly slower than at 100 %.
- [ ] **E-STOP button and F12** work at any moment (state shows the drop, motion stops instantly).
- [ ] Esc = soft stop during a move: motion ramps out, state returns to Enabled.
- [ ] E-stop banner: after faulting (see below) the red banner shows reason + **Acknowledge fault**.

## S26 — Axis cards

- [ ] Five cards (base, shoulder, elbow, wrist, gripper), each showing limits and live
      measured/commanded position (commanded moves first, measured follows).
- [ ] Position bar tracks measured position between soft limits.
- [ ] Target slider cannot be dragged past the soft limits.
- [ ] **Move to** executes a coordinated move; state goes Executing → Enabled; position converges.
- [ ] **Hold-to-jog** buttons: press-and-hold moves continuously; release stops and settles.
- [ ] Jog cannot be started while not Enabled (status line explains).
- [ ] Jog at the soft limit stops there — never crosses.
- [ ] **Set home here** clears the re-home notice (informational in simulator).

## S27 — Process resilience

- [ ] Closing the window mid-move shuts down safely (stop → disable); no error dialog.
- [ ] Launching a second instance shows "already running" and exits.
- [ ] With drives enabled, Windows sleep is prevented (verify via `powercfg /requests`
      → SYSTEM: dotnet.exe, optional check).
- [ ] `data/audit/*.jsonl` records connect/enable/move/stop commands from the session.

## 3D view (schematic)

- [ ] Right pane shows the schematic arm (base column + 3 links + gripper fingers)
      standing on a ground disc with RGB axes (X red, Y green, Z blue).
- [ ] Neutral pose at start; joints move live during jog and "Move to" commands.
- [ ] Base jog rotates the whole arm; shoulder/elbow/wrist bend the chain; gripper
      axis opens/closes the fingers.
- [ ] Left-drag orbits the camera; mouse wheel zooms.
- [ ] **Detach ⤢** moves the 3D view into its own window (drag to a second monitor);
      **Dock back ⤡** or closing that window returns it to the main layout.
- [ ] Resizing via the splitter works; the view keeps rendering while detached.
- [ ] Note: geometry is placeholder until real arm dimensions are captured (Phase 0).

## Simulator fault drill (bonus)

- [ ] Close the app; in a terminal run the engine fuzzer equivalent via
      `dotnet test --filter FuzzTests` — 0 violations expected (already automated).
