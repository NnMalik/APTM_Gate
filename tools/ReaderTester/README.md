# UHF Reader Tester

Tiny standalone WinForms app for bench-testing a UHF RFID reader (Series V2.20 protocol,
same family as UHFReader288 / ZK RFID407) over TCP/IP. No vendor DLL — talks the raw
binary protocol directly, same framing as the gate service's `UhfFrameParser`.

## Run

```bash
dotnet run --project tools/ReaderTester
```

## What it does

- **Connect / Disconnect** — plain TCP to reader IP:port (default port 27011).
  On connect it queries reader info (0x21) and current work mode (0x77) but changes nothing.
- **Set Mode** — Answer (0x76/0x00) or Real-Time (0x76/0x01).
- **Set Power** — 0–30 dBm (0x2F).
- **Beep** — buzzer test (0x33).
- **Live tag list** — in Real-Time mode every 0xEE tag report is shown immediately
  (EPC, antenna, RSSI, read count, first/last seen). "Show raw RX hex" logs every
  byte received, so you can see *any* data the reader pushes.

## Design note

Unlike the gate worker, commands here do NOT drain/discard the stream. A single read
loop parses every frame: 0xEE frames go to the tag list, anything else is matched to
the pending command's response. So you can set power / beep / query mode while tags
keep streaming, with zero lost reads.

## Reader behavior worth knowing

- The reader is a **single-session TCP server**: only one client connection at a time.
  Don't run this tool against a reader the gate service is connected to — one of the
  two sessions will fail or be killed.
- Work mode is stored in the reader's flash. If it was left in Real-Time mode, tags
  start streaming the moment the TCP socket connects — no init/handshake commands
  are required to receive data.
