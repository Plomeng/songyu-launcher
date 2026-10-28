# Songyu Launcher

A free, open-source launcher for **Minecraft: Java Edition** on Windows.

Built with C# / WPF on .NET 10.

---

## What it does

- **Install Minecraft** — vanilla versions straight from Mojang's official metadata
- **Install mod loaders** — Fabric, Quilt, Forge, NeoForge (using their official installers)
- **Browse and install mods** — from the Modrinth public API, with automatic required-dependency resolution
- **Import modpacks** — `.mrpack` and plain `.zip`
- **Launch the game** — with a user-selected Java runtime, per-instance settings

## What it does NOT do

- No telemetry, no analytics, no crash reporting
- **No backend server.** The launcher makes no requests to any server we control
- No piracy, no cracked clients, no account sharing
- Does not bundle, modify or redistribute the official launcher
- Does not call any commerce, entitlement, purchase or moderation endpoint

## Microsoft account sign-in

Signing in is optional and only serves one purpose: letting a player launch Minecraft
with a copy they already own, so they can join multiplayer servers and use their own skin.

Only these endpoints are contacted:

```
https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
https://login.microsoftonline.com/consumers/oauth2/v2.0/token
https://user.auth.xboxlive.com/user/authenticate
https://xsts.auth.xboxlive.com/xsts/authorize
https://api.minecraftservices.com/authentication/login_with_xbox
https://api.minecraftservices.com/minecraft/profile
```

Tokens are stored **only on the user's own machine**, in a local JSON file. The user can
delete them at any time from the launcher's settings. Nothing is uploaded anywhere.

## Requirements

| | |
|---|---|
| OS | Windows 10 / 11 (x64) |
| Java | 17 or newer (21 recommended for 1.20.5+) |
| Runtime | .NET 10, or use the self-contained build |

## Build from source

```powershell
dotnet build Songyu.slnx -c Release

dotnet publish src/Songyu.App/Songyu.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o app
```

## Project layout

```
src/
├─ Songyu.Core.Minecraft/   version manifest, version JSON, library resolution
├─ Songyu.Core.Download/    downloader, mirrors, resume, progress
├─ Songyu.Core.Launch/      command building, natives extraction, process launch
├─ Songyu.Services/         installers, mod handling, auth, configuration
└─ Songyu.App/              WPF UI
```

## Third-party components

| Package | License | Used for |
|---|---|---|
| `Microsoft.Data.Sqlite` | MIT | local content index |
| `System.Text.Json` | MIT | configuration and API parsing |
| `fNbt` | BSD-3-Clause | reading and writing local server lists |
| `Microsoft.Web.WebView2` | BSD-3-Clause | hosting the skin preview |
| `skinview3d` | MIT | interactive 3D skin rendering (license included with assets) |

## Authors

- **Plomeng** — [@Plomeng](https://github.com/Plomeng)
- **Kiriko**

## Disclaimer

This project is **not affiliated with, endorsed by, or supported by** Mojang Studios or
Microsoft. "Minecraft" is a trademark of Mojang Synergies AB.

Users must own a legitimate copy of Minecraft: Java Edition.
