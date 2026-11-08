# Default skins

This directory intentionally contains **no bundled Mojang skin textures**.

At startup `SkinService` scans the configured Minecraft root (and the standard
`%APPDATA%\.minecraft` / `%USERPROFILE%\.minecraft` locations) for a client jar,
then extracts the official default player textures from:

```text
assets/minecraft/textures/entity/player/wide/<name>.png
assets/minecraft/textures/entity/player/slim/<name>.png
```

Older client jars using `assets/minecraft/textures/entity/<name>.png` are also
supported. Extracted files are written to:

```text
%APPDATA%\ILauncher\skins\
```

If no client jar is found, the launcher creates a neutral `Default.png`
placeholder so the UI still works. It does not ship or redistribute Mojang's
default skin textures.

Minecraft, Steve, Alex and the other default skins remain trademarks of Mojang
Studios. This project is not affiliated with Mojang or Microsoft.
