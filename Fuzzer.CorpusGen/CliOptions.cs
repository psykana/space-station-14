using System;
using System.Collections.Generic;
using System.IO;

namespace Fuzzer.CorpusGen;

internal sealed record CliOptions(
    string OutDir,
    int Count,
    int Seed,
    int MaxTypes,
    int MaxMethodsPerType,
    int MaxNesting,
    bool VerifyIl,
    bool WriteSidecarJson,
    bool IncludeBclCalls)
{
    public static CliOptions Parse(string[] args)
    {
        string outDir = Path.Combine("Fuzzer", "corpus", "seed");
        var count = 500;
        var seed = 1;
        var maxTypes = 6;
        var maxMethods = 12;
        var maxNesting = 2;
        var verifyIl = true;
        var writeJson = true;
        var includeBclCalls = false;

        var q = new Queue<string>(args);
        while (q.Count > 0)
        {
            var a = q.Dequeue();
            switch (a)
            {
                case "--out":
                    outDir = Next(q, a);
                    break;
                case "--count":
                    count = int.Parse(Next(q, a));
                    break;
                case "--seed":
                    seed = int.Parse(Next(q, a));
                    break;
                case "--max-types":
                    maxTypes = int.Parse(Next(q, a));
                    break;
                case "--max-methods":
                    maxMethods = int.Parse(Next(q, a));
                    break;
                case "--max-nesting":
                    maxNesting = int.Parse(Next(q, a));
                    break;
                case "--verify-il":
                    verifyIl = true;
                    break;
                case "--no-verify-il":
                    verifyIl = false;
                    break;
                case "--json":
                    writeJson = true;
                    break;
                case "--no-json":
                    writeJson = false;
                    break;
                case "--include-bcl-calls":
                    includeBclCalls = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown arg: {a}. Use --help.");
            }
        }

        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (maxTypes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTypes));
        if (maxMethods <= 0) throw new ArgumentOutOfRangeException(nameof(maxMethods));
        if (maxNesting < 0) throw new ArgumentOutOfRangeException(nameof(maxNesting));

        return new CliOptions(
            OutDir: outDir,
            Count: count,
            Seed: seed,
            MaxTypes: maxTypes,
            MaxMethodsPerType: maxMethods,
            MaxNesting: maxNesting,
            VerifyIl: verifyIl,
            WriteSidecarJson: writeJson,
            IncludeBclCalls: includeBclCalls);
    }

    private static string Next(Queue<string> q, string opt)
    {
        if (q.Count == 0)
            throw new ArgumentException($"Missing value after {opt}");
        return q.Dequeue();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Fuzzer.CorpusGen - generate AssemblyTypeChecker-valid seed assemblies");
        Console.WriteLine("Options:");
        Console.WriteLine("  --out <dir>            Output directory (default: Fuzzer/corpus/seed)");
        Console.WriteLine("  --count <n>            How many valid DLLs to write (default: 500)");
        Console.WriteLine("  --seed <int>           RNG seed (default: 1)");
        Console.WriteLine("  --max-types <n>        Max top-level types per assembly (default: 6)");
        Console.WriteLine("  --max-methods <n>      Max methods per type (default: 12)");
        Console.WriteLine("  --max-nesting <n>      Max nested-type depth (default: 2)");
        Console.WriteLine("  --verify-il            Validate with VerifyIL=true (default)");
        Console.WriteLine("  --no-verify-il         Validate with VerifyIL=false");
        Console.WriteLine("  --json / --no-json     Write sidecar JSON (default: --json)");
        Console.WriteLine("  --include-bcl-calls    Allow some BCL calls (off by default)");
    }
}
