# logiswitch

A small Windows tray app that switches your default audio device when a **Logitech G522 LIGHTSPEED** headset is turned on or off.

## Why

The G522's USB receiver stays plugged in when the headset is powered off, so Windows keeps showing the headset as connected. It never falls back to your speakers. logiswitch listens to the receiver itself, which *does* know whether the headset is on.

- **Headset turns on:** the default output (and mic) switch to the G522 immediately.
- **Headset turns off:** after a short delay (2 s by default, to ride out range drop-outs), they switch to your fallback devices.
- A device you picked by hand is left alone. It only switches away from the headset if the headset is the current default, and only switches back if the fallback is.

## Requirements

- Windows 10 or 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), or the .NET 10 SDK to build it yourself
- G522 LIGHTSPEED with its receiver (USB `046D:0B18`). G HUB can stay running alongside it.

## Install

Download `logiswitch.exe` from the latest [release](../../releases/latest). Put it somewhere permanent, for example `%LOCALAPPDATA%\Programs\logiswitch`, and run it. To launch it at sign-in, turn on **Start with Windows** in its tray menu.

Each push to `main` that changes code builds a new release automatically (`.github/workflows/release.yml`). The version is `<major>.<minor>.<workflow run number>`, with major and minor taken from `<Version>` in `logiswitch.csproj`.

## Build from source

To build and install to `%LOCALAPPDATA%\Programs\logiswitch` in one step:

```bash
pwsh ./install.ps1
```

This needs the .NET 10 SDK. Run it again to update: it stops the running copy, publishes over it and starts the new one.

To build and run the development copy instead:

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows/logiswitch.exe
```

Only one copy can run at a time, so exit the installed one first.

A headphones icon appears in the tray:

| Colour | Meaning |
|---|---|
| White (dark on a light taskbar) | Headset on, with the battery level underneath |
| Grey | Headset off |
| Orange | Receiver not found |
| Dark grey | Starting, or paused |

The battery is shown as a percentage, or as a bar if you choose that style. Its colour is green above 30%, amber from 16% to 30%, red at 15% or lower, and blue while charging.

Click the icon (left or right) to open the menu. From there you can choose the fallback output and microphone, the icon style, pause switching, toggle **Start with Windows**, or open the log and config. The menu follows the Windows light/dark mode, and opening it also re-checks the headset state.

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
| `TrayIconStyle` | `"Percent"` | `"Percent"` (headphones with the percentage underneath) or `"Bar"` (headphones with a battery bar) |

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
