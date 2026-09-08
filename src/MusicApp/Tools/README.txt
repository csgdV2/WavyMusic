Drop the following two executables in THIS folder:

  yt-dlp.exe    https://github.com/yt-dlp/yt-dlp/releases/latest
  ffmpeg.exe    https://www.gyan.dev/ffmpeg/builds/  (from the bin/ folder of an "essentials" build)

They are copied next to the built app automatically (see MusicApp.csproj).
If they are missing here, the app falls back to whatever is on your PATH.

ffmpeg is optional for plain streaming but recommended for robust downloads.
