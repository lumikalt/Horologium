using System.Runtime.InteropServices;

namespace Orrery.Cache;

/// <summary>
///     Loads a Verilator-compiled RTL cache replacement policy wrapped by
///     <c>native/RtlFu/rtl_rp_shim.cpp</c> and drives it through
///     <c>rtl_rp_record_hit</c>/<c>rtl_rp_choose_victim</c>/<c>rtl_rp_record_install</c>
///     via P/Invoke — the replacement-policy counterpart of the RTL functional-unit and
///     branch-predictor substitutions in <c>Mechanism.RtlFu</c>, letting a synthesizable
///     policy (e.g. the Chisel SRRIP in <c>native/RtlFu/SrripRp.scala</c>) stand in for a
///     C# <see cref="IReplacementPolicy" /> inside <see cref="SetAssociativeCache" />.
///     <para>
///         The RTL's geometry is fixed at Chisel elaboration and exposed through the
///         shim; the constructor validates it against the attaching cache's sets/ways
///         (<see cref="TryCreate" /> returns null instead of throwing, so a multi-level
///         hierarchy can fall back to the configured policy kind on non-matching levels).
///     </para>
///     <para>
///         Not thread-safe (one verilated model per instance) and desktop-only
///         (<see cref="NativeLibrary" />) — same constraints as the other RTL FFI
///         wrappers; each cache builds its own instance via the MemoryConfig factory.
///     </para>
/// </summary>
public sealed unsafe class RtlFfiReplacementPolicy : IReplacementPolicy, IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly delegate* unmanaged[Cdecl]<void*, int, int, void> _hit;
    private readonly delegate* unmanaged[Cdecl]<void*, int, int, void> _install;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<void*, int, int, int> _metadata;
    private readonly delegate* unmanaged[Cdecl]<void*, int, int> _victim;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" />, constructs (and
    ///     resets) the wrapped verilated model, and reads its elaborated geometry.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public RtlFfiReplacementPolicy(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_rp_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_rp_destroy");
        var geometry =
            (delegate* unmanaged[Cdecl]<void*, int*, int*, void>)NativeLibrary.GetExport(
                _library, "rtl_rp_geometry"
            );
        _hit = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
            _library, "rtl_rp_record_hit"
        );
        _victim = (delegate* unmanaged[Cdecl]<void*, int, int>)NativeLibrary.GetExport(
            _library, "rtl_rp_choose_victim"
        );
        _install = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
            _library, "rtl_rp_record_install"
        );
        _metadata = (delegate* unmanaged[Cdecl]<void*, int, int, int>)NativeLibrary.GetExport(
            _library, "rtl_rp_metadata"
        );
        _handle = create();
        int sets, ways;
        geometry(_handle, &sets, &ways);
        Sets = sets;
        Ways = ways;
    }

    /// <summary>
    ///     Loads and validates against the attaching cache's geometry.
    /// </summary>
    /// <exception cref="ArgumentException">The model was elaborated for a different geometry.</exception>
    public RtlFfiReplacementPolicy(string libraryPath, int sets, int ways) : this(libraryPath) {
        if (Sets == sets && Ways == ways) return;
        int modelSets = Sets, modelWays = Ways;
        Dispose();
        throw new ArgumentException(
            $"RTL replacement policy '{libraryPath}' was elaborated for {modelSets} sets × "
          + $"{modelWays} ways, but the cache has {sets} sets × {ways} ways."
        );
    }

    /// <summary>Set count the model was elaborated for.</summary>
    public int Sets { get; }

    /// <summary>Way count the model was elaborated for.</summary>
    public int Ways { get; }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }

    /// <inheritdoc />
    public void RecordHit(int set, int way) => _hit(_handle, set, way);

    /// <inheritdoc />
    public int ChooseVictim(int set) => _victim(_handle, set);

    /// <inheritdoc />
    public void RecordInstall(int set, int way) => _install(_handle, set, way);

    /// <inheritdoc />
    public int GetMetadata(int set, int way) => _metadata(_handle, set, way);

    /// <summary>
    ///     Loads the model and returns it only when its elaborated geometry matches the
    ///     given cache geometry; otherwise disposes it and returns null so the caller can
    ///     fall back to a configured C# policy (used for non-matching hierarchy levels).
    /// </summary>
    public static RtlFfiReplacementPolicy? TryCreate(string libraryPath, int sets, int ways) {
        var policy = new RtlFfiReplacementPolicy(libraryPath);
        if (policy.Sets == sets && policy.Ways == ways) return policy;
        policy.Dispose();
        return null;
    }
}