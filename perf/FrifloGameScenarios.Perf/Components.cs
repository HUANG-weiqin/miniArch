// Shared component types for game scenario benchmarks.
// All components implement IComponent for Friflo compatibility;
// MiniArch ignores the interface.

using Friflo.Engine.ECS;

namespace FrifloGameScenarios;

// --- Core components ---
public struct Position : IComponent { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
public struct Velocity : IComponent { public int VX; public int VY; public Velocity(int vx, int vy) { VX = vx; VY = vy; } }
public struct Health   : IComponent { public int Value; public Health(int v) { Value = v; } }
public struct Mana     : IComponent { public int Value; public Mana(int v) { Value = v; } }
public struct Armor    : IComponent { public int Value; public Armor(int v) { Value = v; } }
public struct Damage   : IComponent { public int Value; public Damage(int v) { Value = v; } }
public struct Team     : IComponent { public int Value; public Team(int v) { Value = v; } }
public struct Cooldown : IComponent { public int Ticks; public Cooldown(int t) { Ticks = t; } }
public struct Lifetime : IComponent { public int Ticks; public Lifetime(int t) { Ticks = t; } }
public struct Shield   : IComponent { public int Value; public Shield(int v) { Value = v; } }
public struct Stamina  : IComponent { public int Value; public Stamina(int v) { Value = v; } }
public struct XP       : IComponent { public int Value; public XP(int v) { Value = v; } }

// --- State machine tags (implement IComponent for query compat with MiniArch) ---
public struct StateIdle   : IComponent { public int Dummy; public StateIdle(int d) { Dummy = d; } }
public struct StateMove   : IComponent { public int Dummy; public StateMove(int d) { Dummy = d; } }
public struct StateAttack : IComponent { public int Dummy; public StateAttack(int d) { Dummy = d; } }
public struct StateDead   : IComponent { public int Dummy; public StateDead(int d) { Dummy = d; } }

// --- Buff/Debuff tags ---
public struct Burning        : IComponent { public int Dummy; public Burning(int d) { Dummy = d; } }
public struct Frozen         : IComponent { public int Dummy; public Frozen(int d) { Dummy = d; } }
public struct Poisoned       : IComponent { public int Dummy; public Poisoned(int d) { Dummy = d; } }
public struct ImmuneToPoison : IComponent { public int Dummy; public ImmuneToPoison(int d) { Dummy = d; } }
public struct FireResist     : IComponent { public int Dummy; public FireResist(int d) { Dummy = d; } }

// --- Team tags ---
public struct TagTeamA : IComponent { public int Dummy; public TagTeamA(int d) { Dummy = d; } }
public struct TagTeamB : IComponent { public int Dummy; public TagTeamB(int d) { Dummy = d; } }

// --- Extra components ---
public struct Regen      : IComponent { public int Rate;  public Regen(int r) { Rate = r; } }
public struct Score      : IComponent { public int Value; public Score(int v) { Value = v; } }
public struct Level      : IComponent { public int Value; public Level(int v) { Value = v; } }
public struct LeaderIdx  : IComponent { public int Value; public LeaderIdx(int v) { Value = v; } }
