## Fuzzer.CorpusGen

Generates a **seed corpus of valid .NET assemblies** that pass SS14/Robust's sandbox rules (`Robust.Shared.ContentPack.AssemblyTypeChecker`) and are suitable as starting inputs for an assembly fuzzer.

### Why this works
- The sandbox enforces:
  - **Assembly name prefix**: must start with `Content` (or `OpenDream`).
  - **Whitelisted namespaces**: anything in `Content.*` is treated as allowed.
  - **Hard bans**: unmanaged method defs (P/Invoke) and explicit-layout types with fields.
- This tool generates assemblies named `Content.FuzzSeed_XXXXXX` and puts all user code in `Content.FuzzSeeds.*`.

### Build

```bash
dotnet build Fuzzer.CorpusGen/Fuzzer.CorpusGen.csproj
```

### Generate a corpus

```bash
dotnet run --project Fuzzer.CorpusGen -- --out Fuzzer/corpus/seed --count 500 --seed 1
```

Outputs:
- `Content.FuzzSeed_000000.dll`, ...
- Sidecar `Content.FuzzSeed_000000.json` with generation parameters (optional; enabled by default).

### Notes
- Validation uses `AssemblyTypeChecker` with `VerifyIL=true` by default.
- If you want faster generation for experimentation, you can skip IL verification:

```bash
dotnet run --project Fuzzer.CorpusGen -- --out Fuzzer/corpus/seed --count 500 --seed 1 --no-verify-il
```
