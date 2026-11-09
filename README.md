# OpenWESTWOOD

## The original games it needs

OpenWESTWOOD plays the factions of many Westwood and EA games. Two of them must be installed on the computer
from your own copy:

- **Command & Conquer Generals Zero Hour**: its USA, China and GLA (and their generals), maps and missions.
- **Command & Conquer 3: Kane's Wrath**: its GDI, Nod and Scrin (and their sub-factions) and maps.

Both are in the Command & Conquer Ultimate Collection, on [Steam](https://store.steampowered.com/bundle/39394/)
or the [EA app](https://www.ea.com/games/command-and-conquer/command-and-conquer-the-ultimate-collection).
If one is missing, a message at the main menu says so (with those links), its factions, maps and missions are
locked, and every other faction can still be played.

OpenWESTWOOD finds the games by itself: from the EA app, Origin, retail and Steam installs (the registry, every
Steam library, the usual install folders on each drive), Proton, Wine, Lutris, Bottles, Heroic, CrossOver and
Whisky prefixes, and Windows drives mounted on Linux or macOS. A folder counts only if the game's files are
really in it. If yours is somewhere else, tell it where:

- start it with `--zh-path "<folder>"` or `--kw-path "<folder>"`, or
- write the folders into `installs.ini` in the settings folder (`%APPDATA%\OpenWESTWOOD` on Windows,
  `~/.config/OpenWESTWOOD` on Linux and macOS):

      ZeroHour = D:\Games\Command and Conquer Generals Zero Hour
      KanesWrath = D:\Games\Command Conquer 3 Kanes Wrath

`OpenWESTWOOD --detect-installs` lists where it looked and what it found. `--pretend-missing zh,kw` (or `all`)
starts the game as if the games were not installed.

## Running it

- **Windows**: `OpenWESTWOOD.exe` in this folder.
- **Linux**: `platforms/linux-x64/OpenWESTWOOD`. Run it from a terminal in this folder (the game folder must keep
  `mods/`, `content/` and `fonts/` above it):

      chmod +x platforms/linux-x64/OpenWESTWOOD
      ./platforms/linux-x64/OpenWESTWOOD

  It needs OpenGL 3.3 (any current Mesa or vendor driver) and X11 or Wayland. On a dual-boot PC it finds the games
  on the Windows partition if that is mounted (under `/mnt`, `/media` or `/run/media`).
- **macOS**: `platforms/osx-arm64/OpenWESTWOOD` (Apple silicon) or `platforms/osx-x64/OpenWESTWOOD` (Intel).
  The file is not signed, so macOS needs it signed once on that Mac before it will start:

      chmod +x platforms/osx-arm64/OpenWESTWOOD
      codesign --force --sign - platforms/osx-arm64/OpenWESTWOOD
      xattr -d com.apple.quarantine platforms/osx-arm64/OpenWESTWOOD
      ./platforms/osx-arm64/OpenWESTWOOD

## Building the executable

The game executable lives directly in this folder as `OpenWESTWOOD.exe` (a single self-contained
file; it finds `mods/`, `content/` and `fonts/` next to it). To rebuild it:

    dotnet publish src/OpenWestwood.Game -c Release -r win-x64 -o publish
    copy publish\OpenWESTWOOD.exe .

For macOS or Linux use `-r osx-arm64`, `-r osx-x64` or `-r linux-x64` with `-o platforms/<that name>` (the file is
then called `OpenWESTWOOD`).
