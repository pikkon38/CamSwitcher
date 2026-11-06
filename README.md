<p align="center"><img src="docs/icon.png" width="96" alt="CamSwitch icon"></p>

# CamSwitch

CamSwitch uses whichever webcam you're sitting in front of. It was made for a desk camera plus a camera on a racing rig. Star Citizen FOIP, Discord, Teams, Windows Hello and everything else then use the right camera, with no need to pick one in every app.

## How it works

Windows has no single "default camera". Every app picks its own, and two identical cameras (like two Logitech Brios) even have the same name. So CamSwitch keeps **only one camera turned on**, the one you're in front of, and turns the other one off in Device Manager. Every app, including Windows Hello, simply sees one camera.

In **Automatic** mode it watches the active camera for your face, using the face detection built into Windows (no cloud and no extra models). If nobody has been in view for 8 seconds, it turns the other camera on for a few seconds to look for you. If you're there, it switches; if not, it turns that camera off again and checks again 20 seconds later.

## Modes

| Mode | What happens |
| --- | --- |
| **Automatic** | Follows your face between the two cameras. |
| **Manual** | Stays on the camera you pick. |
| **Off** | Both cameras stay on and Windows and your apps choose as usual. |

You also pick a **default camera**. CamSwitch uses it at start-up, and Automatic mode goes back to it after nobody has been around for 2 minutes (you can change this). That way Windows Hello at the sign-in screen uses the camera you normally sit at.

Switch from anywhere with **Ctrl+Alt+C**, from the tray icon menu, or from the window (click the tray icon). The tray icon is blue for the desk, orange for the rig and grey when off.

## Install

1. Download `CamSwitch-Setup-x.y.z.exe` from [Releases](https://github.com/pikkon38/CamSwitcher/releases/latest) and run it. Windows SmartScreen may warn because the installer isn't code-signed: click **More info → Run anyway**.
2. The first time it runs, sit at your desk and click OK, so it can learn which camera is which.
3. CamSwitch starts when you sign in. It runs elevated through a scheduled task, because turning devices on and off needs admin. You won't see a UAC prompt.

**Updates:** CamSwitch checks GitHub Releases every few hours and when you click **Check for updates**. Click **Install update** and it downloads the new installer, updates and restarts itself.

**Uninstall** from Windows Settings → Apps. Both cameras are turned back on.

## Notes

- **Star Citizen:** select the camera once in the game's Comms/FOIP settings. If the game is already running when CamSwitch switches cameras, you may need to reselect it or toggle FOIP.
- **Windows Hello:** it uses whichever Hello-capable camera is turned on. To sign in with face at the rig, set up face recognition there once (Settings → Accounts → Sign-in options → Facial recognition → Improve recognition, while sitting at the rig).
- Settings are in `%APPDATA%\CamSwitch\config.json` and the log is `%APPDATA%\CamSwitch\camswitch.log`. Both open from the window. In the config you can rename the cameras (`DeskName`, `RigName`) and set the camera IDs if you replace one.
- If a camera ever stays off (for example after a crash), open CamSwitch and set Mode to Off, or run `CamSwitch.exe --restore-cameras` as admin.

## Building

```
dotnet publish src/CamSwitch/CamSwitch.csproj -c Release -o publish
```

This needs the .NET 8 SDK and builds on Windows or Linux. The installer is built with Inno Setup from `installer/CamSwitch.iss`. To release, push a tag like `v1.1.0` and the Release workflow builds the installer and publishes it.
