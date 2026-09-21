using System;

using BenchmarkDotNet.Running;

namespace TraceEventBenchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length == 2 &&
                string.Equals(args[0], "--async-profiler-peak-memory", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse(args[1], ignoreCase: true, out AsyncProfilerWorkloadProfile profile) ||
                    profile == AsyncProfilerWorkloadProfile.Realistic)
                {
                    throw new ArgumentException(
                        "Peak-memory profile must be High or Extreme.",
                        nameof(args));
                }

                AsyncProfilerConversionPeakMemory.Measure(profile);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
