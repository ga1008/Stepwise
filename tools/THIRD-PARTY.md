# Third-party software

Stepwise's own source is MIT-licensed. The components below retain their own
copyrights, licenses, and notices. Crediting Stepwise's authors does not change
third-party ownership or imply endorsement by these projects.

## FFmpeg: downloaded separately for the public release

The public GitHub setup ZIP **does not contain `ffmpeg.exe` or `ffprobe.exe`**.
Run `Prepare-Stepwise.cmd` once after extracting it. The included
`tools/Get-FFmpeg.ps1` downloads the pinned, unmodified FFmpeg 8.1.2 essentials
Windows x64 archive directly from Gyan Doshi's upstream server, checks its
SHA-256, and extracts the two executables plus the upstream license and README.
Internet access is needed for this preparation only. Afterwards the tools run
locally as separate child processes; capture and export do not contact a
conversion service.

- Official FFmpeg download page listing Windows providers: https://ffmpeg.org/download.html
- Build provider: https://www.gyan.dev/ffmpeg/builds/
- Exact archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.7z
- Published SHA256: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.7z.sha256
- Verified archive SHA256: `e25b682664025d49034c981afb4bae36238a40f29a3cc1c713ad9a8b5b3528f6`
- Version: `8.1.2-essentials_build-www.gyan.dev`
- License: GNU General Public License v3, included verbatim in `FFmpeg-LICENSE.txt`.
- Exact FFmpeg source revision from the build's README: https://github.com/FFmpeg/FFmpeg/commit/38b88335f9
- Upstream binary release: https://github.com/GyanD/codexffmpeg/releases/tag/8.1.2
- Build configuration and enabled libraries: `FFmpeg-README.txt` (unmodified upstream file) and `ffmpeg.exe -buildconf`.

The FFmpeg build includes GPL components such as libx264. This project does not
mirror its binaries or claim to supply complete corresponding source for all
of its statically linked dependencies. The FFmpeg revision above identifies
the main upstream source; it is not a complete source bundle for the Gyan
build. Gyan's support repository and its automatically generated source ZIPs
are not the FFmpeg build's corresponding source either.

If you redistribute a locally prepared package containing those binaries, you
must retain the applicable licenses and satisfy their corresponding-source
requirements. The public packaging script deliberately excludes the binaries.
See https://ffmpeg.org/legal.html and `FFmpeg-LICENSE.txt`.

## Microsoft .NET and Windows Desktop runtime

The self-contained Windows x64 package includes .NET 8.0.31 runtime files.
The public package copies the license and third-party notices from the matching
Microsoft NuGet runtime packages:

- `tools/licenses/DOTNET-LICENSE.txt`
- `tools/licenses/DOTNET-THIRD-PARTY-NOTICES.txt`
- `tools/licenses/WINDOWSDESKTOP-LICENSE.txt`

Source package paths, relative to the NuGet cache:
`microsoft.netcore.app.runtime.win-x64/8.0.31/{LICENSE.TXT,THIRD-PARTY-NOTICES.TXT}`
and `microsoft.windowsdesktop.app.runtime.win-x64/8.0.31/LICENSE`.
Projects: https://github.com/dotnet/runtime and https://github.com/dotnet/winforms .

## Microsoft WebView2

The application uses Microsoft.Web.WebView2 **1.0.3537.50**. The SDK license and
notices are copied from `microsoft.web.webview2/1.0.3537.50/{LICENSE.txt,NOTICE.txt}`
in the NuGet cache to `tools/licenses/WebView2-LICENSE.txt` and
`tools/licenses/WebView2-NOTICE.txt` in the public package.

The Microsoft Edge WebView2 Runtime is a separate system component and is not
included in the Stepwise ZIP. Windows 11 normally already provides it.
Official download: https://developer.microsoft.com/microsoft-edge/webview2/ .

## NAudio

Audio capture uses NAudio 2.2.1 (MIT license):
https://github.com/naudio/NAudio/tree/v2.2.1

Upstream MIT license: https://github.com/naudio/NAudio/blob/v2.2.1/license.txt .
The notice is included as `tools/NAudio-LICENSE.txt`.

## Noto Sans CJK

The annotation editor includes the unmodified Noto Sans CJK SC Regular 2.004
font under the SIL Open Font License 1.1. Its license and provenance are in
`Web/fonts/OFL.txt` and `Web/fonts/README.md` in the application package
(`src/Stepwise/Web/fonts` in the source repository).

Upstream: https://github.com/notofonts/noto-cjk/releases/tag/Sans2.004 .

## Media implementation references

- Windows desktop capture: https://ffmpeg.org/ffmpeg-devices.html#gdigrab
- Audio loopback / silence handling: https://github.com/naudio/NAudio/blob/release/2.x/Docs/WasapiLoopbackCapture.md
- MP4 / GIF encoding and filters: https://ffmpeg.org/ffmpeg-filters.html

Capture produces H.264 Matroska source video and separate system/microphone WAV
tracks. Export mixes audio locally or removes it on request. GIF has no audio.
Its default is 12 fps and a maximum width of 1440 pixels; both are configurable.
