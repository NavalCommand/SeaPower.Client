# SeaPower.Client 0.1.0

C# selected-side provider, unchanged game facade and engine-thread validator. No Gateway.Grpc or hosting implementation dependency. The root CMake build resolves Gateway.Api through the same canonical source resolver and builds the managed adapter against compile-only game references. Set SEAPOWER_GAME_ROOT in ignored Build.Local.cmake; no installation path is committed. Existing fake-facade tests run through CTest. No proprietary binary is copied or distributed.

Provider owns handles, validation and status on the captured engine thread. I/O carries immutable canonical bytes only. Bounds, per-session IDs/ledger, waypoint append semantics, protected evasion rejection and conservative grounded/formation restrictions are unchanged. Contacts still use the selected plotting-table detected-position cache and version-specific contact ID range, without hidden identity/velocity/truth fallback. General strategic AI authority and global world coherence remain unverified.

## Build structure (Task 005)

Root CMakeLists.txt initializes/composes only. Shared policy/resolution lives in the independent Build.CMake repository, fetched as ncbuild. Production libraries are defined in src/, mocks in mock/, tests in test/, applications in app/. Namespace paths and existing target names are preserved.

Enter a VS 2026 x64 developer shell, set standard VCPKG_ROOT, and use CMake 4.2+ and Ninja:

```powershell
cmake --preset windows-msvc-release -DFETCHCONTENT_SOURCE_DIR_NCBUILD=/absolute/path/to/Build.CMake
cmake --build o/windows-msvc-release --parallel 8
ctest --test-dir o/windows-msvc-release --output-on-failure --no-tests=error
```

The bootstrap option is unnecessary once Build.CMake is available at its declared Git URL. Optional ignored Build.Local.cmake registers canonical module URLs with NavalCommandSetDependencyPath(URL, absoluteCheckout); it is read once at the top level before declaration and applies transitively. Without a registration the declared Git URL and main reference are used. There is no workspace-root environment variable or implicit sibling lookup. External dependencies remain source-built vcpkg manifest packages. An already provisioned prefix can use standard -DVCPKG_INSTALLED_DIR=... -DVCPKG_MANIFEST_INSTALL=OFF cache options. See the parent project's Docs/MODULE_STRUCTURE.md for exact verified commands and limits.

Presets contain configurePresets only, windows-msvc-debug/release and linux-clang-debug/release where applicable. Output is o/${presetName}. C++26 policy is centralized; MSVC uses /std:c++latest for available features. No full C++26 conformance claim. Build outputs, Build.Local.cmake and CMakeUserPresets.json are ignored. No proprietary game binaries are included.
