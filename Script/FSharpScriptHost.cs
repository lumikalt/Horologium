using static FSharp.Compiler.Interactive.Shell;
using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Pipeline.Spec;
using RiscV32;
using System.Text;
using FSharp.Compiler.Diagnostics;
using Microsoft.FSharp.Core;

namespace Script;

/// <summary>
/// Evaluates a <c>.fsx</c> file whose last expression is a <see cref="MachineSpec"/>.
/// A fresh FSI session is created per evaluation with all project assemblies pre-referenced
/// and all Spec namespaces pre-opened — the script needs no <c>#r</c> or <c>open</c>.
/// <para>Convention: the last top-level expression (not a <c>let</c> binding) is the return value.</para>
/// </summary>
internal static class FSharpScriptHost {
    private static readonly string[] PreOpenedNamespaces = [
        "Mechanism",
        "Pipeline.Spec",
        "Orrery.Spec",
        "Orrery.Cache",
        "RiscV32",
    ];

    private static readonly string[] AssemblyPaths = [
        typeof(IMemory).Assembly.Location,
        typeof(MachineSpec).Assembly.Location,
        typeof(Rv32Mechanism).Assembly.Location,
        typeof(SetAssociativeCache).Assembly.Location,
    ];

    public static async Task<MachineSpec> EvaluateFileAsync(
        string scriptPath,
        CancellationToken ct = default
    ) {
        string source = await File.ReadAllTextAsync(scriptPath, ct);
        return await EvaluateAsync(source, scriptPath, ct);
    }

    public static Task<MachineSpec> EvaluateAsync(
        string source,
        string scriptName = "<fsx>",
        CancellationToken ct = default
    ) => Task.Run(() => EvaluateCore(source, scriptName, ct), ct);

    private static MachineSpec EvaluateCore(string source, string scriptName, CancellationToken ct) {
        FsiEvaluationSession session = CreateSession();

        // Pre-open namespaces so the script doesn't need 'open' directives.
        foreach (string ns in FSharpScriptHost.PreOpenedNamespaces) {
            (FSharpChoice<FSharpOption<FsiValue>?, Exception> openChoice, _)
                = session.EvalInteractionNonThrowing($"open {ns}", null);
            if (openChoice.IsChoice2Of2) throw Fail($"Failed to open {ns}: {UnwrapError(openChoice)}");
        }

        (FSharpChoice<FSharpOption<FsiValue>?, Exception> scriptChoice, FSharpDiagnostic[] diags)
            = session.EvalInteractionNonThrowing(source, null);
        if (scriptChoice.IsChoice2Of2) {
            string diagText = string.Join("\n", diags.Select(d => d.ToString()));
            throw Fail(
                $"Script error: {UnwrapError(scriptChoice)}" +
                (diagText.Length > 0 ? $"\n{diagText}" : "")
            );
        }

        // 'it' is bound to the last bare expression (not a let binding) in the script.
        (FSharpChoice<FSharpOption<FsiValue>?, Exception> itChoice, _)
            = session.EvalExpressionNonThrowing("it", scriptName);
        if (itChoice.IsChoice2Of2) throw Fail($"Could not read 'it': {UnwrapError(itChoice)}");

        FSharpOption<FsiValue>? opt = UnwrapChoice1(itChoice);
        if (opt is null)
            throw Fail(
                "Script must return a MachineSpec as its last expression (no value was produced — 'it' is None)."
            );

        object raw = opt.Value.ReflectionValue;
        if (raw is not MachineSpec spec)
            throw Fail($"Script must return a MachineSpec, got {raw?.GetType().Name ?? "null"}.");

        return spec;
    }

    private static FsiEvaluationSession CreateSession() {
        FsiEvaluationSessionHostConfig config = FsiEvaluationSession.GetDefaultConfiguration();
        string[] fsiArgs = [
            "fsi.exe",
            "--noninteractive",
            "--nologo",
            "--gui-",
            ..FSharpScriptHost.AssemblyPaths.Select(p => $"--reference:{p}"),
        ];
        return FsiEvaluationSession.Create(
            config, fsiArgs,
            new StringReader(""),
            new StringWriter(), // stdout: discard (bind to sbOut if debugging needed)
            new StringWriter(), // stderr: discard
            null, null
        );
    }

    // Unwrap Choice2Of2 (the error branch) as a string.
    private static string UnwrapError<T>(Microsoft.FSharp.Core.FSharpChoice<T, Exception> choice) =>
        ((Microsoft.FSharp.Core.FSharpChoice<T, Exception>.Choice2Of2)choice).Item.Message;

    // Unwrap Choice1Of2 (the success branch).
    private static T UnwrapChoice1<T>(Microsoft.FSharp.Core.FSharpChoice<T, Exception> choice) =>
        ((Microsoft.FSharp.Core.FSharpChoice<T, Exception>.Choice1Of2)choice).Item;

    private static ScriptException Fail(string msg) => new(msg);
}