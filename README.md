# AKKO 3061 • World Tour Tokyo RGB

I couldn't find any existing software to configure the RGB backlighting on my **AKKO 3061 World Tour Tokyo** keyboard, so I built this app with AI assistance. Maybe someone else will find it useful.

---

## Features

- **18 Built-in Lighting Effects + Per-Key Custom Mode**:
  - Static, Breathing, Spectrum Cycle, Blooming, Color Waves, Color Wheel, Rainbow modes, Reactive modes, Starlight, Hurricane, Accumulate, Visor, Surmount.
  - **Per-Key Custom Coloring**: paint individual keys with a brush or eraser, apply built-in presets (`Tokyo`, `WASD`, `Rainbow`), save up to 5 profiles directly to the keyboard, and calibrate keys if needed.
- **Circular 16.7M Color Wheel**:
  - Smooth color wheel, brightness/value slider, HEX input, R/G/B sliders, and quick color swatches with real-time preview on the keyboard.
- **World Tour Tokyo Keyboard Preview**:
  - Interactive 61-key preview matching the Tokyo keycap layout.
- **Themes & Languages**:
  - Switch between **Black & Sakura Pink** and **Light** themes, and between **English / Russian** (`EN / RU`).
- **System Tray**:
  - Minimizes cleanly to the Windows system tray on close.

## Build & Run

Requirements: Windows 10/11 and [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build AkkoTokyoRGB\AkkoTokyoRGB.csproj -c Release
```

Run the compiled app at:
`AkkoTokyoRGB\bin\Release\net8.0-windows\AkkoTokyoRGB.exe`

## Credits

- Lighting protocol reference: [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (`EVisionV2KeyboardController`).
