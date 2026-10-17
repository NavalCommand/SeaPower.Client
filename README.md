# SeaPower.Client

Game-specific managed provider. Only engine callbacks read game objects or apply controls. Publishes native own-unit and per-faction plotting data as immutable canonical protobuf bytes. Equipment is capability-based, with real per-ammunition inventories and resource-free sensors. Native behavior is statically inspected and still requires live validation.

Architecture, development policy and evidence live in [Docs](../Docs/ARCHITECTURE.md). Read [contributor instructions](../Docs/AGENTS.md), [module structure](../Docs/MODULE_STRUCTURE.md), [local source overrides](../Docs/LOCAL_SOURCES.md) and [code style](../Docs/CODE_STYLE.md) before changes. Current validation and live deployment procedures are in [the integration report](../Docs/Tasks/SeaPower-TacticalDisplay/IMPLEMENTATION.md).

Build from a VS x64 developer shell with CMake 4.2+, Ninja, and VCPKG_ROOT configured:

```powershell
cmake --preset windows-msvc-release -DFETCHCONTENT_SOURCE_DIR_NCBUILD=/absolute/path/to/Build.CMake
cmake --build o/windows-msvc-release --parallel 8
ctest --test-dir o/windows-msvc-release --output-on-failure
```

Internal dependencies use shared Build.CMake and optional ignored Build.Local.cmake registrations. Windows uses x64-windows-static and static MSVC CRT. Generated artifacts stay under o/. Managed builds additionally require the configured game/reference context; see Docs/MANAGED_BUILD.md.

## Observation kinematics

Contact direction and speed follow the native contact panel's observer `Vehicle`
movement vector, including TMA adjustments. No true-velocity fallback is made for
zero vectors. Zero speed remains known; physical course is unknown for a zero
horizontal vector. Geographic heading/course require an observer latitude.
Observation `Heading` is the player-reported track heading, converted to a
geographic bearing. It is not verified bow/nose orientation. Nonzero horizontal
motion supplies `CourseOverGround` from the same source; these fields are not
independent measurements. At zero velocity the reported heading remains known
north, as in the panel. Authoritative unit heading still means body orientation.
Altitude/depth follow native panel bindings only for the families that display
those values. Surface depth below keel is not exported as vessel elevation.
Missing object references and unavailable fields remain unknown. Classification
and dormancy do not introduce extra information gates absent from the panel.
See [the source trace and manual acceptance procedure](../Docs/Tasks/024/PR_REVIEW_FIXES.md).
