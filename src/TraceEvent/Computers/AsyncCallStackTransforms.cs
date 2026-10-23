// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Configures normalization, infrastructure cleanup, and annotations for reconstructed async call stacks.
    /// Configure before stack generation. These settings affect presentation, not structural reconstruction.
    /// </summary>
    public sealed class AsyncCallStackTransforms
    {
        /// <summary>
        /// Enables conservative normalization of verified startup, reentrant, completion-return, and handoff
        /// patterns. Enabled by default. Disabling this preserves structural async reconstruction.
        /// </summary>
        public bool EnableNormalization
        {
            get => m_pipeline.EnableNormalization;
            set => m_pipeline.EnableNormalization = value;
        }

        /// <summary>
        /// Enables additional presentation cleanup and overhead attribution for verified async runtime
        /// infrastructure. Disabled by default. This is not general framework filtering and is independent
        /// of <see cref="EnableNormalization"/>.
        /// </summary>
        public bool EnableInfrastructureCleanup
        {
            get => m_pipeline.EnableInfrastructureCleanup;
            set => m_pipeline.EnableInfrastructureCleanup = value;
        }

        /// <summary>
        /// Labels reconstructed async frame names with their V1/V2 kind and, where available, V1 presentation
        /// state. Disabled by default. Applied when frame names are emitted, without enabling additional
        /// reconstruction or cleanup.
        /// </summary>
        public bool EnableFrameAnnotations
        {
            get => m_pipeline.EnableFrameAnnotations;
            set => m_pipeline.EnableFrameAnnotations = value;
        }

        #region private

        internal AsyncCallStackTransforms(AsyncProfilerCallStackTransformPipeline pipeline)
        {
            m_pipeline = pipeline;
        }

        private readonly AsyncProfilerCallStackTransformPipeline m_pipeline;

        #endregion
    }
}
