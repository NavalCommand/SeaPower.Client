# SeaPower.Client 0.1.0
C# selected-side provider and Sea Power adapter. No Gateway.Grpc or hosting dependency.

./scripts/Build.ps1 -Protoc ABSOLUTE_SOURCE_BUILT_PROTOC fetches pinned Gateway.Api into out/sources, builds the native adapter against read-only installation references, then runs game-independent fake-facade tests. GameRoot is configurable. No proprietary binary is copied into Git or a package. A local Git URL is the reviewed development default in modules.lock.json; override NC_REPOSITORY_BASE or provide a relocated lock without changing source.

Provider owns handles, validation and status on its captured engine thread. I/O threads can enqueue immutable canonical bytes and drain immutable outputs only. Capacity: 64 commands, 128 statuses/units/tracks, one latest snapshot. Status ledger admits at most 128 unique requests per session and refuses excess explicitly. IDs are opaque session-scoped handles, not game UIDs. Native facade resolves current own membership adjacent to mutation.

Waypoint means append (refuse GUI SelectedTask), current height, native telegraph 2, no formation propagation. Formation effects/grounded takeoff are conservatively unknown/rejected. Typed AdvancedMissileEvasion and conservative other known urgent states block dispatch. No CeaseFire, direct transform mutation, environment RNG getters or strategic AI suppression.

Contacts come only from selected PlottingTable.DetectedPosition; foreign key range >=7001 is this build's private filtration rule. No hidden identity, raw velocity, physical contact object or truth fallback. Sampling times are cache-copy times, not sensor measurement times; global ECS coherence is unproven.

Actual engine integration requires the exact 0.8.3-261001 assembly hash from Docs reports. Only selected native player taskforce is currently supported by the composition; this is not a general AI-side command authority implementation.

