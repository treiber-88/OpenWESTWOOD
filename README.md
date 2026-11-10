# OpenWESTWOOD

## The original games it needs

OpenWESTWOOD plays the factions of many Westwood and EA games. Two of them come from your own copy of the game:

- **Command & Conquer Generals Zero Hour**: its USA, China and GLA (and their generals), maps and missions, and the
  menus themselves. OpenWESTWOOD can't start without it.
- **Command & Conquer 3: Kane's Wrath**: its GDI, Nod and Scrin (and their sub-factions) and maps.

Both are in the Command & Conquer Ultimate Collection, on [Steam](https://store.steampowered.com/bundle/39394/)
or the [EA app](https://www.ea.com/games/command-and-conquer/command-and-conquer-the-ultimate-collection).

**Installing their artwork.** OpenWESTWOOD only needs your copy of the game to be somewhere on the computer: an EA
app, Origin, Steam or retail install, a copy on another drive or USB stick, or the game's disc (or a mounted disc
image). The first time it starts, it looks for Zero Hour and offers to install its artwork; this copies the original
files it uses into its `content` folder (about 2.8 GB for Zero Hour, 3 GB for Kane's Wrath). After that the original
isn't needed: the disc can go back in its box, and the drive can be unplugged. Kane's Wrath is installed the same way
from the message at the main menu (its Install button). Until it is, its factions, maps and missions are locked and
every other faction can still be played.

Only the games' original files are copied: each is checked against `mods/zh/original-files.txt` (its size and
SHA-256). A file changed by a community patch is not copied; if every copy found has it changed, the installer says
which files, and the game's disc or a fresh install has the originals.

It looks in: the EA app, Origin, retail and Steam installs (the registry, every Steam library, the usual install
folders on each drive), Proton, Wine, Lutris, Bottles, Heroic, CrossOver and Whisky prefixes, Windows drives mounted on
Linux or macOS, and every drive's folders (discs, USB sticks and network drives more deeply). If yours is somewhere
else, tell it where:

- start it with `--zh-path "<folder>"` or `--kw-path "<folder>"`, or
- write the folders into `installs.ini` in the settings folder (`%APPDATA%\OpenWESTWOOD` on Windows,
  `~/.config/OpenWESTWOOD` on Linux and macOS):

      ZeroHour = D:\Games\Command and Conquer Generals Zero Hour
      KanesWrath = D:\Games\Command Conquer 3 Kanes Wrath

`OpenWESTWOOD --detect-installs` lists where it looked, what it found and what is still to copy;
`OpenWESTWOOD --install zh` (or `kw`) installs from the command line. `--pretend-missing zh,kw` (or `all`) starts the
game as if they were not installed.

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
