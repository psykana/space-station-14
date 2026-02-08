using System;
using System.IO;
using System.Text.Json;

namespace Fuzzer.CorpusGen;

public static class Program
{
    public static int Main(string[] args)
    {
        var opt = CliOptions.Parse(args);
        Directory.CreateDirectory(opt.OutDir);

        var validator = new SandboxValidator(opt.VerifyIl);

        var written = 0;
        var attempts = 0;
        var globalRng = new DeterministicRng(opt.Seed);

        while (written < opt.Count)
        {
            attempts++;
            var idx = written; // only increment on success

            // Deterministic per-assembly seed derived from global RNG and index.
            var asmSeed = globalRng.NextInt(int.MaxValue);
            var assemblyName = $"Content.FuzzSeed_{idx:000000}";

            var src = SourceEmitter.EmitAssemblySource(
                assemblyName: assemblyName,
                seed: asmSeed,
                maxTypes: opt.MaxTypes,
                maxMethodsPerType: opt.MaxMethodsPerType,
                maxNesting: opt.MaxNesting,
                includeBclCalls: opt.IncludeBclCalls);

            var (dll, diags) = RoslynCompiler.CompileToDllBytes(assemblyName, src);
            if (dll == null)
            {
                Console.Error.WriteLine($"[{assemblyName}] compile failed ({diags.Count} diagnostics)");
                continue;
            }

            var (ok, err) = validator.Validate(dll);
            if (!ok)
            {
                Console.Error.WriteLine($"[{assemblyName}] rejected: {err}");
                continue;
            }

            var dllPath = Path.Combine(opt.OutDir, assemblyName + ".dll");
            File.WriteAllBytes(dllPath, dll);

            if (opt.WriteSidecarJson)
            {
                var meta = new
                {
                    assemblyName,
                    idx,
                    seed = asmSeed,
                    verifyIl = opt.VerifyIl,
                    maxTypes = opt.MaxTypes,
                    maxMethodsPerType = opt.MaxMethodsPerType,
                    maxNesting = opt.MaxNesting,
                    includeBclCalls = opt.IncludeBclCalls,
                    attempts
                };

                var jsonPath = Path.Combine(opt.OutDir, assemblyName + ".json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }

            written++;
            if (written % 25 == 0)
                Console.WriteLine($"Wrote {written}/{opt.Count} (attempts: {attempts}) -> {opt.OutDir}");
        }

        Console.WriteLine($"Done. Wrote {written} valid assemblies to {opt.OutDir} (attempts: {attempts}).");
        return 0;
    }
}
