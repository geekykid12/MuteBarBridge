# MuteBarBridge

A small Windows bridge app that takes the video feed from [MuteBar](https://mutebar.app/)'s virtual webcam (**SoftCam**, exposed as the DirectShow device `OAW CAM`) and re-publishes it through the [UnityCapture](https://github.com/schellingb/UnityCapture) virtual camera driver — so it can be picked up as a normal webcam source in **OBS Studio**, **Discord**, and anywhere else that reads from a UnityCapture device.

## Why this exists

MuteBar's SoftCam device isn't directly usable in every app that wants a webcam feed. One major issue it addresses is compatibility with **Discord on Windows 11**, where the native virtual feed frequently fails to load properly. 

This bridge sits in between: it captures frames from `OAW CAM` via DirectShow, converts and scales them, and writes them into the shared-memory interface that the UnityCapture driver reads from — turning MuteBar's output into a standard virtual camera device other apps can select.

## How it works

```
MuteBar SoftCam ("OAW CAM")
        │  DirectShow (ISampleGrabber)
        ▼
DirectShowCapture   — captures raw frames, normalizes to top-down BGRA
        ▼
BridgeApp           — matches the output resolution to the capture device's
                       native resolution, keeps pushing the last frame at
                       ~30fps so the driver never times out during quiet/
                       muted periods
        ▼
UnityCaptureSink     — writes frames into the UnityCapture driver's shared
                       memory buffer (mutex + event signaling)
        ▼
UnityCapture virtual camera  →  OBS Studio / Discord (Windows 11 Fix) / any app
```

## Requirements

- Windows 10 / 11
- [MuteBar](https://mutebar.app/) installed and running, with its SoftCam (`OAW CAM`) active
- [UnityCapture](https://github.com/schellingb/UnityCapture) virtual camera driver installed

## Installation

1. Go to the [Releases](../../releases) page of this repository.
2. Download the latest installer: `MuteBarBridge-Setup-1.0.0.exe`.
3. Run `MuteBarBridge-Setup-1.0.0.exe` and follow the on-screen prompts to install the application.

## Building from Source

Requirements for building:
- .NET SDK matching the project's target framework
- [DirectShowLib](https://www.nuget.org/packages/DirectShowLib) (restored via NuGet)

```
dotnet build MuteBarBridge.csproj
```

To compile the installer from source, open the `.iss` script using [Inno Setup](https://jrsoftware.org/isinfo.php) and compile it to generate `MuteBarBridge-Setup-1.0.0.exe`.

## Running

Launch MuteBarBridge from your Start menu or desktop shortcut. 

A console window opens and the bridge starts automatically, capturing from `OAW CAM` and pushing frames to the UnityCapture driver. Once running, select the UnityCapture device as your camera source in OBS Studio, Discord, or any other app.

Press **Enter** in the console window at any time to stop the bridge and exit.

## Notes

- The output resolution adapts dynamically to match the capture device's native resolution rather than being fixed — no manual configuration needed if `OAW CAM`'s resolution changes.
- A keep-alive timer re-sends the last frame at ~30fps so the UnityCapture driver doesn't hit its watchdog timeout during periods when MuteBar isn't actively sending frames.
