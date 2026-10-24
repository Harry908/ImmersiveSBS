# Packaging

Status (2026-10-20): **a one-click installer builds; not released.** It installs the app with
the default model and the Virtual Display Driver (user request: "the virtual driver bundled
inside an installer ... a one click installer, not having to go to places"). The portable
folder and zip still build too. This is plan.md's P6.

## The installer

```
.\packaging\build-installer.ps1             # artifacts\ImmersiveSBS-Setup-<version>.exe (575 MB)
```

It publishes the app with Distill-Any-Depth Base (`-Models` to change), downloads the Virtual
Display Driver 25.7.23 package once into `artifacts\cache` and checks its SHA-256, stages the
64-bit driver files, `devcon.exe` and the certificate of the driver's publisher (taken from its
signed catalog; every file's signature must be valid), and compiles `packaging\ImmersiveSBS.iss`
with Inno Setup 6. Inno Setup lives as a portable copy in `artifacts\tools\InnoSetup` (the
official installer run with `/PORTABLE=1 /CURRENTUSER /DIR=...`); about 3 minutes a build.

What the user sees: a double-click, Windows' administrator prompt, a progress bar, and a finish
page. Setup:

1. installs the app in `Program Files\ImmersiveSBS` with a Start menu entry; a running copy is
   closed first;
2. installs the driver if no device with its hardware ID `Root\MttVDD` exists: its publisher
   (SignPath Foundation) goes into the machine's Trusted Publishers first, so Windows does not
   stop to ask, then `devcon install MttVDD.inf Root\MttVDD`. A driver already there (installed
   by hand, or by its own tool) is left alone; the installer records what it added;
3. writes the driver's settings, `C:\VirtualDisplayDriver\vdd_settings.xml`, only if there are
   none: one monitor, 1920×1080 only, 60 Hz, so the virtual monitor has the right size without
   anyone choosing it;
4. on the finish page, all ticked: start with Windows (the app's own Run value), open Edge's
   extensions page and the extension folder (loading an unpacked extension cannot be done for
   the user), and start the app. A restart is asked for only if the driver needs one.

Uninstalling removes the app and the Start with Windows value, and asks whether to remove the
driver (only if this installer added it; then the publisher's certificate too, if it added that)
and whether to delete the settings, log and prepared engines (models the user added stay).

Checked on the development PC without installing: the script compiles; its driver check finds
the driver installed here (a throwaway setup ran only that check), so a real install here skips
the driver step; the certificate check reads the store's registry key, since `certutil
-verifystore` answers 0 whether the certificate is there or not. Not yet run end to end: that
needs a PC without the driver (a virtual machine, or a fresh Windows install), and on this PC
the user's go-ahead (it installs into Program Files).

## Build the portable package

```
.\packaging\publish.ps1                      # the app only
.\packaging\publish.ps1 -Models dad-base     # with the default model, all three sizes
```

It writes `artifacts\ImmersiveSBS-<version>-win-x64\` and a zip of it (`artifacts\` is not in
git). The version is `<Version>` in `src/ImmersiveSBS/ImmersiveSBS.csproj`; it shows in the log's
first line and in the Settings window's title. The script builds in its own folder
(`artifacts\build`), so a copy of the app running from `bin\Release` does not get in the way.

| Part | What | Size |
|---|---|---|
| The app | Self-contained .NET 10 for win-x64 (no .NET needed on the user's PC), ReadyToRun, no debug symbols | 247 MB |
| `extension\` | The browser extension, loaded unpacked from here | under 0.1 MB |
| `LICENSE`, `README.md`, `THIRD-PARTY-NOTICES.md`, `licenses\` | GPL-3.0, how to use it, every component's licence; ONNX Runtime's own licence and notices | under 1 MB |
| `models\` (optional) | `-Models` picks families; Distill-Any-Depth Base at all sizes | 564 MB |
| The zip, with `dad-base` | | 613 MB |

Only models whose licence allows it can be packaged; the script refuses others. The families:
`dad-base` (the default model, MIT), `da2-small`, `da3-small`, `da3-base` and `vda-small`
(Apache-2.0). All of them at the sizes the app uses (518×294 and 756×420; the 392×224 files
of the removed Fast resolution are left out) come to about 1.35 GB.

## What the user's PC needs

- **Windows 11 24H2 or later** with an **NVIDIA GeForce RTX GPU**: AI 3D runs on the TensorRT
  for RTX execution provider, which Windows ML installs as a Windows package. Without it the
  desktop still shows in the glasses, flat (the app says why in Settings).
- **Video glasses** with a side-by-side 3D mode (3840×1080, 1920×1080 per eye), such as the
  RayNeo GT Max.
- **The Virtual Display Driver** (VirtualDrivers, MIT, signed by SignPath), with one 1920×1080
  virtual monitor, extended (not mirrored) in Windows Settings → Display.
- **Edge or Chrome** with the extension loaded unpacked from the app's `extension` folder.

## Where the app keeps things

Everything it writes goes to `%LOCALAPPDATA%\ImmersiveSBS`: `settings.json`, `ImmersiveSBS.log`,
`engines\` (the compiled TensorRT engines, made on a model's first use, about 20 s each) and
`models\` (models the user adds). The app's own folder is only read, so it can live under
Program Files. A model is looked for in the app's `models` folder first, then in the user's;
the log names the file it loaded. Start with Windows is a value under the user's `Run` key.

## Decisions to make

1. ~~**How it installs.**~~ Settled (2026-10-20): the one-click Inno Setup installer above.
2. **Which models ship, and how the rest arrive.** The installer ships Distill-Any-Depth Base.
   The others: downloaded by the app on first use (needs a host, such as GitHub release
   assets, and a download step in the app), or copied by the user into
   `%LOCALAPPDATA%\ImmersiveSBS\models`.
3. ~~**The virtual display driver.**~~ Settled: bundled and installed by the installer. The
   app itself never installs or removes it.
4. **The browser extension.** The last step a user does by hand. Unpacked (Edge warns about
   developer-mode extensions at each start), or published unlisted on Edge Add-ons and the Chrome Web Store. A store listing
   gets its own extension ID: the app accepts only the ID from the manifest's `key`
   (`BrowserLink.ExtensionId`) and would have to accept the store's too.
5. **Code signing.** An unsigned exe, the installer included, meets a SmartScreen warning on
   download. Options: a code-signing certificate, or SignPath Foundation's free signing for
   open-source projects (which the driver uses).
6. **An update check** (plan.md P6): not built. The simplest is a look at the newest GitHub
   release at start, offered in the tray.
7. **TensorRT for RTX on a fresh PC.** The app uses the execution provider Windows ML installs.
   A fresh Windows 11 PC has none until some app asks for it, so when none is found the app
   asks Windows ML's catalog (`Microsoft.Windows.AI.MachineLearning`, only its projection and
   `Microsoft.Windows.AI.MachineLearning.dll`; `Depth/TensorRtRtx.cs`), once per run; if that
   fails the picture stays flat and the log says why. On the new desktop it failed with
   0x80073D3B ("not applicable") until Windows had a retail-channel licence key: Windows Update
   offers the workload only to retail licences. With one it installed in 16 s (progress.md,
   2026-10-23/24). A user whose Windows has another channel's key sees the same failure; the
   log line names the error. Still to decide: that DLL is under the
   Microsoft Software License Terms (Windows ML Runtime), which allow redistribution with
   conditions (add primary functionality, pass on terms that protect it, no source-disclosing
   licence over it). Whether shipping it beside GPL-3.0 code is fine is not settled; the
   alternative is to leave it out of the package and keep the install a manual step.

## Release checklist

1. Raise `<Version>` in the project file, and the extension's `version` in its manifest if it
   changed.
2. `dotnet test ImmersiveSBS.slnx -c Release`: all pass (the model tests skip without the
   model files).
3. `.\packaging\build-installer.ps1` (and `publish.ps1` for the zip, if one ships too).
4. On a PC without the .NET SDK or the driver: run the installer, load the extension from the
   folder it opens, open the theater on a video, check the log's first lines (version,
   extension connected, model loaded) and that the virtual monitor is 1920×1080. Then
   uninstall, removing the driver, and check it is gone from Device Manager.
5. Check THIRD-PARTY-NOTICES.md against the models shipped and the packages' versions.
6. Tag the commit with the version.
