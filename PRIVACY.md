# DiexvMinecraftLauncher Privacy Policy

Last updated: 2026-11-09

DiexvMinecraftLauncher is a free, non-commercial, source-available Minecraft:
Java Edition launcher for Windows. The repository is publicly viewable under an
All Rights Reserved license. This policy explains what data the launcher
handles when you use Microsoft account login.

## Data we handle

- **Microsoft account login.** When you choose to sign in with Microsoft, the
  launcher uses the OAuth 2.0 device-code flow with the scopes
  `XboxLive.signin` and `XboxLive.offline_access`. Microsoft and Xbox Live
  return an access token, a refresh token, your Minecraft profile name, UUID,
  and skin URL.
- **Local game data.** The launcher reads and writes files in the Minecraft
  directory you select, such as versions, mods, resource packs, shader packs,
  logs, and settings.
- **Local settings.** Player name, selected skin, Java path, memory settings,
  window size, and similar preferences are stored in
  `%APPDATA%\ILauncher\settings.json`.

## Where data is stored

All account tokens and settings are stored **only on your own computer**.
Refresh tokens are protected with Windows DPAPI for the current Windows user.
The launcher has no backend server and does not upload tokens, profile data,
game files, or settings to the developer.

## What we do not do

- We do not collect telemetry or analytics.
- We do not use advertising or sell user data.
- We do not receive or store your Microsoft password.
- We do not provide account sharing, credential sharing, or authentication
  bypasses.
- We do not operate a server that receives your Minecraft access token.

## Third-party services

When you use online features, the launcher may contact:

- Microsoft identity platform, Xbox Live, and Minecraft Services for login and
  profile data;
- Mojang APIs for public skin/profile lookup;
- Modrinth, MC百科, CurseForge search pages, Mojang/BMCLAPI download sources,
  and GitHub for content and updates.

Those services have their own privacy policies.

## Your choices

You can use the launcher without Microsoft login. You can sign out at any time;
signing out removes the stored Microsoft tokens from the local settings. You can
also delete `%APPDATA%\ILauncher\settings.json` to remove all launcher settings
and tokens.

## Contact

Project repository:
https://github.com/1397559266/DiexvMinecraftLauncher

For privacy questions, open an issue in the repository.
