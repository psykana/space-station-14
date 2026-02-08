using System.Diagnostics.CodeAnalysis;
using System.IO;
using SharpFuzz;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;
// (fuzzer harness)

public static class Program
{
    public static void Main()
    {
        var checker = new AssemblyTypeChecker(res: new DummyRes(), sawmill: new DummySawmill())
        {
            VerifyIL = false,        // start here for throughput
            DisableTypeCheck = false // keep whitelist checks on
        };

        // SharpFuzz's callback provides the input file path.
        Fuzzer.Run(filePath =>
        {
            using var stream = File.OpenRead(filePath);
            checker.CheckAssembly(stream); // let exceptions crash = findings
        });
    }

    private sealed class DummySawmill : ISawmill
    {
        public LogLevel? Level { get; set; }
        public string Name => "fuzz";

        public void AddHandler(ILogHandler handler) { }
        public void RemoveHandler(ILogHandler handler) { }

        public void Log(LogLevel level, string message, params object?[] args) { }
        public void Log(LogLevel level, Exception? exception, string message, params object?[] args) { }
        public void Log(LogLevel level, string message) { }

        public void Debug(string message, params object?[] args) { }
        public void Debug(string message) { }
        public void Info(string message, params object?[] args) { }
        public void Info(string message) { }
        public void Warning(string message, params object?[] args) { }
        public void Warning(string message) { }
        public void Error(string message, params object?[] args) { }
        public void Error(string message) { }
        public void Fatal(string message, params object?[] args) { }
        public void Fatal(string message) { }
    }

    private sealed class DummyRes : Robust.Shared.ContentPack.IResourceManager
    {
        // Implement members with throw; with VerifyIL=false the resolver shouldn’t use it.
        public Robust.Shared.ContentPack.IWritableDirProvider UserData => throw new System.NotSupportedException();
        public void AddRoot(Robust.Shared.Utility.ResPath prefix, Robust.Shared.ContentPack.IContentRoot loader) => throw new System.NotSupportedException();
        public Stream ContentFileRead(Robust.Shared.Utility.ResPath path) => throw new System.NotSupportedException();
        public Stream ContentFileRead(string path) => throw new System.NotSupportedException();
        public bool ContentFileExists(Robust.Shared.Utility.ResPath path) => throw new System.NotSupportedException();
        public bool ContentFileExists(string path) => throw new System.NotSupportedException();
        public bool TryContentFileRead(Robust.Shared.Utility.ResPath? path, [NotNullWhen(true)] out Stream? fileStream) { fileStream = null; return false; }
        public bool TryContentFileRead(string path, [NotNullWhen(true)] out Stream? fileStream) { fileStream = null; return false; }
        public System.Collections.Generic.IEnumerable<Robust.Shared.Utility.ResPath> ContentFindFiles(Robust.Shared.Utility.ResPath? path) => System.Array.Empty<Robust.Shared.Utility.ResPath>();
        public System.Collections.Generic.IEnumerable<Robust.Shared.Utility.ResPath> ContentFindFiles(string path) => System.Array.Empty<Robust.Shared.Utility.ResPath>();
        public System.Collections.Generic.IEnumerable<string> ContentGetDirectoryEntries(Robust.Shared.Utility.ResPath path) => System.Array.Empty<string>();
#pragma warning disable CS0618
        public System.Collections.Generic.IEnumerable<Robust.Shared.Utility.ResPath> GetContentRoots() => System.Array.Empty<Robust.Shared.Utility.ResPath>();
#pragma warning restore CS0618
    }
}
