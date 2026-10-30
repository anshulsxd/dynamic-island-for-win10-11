# Island — a Dynamic Island / Notch for Windows 10 & 11

A small pill (or MacBook-style notch) at the top of your screen. Hover to expand.

## Features
- Collapsed pill shows live weather ("Rainy · 18°C") — you choose the city, no location access
- Expanded header: clock, weather, iPhone-style green battery ring
- **Notifications**: every Windows notification (system + apps) pops out with the app's name and icon
- **Device popups**: charger plugged in / out ⚡, USB devices, drives & SD cards 💾, headphones & audio 🎧,
  monitors 🖥️, Ethernet cable 🌐
- **Music** (Spotify or any player): cover art, timeline (dot appears on hover, click/drag to seek), prev / pause / next
- **Controls**: Wi-Fi, Bluetooth, Airplane tiles + vertical Volume bar (+ Brightness bar on laptops)
- Calculator, Calendar, Weather (city search, °C/°F)
- **Alerts** log with a command box that runs CMD commands
- Red power button: Exit island, Sleep (laptops), Restart, Shut down
- **Version** tab: green Install button when a new GitHub release exists (see RELEASING.md)
- Tray icon: Start with Windows, **Notch mode**, Show/Reset, Hide, Send test notification, Check for updates, Exit

## Build the .exe
Needs the free .NET 8 (or newer) SDK. In this folder run:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out

Your app is `out\Island.exe`. (No SDK? See `BUILD_WITHOUT_INSTALLING.md`.)

## How notification popups work
Windows' official notification-reader API needs a signed MSIX package identity, which a portable `.exe` doesn't
have. Island instead reads Windows' local notification history (`%LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db`)
read-only, once per second. Nothing leaves your PC. Limits: it's an undocumented file (a future Windows update could
change it), up to ~1 s delay, and apps whose notifications are off in Windows Settings never reach it.

## Notes
- Errors are saved to `%LocalAppData%\IslandEye\log.txt`.
- "Airplane" turns all radios off/on (it doesn't flip Windows' own airplane flag).
- Brightness only appears on laptop screens that support it.

## License
MIT
