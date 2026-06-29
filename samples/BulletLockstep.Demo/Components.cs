namespace BulletLockstep.Demo;

// All positions/velocities use fixed-point int (milli-pixels) to guarantee
// cross-host determinism. 1 unit == 1/1000 pixel.

public readonly record struct Position(int X, int Y);
public readonly record struct Velocity(int Dx, int Dy);

// HostId identifies which host owns this entity. Used by deterministic
// post-replay systems to find "my player" via world.Query.
public readonly record struct PlayerTag(int HostId);

// SpawnFrame marks when a transient entity (bullet / ping) was created.
// Deterministic lifetime systems destroy entities whose SpawnFrame is too old.
public readonly record struct SpawnFrame(int Frame);

// FiredBy ties a transient entity to its originating host. Lets deterministic
// systems identify which host owns each transient entity without cross-host
// entity-id references (placeholder refs are single-frame only).
public readonly record struct FiredBy(int HostId);
