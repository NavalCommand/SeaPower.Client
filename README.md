# SeaPower.Client 0.1.0

Project-wide documentation is maintained in the separate **Docs Git repository**. Before changing this module, explicitly read [Docs/AGENTS.md](../Docs/AGENTS.md), [architecture](../Docs/ARCHITECTURE.md), [module/build policy](../Docs/MODULE_STRUCTURE.md), [local sources](../Docs/LOCAL_SOURCES.md), and [code style](../Docs/CODE_STYLE.md) with its [owner export](../Docs/CodeStyle.xml). The shared instructions are not discovered automatically across repository boundaries; do not create a competing AGENTS.md in the parent workspace. Historical reports and reproduction guides are indexed under [Docs/Tasks/](../Docs/Tasks/README.md), with run receipts under [Docs/Evidence/](../Docs/Evidence/README.md).


C# selected-side provider, game facade and engine-thread validator. No Gateway.Grpc or hosting implementation dependency. The root CMake build resolves Gateway.Api through the same canonical source resolver and prepares the managed adapter context against compile-only game references. Set SEAPOWER_GAME_ROOT in ignored Build.Local.cmake; no installation path is committed. Existing fake-facade tests run through CTest. No proprietary binary is copied or distributed.

Provider owns handles, validation and status on the captured engine thread. I/O carries immutable canonical bytes only. Legacy waypoints retain idle-only behavior; explicit replace/append support proven isolated surface navigation, preserve speed, and expose native route receipts/observations. Protected automation, unrelated tasks and formation contexts remain conservative. The first facility launch supports one validated own small-airfield/F-4E/AirToAir combination, using native preparation and distinct grounded-spawn/airborne progress. Contacts still use the selected plotting-table detected-position cache and version-specific contact ID range, without hidden identity/velocity/truth fallback. General strategic AI authority and global world coherence remain unverified. See [operational implementation and evidence limits](../Docs/Tasks/006/TASK006_IMPLEMENTATION.md).

## Build structure

Root CMakeLists.txt initializes/composes only. Shared policy/resolution lives in the independent Build.CMake repository, fetched as ncbuild. Managed implementation/project sources are in managed/ with namespace-mirrored managed/src/ paths; fake regression projects are in test/managed/. There is no empty native src/ module. Namespace paths and existing target names are preserved.

Enter a VS 2026 x64 developer shell, set standard VCPKG_ROOT, and use CMake 4.2+ and Ninja:

```powershell
cmake --preset windows-msvc-release -DFETCHCONTENT_SOURCE_DIR_NCBUILD=/absolute/path/to/Build.CMake
cmake --build o/windows-msvc-release --parallel 8
ctest --test-dir o/windows-msvc-release --output-on-failure --no-tests=error
```

The bootstrap option is unnecessary once Build.CMake is available at its declared Git URL. Optional ignored Build.Local.cmake registers canonical module URLs with NavalCommandSetDependencyPath(URL, absoluteCheckout); it is read once at the top level before declaration and applies transitively. Without a registration the declared Git URL and main reference are used. There is no workspace-root environment variable or implicit sibling lookup. External dependencies remain source-built vcpkg manifest packages. An already provisioned prefix can use standard -DVCPKG_INSTALLED_DIR=... -DVCPKG_MANIFEST_INSTALL=OFF cache options. See the separate [Docs repository build policy](../Docs/MODULE_STRUCTURE.md) for current commands and limits; dated measurements belong to the [task archive](../Docs/Tasks/README.md).

Presets contain configurePresets only, windows-msvc-debug/release and linux-clang-debug/release where applicable. Output is o/${presetName}. C++26 policy is centralized; MSVC uses /std:c++latest for available features. No full C++26 conformance claim. Build outputs, Build.Local.cmake and CMakeUserPresets.json are ignored. No proprietary game binaries are included.

Windows target and host dependencies use `x64-windows-static`, matching `/MT` in Release and `/MTd` in Debug. Reconfigure with `--fresh` and relink when migrating earlier dynamic-CRT artifacts.

## Managed build integration

Follow the authoritative [managed build policy](../Docs/MANAGED_BUILD.md). One CMake configure resolves internal source dependencies and persists each managed repository's ignored `o/managed/Build.Context.props`. Then ordinary `.csproj` builds in Visual Studio/Rider or `dotnet build ... -c Debug/Release` use those sources through real ProjectReferences without extra properties or another configure. Change source/tool/game-reference locations by reconfiguring, not by editing generated projects. Conflicting contexts require explicit `-DNAVALCOMMAND_REPLACE_MANAGED_CONTEXT=ON`; existing CMake consumers must then be reconfigured.

All repository-owned generated files stay under `o/`: native preset directories, per-project MSBuild `o/managed/<project>/{bin,obj}/` directories and mod `o/package`/`o/harness` stages. MSBuild preserves configuration/platform/framework dimensions. The shared helper discovers managed/CMakeLists.txt and invokes only the requested top-level project; MSBuild owns NuGet and transitive compilation. Game-reference absence blocks only requested game-dependent projects.

From this repository after configure: `dotnet build managed/SeaPower.Client.csproj -c Release`, `dotnet run --project test/managed/ProviderTests.csproj -c Release`, or `cmake --build o/windows-msvc-release --target SeaPower.Client.Managed`.
