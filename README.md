# Windows Notification Monitor

A native Windows desktop application that monitors **Google Chrome notifications** delivered through the Windows notification system and triggers local alarms and mobile push alerts through **ntfy**.

## Technologies

- **C# / .NET 8**, WPF, Windows Runtime (WinRT)
- **Windows UserNotificationListener API**
- **PowerShell**, MSIX packaging and installation
- **ntfy** for push notifications

## Core Concepts

- **Event-Driven Programming:** reacts to native notification-change events
- **Native API Integration:** accesses Windows notifications with user permission
- **Filtering & Deduplication:** matches Chrome notifications and avoids repeated alerts
- **Reliability:** periodically reconciles notification state as a fallback
- **Desktop-to-Mobile Messaging:** sends remote alerts via ntfy

## Run Locally (Windows)

1. Extract the project and open **PowerShell** in its folder.
2. Run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\build-install.ps1 -NtfyTopic "YOUR_PRIVATE_TOPIC"
   ```

3. Approve installation and notification access when prompted.
4. Subscribe to the same topic in the **ntfy** mobile app; use **Testar alarme + ntfy** to verify alerts.

The installer may request administrator access and install required development tools. Keep your ntfy topic private; the default public ntfy service does not provide private-topic access control by default.

## Repository structure

- `MainWindow.xaml(.cs)` — UI and notification processing
- `App.xaml(.cs)` — application startup
- `build-install.ps1` / `uninstall.ps1` — install and removal
- `Package.appxmanifest` — Windows package identity

The internal C# namespace and package identity remain `ChromeNotificationWatcher` for compatibility.

## Configuration

Edit `appsettings.json` to adjust notification filters, local alarm behavior, mobile alert repeats, and the ntfy server. Leave `TitleOrBodyContains` empty to watch all Chrome notifications.

## Architecture

`Chrome → Windows notifications → UserNotificationListener → filter / deduplicate → PC alarm + ntfy push`

Uses native Windows notifications; it does **not** inspect browser pages or use OCR. To uninstall, run `uninstall.ps1`.
