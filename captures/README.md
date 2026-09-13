# captures/ — Phase 0 USB capture corpus

Pcap files are git-ignored (large). Keep them here locally + backup drive; register every
capture in `index.csv` so protocol tests can reference fixtures deterministically.

## index.csv format

```csv
file,scenario,axis,direction,feed_mms,notes
01-idle.pcapng,idle-30s,,,,baseline keepalive cadence
04-base-jog-pos-slow.pcapng,jog-single,base,pos,100,first feed level
```

## How to capture (Windows, per docs/02)

1. Install Wireshark **with USBPcap** option checked.
2. Close everything else using COM; plug the board into a root hub port if possible.
3. Capture on the USBPcap interface for the device; run the scenario from a clean idle state.
4. Stop, save as `<nn>-<name>.pcapng`, add row to index.csv.
5. Scenario list: docs/02 §Capture corpus (23 scenarios incl. failure cases 22–23).
