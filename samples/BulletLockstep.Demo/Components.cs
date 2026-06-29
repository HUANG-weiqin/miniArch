namespace BulletLockstep.Demo;

// All positions/velocities use fixed-point int (milli-pixels) to guarantee
// cross-host determinism. 1 unit == 1/1000 pixel.

public readonly record struct Position(int X, int Y);
public readonly record struct Velocity(int Dx, int Dy);

// HostId identifies which host owns this entity. Used by deterministic
// post-replay systems to find "player N" via world.Query, since entity ids
// differ across hosts (placeholder mode) but PlayerTag.HostId is logically
// stable.
public readonly record struct PlayerTag(int HostId);

// Health is mutated every frame by the damage system. Shield absorbs damage
// first; when Shield.Cur hits 0, the Shield component is removed (archetype
// migration). Health.Cur at 0 keeps the player alive for demo simplicity
// (Slice 6 handles real death).
public readonly record struct Health(int Cur, int Max);
public readonly record struct Shield(int Cur, int Max);

// Burning status. BurningTimer ticks down each frame; when it hits 0 the
// BurningTimer component is removed (archetype migration). While BurningTimer
// is present, the player takes extra burn damage per frame.
public readonly record struct BurningTimer(int Remaining);

// Powerup lifecycle marker. While present, the player regenerates Shield.Cur
// by 1/frame. When Remaining hits 0, PowerupState is removed.
public readonly record struct PowerupState(int Type, int Remaining);

// ── Transient entities ────────────────────────────────────────────────

// SpawnFrame marks when a transient entity (bullet) was created. Lifetime
// systems destroy entities whose SpawnFrame is too old.
public readonly record struct SpawnFrame(int Frame);

// FiredBy ties a transient entity to its originating host. Lets deterministic
// systems identify which host owns each transient entity without cross-host
// entity-id references (placeholder refs are single-frame only).
public readonly record struct FiredBy(int HostId);

// BulletTag marks any projectile. Multiple bullet archetypes coexist:
//   BasicBullet    = BulletTag + Position + Velocity + Damage + SpawnFrame
//   BurningBullet  = BasicBullet + BurningTimer   (Slice 4 variant)
//   HomingBullet   = BulletTag + Position + Velocity + Target + TurnRate + Damage + SpawnFrame  (Slice 5)
// Query With<BulletTag> catches all variants.
public readonly record struct BulletTag();
public readonly record struct Damage(int Amount);
