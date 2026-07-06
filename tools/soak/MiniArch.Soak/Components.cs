using MiniArch;

namespace MiniArch.Soak;

readonly record struct CompA(int Value);                         // 4B
readonly record struct CompB(long Value);                        // 8B
readonly record struct CompC(float X, float Y);                  // 8B
readonly record struct CompD(long V0, long V1, long V2, long V3,
    long V4, long V5, long V6, long V7);                        // 64B - medium
readonly record struct EntityRef(Entity Target);                 // 8B - Entity field
