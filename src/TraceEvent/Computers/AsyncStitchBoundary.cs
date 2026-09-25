// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// The kind of async dispatch boundary a synchronous (CPU-sample) frame represents. When stitching an
    /// async call stack (from <see cref="AsyncCallStacksIndex"/>) into a native sync stack, the stitcher walks
    /// the sync stack top-down (leaf to root) and consumes one async segment each time it crosses one of these
    /// boundaries. See the design note (callstack-stitching) for the full algorithm.
    /// </summary>
    public enum AsyncStitchBoundaryKind
    {
        /// <summary>Not an async dispatch boundary; an ordinary frame.</summary>
        None = 0,

        /// <summary>
        /// A V2 (RuntimeAsync) continuation-wrapper frame (<c>Continuation_Wrapper_0</c> ..
        /// <c>Continuation_Wrapper_31</c>). It carries an explicit wrapper index (the low bits of the
        /// continuation index) and directly calls the resumed async method.
        /// </summary>
        V2ContinuationWrapper,

        /// <summary>
        /// A V2 (RuntimeAsync) dispatch-continuation frame with no wrapper present
        /// (<c>DispatchContinuations</c> uninstrumented, or <c>InstrumentedDispatchContinuations</c>
        /// instrumented, e.g. on late attach). The current dispatched position is assumed to be index 0.
        /// </summary>
        V2DispatchContinuation,

        /// <summary>
        /// A V1 (StateMachineAsync) dispatcher frame (<c>MoveNextAsDispatcher</c>). This is the outer,
        /// scheduler-dispatched merged box that pushed the async TLS and emitted the segment; it masquerades
        /// as a state-machine box but is recognized by name. Its segment index is <b>derived</b> by counting
        /// the inline <c>MoveNext</c> frames from the first resumed state-machine frame down to this boundary.
        /// </summary>
        V1Dispatcher,

        /// <summary>
        /// A frame declared on the CoreLib <c>System.Runtime.CompilerServices.*AsyncStateMachineBox</c> type
        /// family. These frames are V1 dispatcher plumbing when they occur contiguously root-ward of a consumed
        /// <see cref="V1Dispatcher"/> boundary; they are not themselves splice boundaries.
        /// </summary>
        V1DispatcherInfrastructure,
    }

    /// <summary>
    /// The boundary classification of a single method: its <see cref="AsyncStitchBoundaryKind"/> plus, for a
    /// <see cref="AsyncStitchBoundaryKind.V2ContinuationWrapper"/>, the wrapper index it encodes.
    /// </summary>
    public readonly struct AsyncStitchBoundaryInfo
    {
        /// <summary>The classification of the method.</summary>
        public readonly AsyncStitchBoundaryKind Kind;

        /// <summary>For <see cref="AsyncStitchBoundaryKind.V2ContinuationWrapper"/>, the wrapper index
        /// (0..<see cref="AsyncStitchBoundary.WrapperPoolCount"/>-1); otherwise -1.</summary>
        public readonly int WrapperIndex;

        public AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind kind, int wrapperIndex)
        {
            Kind = kind;
            WrapperIndex = wrapperIndex;
        }

        /// <summary>The "ordinary frame" classification (not a boundary).</summary>
        public static readonly AsyncStitchBoundaryInfo None = new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.None, -1);

        /// <summary>True if this method is an async dispatch boundary.</summary>
        public bool IsBoundary =>
            Kind == AsyncStitchBoundaryKind.V2ContinuationWrapper ||
            Kind == AsyncStitchBoundaryKind.V2DispatchContinuation ||
            Kind == AsyncStitchBoundaryKind.V1Dispatcher;
    }

    /// <summary>
    /// Recognizes the async dispatch boundary methods on a native (CPU-sample) call stack by their method
    /// names. These names are a <b>contract</b> between the .NET runtime async profiler and this stitcher:
    /// the runtime marks each boundary method <c>[MethodImpl(MethodImplOptions.NoInlining)]</c> (so it always
    /// appears as its own frame with a stable name) and documents that external tools depend on the name.
    /// A reflection guard test in dotnet/runtime asserts these names and the NoInlining attribute hold.
    /// <para>
    /// This type does the (string-based) recognition; a per-trace <see cref="AsyncStitchBoundaryCache"/>
    /// resolves the names to <c>MethodIndex</c> values once so the per-frame stitch hot path avoids string
    /// compares entirely.
    /// </para>
    /// <para>
    /// Sources (dotnet/runtime), all in <c>System.Private.CoreLib</c>:
    /// <list type="bullet">
    ///   <item><c>AsyncProfiler.ContinuationWrapper.Continuation_Wrapper_{0}</c> (0..31), with the public
    ///   contract const <c>ContinuationWrapper.NameTemplate = "Continuation_Wrapper_{0}"</c> and pool size
    ///   <c>COUNT = 32</c> — <c>AsyncProfiler.CoreCLR.cs</c>.</item>
    ///   <item><c>RuntimeAsyncTask&lt;T&gt;.DispatchContinuations</c> and
    ///   <c>InstrumentedDispatchContinuations</c> — <c>AsyncHelpers.CoreCLR.cs</c>.</item>
    ///   <item><c>AsyncProfilerAsyncStateMachineBox&lt;TStateMachine&gt;.MoveNextAsDispatcher</c> —
    ///   <c>AsyncTaskMethodBuilderT.cs</c> (the V1 merged-box dispatcher).</item>
    ///   <item><c>AsyncStateMachineDispatcher.MoveNext</c> — <c>AsyncStateMachineDispatcher.cs</c> (the V1
    ///   non-merged/standalone dispatcher, a separate wrapper task; recognized type-qualified because the bare
    ///   <c>MoveNext</c> name is not unique).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Note: the continuation-wrapper class is <c>[StackTraceHidden]</c>, but that only hides frames from the
    /// managed <see cref="System.Diagnostics.StackTrace"/> text; CPU-sample native IPs (ETW/EventPipe) still
    /// capture them, which is what this recognizer keys on.
    /// </para>
    /// </summary>
    public static class AsyncStitchBoundary
    {
        /// <summary>The V2 continuation-wrapper method-name prefix; the numeric suffix is the wrapper index.
        /// Mirrors the runtime contract const <c>ContinuationWrapper.NameTemplate = "Continuation_Wrapper_{0}"</c>.</summary>
        public const string ContinuationWrapperPrefix = "Continuation_Wrapper_";

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c> in the runtime).
        /// Valid wrapper indices are <c>0 .. WrapperPoolCount-1</c>.</summary>
        public const int WrapperPoolCount = 32;

        /// <summary>The V2 uninstrumented dispatch-continuation method name (<c>RuntimeAsyncTask&lt;T&gt;</c>).</summary>
        public const string DispatchContinuationsName = "DispatchContinuations";

        /// <summary>The V2 instrumented dispatch-continuation method name (<c>RuntimeAsyncTask&lt;T&gt;</c>).</summary>
        public const string InstrumentedDispatchContinuationsName = "InstrumentedDispatchContinuations";

        /// <summary>The V1 merged-box dispatcher method name (<c>AsyncProfilerAsyncStateMachineBox&lt;T&gt;</c>),
        /// i.e. the dispatcher that is merged into (is itself) a state-machine box.</summary>
        public const string MoveNextAsDispatcherName = "MoveNextAsDispatcher";

        /// <summary>The V1 async-state-machine-dispatcher type name (<c>AsyncStateMachineDispatcher</c>): a separate
        /// wrapper task that dispatches an inner box rather than being merged into a state-machine box.</summary>
        public const string AsyncStateMachineDispatcherTypeName = "AsyncStateMachineDispatcher";

        /// <summary>The common type-name suffix for the V1 async-state-machine box family.</summary>
        public const string AsyncStateMachineBoxTypeSuffix = "AsyncStateMachineBox";

        /// <summary>The V1 async-state-machine-dispatcher method name (<c>AsyncStateMachineDispatcher.MoveNext</c>).
        /// Because this bare name is not unique (every state machine has a <c>MoveNext</c>), the dispatcher is
        /// recognized type-qualified via <see cref="AsyncStateMachineDispatcherTypeName"/>.</summary>
        public const string AsyncStateMachineDispatcherMethodName = "MoveNext";

        /// <summary>The short module name (no extension) that hosts all async boundary methods.</summary>
        public const string HostModuleName = "System.Private.CoreLib";

        /// <summary>
        /// Classifies frames used by the V1 synchronous async-method startup sequence. Builder frames must be
        /// declared in <c>System.Private.CoreLib!System.Runtime.CompilerServices</c>; the generated state-machine
        /// <c>MoveNext</c> is expected in the application module.
        /// </summary>
        public static StitchSyncFrameKind ClassifyV1SynchronousFrame(string frameName) =>
            ClassifyV1SynchronousFrame(frameName, IsHostModuleQualifiedFrame(frameName));

        /// <summary>
        /// Classifies a V1 synchronous-startup frame when the caller has already determined whether the method is
        /// declared in <c>System.Private.CoreLib</c>. This overload supports TraceEvent's split module/method model,
        /// where <c>TraceCodeAddress.ModuleName</c> and <c>FullMethodName</c> are separate.
        /// </summary>
        public static StitchSyncFrameKind ClassifyV1SynchronousFrame(string frameName, bool isHostModule)
        {
            string method = GetBareMethodName(frameName);
            if (method == AsyncStateMachineDispatcherMethodName)
            {
                string declaringType = GetDeclaringTypeName(frameName);
                if (declaringType != null &&
                    declaringType.StartsWith("<", StringComparison.Ordinal) &&
                    declaringType.IndexOf(">d__", StringComparison.Ordinal) >= 0)
                {
                    return StitchSyncFrameKind.V1StateMachineMoveNext;
                }
            }

            if (!isHostModule || !IsRuntimeCompilerServicesMethod(frameName))
            {
                return StitchSyncFrameKind.None;
            }

            string builderType = GetDeclaringTypeName(frameName);
            if (builderType is null)
            {
                return StitchSyncFrameKind.None;
            }

            int genericArity = builderType.IndexOf('`');
            if (genericArity >= 0)
            {
                builderType = builderType.Substring(0, genericArity);
            }

            if (method == "SetExistingTaskResult" && builderType == "AsyncTaskMethodBuilder")
            {
                return StitchSyncFrameKind.V1MethodBuilderCompletion;
            }

            if (method != "Start")
            {
                return StitchSyncFrameKind.None;
            }

            switch (builderType)
            {
                case "AsyncMethodBuilderCore":
                case "AsyncTaskMethodBuilder":
                case "AsyncValueTaskMethodBuilder":
                case "PoolingAsyncValueTaskMethodBuilder":
                case "AsyncVoidMethodBuilder":
                    return StitchSyncFrameKind.V1MethodBuilderStart;
                default:
                    return StitchSyncFrameKind.None;
            }
        }

        /// <summary>
        /// Classifies a frame name as an async dispatch boundary. <paramref name="frameName"/> may be either a
        /// full TraceEvent frame name (<c>module!Namespace.Type.Method</c>, optionally with an optimization-tier
        /// prefix and/or a parameter signature) or a bare <c>Namespace.Type.Method</c> full method name; only
        /// the bare method-name component is examined.
        /// </summary>
        /// <param name="frameName">The frame/method name to classify. Null/empty yields
        /// <see cref="AsyncStitchBoundaryKind.None"/>.</param>
        /// <param name="wrapperIndex">On a <see cref="AsyncStitchBoundaryKind.V2ContinuationWrapper"/> result,
        /// the wrapper index (0..<see cref="WrapperPoolCount"/>-1); otherwise -1.</param>
        /// <returns>The boundary kind, or <see cref="AsyncStitchBoundaryKind.None"/>.</returns>
        public static AsyncStitchBoundaryKind Classify(string frameName, out int wrapperIndex)
        {
            wrapperIndex = -1;
            string method = GetBareMethodName(frameName);
            if (method is null || method.Length == 0)
            {
                return AsyncStitchBoundaryKind.None;
            }

            if (TryGetWrapperIndex(method, out wrapperIndex))
            {
                return AsyncStitchBoundaryKind.V2ContinuationWrapper;
            }

            if (method == DispatchContinuationsName || method == InstrumentedDispatchContinuationsName)
            {
                return AsyncStitchBoundaryKind.V2DispatchContinuation;
            }

            if (method == MoveNextAsDispatcherName)
            {
                return AsyncStitchBoundaryKind.V1Dispatcher;
            }

            // The non-merged (standalone) V1 dispatcher is AsyncStateMachineDispatcher.MoveNext. Its bare method
            // name ("MoveNext") is shared by every state machine, so it is only a boundary when declared on the
            // async-state-machine-dispatcher type.
            if (method == AsyncStateMachineDispatcherMethodName &&
                GetDeclaringTypeName(frameName) == AsyncStateMachineDispatcherTypeName)
            {
                return AsyncStitchBoundaryKind.V1Dispatcher;
            }

            if (IsV1DispatcherInfrastructure(frameName))
            {
                return AsyncStitchBoundaryKind.V1DispatcherInfrastructure;
            }

            return AsyncStitchBoundaryKind.None;
        }

        private static bool IsV1DispatcherInfrastructure(string frameName)
        {
            int qualifiedStart = frameName.IndexOf('!');
            qualifiedStart = qualifiedStart >= 0 ? qualifiedStart + 1 : 0;
            const string namespacePrefix = "System.Runtime.CompilerServices.";
            if (frameName.Length - qualifiedStart < namespacePrefix.Length ||
                string.CompareOrdinal(frameName, qualifiedStart, namespacePrefix, 0, namespacePrefix.Length) != 0)
            {
                return false;
            }

            string declaringType = GetDeclaringTypeName(frameName);
            if (declaringType is null)
            {
                return false;
            }

            int genericArity = declaringType.IndexOf('`');
            if (genericArity >= 0)
            {
                declaringType = declaringType.Substring(0, genericArity);
            }

            return declaringType.EndsWith(AsyncStateMachineBoxTypeSuffix, StringComparison.Ordinal) ||
                   declaringType == AsyncStateMachineDispatcherTypeName;
        }

        private static bool IsHostModuleQualifiedFrame(string frameName)
        {
            const string prefix = HostModuleName + "!";
            return frameName != null && frameName.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool IsRuntimeCompilerServicesMethod(string frameName)
        {
            int qualifiedStart = IsHostModuleQualifiedFrame(frameName)
                ? HostModuleName.Length + 1
                : 0;
            const string namespacePrefix = "System.Runtime.CompilerServices.";
            return frameName.Length - qualifiedStart >= namespacePrefix.Length &&
                   string.CompareOrdinal(
                       frameName, qualifiedStart, namespacePrefix, 0, namespacePrefix.Length) == 0;
        }

        /// <summary>
        /// If <paramref name="method"/> is a continuation-wrapper method name
        /// (<c>Continuation_Wrapper_&lt;n&gt;</c>), returns true and sets <paramref name="wrapperIndex"/> to
        /// <c>n</c> (0..<see cref="WrapperPoolCount"/>-1). <paramref name="method"/> is the bare method-name
        /// component (see <see cref="GetBareMethodName"/>).
        /// </summary>
        public static bool TryGetWrapperIndex(string method, out int wrapperIndex)
        {
            wrapperIndex = -1;
            if (method is null || !method.StartsWith(ContinuationWrapperPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string suffix = method.Substring(ContinuationWrapperPrefix.Length);
            if (suffix.Length == 0 ||
                !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                (uint)index >= (uint)WrapperPoolCount)
            {
                return false;
            }

            wrapperIndex = index;
            return true;
        }

        /// <summary>
        /// Extracts the bare method-name component from a frame or full method name. Strips the
        /// <c>module!</c> prefix, any parameter signature (<c>(...)</c>), and any namespace/type qualification,
        /// returning the substring after the final <c>'.'</c>. Returns the input unchanged if it has no
        /// qualification, or null/empty for null/empty input.
        /// </summary>
        /// <example><c>"System.Private.CoreLib!...AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_0"</c>
        /// =&gt; <c>"Continuation_Wrapper_0"</c>.</example>
        public static string GetBareMethodName(string frameName)
        {
            if (frameName is null || frameName.Length == 0)
            {
                return frameName;
            }

            // Drop a parameter signature if present: keep everything before the first '('.
            int paren = frameName.IndexOf('(');
            int end = paren >= 0 ? paren : frameName.Length;
            if (end <= 0)
            {
                return string.Empty;
            }

            // The method name is the token after the last '.' (namespace/type separator) that precedes 'end'.
            // Nested types may render with '+' in TraceEvent names, but the type/method boundary is always '.'.
            int lastDot = frameName.LastIndexOf('.', end - 1, end);
            int start;
            if (lastDot >= 0)
            {
                start = lastDot + 1;
            }
            else
            {
                // No '.', but the module '!' separator (if any) still qualifies the token.
                int bang = frameName.LastIndexOf('!', end - 1, end);
                start = bang >= 0 ? bang + 1 : 0;
            }

            return frameName.Substring(start, end - start);
        }

        /// <summary>
        /// Extracts the declaring-type simple name from a frame or full method name: the token between the last two
        /// separators that precede the method name (a namespace <c>'.'</c> or a nested-type <c>'+'</c>). Strips the
        /// <c>module!</c> prefix and any parameter signature. Returns null if there is no type qualification (e.g. a
        /// bare method name).
        /// </summary>
        /// <example><c>"...!System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext"</c> =&gt;
        /// <c>"AsyncStateMachineDispatcher"</c>.</example>
        public static string GetDeclaringTypeName(string frameName)
        {
            if (frameName is null || frameName.Length == 0)
            {
                return null;
            }

            int paren = frameName.IndexOf('(');
            int end = paren >= 0 ? paren : frameName.Length;
            if (end <= 0)
            {
                return null;
            }

            // The method name is the token after the final '.' before 'end'; the type is the token before that '.'.
            int methodDot = frameName.LastIndexOf('.', end - 1, end);
            if (methodDot <= 0)
            {
                return null; // no type qualifier (bare method name)
            }

            int typeEnd = methodDot;
            // The type's simple name starts after the preceding namespace '.' or nested-type '+' (or module '!').
            // Ignore separators inside constructed generic arguments, such as the '+' in
            // AsyncStateMachineBox`1[System.Int64,MyType+<Method>d__1].
            int typeStart = 0;
            int bracketDepth = 0;
            for (int i = typeEnd - 1; i >= 0; i--)
            {
                char c = frameName[i];
                if (c == ']')
                {
                    bracketDepth++;
                }
                else if (c == '[')
                {
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                }
                else if (bracketDepth == 0 && (c == '.' || c == '+' || c == '!'))
                {
                    typeStart = i + 1;
                    break;
                }
            }

            return typeStart < typeEnd ? frameName.Substring(typeStart, typeEnd - typeStart) : null;
        }
    }

    /// <summary>
    /// A per-trace cache that maps a method (by <c>MethodIndex</c>, i.e. TraceEvent's per-method "IP range"
    /// bucket) to its <see cref="AsyncStitchBoundaryInfo"/>. The runtime async boundary methods are found once,
    /// by name, over the symbolized methods table (<see cref="AsyncStitchBoundary"/> is applied a single time
    /// per candidate method); the per-frame stitch hot path is then an O(1) <c>MethodIndex</c> dictionary
    /// lookup with no string work.
    /// <para>
    /// Because all boundary methods live in <c>System.Private.CoreLib</c>, the scan is scoped to that module:
    /// the recognizer (and the <c>FullMethodName</c> access) is only invoked for methods whose module is SPC,
    /// so methods in every other module are skipped with a cheap <c>ModuleFileIndex</c> set check.
    /// </para>
    /// <para>
    /// Build it after the <see cref="TraceLog"/>'s managed symbols are resolved (e.g. as part of the stitching
    /// pass). The scan is lazy: it runs on the first classify call.
    /// </para>
    /// </summary>
    public sealed class AsyncStitchBoundaryCache
    {
        private readonly TraceCodeAddresses _codeAddresses;

        // Only recognized boundary/infrastructure methods are stored; any absent MethodIndex is None.
        private readonly Dictionary<MethodIndex, AsyncStitchBoundaryInfo> _boundaries = new Dictionary<MethodIndex, AsyncStitchBoundaryInfo>();
        private bool _built;

        /// <summary>Creates a cache over the methods of <paramref name="codeAddresses"/> (typically
        /// <c>traceLog.CodeAddresses</c>).</summary>
        public AsyncStitchBoundaryCache(TraceCodeAddresses codeAddresses)
        {
            _codeAddresses = codeAddresses ?? throw new ArgumentNullException(nameof(codeAddresses));
        }

        /// <summary>The number of distinct methods classified as async boundaries or dispatcher infrastructure
        /// (valid after the first classify call, which triggers the one-time scan).</summary>
        public int BoundaryMethodCount
        {
            get
            {
                EnsureBuilt();
                return _boundaries.Count;
            }
        }

        /// <summary>Classifies the method containing <paramref name="codeAddressIndex"/>. Returns
        /// <see cref="AsyncStitchBoundaryInfo.None"/> for an invalid or unresolved code address.</summary>
        public AsyncStitchBoundaryInfo Classify(CodeAddressIndex codeAddressIndex)
        {
            if (codeAddressIndex == CodeAddressIndex.Invalid)
            {
                return AsyncStitchBoundaryInfo.None;
            }

            return Classify(_codeAddresses.MethodIndex(codeAddressIndex));
        }

        /// <summary>Classifies method <paramref name="methodIndex"/>. Returns
        /// <see cref="AsyncStitchBoundaryInfo.None"/> for an invalid method.</summary>
        public AsyncStitchBoundaryInfo Classify(MethodIndex methodIndex)
        {
            if (methodIndex == MethodIndex.Invalid)
            {
                return AsyncStitchBoundaryInfo.None;
            }

            EnsureBuilt();
            return _boundaries.TryGetValue(methodIndex, out AsyncStitchBoundaryInfo info) ? info : AsyncStitchBoundaryInfo.None;
        }

        /// <summary>
        /// Scans the <c>System.Private.CoreLib</c> methods once, recording each async boundary method (by
        /// <c>MethodIndex</c>). The string recognizer runs a single time per SPC method; all other modules'
        /// methods are skipped by a cheap <c>ModuleFileIndex</c> check. Subsequent classify calls are pure
        /// dictionary lookups.
        /// </summary>
        private void EnsureBuilt()
        {
            if (_built)
            {
                return;
            }

            HashSet<ModuleFileIndex> hostModules = GetHostModules();
            if (hostModules.Count != 0)
            {
                TraceMethods methods = _codeAddresses.Methods;
                int count = methods.Count;
                for (int i = 0; i < count; i++)
                {
                    var methodIndex = (MethodIndex)i;
                    if (!hostModules.Contains(methods.MethodModuleFileIndex(methodIndex)))
                    {
                        continue; // not System.Private.CoreLib; skip before any string work
                    }

                    AsyncStitchBoundaryKind kind = AsyncStitchBoundary.Classify(methods.FullMethodName(methodIndex), out int wrapperIndex);
                    if (kind != AsyncStitchBoundaryKind.None)
                    {
                        _boundaries[methodIndex] = new AsyncStitchBoundaryInfo(kind, wrapperIndex);
                    }
                }
            }

            _built = true;
        }

        /// <summary>The set of <c>ModuleFileIndex</c> values whose module is <c>System.Private.CoreLib</c>
        /// (there may be more than one, e.g. the IL image plus an R2R native image).</summary>
        private HashSet<ModuleFileIndex> GetHostModules()
        {
            var result = new HashSet<ModuleFileIndex>();
            TraceModuleFiles moduleFiles = _codeAddresses.ModuleFiles;
            foreach (TraceModuleFile moduleFile in moduleFiles)
            {
                if (string.Equals(moduleFile.Name, AsyncStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(moduleFile.ModuleFileIndex);
                }
            }

            return result;
        }
    }
}
