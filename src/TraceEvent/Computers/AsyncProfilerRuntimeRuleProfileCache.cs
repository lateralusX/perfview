// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Selects async classification profiles once per captured CoreLib module. Loaded engine modules supply
    /// runtime flavor without replaying events or consulting local binaries. A module shared by processes with
    /// different or unknown flavors uses common rules, as does a process containing conflicting engine flavors.
    /// </summary>
    internal sealed class AsyncProfilerRuntimeRuleProfileCache
    {
        public AsyncProfilerRuntimeRuleProfileCache(TraceModuleFiles moduleFiles)
        {
            m_moduleFiles = moduleFiles ?? throw new ArgumentNullException(nameof(moduleFiles));
        }

        /// <summary>Gets the rules for a captured module, including a native image linked to its IL module.</summary>
        public AsyncProfilerRuntimeRuleProfile GetProfile(ModuleFileIndex moduleIndex)
        {
            if (m_profiles.TryGetValue(moduleIndex, out AsyncProfilerRuntimeRuleProfile profile))
            {
                return profile;
            }
            TraceModuleFile module = m_moduleFiles[moduleIndex];
            if (!IsHostModule(module))
            {
                m_profiles.Add(moduleIndex, AsyncProfilerRuntimeRuleProfile.Common);
                return AsyncProfilerRuntimeRuleProfile.Common;
            }
            module = module.ManagedModule ?? module;
            ModuleFileIndex canonicalIndex = module.ModuleFileIndex;
            if (!m_profiles.TryGetValue(canonicalIndex, out profile))
            {
                if (m_runtimeFlavors == null)
                {
                    m_runtimeFlavors = GetRuntimeFlavors();
                }
                m_runtimeFlavors.TryGetValue(canonicalIndex, out RuntimeSku flavor);
                profile = AsyncProfilerRuntimeRuleProfile.Select(module.FileVersion, module.ProductVersion, flavor);
                m_profiles.Add(canonicalIndex, profile);
            }
            if (moduleIndex != canonicalIndex)
            {
                m_profiles.Add(moduleIndex, profile);
            }
            return profile;
        }

        /// <summary>
        /// Selects whole presentation stages from a captured process, including samples whose runtime frames
        /// were removed structurally. Conflicting CoreLib profiles never select one version arbitrarily.
        /// </summary>
        public AsyncProfilerRuntimeRuleProfile GetProcessProfile(ProcessIndex processIndex)
        {
            if (processIndex == ProcessIndex.Invalid)
            {
                return AsyncProfilerRuntimeRuleProfile.Common;
            }
            if (!m_processProfiles.TryGetValue(processIndex, out AsyncProfilerRuntimeRuleProfile profile))
            {
                foreach (TraceLoadedModule loaded in m_moduleFiles.Log.Processes[processIndex].LoadedModules)
                {
                    if (!IsHostModule(loaded.ModuleFile))
                    {
                        continue;
                    }
                    AsyncProfilerRuntimeRuleProfile current = GetProfile(loaded.ModuleFile.ModuleFileIndex);
                    if (profile != null && !ReferenceEquals(profile, current))
                    {
                        profile = AsyncProfilerRuntimeRuleProfile.Common;
                        break;
                    }
                    profile = current;
                }
                profile = profile ?? AsyncProfilerRuntimeRuleProfile.Common;
                m_processProfiles.Add(processIndex, profile);
            }
            return profile;
        }

        /// <summary>Checks captured module identity rather than accepting an untrusted method-name prefix.</summary>
        public static bool IsHostModule(TraceModuleFile module) =>
            module != null &&
            (string.Equals(module.Name, AsyncProfilerCallStackStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase) ||
             (module.ManagedModule != null &&
              string.Equals(module.ManagedModule.Name, AsyncProfilerCallStackStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase)));

        #region private

        private Dictionary<ModuleFileIndex, RuntimeSku> GetRuntimeFlavors()
        {
            var result = new Dictionary<ModuleFileIndex, RuntimeSku>();
            foreach (TraceProcess process in m_moduleFiles.Log.Processes)
            {
                RuntimeSku flavor = RuntimeSku.None;
                foreach (TraceLoadedModule loaded in process.LoadedModules)
                {
                    flavor |= GetEngineFlavor(loaded.Name);
                }
                if (flavor != RuntimeSku.CoreClr && flavor != RuntimeSku.Mono && flavor != RuntimeSku.DesktopClr)
                {
                    flavor = RuntimeSku.None;
                }

                foreach (TraceLoadedModule loaded in process.LoadedModules)
                {
                    TraceModuleFile module = loaded.ModuleFile;
                    if (!IsHostModule(module))
                    {
                        continue;
                    }
                    ModuleFileIndex index = (module.ManagedModule ?? module).ModuleFileIndex;
                    if (result.TryGetValue(index, out RuntimeSku existing) && existing != flavor)
                    {
                        // Do not let another process's known runtime override an unknown or conflicting owner.
                        result[index] = RuntimeSku.None;
                    }
                    else
                    {
                        result[index] = flavor;
                    }
                }
            }
            return result;
        }

        private static RuntimeSku GetEngineFlavor(string moduleName)
        {
            // TraceModuleFile.Name strips Windows extensions but retains Unix shared-library extensions.
            if (moduleName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                moduleName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                moduleName = moduleName.Substring(0, moduleName.LastIndexOf('.'));
            }
            if (string.Equals(moduleName, "coreclr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moduleName, "libcoreclr", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeSku.CoreClr;
            }
            if (string.Equals(moduleName, "mono-2.0-sgen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moduleName, "libmonosgen-2.0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moduleName, "libmonobdwgc-2.0", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeSku.Mono;
            }
            if (string.Equals(moduleName, "clr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moduleName, "mscorwks", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeSku.DesktopClr;
            }
            return RuntimeSku.None;
        }

        private readonly TraceModuleFiles m_moduleFiles;
        private readonly Dictionary<ModuleFileIndex, AsyncProfilerRuntimeRuleProfile> m_profiles =
            new Dictionary<ModuleFileIndex, AsyncProfilerRuntimeRuleProfile>();
        private readonly Dictionary<ProcessIndex, AsyncProfilerRuntimeRuleProfile> m_processProfiles =
            new Dictionary<ProcessIndex, AsyncProfilerRuntimeRuleProfile>();
        private Dictionary<ModuleFileIndex, RuntimeSku> m_runtimeFlavors;

        #endregion
    }
}
