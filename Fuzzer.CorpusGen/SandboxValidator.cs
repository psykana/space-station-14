using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Fuzzer.CorpusGen;

internal sealed class SandboxValidator
{
    private readonly object _checker;
    private readonly Type _checkerType;

    public SandboxValidator(bool verifyIl)
    {
        var res = new DummyRes();
        var sawmill = new NoopSawmill();

        var robustSharedAsm = typeof(IResourceManager).Assembly;
        _checkerType = robustSharedAsm.GetType("Robust.Shared.ContentPack.AssemblyTypeChecker", throwOnError: true)!;

        _checker = Activator.CreateInstance(
            _checkerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { res, sawmill },
            culture: null) ?? throw new InvalidOperationException("Failed to create AssemblyTypeChecker instance");

        _checkerType.GetProperty("VerifyIL", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(_checker, verifyIl);
        _checkerType.GetProperty("DisableTypeCheck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(_checker, false);
    }

    public (bool ok, string? error) Validate(byte[] dllBytes)
    {
        try
        {
            using var ms = new MemoryStream(dllBytes);
            var mi = _checkerType.GetMethod("CheckAssembly", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Stream) }, modifiers: null);

            if (mi == null)
                return (false, "AssemblyTypeChecker.CheckAssembly(Stream) not found");

            var okObj = mi.Invoke(_checker, new object[] { ms });
            if (okObj is bool ok)
                return (ok, ok ? null : "Rejected by sandbox checker");

            return (false, "Unexpected return type from CheckAssembly");
        }
        catch (TargetInvocationException tie)
        {
            return (false, tie.InnerException?.ToString() ?? tie.ToString());
        }
        catch (Exception e)
        {
            return (false, e.ToString());
        }
    }

    private sealed class DummyRes : IResourceManager
    {
        public IWritableDirProvider UserData => throw new NotSupportedException();
        public void AddRoot(ResPath prefix, IContentRoot loader) => throw new NotSupportedException();
        public Stream ContentFileRead(ResPath path) => throw new NotSupportedException();
        public Stream ContentFileRead(string path) => throw new NotSupportedException();
        public bool ContentFileExists(ResPath path) => false;
        public bool ContentFileExists(string path) => false;

        public bool TryContentFileRead(ResPath? path, [NotNullWhen(true)] out Stream? fileStream)
        {
            fileStream = null;
            return false;
        }

        public bool TryContentFileRead(string path, [NotNullWhen(true)] out Stream? fileStream)
        {
            fileStream = null;
            return false;
        }

        public IEnumerable<ResPath> ContentFindFiles(ResPath? path) => Array.Empty<ResPath>();
        public IEnumerable<ResPath> ContentFindFiles(string path) => Array.Empty<ResPath>();
        public IEnumerable<string> ContentGetDirectoryEntries(ResPath path) => Array.Empty<string>();

#pragma warning disable CS0618
        public IEnumerable<ResPath> GetContentRoots() => Array.Empty<ResPath>();
#pragma warning restore CS0618
    }

    private sealed class NoopSawmill : ISawmill
    {
        public string Name => "corpusgen";
        public LogLevel? Level { get; set; }

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
}
