# Publishing a version

The order matters, and three of these steps exist because skipping them has cost somebody a
release before.

```powershell
# 1. Build the portable executables.
powershell -ExecutionPolicy Bypass -File packaging\publish.ps1 -Version 0.1.0

# 2. Prove they run somewhere they were not built. NOT optional — see below.
powershell -ExecutionPolicy Bypass -File packaging\verify-portable.ps1 `
    -PublishDir artifacts\gui -Executable RolloutLoud.exe -ExpectWindow
powershell -ExecutionPolicy Bypass -File packaging\verify-portable.ps1 `
    -PublishDir artifacts\cli -Executable rollout.exe -Arguments help

# 3. Cut the GitHub release and upload artifacts\release\*.exe.

# 4. DOWNLOAD the asset back from the release and hash THAT file.
$sha = (Get-FileHash .\RolloutLoud-0.1.0-win-x64.exe -Algorithm SHA256).Hash

# 5. Generate and validate.
powershell -ExecutionPolicy Bypass -File packaging\winget\new-manifest.ps1 `
    -Version 0.1.0 -InstallerSha256 $sha
winget validate --manifest packaging\winget\0.1.0
winget install --manifest packaging\winget\0.1.0     # optional, see the elevation note

# 6. Open the PR against microsoft/winget-pkgs.
```

## Step 2 is the one people skip

Avalonia carries native libraries — Skia, HarfBuzz, ANGLE. If they extract *beside* the
executable rather than into it, **running from the publish folder works perfectly** and copying
the single file anywhere else kills the process before a window appears. Testing where you built
it proves nothing at all.

`publish.ps1` sets `IncludeNativeLibrariesForSelfExtract`; `verify-portable.ps1` copies the exe
alone into an empty temp folder and requires that a window actually opens. An exit code of
`0xC000041D` is the signature: a fail-fast raised inside the window procedure, which escapes
every managed exception handler, so nothing is logged and nothing is caught.

## Step 4: the hash is of the DOWNLOADED file

winget fetches the published `InstallerUrl` and checks the hash against what it downloads. Hash
your local build output instead and you can be off by a re-upload, a release edit, or a CDN
artefact — and the failure appears in the validation pipeline, hours later, in a queue you then
have to rejoin.

## The `Icons` field is a trap — do not add it

It exists in schema 1.6.0 and looks like exactly what you want. `winget validate` answers:

```
Manifest Warning: Field usage requires verified publishers. [Icons]
```

A metadata-only PR carrying it was closed for precisely that reason. The block does not belong
in these manifests, and `new-manifest.ps1` deliberately does not emit one.

## What actually puts the icon in the catalogues: `PackageUrl`

winstall.app runs `get-website-favicon` against `PackageUrl` and stores the highest-resolution
favicon it finds. Measured both ways:

| `PackageUrl` | Icon |
|---|---|
| A GitHub repository page | GitHub's own favicon, i.e. the default grey square |
| A site with a real `<link rel="icon">` | The app icon |

So `PackageUrl` points at **https://joedsonalves.github.io/rolloutloud/**, served by GitHub Pages
from `docs/`, whose favicon is written by `assets/generate-icon.ps1`.

⚠️ **The copies in `docs/` come from the generator, never by hand.** Pages only serves what is
inside the published folder, so `../assets/` does not resolve — and a hand-made copy would become
a second truth about the drawing, free to drift from the application icon.

⚠️ **The catalogue scrapes on its own schedule.** The icon does not appear the moment the PR
merges. That is not a bug to chase.

## Generated manifests are committed *after* they are submitted, never before

`new-manifest.ps1` writes into `packaging/winget/<version>/`. Those files are the record of
what was actually sent to microsoft/winget-pkgs, so they get committed once the hash in them is
the hash of a **published** asset.

A manifest generated as a dry run carries the hash of a local build, and committing that
enshrines a number that is going to be wrong. Generate, validate, throw it away; generate again
for real at release time.

## Once the PR is open, do not touch it

> **A new commit restarts the validation pipeline and loses your position in the queue.**

Editing the PR body is safe. Pushing a fix is not, unless a moderator asks for one.

## About the checkboxes in the PR template

Tick what is true and say what is not, in the body. Declaring an untested step has never cost a
PR; **ticking the CLA box on somebody's behalf has cost a correction.** Nothing gets signed,
agreed or attested in the author's name without the author.

The `winget install --manifest` box: an elevated session **installs** fine — it downloads from
the published `InstallerUrl` and verifies the hash. It refuses to **uninstall** a user-scope
package (*"The package installed for user scope cannot be uninstalled when running with
administrator privileges"*). So test the install elevated if that is the shell you have, and do
the uninstall from an ordinary one.

## PowerShell scripts here are UTF-8 **with BOM**

Windows PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI. A single em dash in a comment then
becomes two garbage characters, and the parse fails somewhere unrelated to the real line — the
error I saw pointed at a `throw` twelve lines away from the actual problem. Every `.ps1` in this
repository starts with `EF BB BF`, and `.gitattributes` keeps it that way.
