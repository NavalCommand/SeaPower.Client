# SeaPower.Client 0.1.0

Project-wide documentation is maintained in the separate **Docs Git repository**. Before changing this module, explicitly read [Docs/AGENTS.md](../Docs/AGENTS.md), [architecture](../Docs/ARCHITECTURE.md), [module/build policy](../Docs/MODULE_STRUCTURE.md), [local sources](../Docs/LOCAL_SOURCES.md), and [code style](../Docs/CODE_STYLE.md) with its [owner export](../Docs/CodeStyle.xml). The shared instructions are not discovered automatically across repository boundaries; do not create a competing AGENTS.md in the parent workspace. Historical reports and reproduction guides are indexed under [Docs/Tasks/](../Docs/Tasks/README.md), with run receipts under [Docs/Evidence/](../Docs/Evidence/README.md).


C# selected-side provider, game facade and engine-thread validator. No Gateway.Grpc or hosting implementation dependency. The root CMake build resolves Gateway.Api through the same canonical source resolver and prepares the managed adapter context against compile-only game references. Set SEAPOWER_GAME_ROOT in ignored Build.Local.cmake; no installation path is committed. Existing fake-facade tests run through CTest. No proprietary binary is copied or distributed.

Provider owns handles, validation and status on the captured engine thread. I/O carries immutable canonical bytes only. Idle-only waypoints retain idle-only behavior; explicit replace/append support proven isolated surface navigation, preserve speed, and expose native route receipts/observations. Protected automation, unrelated tasks and formation contexts remain conservative. The first facility launch supports one validated own small-airfield/F-4E/AirToAir combination, using native preparation and distinct grounded-spawn/airborne progress. Contacts still use the selected plotting-table detected-position cache and version-specific contact ID range, without hidden identity/velocity/truth fallback. General strategic AI authority and global world coherence remain unverified. See [operational implementation and evidence limits](../Docs/Tasks/006/TASK006_IMPLEMENTATION.md).

## Operational contract

The provider publishes canonical schema **4** `ProviderObservation` bytes whose `Snapshot` is an `ObserverSnapshot` only, with a stable `ObserverId`, session, version, capture time and `UnitObservation` rows. Own units and plotting contacts both receive opaque `ObservationId` handles; source-backed own names are present only when reported and plotting-contact names remain absent. Operational wire payloads contain no authoritative `UnitId`, truth snapshot, or truth-to-contact correlation. Independent source maps prevent equal native-source keys from merging own observations with contacts; native plotting records are keyed by record lifetime, not their reusable numeric contact ID or hidden platform identity. Handles expire on disappearance, readiness loss, explicit invalidation, or a changed native game/player-taskforce context, including changes while the facade remains ready.

Order discovery and requests are scoped to `ObserverId`, session, `ObservationId`, and observation version. Only current own-observation handles map to native controlled sources, privately inside the provider; contacts, false/fused plotting records, stale handles, and other observers never acquire authority by ID. Ownership, membership, control and protected native automation are checked again adjacent to native route/launch mutation; discovery is not authorization. Requests contain one typed waypoint or aircraft-launch payload. Launch receipts report an assigned spawned `ObservationId`, never a truth entity ID.

Navigation uses an optional route: a present empty route means known empty, while an absent route means unknown. Source-backed own platform coordinates use the exact-position alternative; approximate contact coordinates use a concrete partial-position observation and are never represented as exact. Optional observed `AirOperations` is attached to the platform observation; inventory and loadout lists contain only currently reported entries and an empty list does not prove exhaustive inventory or disarmament. Exact optional counts preserve known zero separately from unknown. Aircraft stock is grouped by type and squadron once; launch selectors and executable/blocked/unknown availability exist only in the orders overlay. Missing inventory reports remain unreported, never complete-empty.

Own orientation is sampled from native `ObjectBase.Heading.Value` (degrees converted to radians); course over ground is not inferred from heading. Cached contact coordinates carry neither a fabricated measurement time nor sensor identity. Snapshot capture time is distinct from contact measurement time. Contact contributor lists and sensor identities remain empty unless actually source-verified; aggregate plotting data does not identify a contributing unit or sensor. Source-backed own-taskforce membership is a partial observer relationship keyed by `ObservationId`, with an opaque session-scoped taskforce handle; faction and enemy membership remain unknown.

Offline fake-facade regressions cover separate observer identities, stable own/contact `ObservationId`s, equal-source-key isolation, false/fused observations without truth mappings, enemy/stale/reused handle rejection, continuous-ready session changes, native-adjacent ownership/protection checks, observer-scoped availability, known-empty/unknown routes, partial locations, exact optional zero/false values, reported-list semantics, grouped air inventory, spawned-observation launch lifecycle and backpressure cleanup. They do not constitute live-game verification.


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
