using RoboArm.Protocol;

Console.WriteLine("ReWorkbench — Phase 0 capture/replay tool");
Console.WriteLine($"Expected board: CH340 USB-serial, {NmotionCodec.BaudRate} baud, VID/PID {NmotionCodec.UsbVendorId:X4}:{NmotionCodec.UsbProductId:X4}");
Console.WriteLine("Plan: docs/02-protocol-plan.md | Captures: captures/");
