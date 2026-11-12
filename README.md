# WinAppVSCE, Surface only (LeXtudio fork)

This branch is a trimmed fork of [microsoft/WinAppVSCE](https://github.com/microsoft/WinAppVSCE), taken at the head of
the previewer pull request (`6e4b388`). It keeps only `surface/`: the WinUI **Surface** renderer, the
**SurfaceProvisioner** and the shared protocol notes. The Visual Studio extension, the VS Code client and the TypeScript
tooling are removed. It exists so the [OpenDevelop](https://github.com/lextudio/OpenDevelop) WinUI designer can use the
Surface as a rendering backend without a submodule.

| Change from upstream | Why |
| --- | --- |
| `global.json` pins .NET 10 | upstream has none and picked up a preview SDK |
| `SurfaceProvisioner` and its tests target `net10.0` | it failed to start with only .NET 10+ installed |
| `surface/Package/` and `.github/workflows/release-surface.yml` | build and publish the `LeXtudio.WinUISurface` package |
| `surface/Smoke-Surface.ps1` | checks that the surface runs and renders without any IDE |

`surface/protocol/Compatibility.csproj` still links sources from the removed `vs-extension` and does not build here.

## The package

`LeXtudio.WinUISurface` (GitHub Packages, `https://nuget.pkg.github.com/lextudio/index.json`) is a content-only package:

```text
HostSDK/            sources, compiled SurfaceProvisioner and engine.stamp (the layout the provisioner expects)
Surface/win-x64/    bundled surface, x64
Surface/win-arm64/  bundled surface, ARM64
```

**Prerequisite:** the Windows App Runtime must be installed on the machine that runs a surface (its bootstrapper needs the
Main, Singleton and DDLM packages, not only the framework package). Without it the surface exits `0x80670016`.
Install it with `windowsappruntimeinstall-x64.exe --quiet --force` from <https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe>.

Run a surface from a **short** directory (for example `C:\odsurf`): a deep path fails in the Windows App SDK bootstrap with
`filename too long`. The wire protocol is in `surface/protocol/README.md`.

Changes to the surface itself should go to upstream first; this branch carries only what upstream does not take.
The source is MIT licensed (see `LICENSE` and `surface/LICENSE.winui-vsc.txt`).
