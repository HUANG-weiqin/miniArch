namespace MiniArchBenchmarks.GameTick;

public record struct PlayerTag;
public record struct EnemyTag;
public record struct NpcTag;
public record struct ProjectileTag;
public record struct DeadTag;

public record struct Team(int Value);
public record struct Level(int Value);
public record struct Gold(int Value);
public record struct Faction(int Value);
public record struct Damage { public int Base; public int Bonus; }
public record struct Armor(int Value);
public record struct Speed(float Value);
public record struct SightRange(float Value);
public record struct AttackRange(float Value);
public record struct AggroRange(float Value);
public record struct TargetId(int Value);
public record struct PatrolPathId(int Value);
public record struct WaypointIndex(int Value);
public record struct RenderLayer(int Value);
public record struct MeshId(int Value);
public record struct MaterialId(int Value);
public record struct AiState { public int Kind; public float Timer; }
public record struct BuffId(int Value);
public record struct BuffRemaining { public float Value; }

public record struct Position { public float X; public float Y; public float Z; }
public record struct Velocity { public float X; public float Y; public float Z; }
public record struct Rotation { public float Pitch; public float Yaw; public float Roll; }
public record struct Scale { public float X; public float Y; public float Z; }
public record struct Health { public float Current; public float Max; }
public record struct Mana { public float Current; public float Max; }
public record struct Stamina { public float Current; public float Max; }
public record struct Experience(int Current, int Next);
public record struct Color(float R, float G, float B, float A);
public record struct Size { public float W; public float H; public float D; }
public record struct Bounds(float Min, float Max);

public record struct Resistance(int Fire, int Ice, int Poison, int Lightning);
public record struct Attribute(int Str, int Dex, int Int, int Wis);
public record struct CooldownSet(float Q, float W, float E, float R);
public record struct LootTable(int GoldMin, int GoldMax, int ItemChance);

public record struct StatusEffects(int V0, int V1, int V2, int V3, int V4, int V5, int V6, int V7);
public record struct QuestProgress(int V0, int V1, int V2, int V3, int V4, int V5, int V6, int V7);
public record struct Abilities(int Id0, int Id1, int Id2, int Id3, float Cd0, float Cd1, float Cd2, float Cd3);
public record struct SkillCooldowns(float V0, float V1, float V2, float V3, float V4, float V5, float V6, float V7);

public record struct TransformMatrix(
    float M00, float M01, float M02, float M03,
    float M10, float M11, float M12, float M13,
    float M20, float M21, float M22, float M23,
    float M30, float M31, float M32, float M33);

public record struct EntityRef(int Id);
public record struct BuffSet(EntityRef E0, EntityRef E1, EntityRef E2, EntityRef E3);
public record struct Equipment(int V0, int V1, int V2, int V3, int V4, int V5);

public record struct DamageEvent { public int Amount; }

public record struct Debuff { public float Timer; }
public record struct BossTag;
public record struct Lifetime { public float Value; }
