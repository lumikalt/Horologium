using Mechanism;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Orrery.Cache;
using Pipeline.Spec;
using RiscV32;

namespace Script;

/// <summary>
///     Evaluates a <c>.csx</c> or <c>.fsx</c> file whose last expression is a <see cref="MachineSpec" />.
///     Dispatches to the Roslyn (C#) or FSI (F#) host based on file extension.
///     In both cases all Spec namespaces are pre-imported and project assemblies pre-referenced —
///     no <c>#r</c> directives or <c>using</c>/<c>open</c> statements needed in the script.
/// </summary>
public static class ScriptHost {
    private static readonly ScriptOptions CSharpOptions = ScriptOptions.Default
                                                                       .AddReferences(
                                                                            typeof(IMemory).Assembly,       // Mechanism
                                                                            typeof(MachineSpec).Assembly,   // Pipeline
                                                                            typeof(Rv32Mechanism).Assembly, // RiscV32
                                                                            typeof(RiscV64.Rv64Mechanism)
                                                                               .Assembly, // RiscV64
                                                                            typeof(SetAssociativeCache)
                                                                               .Assembly // Orrery (includes Orrery.Spec)
                                                                        )
                                                                       .AddImports(
                                                                            "System",
                                                                            "Mechanism",
                                                                            "Mechanism.BranchPredictModels",
                                                                            "Mechanism.RtlFu",
                                                                            "Pipeline.Spec",
                                                                            "Orrery.Spec",
                                                                            "Orrery.Cache",
                                                                            "RiscV32",
                                                                            "RiscV32.Execute",
                                                                            "RiscV64",
                                                                            "RiscV64.Execute"
                                                                        );

    /// <summary>
    ///     Evaluates <paramref name="scriptPath" /> (.csx or .fsx) and returns the resulting <see cref="MachineSpec" />.
    /// </summary>
    /// <exception cref="ScriptException">Thrown when the script fails or does not return a <see cref="MachineSpec" />.</exception>
    public static Task<MachineSpec> EvaluateFileAsync(
        string scriptPath,
        CancellationToken ct = default
    ) => Path.GetExtension(scriptPath).ToLowerInvariant() switch {
        ".fsx" => FSharpScriptHost.EvaluateFileAsync(scriptPath, ct),
        _      => EvaluateCsxFileAsync(scriptPath, ct),
    };

    /// <summary>Evaluates a C# script string and returns the resulting <see cref="MachineSpec" />.</summary>
    public static async Task<MachineSpec> EvaluateCsxAsync(
        string source,
        CancellationToken ct = default
    ) {
        object? raw = await CSharpScript.EvaluateAsync(source, ScriptHost.CSharpOptions, cancellationToken: ct);
        if (raw is not MachineSpec spec)
            throw new ScriptException(
                $"Script must return a MachineSpec, got {raw?.GetType().Name ?? "null"}."
            );
        return spec;
    }

    /// <summary>Evaluates an F# script string and returns the resulting <see cref="MachineSpec" />.</summary>
    public static Task<MachineSpec> EvaluateFsxAsync(
        string source,
        CancellationToken ct = default
    ) => FSharpScriptHost.EvaluateAsync(source, "<fsx>", ct);

    private static async Task<MachineSpec> EvaluateCsxFileAsync(string path, CancellationToken ct) {
        string source = await File.ReadAllTextAsync(path, ct);
        return await EvaluateCsxAsync(source, ct);
    }
}

/// <summary>Thrown when script evaluation fails or returns the wrong type.</summary>
public sealed class ScriptException(string message, Exception? inner = null)
    : Exception(message, inner);