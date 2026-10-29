# Songyu Launcher

A free, open-source launcher for **Minecraft: Java Edition** on Windows.

Built with C# / WPF on .NET 10.

**Unofficial project. Not approved by or associated with Mojang or Microsoft.**
Published by Plomeng. Minecraft is a trademark of Mojang Synergies AB.
Contact: [Plomeng_Fan@outlook.com](mailto:Plomeng_Fan@outlook.com).

---

## What it does

- **Install Minecraft** — vanilla versions using Mojang's official metadata and download URLs
- **Install mod loaders** — Fabric and Quilt from their metadata services; Forge and NeoForge with their installers
- **Browse and install mods** — from the Modrinth public API, with automatic required-dependency resolution
- **Import modpacks** — `.mrpack` and plain `.zip`
- **Launch the game** — with a signed-in account that owns Minecraft: Java Edition, a user-selected Java runtime and per-instance settings

## What it does NOT do

- No telemetry, no analytics, no crash reporting
- **No backend server.** The launcher makes no requests to any server we control
- No offline identity launch or account sharing
- Does not bundle, modify or redistribute the official launcher
- Does not call any commerce, entitlement, purchase or moderation endpoint

## Microsoft account sign-in

Game launch requires sign-in with a Microsoft account that owns Minecraft: Java Edition.
The application must also use its own Client ID approved for the Minecraft Services
allow list. An Azure app registration alone does not grant access. Until approval,
version management and local skin preview remain available, but game launch is disabled.

The sign-in flow contacts these Microsoft, Xbox and Minecraft endpoints:

```
https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
https://login.microsoftonline.com/consumers/oauth2/v2.0/token
https://user.auth.xboxlive.com/user/authenticate
https://xsts.auth.xboxlive.com/xsts/authorize
https://api.minecraftservices.com/authentication/login_with_xbox
https://api.minecraftservices.com/minecraft/profile
```

Refresh and Minecraft access tokens, profile ID, name and skin/cape URLs are stored in
`account.json` inside the application's data directory. The directory is normally
`data` beside the application; `%LOCALAPPDATA%\Songyu` is used if that directory is
not writable. The file is plain JSON and can be deleted by signing out. Credentials
are sent only to the relevant Microsoft, Xbox and Minecraft authentication services;
the project has no server that receives them.

Game files are requested from Mojang's official download URLs. Other features contact
the relevant mod loader metadata services, Modrinth, and MC Encyclopedia for mod name
localization. The application does not transmit analytics or crash reports; diagnostic
logs stay on the user's computer.

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
├─ Songyu.Core.Download/    downloader, resume, progress
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
| Maple Mono, Monocraft | OFL-1.1 | bundled interface fonts (license texts included with assets) |

## Authors

- **Plomeng** — [@Plomeng](https://github.com/Plomeng)
- **Kiriko**

## License

Project code is licensed under the [MIT License](LICENSE). Bundled third-party assets
retain their own licenses, listed above and included with the relevant files.
