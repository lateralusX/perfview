using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfAsyncScenarios;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Evidence.Phase("error", new { Error = e.Exception.ToString() });
            e.Handled = true;
            app.Shutdown(1);
        };
        return app.Run(new ScenarioWindow(options));
    }
}

internal sealed class ScenarioWindow : Window
{
    public ScenarioWindow(Options options)
    {
        _options = options;
        Title = "Native V2 WPF depth and repeated suspension";
        Width = 690;
        Height = 300;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Bare UI awaits, two gated leaf resumptions, bounded CPU work at every returning level.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _capture = new Button { Content = "Depth 8", Tag = "depth-08", Margin = new Thickness(0, 0, 0, 8) };
        _noCapture = new Button { Content = "Depth 32", Tag = "depth-32", Margin = new Thickness(0, 0, 0, 12) };
        _depth64 = new Button { Content = "Depth 64", Tag = "depth-64", Margin = new Thickness(0, 0, 0, 12) };
        _depth64.Click += OnScenarioClick;
        _capture.Click += OnScenarioClick;
        _noCapture.Click += OnScenarioClick;
        _status = new TextBlock { Text = "Waiting for the real WPF dispatcher.", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_capture);
        panel.Children.Add(_noCapture);
        panel.Children.Add(_depth64);
        panel.Children.Add(_status);
        Content = panel;
        ContentRendered += OnReady;
        Closing += (_, _) =>
        {
            if (_active)
            {
                Evidence.Phase("aborted", new { Reason = "Window closed while a scenario or flush wait was active." });
                Environment.ExitCode = 2;
                Application.Current.Shutdown(2);
            }
        };
    }

    private void OnReady(object? sender, EventArgs e)
    {
        _uiThread = Evidence.OsThreadId;
        AssertUiContext();
        Evidence.UiDispatcher = Dispatcher;
        Evidence.UiThreadId = _uiThread;
        Evidence.Phase("startup", new
        {
            Scenario = _options.AutoMode ?? "manual",
            _options.WorkMs,
            _options.FlushMs,
            _options.GateDelayMs,
            _options.StartFile,
            _options.SmokeStart,
            CommandLine = Environment.CommandLine,
            Framework = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Host = Evidence.Binary(Environment.ProcessPath!),
            CoreLib = Evidence.Binary(typeof(object).Assembly.Location),
            CoreClr = Evidence.LoadedNative("coreclr.dll"),
            PresentationFramework = Evidence.Binary(typeof(Window).Assembly.Location),
            PresentationCore = Evidence.Binary(typeof(Visual).Assembly.Location),
            WindowsBase = Evidence.Binary(typeof(Dispatcher).Assembly.Location),
            Producer = Evidence.Binary(typeof(Program).Assembly.Location),
            AsyncProfilerTypePresent = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.AsyncProfilerEventSource", false) != null,
            CompatibilityScope = "Actual coherent SDK11 CoreCLR/CoreLib/WindowsDesktop graph; not the earlier source-built12 testhost.",
            ValidationClaim = "Producer/context proof only until collected async records and public TraceEvent output are audited."
        });
        Runner.VerifyCodegen();
        Evidence.Phase("window.ready", new { ActualWindowVisible = IsVisible, DispatcherType = Dispatcher.GetType().FullName });
        _status.Text = $"Ready. PID {Environment.ProcessId}, UI OS thread {_uiThread}. Capture mode deliberately freezes this window for {_options.WorkMs} ms.";
        if (_options.AutoMode == null) return;
        if (_options.SmokeStart)
        {
            Evidence.Phase("attach.skipped", new { Reason = "Explicit producer smoke; no collector confirmation claimed." });
            QueueButton();
            return;
        }
        Evidence.Phase("attach.wait", new { Instruction = "Controller must confirm collection before creating the unique start file." });
        var poll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        poll.Tick += (_, _) =>
        {
            if (!File.Exists(_options.StartFile!)) return;
            poll.Stop();
            Evidence.Phase("attach.released", new { StartFile = _options.StartFile });
            QueueButton();
        };
        poll.Start();
    }

    private void QueueButton()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AssertUiContext();
            Evidence.Phase("button.dispatch", new { Mode = _options.AutoMode, ActualWindowVisible = IsVisible });
            Button button = _options.AutoMode == "depth-08" ? _capture : _options.AutoMode == "depth-32" ? _noCapture : _depth64;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        });
    }

    [RuntimeAsyncMethodGeneration(false)]
    private async void OnScenarioClick(object sender, RoutedEventArgs e)
    {
        AssertUiContext();
        if (_active) { Evidence.Phase("launch.rejected", new { Reason = "A depth run is active." }); return; }
        _active = true;
        _capture.IsEnabled = _noCapture.IsEnabled = _depth64.IsEnabled = false;
        string mode = (string)((Button)sender).Tag;
        _status.Text = "Bare UI awaits; bounded CPU work per returned level. Two leaf gates release without blocking the dispatcher.";
        var state = new RunState(mode, _options.WorkMs, Dispatcher, _uiThread);
        Evidence.Phase("scenario.begin", new { Scenario = mode, state.Depth, Rounds = 2, AwaitPolicy = "bare UI awaits at every native edge" });
        Task<WorkResult> root = mode == "depth-08" ? Runner.Depth08Level01Async(state) :
            mode == "depth-32" ? Runner.Depth32Level01Async(state) : Runner.Depth64Level01Async(state);
        if (root.IsCompleted || state.Gates.Any(g => g.Task.IsCompleted) || Volatile.Read(ref state.EnteredLevels) != state.Depth)
            throw new InvalidOperationException("Root and both gates must be incomplete after every intended level entered.");
        Evidence.Phase("suspension.verified", new
        {
            RootIncomplete = !root.IsCompleted,
            GateIncomplete = state.Gates.All(g => !g.Task.IsCompleted),
            EnteredLevels = Volatile.Read(ref state.EnteredLevels),
            ChildTasksStoredForInspection = false,
            RunContinuationsAsynchronously = true
        });
        var release = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
        int nextRound = 0;
        release.Tick += (_, _) =>
        {
            AssertUiContext();
            if (state.SuspendedRound != nextRound) return;
            if (root.IsCompleted || state.Gates[nextRound].Task.IsCompleted)
                throw new InvalidOperationException("Next closed gate/root suspension proof failed.");
            Evidence.Phase("gate.suspension.verified", new { Round = nextRound, RootIncomplete = true, GateIncomplete = true, state.EnteredLevels });
            Evidence.Phase("gate.delay.end");
            Evidence.Phase("gate.release", new { Round = nextRound, Mechanism = "Real WPF DispatcherTimer; no UI Task wait." });
            state.Gates[nextRound++].SetResult(true);
            if (nextRound == 2) release.Stop();
        };
        Evidence.Phase("gate.delay.begin", new { _options.GateDelayMs });
        release.Start();
        WorkResult result = await root.ConfigureAwait(false);
        Evidence.Phase("scenario.work.finished", new { Scenario = mode, result.Iterations, Checksum = result.Checksum.ToString("X16", CultureInfo.InvariantCulture) });
        Evidence.Phase("ui.update.queued", new { Purpose = "Status only; outside sampled work." });
        await Dispatcher.InvokeAsync(() =>
        {
            AssertUiContext();
            _status.Text = "Work finished. Idle flush wait; the dispatcher remains usable.";
            Evidence.Phase("ui.update.applied");
        }).Task.ConfigureAwait(false);
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            var idle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.FlushMs) };
            idle.Tick += (_, _) =>
            {
                idle.Stop();
                Evidence.Phase("flush.end");
                flushed.SetResult(true);
            };
            Evidence.Phase("flush.begin", new { _options.FlushMs, Kind = "Nonblocking dispatcher idle wait, not proof of buffer delivery." });
            idle.Start();
        }).Task.ConfigureAwait(false);
        await flushed.Task.ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            AssertUiContext();
            _active = false;
            _capture.IsEnabled = _noCapture.IsEnabled = _depth64.IsEnabled = true;
            _status.Text = "Complete. Select either control to run again.";
            Evidence.Phase("complete", new { Scenario = mode, ExitCode = 0 });
            if (_options.AutoClose) Close();
        }).Task.ConfigureAwait(false);
    }

    private void AssertUiContext()
    {
        if (!Dispatcher.CheckAccess() || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("A real WPF DispatcherSynchronizationContext on the UI thread is required.");
    }

    private readonly Options _options;
    private readonly Button _capture;
    private readonly Button _noCapture;
    private readonly Button _depth64;
    private readonly TextBlock _status;
    private bool _active;
    private uint _uiThread;
}

internal static partial class Runner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WorkResult DepthCpuLeaf(int workMs)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(workMs * (double)Stopwatch.Frequency / 1000);
        ulong sum = 0x9E3779B97F4A7C15;
        long iterations = 0;
        do
        {
            for (int i = 0; i < 4096; i++) sum = unchecked((sum ^ (sum >> 7)) * 0x100000001B3 + (uint)i);
            iterations += 4096;
        } while (Stopwatch.GetTimestamp() < deadline);
        return new WorkResult(sum, iterations);
    }

    internal static void VerifyCodegen()
    {
        var runtime = typeof(Runner).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Depth", StringComparison.Ordinal) && m.Name.EndsWith("Async", StringComparison.Ordinal)).Select(method =>
        {
            int flags = (int)method.GetMethodImplementationFlags();
            if ((flags & 0x2000) == 0 || method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                throw new InvalidOperationException($"{method.Name} is not genuine runtime-async V2.");
            byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
            return new { Method = method.Name, Flags = flags, RuntimeAsync0x2000 = true, IlHex = Convert.ToHexString(il), IlSha256 = Convert.ToHexString(SHA256.HashData(il)) };
        }).ToArray();
        if (runtime.Length != 104) throw new InvalidOperationException("Expected all104 generated native methods.");
        MethodInfo entry = typeof(ScenarioWindow).GetMethod("OnScenarioClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var classic = entry.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (((int)entry.GetMethodImplementationFlags() & 0x2000) != 0 || classic == null)
            throw new InvalidOperationException("The WPF event-entry wrapper must remain explicitly classic.");
        Evidence.Phase("codegen.verified", new
        {
            RuntimeMethods = runtime,
            WpfEventEntry = new { Method = entry.Name, RuntimeAsync0x2000 = false, ClassicStateMachine = classic.StateMachineType.FullName },
            Scope = "Classic WPF entry separate from explicit generated native depth chains."
        });
    }
}

internal sealed class RunState
{
    internal RunState(string mode, int workMs, Dispatcher dispatcher, uint uiThread)
    {
        Mode = mode; Depth = int.Parse(mode.Substring(6), CultureInfo.InvariantCulture); WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread;
    }
    internal void Enter(int level)
    {
        Interlocked.Increment(ref EnteredLevels);
        Evidence.Phase("level.enter", new { Level = level, Depth });
    }
    internal WorkResult AfterAwaitAndWork(int level, int round)
    {
        bool onUi = Dispatcher.CheckAccess() && Evidence.OsThreadId == UiThread;
        bool dispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
        Evidence.Phase("level.after-await", new { Mode, Level = level, Round = round, OnUi = onUi, DispatcherContext = dispatcherContext });
        if (!onUi || !dispatcherContext)
            throw new InvalidOperationException($"Await-context/thread assertion failed at level {level}.");
        string window = $"{Mode}-L{level:D2}-R{round}";
        string[] expected = Enumerable.Range(1, level).Reverse().Select(i => $"Depth{Depth:D2}Level{i:D2}Async").ToArray();
        Evidence.Phase("sampling.begin", new { Window = window, Leaf = "DepthCpuLeaf", WorkMs, Scenario = Mode, Level = level, Round = round, ExpectedMethods = expected });
        WorkResult result = Runner.DepthCpuLeaf(WorkMs);
        Evidence.Phase("sampling.end", new { Window = window, Level = level, Round = round, result.Iterations });
        return result;
    }
    internal string Mode { get; }
    internal int Depth { get; }
    internal int WorkMs { get; }
    internal Dispatcher Dispatcher { get; }
    internal uint UiThread { get; }
    internal TaskCompletionSource<bool>[] Gates { get; } = new[] { new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
    internal int SuspendedRound = -1;
    internal int EnteredLevels;
}

internal readonly record struct WorkResult(ulong Checksum, long Iterations);

internal sealed record Options(string? AutoMode, string? StartFile, bool SmokeStart, bool AutoClose, int WorkMs, int GateDelayMs, int FlushMs)
{
    internal static Options Parse(string[] args)
    {
        string? mode = null, start = null;
        bool smoke = false, close = false;
        int work = 50, delay = 250, flush = 3000;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--smoke-start") { smoke = true; continue; }
            if (arg == "--auto-close") { close = true; continue; }
            if (++i == args.Length) throw new ArgumentException("Missing option value.");
            string value = args[i];
            switch (arg)
            {
                case "--auto-mode": mode = value; break;
                case "--start-file": start = Path.GetFullPath(value); break;
                case "--work-ms": work = Milliseconds(value, 1); break;
                case "--gate-ms": delay = Milliseconds(value, 1); break;
                case "--flush-ms": flush = Milliseconds(value, 1); break;
                default: throw new ArgumentException($"Unknown option {arg}.");
            }
        }
        if (mode != null && mode != "depth-08" && mode != "depth-32" && mode != "depth-64")
            throw new ArgumentException("Select depth-08, depth-32 or depth-64.");
        if (mode != null && !smoke && (start == null || File.Exists(start)))
            throw new ArgumentException("Automated collection needs a new, initially absent --start-file.");
        if (mode == null && (smoke || close || start != null))
            throw new ArgumentException("Automation options require an explicit --auto-mode.");
        return new Options(mode, start, smoke, close, work, delay, flush);
    }
    private static int Milliseconds(string value, int minimum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result >= minimum && result <= 60000
            ? result : throw new ArgumentException("Milliseconds must be within the documented bounded range.");
}

internal static class Evidence
{
    internal static Dispatcher? UiDispatcher;
    internal static uint UiThreadId;
    internal static uint OsThreadId => GetCurrentThreadId();
    internal static void Phase(string phase, object? data = null)
    {
        lock (s_lock)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Phase = phase, Utc = DateTimeOffset.UtcNow, TimestampQpc = Stopwatch.GetTimestamp(),
                StopwatchFrequency = Stopwatch.Frequency, Pid = Environment.ProcessId,
                ManagedThreadId = Environment.CurrentManagedThreadId, OsThreadId,
                UiOsThreadId = UiThreadId, DispatcherAccess = UiDispatcher?.CheckAccess(),
                SynchronizationContext = SynchronizationContext.Current?.GetType().FullName,
                Data = data
            }));
            Console.Out.Flush();
        }
    }
    internal static object Binary(string path) => new
    {
        Path = path, FileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion,
        ProductVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
    };
    internal static object? LoadedNative(string name)
    {
        using Process process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
            if (module.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase)) return Binary(module.FileName);
        return null;
    }
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    private static readonly object s_lock = new();
}
