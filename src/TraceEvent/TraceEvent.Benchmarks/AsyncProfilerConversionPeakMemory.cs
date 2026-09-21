using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace TraceEventBenchmarks
{
    internal static class AsyncProfilerConversionPeakMemory
    {
        public static void Measure(AsyncProfilerWorkloadProfile profile)
        {
            using (AsyncProfilerEndToEndFixture fixture =
                AsyncProfilerEndToEndFixture.Create(profile, AsyncProfilerKindMode.V2Only))
            {
                fixture.WriteNettrace(fixture.NetTracePath, includeAsyncProfilerData: true);
                CollectGarbage();

                using (var process = Process.GetCurrentProcess())
                using (var stop = new ManualResetEventSlim())
                {
                    process.Refresh();
                    var sampler = new PeakMemorySampler(
                        GC.GetTotalMemory(forceFullCollection: false),
                        process.PrivateMemorySize64,
                        process.WorkingSet64);
                    var thread = new Thread(() => sampler.SampleUntilStopped(process, stop))
                    {
                        IsBackground = true,
                        Name = "AsyncProfilerConversionPeakMemory",
                    };

                    var stopwatch = Stopwatch.StartNew();
                    thread.Start();
                    try
                    {
                        TraceLog.CreateFromEventPipeDataFile(fixture.NetTracePath, fixture.ConversionEtlxPath);
                    }
                    finally
                    {
                        stopwatch.Stop();
                        stop.Set();
                        thread.Join();
                    }

                    Console.WriteLine(
                        $"Async conversion peak memory: profile={profile}, contexts={fixture.ContextCount:N0}, " +
                        $"elapsed={stopwatch.Elapsed.TotalSeconds:F3}s, " +
                        $"managedHeap={sampler.ManagedHeapBaseline:N0}->{sampler.ManagedHeapPeak:N0} bytes " +
                        $"(delta={sampler.ManagedHeapPeak - sampler.ManagedHeapBaseline:N0}), " +
                        $"privateBytes={sampler.PrivateBytesBaseline:N0}->{sampler.PrivateBytesPeak:N0} bytes " +
                        $"(delta={sampler.PrivateBytesPeak - sampler.PrivateBytesBaseline:N0}), " +
                        $"workingSet={sampler.WorkingSetBaseline:N0}->{sampler.WorkingSetPeak:N0} bytes " +
                        $"(delta={sampler.WorkingSetPeak - sampler.WorkingSetBaseline:N0}), " +
                        $"etlx={new FileInfo(fixture.ConversionEtlxPath).Length:N0} bytes.");
                }
            }
        }

        private static void CollectGarbage()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private sealed class PeakMemorySampler
        {
            public PeakMemorySampler(long managedHeapBaseline, long privateBytesBaseline, long workingSetBaseline)
            {
                ManagedHeapBaseline = ManagedHeapPeak = managedHeapBaseline;
                PrivateBytesBaseline = PrivateBytesPeak = privateBytesBaseline;
                WorkingSetBaseline = WorkingSetPeak = workingSetBaseline;
            }

            public long ManagedHeapBaseline { get; }
            public long ManagedHeapPeak { get; private set; }
            public long PrivateBytesBaseline { get; }
            public long PrivateBytesPeak { get; private set; }
            public long WorkingSetBaseline { get; }
            public long WorkingSetPeak { get; private set; }

            public void SampleUntilStopped(Process process, ManualResetEventSlim stop)
            {
                while (!stop.IsSet)
                {
                    Sample(process);
                    stop.Wait(5);
                }
                Sample(process);
            }

            private void Sample(Process process)
            {
                long managedHeap = GC.GetTotalMemory(forceFullCollection: false);
                process.Refresh();
                long privateBytes = process.PrivateMemorySize64;
                long workingSet = process.WorkingSet64;

                if (managedHeap > ManagedHeapPeak) ManagedHeapPeak = managedHeap;
                if (privateBytes > PrivateBytesPeak) PrivateBytesPeak = privateBytes;
                if (workingSet > WorkingSetPeak) WorkingSetPeak = workingSet;
            }
        }
    }
}
