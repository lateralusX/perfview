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
    /// async call stack (from <see cref="AsyncProfilerCallStacksIndex"/>) into a native sync stack, the stitcher walks
    /// the sync stack top-down (leaf to root) and consumes one async segment each time it crosses one of these
    /// boundaries. See the design note (callstack-stitching) for the full algorithm.
    /// </summary>
    internal enum AsyncProfilerCallStackStitchBoundaryKind
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
    /// The boundary classification of a single method: its <see cref="AsyncProfilerCallStackStitchBoundaryKind"/> plus, for a
    /// <see cref="AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper"/>, the wrapper index it encodes.
    /// </summary>
    internal readonly struct AsyncProfilerCallStackStitchBoundaryInfo
    {
        /// <summary>The classification of the method.</summary>
        public readonly AsyncProfilerCallStackStitchBoundaryKind Kind;

        /// <summary>For <see cref="AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper"/>, the wrapper index
        /// (0..<see cref="AsyncProfilerCallStackStitchBoundary.WrapperPoolCount"/>-1); otherwise -1.</summary>
        public readonly int WrapperIndex;

        public AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind kind, int wrapperIndex)
        {
            Kind = kind;
            WrapperIndex = wrapperIndex;
        }

        /// <summary>The "ordinary frame" classification (not a boundary).</summary>
        public static readonly AsyncProfilerCallStackStitchBoundaryInfo None = new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.None, -1);

        /// <summary>True if this method is an async dispatch boundary.</summary>
        public bool IsBoundary =>
            Kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper ||
            Kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation ||
            Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
    }

    /// <summary>
    /// Recognizes the async dispatch boundary methods on a native (CPU-sample) call stack by their method
    /// names. These names are a <b>contract</b> between the .NET runtime async profiler and this stitcher:
    /// the runtime marks each boundary method <c>[MethodImpl(MethodImplOptions.NoInlining)]</c> (so it always
    /// appears as its own frame with a stable name) and documents that external tools depend on the name.
    /// A reflection guard test in dotnet/runtime asserts these names and the NoInlining attribute hold.
    /// <para>
    /// This type does the (string-based) recognition; a per-trace <see cref="AsyncProfilerCallStackStitchBoundaryCache"/>
    /// resolves the names to <c>MethodIndex</c> values once so the per-frame stitch hot path avoids string
    /// compares entirely.
    /// </para>
    /// <para>
    /// Compiler-generated state-machine names are decoded here independently of captured runtime versions.
    /// CoreLib implementation-specific recognition belongs to <see cref="AsyncProfilerRuntimeRuleProfile"/>;
    /// the public classification APIs also expose its common rules for metadata-free callers.
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
    internal static class AsyncProfilerCallStackStitchBoundary
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

        /// <summary>The V1 async-state-machine-dispatcher method name (<c>AsyncStateMachineDispatcher.MoveNext</c>).
        /// Because this bare name is not unique (every state machine has a <c>MoveNext</c>), the dispatcher is
        /// recognized type-qualified via <see cref="AsyncStateMachineDispatcherTypeName"/>.</summary>
        public const string AsyncStateMachineDispatcherMethodName = "MoveNext";

        /// <summary>The short module name (no extension) that hosts all async boundary methods.</summary>
        public const string HostModuleName = "System.Private.CoreLib";

        /// <summary>
        /// Classifies synchronous frames used by async completion counting and presentation: generated V1
        /// state-machine methods, known CoreLib builders and awaiters, and Task/execution-context infrastructure
        /// shared by V1 and V2. These classifications are not dispatch boundaries; use <see cref="Classify"/>
        /// for structural boundary recognition. This metadata-free API uses the common runtime rules;
        /// production stack sources select a profile from captured CoreLib metadata before caching results.
        /// </summary>
        /// <param name="frameName">
        /// A method name, optionally including parameters. CoreLib infrastructure requires the
        /// <c>System.Private.CoreLib!</c> module prefix; generated V1 <c>MoveNext</c> methods do not.
        /// </param>
        public static AsyncProfilerStitchSyncFrameKind ClassifySynchronousFrame(string frameName) =>
            ClassifySynchronousFrame(frameName, IsHostModuleQualifiedFrame(frameName));

        /// <summary>
        /// Classifies an async-related synchronous frame when module ownership is supplied separately.
        /// This supports TraceEvent's split module/method model, where <c>TraceCodeAddress.ModuleName</c>
        /// and <c>FullMethodName</c> are separate.
        /// </summary>
        /// <param name="frameName">The qualified method name, optionally including a module prefix and parameters.</param>
        /// <param name="isHostModule">
        /// True when the method belongs to <see cref="HostModuleName"/>. Required for known runtime infrastructure,
        /// but not for generated application state-machine methods.
        /// </param>
        public static AsyncProfilerStitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(frameName, isHostModule);

        /// <summary>Recognizes a compiler-generated state machine using its frame and bare method names.</summary>
        internal static bool IsStateMachineMoveNext(string frameName, string method)
        {
            if (method != "MoveNext")
            {
                return false;
            }
            string declaringType = GetDeclaringTypeName(frameName);
            return declaringType != null &&
                   declaringType.StartsWith("<", StringComparison.Ordinal) &&
                   declaringType.IndexOf(">d__", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Classifies a frame name as an async dispatch boundary. <paramref name="frameName"/> may be either a
        /// full TraceEvent frame name (<c>module!Namespace.Type.Method</c>, optionally with an optimization-tier
        /// prefix and/or a parameter signature) or a bare <c>Namespace.Type.Method</c> full method name; only
        /// the bare method-name component is examined for contract boundaries. V1 dispatcher infrastructure
        /// uses the common runtime rules; production caches select these rules from captured runtime metadata.
        /// </summary>
        /// <param name="frameName">The frame/method name to classify. Null/empty yields
        /// <see cref="AsyncProfilerCallStackStitchBoundaryKind.None"/>.</param>
        /// <param name="wrapperIndex">On a <see cref="AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper"/> result,
        /// the wrapper index (0..<see cref="WrapperPoolCount"/>-1); otherwise -1.</param>
        /// <returns>The boundary kind, or <see cref="AsyncProfilerCallStackStitchBoundaryKind.None"/>.</returns>
        public static AsyncProfilerCallStackStitchBoundaryKind Classify(string frameName, out int wrapperIndex)
        {
            AsyncProfilerCallStackStitchBoundaryInfo info = AsyncProfilerRuntimeRuleProfile.Common.ClassifyBoundary(frameName);
            wrapperIndex = info.WrapperIndex;
            return info.Kind;
        }

        // Only these names are profiler contracts. Box/helper recognition belongs to a runtime rule profile.
        internal static AsyncProfilerCallStackStitchBoundaryKind ClassifyContractBoundary(string frameName, out int wrapperIndex)
        {
            wrapperIndex = -1;
            string method = GetBareMethodName(frameName);
            if (method is null || method.Length == 0)
            {
                return AsyncProfilerCallStackStitchBoundaryKind.None;
            }

            if (TryGetWrapperIndex(method, out wrapperIndex))
            {
                return AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper;
            }

            if (method == DispatchContinuationsName || method == InstrumentedDispatchContinuationsName)
            {
                return AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation;
            }

            if (method == MoveNextAsDispatcherName)
            {
                return AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
            }

            // The non-merged (standalone) V1 dispatcher is AsyncStateMachineDispatcher.MoveNext. Its bare method
            // name ("MoveNext") is shared by every state machine, so it is only a boundary when declared on the
            // async-state-machine-dispatcher type.
            if (method == AsyncStateMachineDispatcherMethodName &&
                GetDeclaringTypeName(frameName) == AsyncStateMachineDispatcherTypeName)
            {
                return AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
            }

            return AsyncProfilerCallStackStitchBoundaryKind.None;
        }

        internal static bool IsHostModuleQualifiedFrame(string frameName)
        {
            const string prefix = HostModuleName + "!";
            return frameName != null && frameName.StartsWith(prefix, StringComparison.Ordinal);
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

        internal static string GetStateMachineTypeName(string methodName)
        {
            if (GetBareMethodName(methodName) != "MoveNext")
            {
                return null;
            }

            int paren = methodName.IndexOf('(');
            int end = paren >= 0 ? paren : methodName.Length;
            int methodDot = methodName.LastIndexOf('.', end - 1, end);
            int typeStart = methodName.IndexOf('!') + 1;
            return methodDot > typeStart
                ? methodName.Substring(typeStart, methodDot - typeStart)
                : null;
        }

        /// <summary>
        /// Converts a compiler-generated V1 state-machine method name such as
        /// <c>Namespace.Type+&lt;FooAsync&gt;d__5.MoveNext</c> to <c>Namespace.Type.FooAsync</c>.
        /// </summary>
        public static bool TryGetLogicalStateMachineMethodName(string methodName, out string logicalName)
        {
            return TryGetLogicalStateMachineMethodName(
                methodName, out logicalName, out _);
        }

        /// <summary>
        /// Converts a compiler-generated V1 state-machine method name to its logical source method and returns
        /// the generated state-machine suffix, such as <c>d__5</c>, for unambiguous fallback presentation.
        /// </summary>
        public static bool TryGetLogicalStateMachineMethodName(
            string methodName,
            out string logicalName,
            out string stateMachineSuffix)
        {
            logicalName = null;
            stateMachineSuffix = null;
            if (string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            int moveNext = methodName.IndexOf(".MoveNext", StringComparison.Ordinal);
            if (moveNext < 0)
            {
                return false;
            }

            int stateMachineSuffixStart = methodName.LastIndexOf(">d__", moveNext, StringComparison.Ordinal);
            if (stateMachineSuffixStart < 0)
            {
                stateMachineSuffixStart = methodName.LastIndexOf(">d", moveNext, StringComparison.Ordinal);
            }
            if (stateMachineSuffixStart < 0)
            {
                return false;
            }

            int methodStart = methodName.LastIndexOf("+<", stateMachineSuffixStart, StringComparison.Ordinal);
            if (methodStart < 0 || methodStart + 2 >= stateMachineSuffixStart)
            {
                return false;
            }

            string declaringType = methodName.Substring(0, methodStart).Replace('+', '.');
            string sourceMethod = methodName.Substring(
                methodStart + 2,
                stateMachineSuffixStart - methodStart - 2);
            logicalName = declaringType + "." + sourceMethod;
            stateMachineSuffix = methodName.Substring(
                stateMachineSuffixStart + 1,
                moveNext - stateMachineSuffixStart - 1);
            return true;
        }

        /// <summary>
        /// Returns a stitching-only view of a method name with perfmap's type/method separator replaced by
        /// rundown's separator. Stored/displayed symbols and parameter type spellings remain unchanged.
        /// </summary>
        internal static string NormalizeMethodName(string methodName)
        {
            if (string.IsNullOrEmpty(methodName))
            {
                return methodName;
            }

            int parameters = methodName.IndexOf('(');
            int nameEnd = parameters >= 0 ? parameters : methodName.Length;
            int separator = nameEnd > 0
                ? methodName.LastIndexOf("::", nameEnd - 1, nameEnd, StringComparison.Ordinal)
                : -1;
            return separator >= 0
                ? methodName.Substring(0, separator) + "." + methodName.Substring(separator + 2)
                : methodName;
        }
    }

    /// <summary>
    /// A per-trace cache that maps a method (by <c>MethodIndex</c>, i.e. TraceEvent's per-method "IP range"
    /// bucket) to its <see cref="AsyncProfilerCallStackStitchBoundaryInfo"/>. The runtime async boundary methods are found once,
    /// by name, over the symbolized methods table (<see cref="AsyncProfilerCallStackStitchBoundary"/> is applied a single time
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
    internal sealed class AsyncProfilerCallStackStitchBoundaryCache
    {
        /// <summary>Creates a cache over the methods of <paramref name="codeAddresses"/> (typically
        /// <c>traceLog.CodeAddresses</c>).</summary>
        public AsyncProfilerCallStackStitchBoundaryCache(TraceCodeAddresses codeAddresses)
            : this(codeAddresses, new AsyncProfilerRuntimeRuleProfileCache(
                (codeAddresses ?? throw new ArgumentNullException(nameof(codeAddresses))).ModuleFiles))
        {
        }

        internal AsyncProfilerCallStackStitchBoundaryCache(TraceCodeAddresses codeAddresses, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            _codeAddresses = codeAddresses ?? throw new ArgumentNullException(nameof(codeAddresses));
            _runtimeProfiles = runtimeProfiles ?? throw new ArgumentNullException(nameof(runtimeProfiles));
        }

        internal AsyncProfilerRuntimeRuleProfileCache RuntimeProfiles => _runtimeProfiles;
        internal TraceLog TraceLog => _codeAddresses.ModuleFiles.Log;
        internal AsyncProfilerRuntimeRuleProfile GetRuntimeProfile(CodeAddressIndex codeAddress) =>
            codeAddress == CodeAddressIndex.Invalid ? AsyncProfilerRuntimeRuleProfile.Common :
                _runtimeProfiles.GetProfile(_codeAddresses.ModuleFileIndex(codeAddress));

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
        /// <see cref="AsyncProfilerCallStackStitchBoundaryInfo.None"/> for an invalid or unresolved code address.</summary>
        public AsyncProfilerCallStackStitchBoundaryInfo Classify(CodeAddressIndex codeAddressIndex)
        {
            if (codeAddressIndex == CodeAddressIndex.Invalid)
            {
                return AsyncProfilerCallStackStitchBoundaryInfo.None;
            }

            return Classify(_codeAddresses.MethodIndex(codeAddressIndex));
        }

        /// <summary>Classifies method <paramref name="methodIndex"/>. Returns
        /// <see cref="AsyncProfilerCallStackStitchBoundaryInfo.None"/> for an invalid method.</summary>
        public AsyncProfilerCallStackStitchBoundaryInfo Classify(MethodIndex methodIndex)
        {
            if (methodIndex == MethodIndex.Invalid)
            {
                return AsyncProfilerCallStackStitchBoundaryInfo.None;
            }

            EnsureBuilt();
            return _boundaries.TryGetValue(methodIndex, out AsyncProfilerCallStackStitchBoundaryInfo info) ? info : AsyncProfilerCallStackStitchBoundaryInfo.None;
        }

        #region private

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

            Dictionary<ModuleFileIndex, AsyncProfilerRuntimeRuleProfile> hostModules = GetHostModules();
            if (hostModules.Count != 0)
            {
                TraceMethods methods = _codeAddresses.Methods;
                int count = methods.Count;
                for (int i = 0; i < count; i++)
                {
                    var methodIndex = (MethodIndex)i;
                    ModuleFileIndex moduleIndex = methods.MethodModuleFileIndex(methodIndex);
                    if (!hostModules.TryGetValue(moduleIndex, out AsyncProfilerRuntimeRuleProfile profile))
                    {
                        continue; // not System.Private.CoreLib; skip before any string work
                    }

                    string name = AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(methods.FullMethodName(methodIndex));
                    AsyncProfilerCallStackStitchBoundaryInfo info = profile.ClassifyBoundary(name);
                    if (info.Kind != AsyncProfilerCallStackStitchBoundaryKind.None)
                    {
                        _boundaries[methodIndex] = info;
                    }
                }
            }

            _built = true;
        }

        /// <summary>Profiles for captured <c>System.Private.CoreLib</c> modules
        /// (there may be more than one, e.g. the IL image plus an R2R native image).</summary>
        private Dictionary<ModuleFileIndex, AsyncProfilerRuntimeRuleProfile> GetHostModules()
        {
            var result = new Dictionary<ModuleFileIndex, AsyncProfilerRuntimeRuleProfile>();
            TraceModuleFiles moduleFiles = _codeAddresses.ModuleFiles;
            foreach (TraceModuleFile moduleFile in moduleFiles)
            {
                if (_runtimeProfiles.IsHostModule(moduleFile.ModuleFileIndex))
                {
                    result.Add(moduleFile.ModuleFileIndex, _runtimeProfiles.GetProfile(moduleFile.ModuleFileIndex));
                }
            }

            return result;
        }

        private readonly TraceCodeAddresses _codeAddresses;
        private readonly AsyncProfilerRuntimeRuleProfileCache _runtimeProfiles;
        // Only recognized boundary/infrastructure methods are stored; any absent MethodIndex is None.
        private readonly Dictionary<MethodIndex, AsyncProfilerCallStackStitchBoundaryInfo> _boundaries =
            new Dictionary<MethodIndex, AsyncProfilerCallStackStitchBoundaryInfo>();
        private bool _built;

        #endregion
    }
}
