using System;
using System.Text;

namespace Fuzzer.CorpusGen;

internal static class SourceEmitter
{
    public static string EmitAssemblySource(
        string assemblyName,
        int seed,
        int maxTypes,
        int maxMethodsPerType,
        int maxNesting,
        bool includeBclCalls)
    {
        var rng = new DeterministicRng(seed);
        var sb = new StringBuilder(capacity: 64 * 1024);

        sb.AppendLine("// Auto-generated fuzz seed. Deterministic.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();

        // Keep everything under Content.* so referenced content types are automatically whitelisted.
        sb.AppendLine($"namespace Content.FuzzSeeds.{SanitizeNs(assemblyName)}");
        sb.AppendLine("{");

        var typeCount = rng.NextInt(1, Math.Max(2, maxTypes + 1));
        for (var t = 0; t < typeCount; t++)
        {
            EmitType(sb, ref rng, $"T{t}", depth: 0, maxMethodsPerType, maxNesting, includeBclCalls);
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string SanitizeNs(string s)
    {
        // Assembly names contain '.'; namespaces allow it but we want a single identifier segment.
        // Keep alnum + '_' only.
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }

    private static void EmitType(
        StringBuilder sb,
        ref DeterministicRng rng,
        string typeName,
        int depth,
        int maxMethodsPerType,
        int maxNesting,
        bool includeBclCalls)
    {
        var kind = rng.NextInt(3);
        switch (kind)
        {
            case 0:
                EmitClass(sb, ref rng, typeName, depth, maxMethodsPerType, maxNesting, includeBclCalls);
                break;
            case 1:
                EmitStruct(sb, ref rng, typeName, maxMethodsPerType);
                break;
            default:
                EmitEnum(sb, ref rng, typeName);
                break;
        }

        if (depth < maxNesting && rng.NextBool())
        {
            var nestedCount = rng.NextInt(0, 3);
            for (var i = 0; i < nestedCount; i++)
            {
                sb.AppendLine();
                Indent(sb, depth + 1);
                sb.AppendLine($"public class N{i}");
                Indent(sb, depth + 1);
                sb.AppendLine("{");
                Indent(sb, depth + 2);
                sb.AppendLine("public int X;");
                Indent(sb, depth + 2);
                sb.AppendLine("public int F(int a) { return a + X; }");
                Indent(sb, depth + 1);
                sb.AppendLine("}");
            }
        }
    }

    private static void EmitClass(
        StringBuilder sb,
        ref DeterministicRng rng,
        string typeName,
        int depth,
        int maxMethodsPerType,
        int maxNesting,
        bool includeBclCalls)
    {
        Indent(sb, depth + 1);
        sb.AppendLine($"public class {typeName}");
        Indent(sb, depth + 1);
        sb.AppendLine("{");

        Indent(sb, depth + 2);
        sb.AppendLine("public int A;");
        Indent(sb, depth + 2);
        sb.AppendLine("public int B;");

        // Simple deterministic constructor.
        Indent(sb, depth + 2);
        sb.AppendLine($"public {typeName}() {{ A = {rng.NextInt(0, 1000)}; B = {rng.NextInt(0, 1000)}; }}");

        var methodCount = rng.NextInt(1, Math.Max(2, maxMethodsPerType + 1));
        for (var i = 0; i < methodCount; i++)
        {
            sb.AppendLine();
            EmitMethod(sb, ref rng, depth + 2, $"M{i}", includeBclCalls);
        }

        // A tiny generic type/method in our own namespace (no extra external refs).
        sb.AppendLine();
        Indent(sb, depth + 2);
        sb.AppendLine("public static T Id<T>(T x) { return x; }");

        Indent(sb, depth + 1);
        sb.AppendLine("}");
    }

    private static void EmitStruct(StringBuilder sb, ref DeterministicRng rng, string typeName, int maxMethodsPerType)
    {
        sb.AppendLine($"    public struct {typeName}");
        sb.AppendLine("    {");
        sb.AppendLine("        public int X;");
        sb.AppendLine("        public int Y;");
        sb.AppendLine($"        public {typeName}(int x, int y) {{ X = x; Y = y; }}");

        var methodCount = rng.NextInt(1, Math.Max(2, maxMethodsPerType + 1));
        for (var i = 0; i < methodCount; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"        public int S{i}(int a)");
            sb.AppendLine("        {");
            sb.AppendLine("            var v = a;");
            sb.AppendLine("            v = v + X;");
            sb.AppendLine("            v = v ^ Y;");
            sb.AppendLine("            return v;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
    }

    private static void EmitEnum(StringBuilder sb, ref DeterministicRng rng, string typeName)
    {
        sb.AppendLine($"    public enum {typeName}");
        sb.AppendLine("    {");
        sb.AppendLine("        Z0 = 0,");
        sb.AppendLine($"        Z1 = {rng.NextInt(1, 1000)},");
        sb.AppendLine($"        Z2 = {rng.NextInt(1, 1000)}");
        sb.AppendLine("    }");
    }

    private static void EmitMethod(StringBuilder sb, ref DeterministicRng rng, int indent, string name, bool includeBclCalls)
    {
        Indent(sb, indent);
        sb.AppendLine($"public int {name}(int x)");
        Indent(sb, indent);
        sb.AppendLine("{");

        Indent(sb, indent + 1);
        sb.AppendLine("var v = x;");

        // Add varied IL without invoking many external APIs.
        var ops = rng.NextInt(3, 10);
        for (var i = 0; i < ops; i++)
        {
            var choice = rng.NextInt(6);
            Indent(sb, indent + 1);
            switch (choice)
            {
                case 0:
                    sb.AppendLine("v = v + A;");
                    break;
                case 1:
                    sb.AppendLine("v = v - B;");
                    break;
                case 2:
                    sb.AppendLine("v = v ^ (A << 1);");
                    break;
                case 3:
                    sb.AppendLine("v = (v * 1664525) + 1013904223;");
                    break;
                case 4:
                    sb.AppendLine("if ((v & 1) == 0) v = v >> 1; else v = v << 1;");
                    break;
                default:
                    sb.AppendLine("switch (v & 3) { case 0: v += 1; break; case 1: v += 2; break; case 2: v += 3; break; default: v += 4; break; }");
                    break;
            }
        }

        if (includeBclCalls && rng.NextBool())
        {
            // Very conservative BCL call that typically exists in the whitelist.
            Indent(sb, indent + 1);
            sb.AppendLine("v = System.Math.Abs(v);");
        }

        Indent(sb, indent + 1);
        sb.AppendLine("return v;");
        Indent(sb, indent);
        sb.AppendLine("}");
    }

    private static void Indent(StringBuilder sb, int n)
    {
        for (var i = 0; i < n; i++) sb.Append("    ");
    }
}
