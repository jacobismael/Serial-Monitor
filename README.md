# logicom

A simple C# / Avalonia UART serial monitor for macOS.

<img width="1018" height="696" alt="Screenshot 2026-05-26 at 9 41 39 AM" src="https://github.com/user-attachments/assets/b6d6b47f-580e-489d-b3e3-d32df1323d11" />

## Features

- List available serial ports
- Connect/disconnect UART devices
- Send commands
- Select line endings: None, LF, CR, CRLF
- Save serial output to a log file
- Toggle timestamps

## Run

```bash
dotnet run --project src/serial.Desktop
```
