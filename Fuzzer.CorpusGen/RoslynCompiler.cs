using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuzzer.CorpusGen;

internal static class RoslynCompiler
{
    public static (byte[]? dll, IReadOnlyList<string> diagnostics) CompileToDllBytes(
        string assemblyName,
        string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var refs = GetTrustedPlatformReferences();

        var options = new CSharpCompilationOptions(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            deterministic: true,
            nullableContextOptions: NullableContextOptions.Disable);

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: new[] { tree },
            references: refs,
            options: options);

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        var diags = emitResult.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToArray();

        if (!emitResult.Success)
            return (null, diags);

        return (peStream.ToArray(), diags);
    }

    private static ImmutableArray<MetadataReference> GetTrustedPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES not available");

        var paths = tpa.Split(Path.PathSeparator).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = new List<MetadataReference>();
        foreach (var p in paths)
        {
            if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                list.Add(MetadataReference.CreateFromFile(p));
        }

        return list.ToImmutableArray();
    }
}
