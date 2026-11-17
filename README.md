# logiswitch

A small Windows tray app that switches your default audio device when a **Logitech G522 LIGHTSPEED** headset is turned on or off.

## Why

The G522's USB receiver stays plugged in when the headset is powered off, so Windows keeps showing the headset as connected. It never falls back to your speakers. logiswitch listens to the receiver itself, which *does* know whether the headset is on.

- **Headset turns on:** the default output (and mic) switch to the G522 immediately.
- **Headset turns off:** after a short delay (2 s by default, to ride out range drop-outs), they switch to your fallback devices.
- A device you picked by hand is left alone. It only switches away from the headset if the headset is the current default, and only switches back if the fallback is.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build (or the .NET 10 Desktop Runtime to run a framework-dependent build)
- G522 LIGHTSPEED with its receiver (USB `046D:0B18`). G HUB can stay running alongside it.

## Build and run

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows/logiswitch.exe
```

A headphones icon appears in the tray:

| Colour | Meaning |
|---|---|
| Green | Headset on |
| Grey | Headset off |
| Orange | Receiver not found |
| Dark grey | Starting, or paused |

Right-click it to choose the fallback output and microphone, pause switching, toggle **Start with Windows**, or open the log and config. Left-clicking asks the receiver for its state right away.

## Configuration

Settings live in `%APPDATA%\logiswitch\config.json` and are created on first run:

| Setting | Default | Meaning |
|---|---|---|
| `HeadsetNameMatch` | `"G522"` | Text that identifies the headset's audio endpoints |
| `FallbackOutputId` | `null` | Output to use while the headset is off (set from the tray menu) |
| `FallbackOutputNameMatch` | `"NuForce"` | Used when no output ID is set: first active output whose name contains this |
| `SwitchInput` | `true` | Also move the default microphone |
| `FallbackInputId` | `null` | Mic to use while the headset is off; `null` = first active non-headset mic |
| `OffDelayMs` | `2000` | Delay before switching away after power-off |
| `RespectManualChoice` | `true` | Don't override a device you picked yourself |
| `StatusPollSeconds` | `60` | How often to re-check the headset state as a safety net |
| `Paused` | `false` | Stop switching |

The log is at `%LOCALAPPDATA%\logiswitch\logiswitch.log`.

## How it works

The receiver exposes a vendor HID interface (usage page `0xFFA0`, 64-byte reports). It speaks Logitech's "Centurion" protocol, with frames prefixed `50 23`:

```
50 23 | len | flags | feature index | function | sw-id | params…
```

`len` counts the flags byte plus the payload.

Its **bridge** feature (ID `0x0003`, index looked up at runtime through the root feature) provides both signals:

| Signal | Payload | Meaning |
|---|---|---|
| Unsolicited event | `[bridge, 00, 00, 01]` / `[bridge, 00, 00, 00]` | Headset turned on / off |
| Status (function 0) | `03 E8 00 nn 01` | `nn` = number of connected headsets |

logiswitch asks for the status at startup, after resume from sleep, when the receiver is re-plugged and every `StatusPollSeconds`. Between those checks it reacts to the events. Its requests use their own software ID (`0xB`), so they don't collide with G HUB's.

Changing the default device uses the undocumented `IPolicyConfig` COM interface, which is the same approach as the Windows Sound control panel, SoundSwitch and EarTrumpet. All three roles (console, multimedia, communications) are set.

The protocol details come from [HeadsetControl](https://github.com/Sapd/HeadsetControl)'s G522 support and were confirmed by sniffing the receiver.

## Limitations

- Only the G522 receiver (`0B18`) is recognised. Other Centurion headsets, like the PRO X 2, use a different frame prefix and product ID.
- Apps pinned to a specific device in Windows' per-app sound settings ignore default changes.
- Some apps only pick up a new default device after a restart.
