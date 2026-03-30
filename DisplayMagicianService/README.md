# DisplayMagicianService

A Windows service that provides remote display profile management via a Telegram bot. It allows you to switch display configurations, automate user session switching, launch applications, and set up automatic profile revert when the PC is locked — all from your phone.

---

## What It Does

- Exposes a Telegram bot interface for managing display profiles stored by DisplayMagician
- Switches display profiles by running `DisplayMagicianConsole.exe ChangeProfile` in the active user session
- Switches Windows user sessions (using `tscon` for password-free accounts, `tsdiscon` for password-protected ones)
- Optionally closes all user apps, logs off a target user, and launches an application after switching
- Listens for Windows session lock events and automatically reverts the display profile when the PC locks
- Restricts access to a configurable whitelist of Telegram user IDs

---

## appsettings.json Structure

```json
{
  "Telegram": {
    "BotToken": "",
    "AllowedUsers": ""
  },
  "Service": {
    "ProfilesPath": ""
  },
  "UserProfiles": [
    {
      "Name": "TV",
      "WindowsUser": "KidsAccount",
      "HasPassword": false,
      "DisplayProfile": "TV Display",
      "LaunchApp": "steam://rungameid/123456",
      "CloseApps": true,
      "LogoutUser": true,
      "LogoutTarget": "WorkAccount",
      "RevertDisplayProfile": "Desktop"
    }
  ]
}
```

### Field Reference

| Field | Description |
|-------|-------------|
| `Telegram:BotToken` | Bot token from @BotFather. Can also be set via `TELEGRAM_BOT_TOKEN` environment variable. |
| `Telegram:AllowedUsers` | Comma-separated list of Telegram user IDs allowed to use the bot. Can also be set via `TELEGRAM_ALLOWED_USERS`. Send `/myid` to the bot to get your ID. |
| `Service:ProfilesPath` | Full path to `DisplayProfiles.json`. Defaults to `%LOCALAPPDATA%\DisplayMagician\Profiles\DisplayProfiles.json`. |
| `UserProfiles[].Name` | Display name shown in the Telegram automation menu. |
| `UserProfiles[].WindowsUser` | Windows account username to switch to. Leave empty to skip user switching. |
| `UserProfiles[].HasPassword` | `true` if the target account has a password (will show login screen). `false` for automatic switch via `tscon`. |
| `UserProfiles[].DisplayProfile` | Name of the DisplayMagician profile to apply when this automation runs. |
| `UserProfiles[].LaunchApp` | Path to an `.exe` (with optional arguments) or a protocol URL (`steam://`, `origin2://`) to launch after switching. Leave empty to skip. |
| `UserProfiles[].CloseApps` | If `true`, kills all non-explorer processes for `LogoutTarget` (or `WindowsUser` if no LogoutTarget) before switching. |
| `UserProfiles[].LogoutUser` | If `true`, logs off the session for `LogoutTarget` after the user switch. |
| `UserProfiles[].LogoutTarget` | Windows username of the session to close/log off. Used by `CloseApps` and `LogoutUser`. |
| `UserProfiles[].RevertDisplayProfile` | If set, the service will automatically switch back to this display profile when the PC session is locked. |

---

## Telegram Commands

| Command | Description |
|---------|-------------|
| `/start` or `/help` | Opens the main menu |
| `/profiles` | Lists all display profiles with switch and remove buttons |
| `/status` | Shows current auto-revert state |
| `/cancel` | Cancels any pending auto-revert on lock |
| `/create <name>` | Opens DisplayMagician's profile creation UI |
| `/myid` | Returns your Telegram user ID (works even for unauthorized users) |

---

## Telegram Menus

### Profiles Menu
Reached via the main menu. Shows one row per display profile with two buttons:
- **Switch** — immediately switches to that profile
- **Switch (revert on lock)** — switches to that profile and arms an automatic revert to another profile when the session is locked

### Automation Menu
Reached via the main menu. Shows one button per `UserProfiles` entry. Pressing a button:
1. Applies the configured display profile
2. Switches the Windows user session
3. Optionally closes all apps for the target user
4. Optionally logs off the target user
5. Optionally launches an application
6. Optionally arms auto-revert of the display profile on session lock

---

## Setup

### Prerequisites
- Windows 10/11
- .NET 8 or later runtime
- DisplayMagician installed with at least one saved profile
- A Telegram bot token from @BotFather

### Install and Run as a Service

```bat
sc create DisplayMagicianService binPath= "C:\path\to\DisplayMagicianService.exe" start= auto
sc start DisplayMagicianService
sc stop DisplayMagicianService
sc delete DisplayMagicianService
```

Place `appsettings.json` in the same directory as the executable. The service reads it on startup.

### Running Interactively (for testing)

```bat
DisplayMagicianService.exe
```

---

## Security Features

| Feature | Detail |
|---------|--------|
| User ID whitelist | Only Telegram user IDs listed in `AllowedUsers` can use the bot. All others are silently ignored. |
| Rate limiting | Each user is limited to 10 requests per minute. Excess requests are rejected. |
| Silent deny | Unauthorized users receive no response, making the bot invisible to strangers. |
| Private DM only | The bot ignores all messages from group chats. |
| Old message filtering | Messages older than 30 seconds are ignored (prevents replay of stale commands after service restart). |
| Username validation | Windows usernames are validated against `^[a-zA-Z0-9_.\-]+$` before being passed to any system calls. |
| No exception details | Error details are logged server-side only; the bot sends a generic error message to users. |

---

## How Session Lock Detection Works

The service registers `SessionAwareServiceLifetime`, which extends `WindowsServiceLifetime` and sets `CanHandleSessionChangeEvent = true`. Windows delivers `WM_WTSSESSION_CHANGE` events to the service via `ServiceBase.OnSessionChange`. When a `SessionLock` event arrives, `SessionMonitor.HandleSessionChange` fires and (if armed) switches the display profile on a background thread.

This is entirely event-driven — no polling timers are used.

---

## How User Switching Works

- **No-password account** (`HasPassword: false`): Uses `tscon.exe <sessionId> /dest:console` to directly connect an existing disconnected session to the console, or `tsdiscon.exe` to show the login screen if no session exists.
- **Password-protected account** (`HasPassword: true`): Uses `tsdiscon.exe <currentSession>` to disconnect the current session and show the Windows login screen. The user must enter the password manually.

`FindUserSession` enumerates all sessions via `WTSEnumerateSessions` and `WTSQuerySessionInformation` to find an existing session for the target username.

---

## How App Launching Works

Apps are launched in the active user's desktop session from the SYSTEM service context using `CreateProcessAsUser` with a duplicated user token obtained via `WTSQueryUserToken`. This gives the process access to the user's display, environment, and credentials.

- **Direct `.exe`**: The path up to and including `.exe` is used as the executable; any text after is passed as arguments.
- **Protocol URLs** (e.g. `steam://rungameid/123456`): Launched via `cmd.exe /c start "" "<url>"`, which invokes the registered URL handler.

The user's environment block is inherited and optionally extended with `DISPLAYMAGICIAN_DATA_PATH`.

---

## DISPLAYMAGICIAN_DATA_PATH

The service automatically sets `DISPLAYMAGICIAN_DATA_PATH` in the environment of every process it launches in the user session. This is derived from the configured `ProfilesPath` by going up two directory levels (from `Profiles/DisplayProfiles.json` to the `DisplayMagician` data directory). This allows `DisplayMagicianConsole.exe` to find the correct profile data when run from a service context.
