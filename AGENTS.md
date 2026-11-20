# AGENTS.md

## Repo Shape
- Main solution: `ClassPixelPro.sln`; platform-filtered solutions: `ClassPixelPro.Filter.Linux.slnf`, `ClassPixelPro.Filter.MacOs.slnf`.
- Runtime app entrypoint: `ClassPixelPro.Desktop/Program.cs`; Avalonia app/library: `ClassPixelPro/`; shared UI/services: `ClassPixelPro.Core/`.
- Platform services: `platforms/ClassPixelPro.Platforms.{Windows,Linux,MacOs}`, wired by `CrossPlatformProps.props`.
- `ClassPixelPro.Shared` and `ClassPixelPro.Shared.IPC` multi-target `net8.0;net472` **on Windows only**; off-Windows (Linux/macOS) they target `net8.0` only (net472 unavailable there). When targeting net472, avoid APIs unavailable on net472.
- `ClassPixelPro.Launcher` targets `net9.0`; most app projects target `net8.0`.

## Architecture / Layering
Dependency direction (low to high):
- `ClassPixelPro.Platforms.Abstractions` -> platform contracts (`IPlatformService`, etc.).
- `platforms/ClassPixelPro.Platforms.{Windows,Linux,MacOs}` -> OS-specific implementations, selected by `CrossPlatformProps.props`.
- `ClassPixelPro.Shared` / `ClassPixelPro.Shared.IPC` -> multi-target `net8.0;net472` shared logic + IPC contract.
- `ClassPixelPro.Core` -> `net8.0`, UI-agnostic controls/services (Avalonia-based).
- `ClassPixelPro` -> main app library (`net8.0`, `UseWPF=false`; Avalonia UI, MVVM/ReactiveUI/DynamicData).
- `ClassPixelPro.Desktop` -> exe entrypoint (`Program.cs`); references exactly one platform assembly.
- `ClassPixelPro.Launcher` -> `net9.0` native-AOT self-contained bootstrap.
Side libraries: `ClassPixelPro.PluginSdk` (`.cipx` authoring), `ClassPixelPro.ExamplePlugin` (reference plugin), `ClassPixelPro.DocsGenerator`, `Demos/ClassPixelPro.IpcDemo`.
Vendored (git submodule / in-tree): `vendors/EdgeTtsSharp`, `vendors/System.Device.Location`, `vendors/PhainonDistributionCenter.Shared`.

Note: the core app is Avalonia-based (`<UseWPF>false</UseWPF>`), despite `doc/Dependencies.md` still listing WPF libraries (that doc looks stale).

## IPC / Cross-Process Communication
External clients and plugins communicate with the running app through `ClassPixelPro.Shared.IPC` (dotnetCampus.Ipc + gRPC/Protobuf; see `IpcClient` and `Abstractions/Services/*`). `Demos/ClassPixelPro.IpcDemo` is a working example. Full protocol docs: https://docs.classpixelpro.tech/dev/

## Plugin Model
Plugins ship as `.cipx` packages, authored against `ClassPixelPro.PluginSdk`; `ClassPixelPro.ExamplePlugin` is the reference implementation (its README renders in Settings -> Plugins). The NUKE target `InitPluginDevEnv` wires a dev loop pointing at `out/ClassPixelPro_Dev` -- do not run it during normal verification.

## Further Reading (doc/)
- `doc/Dependencies.md`, `doc/NotificationHostService.md`, `doc/AttachedSettings.md`
- `doc/Design/Cyrene_MSP/` (design notes + `command_flow.md`)
- `doc/Contributing/Scopes.md` (valid Conventional-Commit scope names -- read before committing)
- `doc/ChangeLogs/` (per-version `App.md` release notes)

## SDK And Restore
- `global.json` requests .NET SDK `9.0.100` with `rollForward: latestMajor` and `allowPrerelease: true`; launcher/native-AOT work needs .NET 9 SDK, not just .NET 8.
- Repo depends on `vendors/EdgeTtsSharp` (`classpixelpro-v2` branch). If missing, run `git submodule update --init --recursive`.
- NuGet feed `https://nuget.pkg.github.com/ClassPixelPro/index.json` is referenced here as the CI Package Registry, but no `NuGet.config`/package source referencing it exists in the private repo source (AppVeyor uses its own project feed). Verify before relying on it.

## Build Commands
- Desktop app: `dotnet build ClassPixelPro.Desktop/ClassPixelPro.Desktop.csproj -c Debug`
- Single library: `dotnet build ClassPixelPro.Core/ClassPixelPro.Core.csproj -c Debug`
- Use NUKE only for release/publish builds, not verification.
- Release packaging: `./build.ps1 PublishApp` (Windows) or `./build.sh PublishApp` (Unix) with required metadata.
- `PublishApp` requires metadata: `--OsName windows|linux|macos`, `--Arch x64|x86|arm64`, `--Package folder|deb|pkg`, `--BuildType full|selfContained`, `--BuildName appBase|app`, `--AppVersion <version>`.
- Launcher packaging: `PublishLauncher` (native AOT self-contained).
- Plugin dev environment target `InitPluginDevEnv` writes user environment variables/profile blocks pointing at `out/ClassPixelPro_Dev`; do not run it casually during verification.

## Agent Workflow
Before editing:
1. Inspect related existing code.
2. Identify the current design pattern.
3. Explain the planned changes.
4. Only then modify files.
Avoid speculative refactoring.
When requirements are unclear:
- Ask the user when changes affect architecture, behavior, compatibility, or public APIs.
- For minor implementation details, follow existing project patterns.

## Priority Rules
When instructions conflict, follow this order:
1. Preserve existing architecture and behavior.
2. Keep changes minimal and focused.
3. Prefer existing patterns over introducing new abstractions.
4. Do not modify unrelated files.
5. Ask before making large architectural changes.

## Compatibility Rules
- Preserve existing public APIs whenever possible.
- Avoid breaking API changes unless explicitly required.
- If an API change is necessary:
  - Explain the reason.
  - Identify affected consumers.
  - Inform the user before making the change.
  - Consider backward-compatible alternatives or migration paths.

## Platform Targeting
- `CrossPlatformProps.props` auto-selects platform constants from host OS for normal builds; release builds override this with `PublishBuilding=true` and `PublishPlatform=<os>`.
- `ClassPixelPro.Desktop` conditionally references one platform based on `Platforms_Windows`, `Platforms_Linux`, `Platforms_MacOs`.
- Linux requires X11; Wayland/XWayland not supported.

## Generated And Output Files
- NUKE artifacts: `out/`; plugin builds: `.cipx` files.
- `ClassPixelPro/secrets.g.cs` is generated by NUKE `GenerateSecrets` and deleted by `PostCleanup`; never commit.
- Ignore `*_wpftmp.csproj` files.
- CsWin32 inputs: `NativeMethods.txt`/`NativeMethods.json`.

## Tests And Verification
- No test projects in solution; prefer `dotnet build <project>`.
- After code changes, run at least one compile check; for app changes, use `dotnet build ClassPixelPro.Desktop/ClassPixelPro.Desktop.csproj -c Debug` rather than NUKE.
- If automated tests become available, run the relevant automated tests too.
- If automated testing is not available, tell the user what related behavior still needs manual verification.
- For platform-specific changes, build with intended host/platform settings; a Windows-host debug build will not compile Linux/macOS-specific code unless invoked through publish properties.

## Verification Rules
Before claiming work is complete:
1. Run the relevant verification command.
2. Read the full output.
3. Report results based on actual verification.
When verification is unavailable:
- Clearly state what was checked.
- Clearly state what could not be verified.
- Do not present assumptions as confirmed results.

## Debugging Rules
When encountering any bug, test failure, or unexpected behavior:
1. Read error messages carefully.
2. Reproduce the issue when possible.
3. Check recent changes.
4. Trace data flow and identify the root cause before fixing.
Avoid:
- Making changes based only on assumptions.
- Applying multiple unrelated fixes at once.
- Changing code without a clear hypothesis.
Red flags — STOP and follow process:
- "Quick fix for now, investigate later"
- "Just try changing X and see if it works"
- Proposing solutions before tracing data flow

## Avalonia Development Rules
1. Search first for existing patterns.
2. Follow MVVM, ReactiveUI, DynamicData.
3. Composition over inheritance.
4. Keep ViewModels platform-independent.

## Anti-Patterns
Do NOT:
- Skip design for "simple" tasks.
- Add comments that only explain what the code already expresses.
- Use `this.` qualifier.
- Claim completion without verification.
- Fix symptoms without root cause.
Comments should explain intent, constraints, workarounds, or non-obvious decisions.
When unsure: ask the user.

## Contribution Conventions
- Use Conventional Commits with scopes from `doc/Contributing/Scopes.md`.
- Feature work → `master`; fixes → maintenance branch.
- Before writing commit messages, read `doc/Contributing/Scopes.md` to get valid scope names.
- Before contributing code, read `CONTRIBUTING.md` to understand contribution guidelines.

## Release Packaging
Only when explicitly requested:
```bash
./build.ps1 PublishApp --OsName windows --Arch x64 --Package folder --BuildType full --BuildName appBase --AppVersion 0.0.0.0
```

## Dual-Repo Publishing
The desktop app uses a two-repository publishing model:
- Private repo `CaizheZhou/ClassPixelPro` holds the source (local working copy is `ClassPixelPro/`).
- Public repo `CaizheZhou/ClassPixelPro-Releases` holds the automated build/release pipeline (local mirror `ClassPixelPro-Releases/`).
Trigger flow:
- Tag the private repo with a `2.*` version (see `VERSION_CONVENTION.md`) and push -> `ClassPixelPro/.github/workflows/trigger-build.yml` fires `gh workflow run build-and-release.yml` on the public repo via `PUBLIC_REPO_PAT`.
- `ClassPixelPro-Releases/.github/workflows/build-and-release.yml` checks out the private repo at that tag (`PRIVATE_REPO_TOKEN`, `submodules: recursive`), builds with `pack.ps1` (NOT NUKE `build.ps1`), generates release notes from the public repo root `CHANGELOG.md`, creates the GitHub Release, then POSTs the distribution to the website backend (`/api/admin/distributions/create-with-url`) so the in-app updater sees it.
Implications:
- Build/release CI lives in the PUBLIC repo, not the private source repo. Local source changes are built by the public repo pipeline.
- CI build entry is `pack.ps1`, not NUKE `build.ps1`.
- `CHANGELOG.md` for releases is maintained in the public `ClassPixelPro-Releases` repo, not the private repo.
- Releases are driven by git tags (`2.*`); do not edit CI workflows in the private repo expecting them to build.
- There is also an AppVeyor pipeline (`appveyor.yml`) that builds via `tools/build-appveyor.ps1` (-> `tools/release-gen/publish.ps1`) and deploys to GitHub Releases on tag; it is independent of the public-repo GitHub Actions flow.
