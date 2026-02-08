using System;

namespace Fuzzer.CorpusGen;

/// <summary>
/// Tiny deterministic PRNG (xorshift64*). Stable across runtimes.
/// </summary>
internal struct DeterministicRng
{
    private ulong _state;

    public DeterministicRng(int seed)
    {
        // SplitMix64-style scramble to avoid weak seeds.
        _state = 0x9E3779B97F4A7C15UL + (uint)seed;
        _state = Mix(_state);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private ulong NextU64()
    {
        var x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        return (int)(NextU64() % (uint)exclusiveMax);
    }

    public int NextInt(int inclusiveMin, int exclusiveMax)
    {
        if (exclusiveMax <= inclusiveMin) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        return inclusiveMin + NextInt(exclusiveMax - inclusiveMin);
    }

    public bool NextBool() => (NextU64() & 1) != 0;

    public string NextIdent(string prefix, int digits = 6)
    {
        var n = NextInt((int)Math.Pow(10, Math.Clamp(digits, 1, 9)));
        // Can't use nested interpolation inside a format specifier.
        return prefix + n.ToString("D" + Math.Clamp(digits, 1, 9));
    }
}
