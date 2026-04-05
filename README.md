# PS Controller Monitor

PS Controller Monitor is a small Windows utility for monitoring DualSense and DualShock 4 connection state, transport type, charging state, and battery level.

This repository contains a controller host monitor and a WinForms tray client.

## Features

- Detects whether a controller is connected over Bluetooth or USB
- Shows charging state and battery level
- Displays a tray icon with tooltip information and detailed information on double-click
- Supports re-ordering the reported controller list
- Supports multiple controller detection (only tested with two tho)

## Requirements

- Windows
- .NET 10 SDK

## How It Works

- `BluetoothBatteryMonitor` reads controller state from Windows HID interfaces and publishes status updates.
- `PSControllerMonitor` reads those updates and presents them through a Windows tray icon, hover panel, and details window.
- The portable published executable embeds the monitor host into the tray application so you can run a single file instead of two separate processes.

## Use Portable Release

If you want the single-file portable app, publish the tray project with the included profile:

```powershell
dotnet publish ".\PSControllerMonitor\PSControllerMonitor.csproj" /p:PublishProfile=PortableSingleFile
```

The published executable is written to:

```text
.\PSControllerMonitor\bin\Release\net10.0-windows10.0.19041.0\publish-portable\PSControllerMonitor.exe
```

## Build From Source

Open PowerShell in the project root and run:

```powershell
dotnet restore ".\Bluetooth Detector.sln"
dotnet build ".\Bluetooth Detector.sln"
```

## Run From Source

Start the monitor first in one terminal:

```powershell
dotnet run --project ".\BluetoothBatteryMonitor\BluetoothBatteryMonitor.csproj"
```

Then start the tray client in a second terminal:

```powershell
dotnet run --project ".\PSControllerMonitor\PSControllerMonitor.csproj"
```

If you just cloned the repo, the full command sequence is:

```powershell
dotnet restore ".\Bluetooth Detector.sln"
dotnet build ".\Bluetooth Detector.sln"
dotnet run --project ".\BluetoothBatteryMonitor\BluetoothBatteryMonitor.csproj"
dotnet run --project ".\PSControllerMonitor\PSControllerMonitor.csproj"
```

## Known Limitations

- Windows only
- Focused on DualSense and DualShock 4 controllers
- Multiple controller support is implemented, but it has only been tested with two controllers
- The Raw Details tab is a plain status dump, not live controller input telemetry

## References And Credits

These sources were especially useful while debugging and correcting the Bluetooth parsing logic:

1. `nondebug/dualsense`
Used to compare DualSense USB and Bluetooth report layouts, especially the difference between Bluetooth report `0x01` and full report `0x31`.
Repository: https://github.com/nondebug/dualsense

2. `torvalds/linux`
The Linux `hid-playstation` driver was especially useful for confirming DualSense battery and charging interpretation, including the charging-status nibble and the full Bluetooth report size.
Repository: https://github.com/torvalds/linux

3. Sony / Windows HID behavior itself
Some final behavior still had to be confirmed empirically from real controller reports on Windows, especially for Windows-specific Bluetooth HID behavior.

## Disclaimer

This software is provided as is, without warranty of any kind, express or implied. Use it at your own risk.

You are responsible for making sure your use of this project complies with all applicable laws, platform rules, workplace policies, and device terms of service. Do not use this software to violate laws, interfere with systems you do not own or control, or bypass restrictions you are not authorized to bypass.

This project is intended for personal monitoring, development, testing, and learning purposes.

The project was completed with the help of Copilot.