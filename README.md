# Wavy

A music player for Windows with search, local playlists, lyrics, and Discord activity. No account required.

## Features

- Search songs, artists, albums, and playlists, with recent searches and recently played songs in one place.
- Create playlists, save favorite albums, and manage your playback queue.
- Choose audio quality, adjust the ten-band equalizer, and enable loudness normalization.
- Read lyrics and jump to a line during playback when timed lyrics are available.
- Share the current song and artist through Discord Rich Presence.
- Get notified about new releases, then download and install updates from Settings.
- Use light, dark, or darker mode, with artwork based ambient backgrounds.

## Download

Get the Windows installer from [GitHub Releases](https://github.com/csgdV2/WavyMusic/releases). Install it, then open Wavy from the Start menu.

## Build

You'll need Windows, the .NET 8 SDK, and the Windows build tools for WinUI 3. Visual Studio with the Windows application development workload is the easiest way to get the required tools.

```powershell
git clone https://github.com/csgdV2/WavyMusic.git
cd WavyMusic
dotnet restore Wavy.sln
dotnet build Wavy.sln -c Release -p:Platform=x64
```

To create a self-contained build:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
.\publish\win-x64\Wavy.exe
```

The output is in `publish/win-x64`. To also build the installer with its light and dark themes, install Inno Setup 7 and run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1 -BuildInstaller
```

Wavy provisions yt-dlp when needed. Audio processing also uses FFmpeg; see [the tools folder](src/Wavy/Tools/README.txt) for setup.

## Development

`src/Wavy` contains the WinUI app. `src/Wavy.Core` contains the catalog, stream, and lyrics services. The website lives in `website`.

Run the regression checks with:

```powershell
dotnet run --project tests/WavyMusic.Regression -c Release
```

## Feedback

Found a bug or have an idea? [Open an issue](https://github.com/csgdV2/WavyMusic/issues) with your Wavy version and steps to reproduce it.
