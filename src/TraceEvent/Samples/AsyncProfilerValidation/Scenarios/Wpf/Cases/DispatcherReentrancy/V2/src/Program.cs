using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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
        Title = "WPF native async controlled nested dispatcher";
        Width = 690;
        Height = 300;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "A resumes, pumps one owned nested dispatcher frame while B suspends/resumes inside it, then A continues.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _capture = new Button { Content = "Run A / nested B / A", Tag = "nested-ui", Margin = new Thickness(0, 0, 0, 8) };
        _noCapture = new Button { Content = "Repeat the same controlled nested case", Tag = "nested-ui", Margin = new Thickness(0, 0, 0, 12) };
        _capture.Click += OnScenarioClick;
        _noCapture.Click += OnScenarioClick;
        _status = new TextBlock { Text = "Waiting for the real WPF dispatcher.", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_capture);
        panel.Children.Add(_noCapture);
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
            Button button = _capture;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        });
    }

    [RuntimeAsyncMethodGeneration(false)]
    private async void OnScenarioClick(object sender, RoutedEventArgs e)
    {
        AssertUiContext();
        if (_active) throw new InvalidOperationException("Overlapping runs are not allowed.");
        _active = true;
        _capture.IsEnabled = _noCapture.IsEnabled = false;
        string mode = (string)((Button)sender).Tag;
        _status.Text = "A-before, nested B, A-after: bounded CPU on one UI thread. Nested pump owns its own frame/watchdog.";
        var state = new RunState("a", true, _options.WorkMs, Dispatcher, _uiThread, _options.GateDelayMs);
        Evidence.Phase("scenario.begin", new { Scenario = mode, CaptureUi = state.CaptureUi,
            AwaitPolicy = "bare UI awaits in both native chains" });
        Task<WorkResult> root = Runner.ReentrantALevel1Async(state);
        if (root.IsCompleted || state.Gate.Task.IsCompleted || Volatile.Read(ref state.EnteredLevels) != 7)
            throw new InvalidOperationException("Root and gate must be incomplete after all three levels enter their awaited call chain.");
        Evidence.Phase("suspension.verified", new
        {
            RootIncomplete = !root.IsCompleted,
            GateIncomplete = !state.Gate.Task.IsCompleted,
            EnteredLevels = Volatile.Read(ref state.EnteredLevels),
            ChildTasksStoredForInspection = false,
            RunContinuationsAsynchronously = true
        });
        var release = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
        release.Tick += (_, _) =>
        {
            release.Stop();
            AssertUiContext();
            Evidence.Phase("gate.delay.end");
            Evidence.Phase("gate.release", new { Mechanism = "Real WPF DispatcherTimer; UI is not blocked waiting for the gate." });
            state.Gate.SetResult(true);
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
            _capture.IsEnabled = _noCapture.IsEnabled = true;
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
    private readonly TextBlock _status;
    private bool _active;
    private uint _uiThread;
}

internal static class Runner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> ReentrantALevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await ReentrantALevel2Async(state);
        state.AssertAfterAwait(1);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> ReentrantALevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await ReentrantALevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> ReentrantALevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        await state.Gate.Task;
        state.AssertAfterAwait(3);
        state.WorkBegin("a-before", nameof(ABeforeCpuLeaf), nameof(ReentrantALevel3Async), nameof(ReentrantALevel2Async), nameof(ReentrantALevel1Async));
        WorkResult before = ABeforeCpuLeaf(state.WorkMs);
        state.WorkEnd("a-before", before);
        RunOwnedNestedDispatcher(state);
        state.AssertAfterAwait(3);
        state.WorkBegin("a-after", nameof(AAfterCpuLeaf), nameof(ReentrantALevel3Async), nameof(ReentrantALevel2Async), nameof(ReentrantALevel1Async));
        WorkResult after = AAfterCpuLeaf(state.WorkMs);
        state.WorkEnd("a-after", after);
        return after;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> NestedBLevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await NestedBLevel2Async(state);
        state.AssertAfterAwait(1);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> NestedBLevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await NestedBLevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> NestedBLevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        await state.Gate.Task;
        state.AssertAfterAwait(3);
        if (state.Pump == null || !state.Pump.Active) throw new InvalidOperationException("B must resume while A's owned nested frame remains active.");
        state.WorkBegin("b-nested", nameof(NestedBCpuLeaf), nameof(NestedBLevel3Async), nameof(NestedBLevel2Async), nameof(NestedBLevel1Async));
        WorkResult result = NestedBCpuLeaf(state.WorkMs);
        state.WorkEnd("b-nested", result);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult ABeforeCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult NestedBCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult AAfterCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOwnedNestedDispatcher(RunState a)
    {
        a.AssertAfterAwait(3);
        var pump = new NestedPump(a);
        a.Pump = pump;
        DispatcherOperation? bootstrap = null, startB = null;
        var watchdog = new DispatcherTimer(DispatcherPriority.Send, a.Dispatcher) { Interval = TimeSpan.FromSeconds(10) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            pump.Failure = ExceptionDispatchInfo.Capture(new TimeoutException("Owned nested dispatcher exceeded its bounded B lifecycle."));
            Evidence.Phase("pump.timeout");
            pump.Frame.Continue = false;
        };
        try
        {
            pump.Active = true;
            Evidence.Phase("pump.enter", new { OwnedFrame = true, UiThread = a.UiThread });
            bootstrap = a.Dispatcher.BeginInvoke(() =>
            {
                a.AssertAfterAwait(3);
                if (!pump.Active) throw new InvalidOperationException("Bootstrap did not execute inside the owned nested frame.");
                Evidence.Phase("pump.bootstrap", new { InsideOwnedFrame = pump.Active });
                startB = a.Dispatcher.BeginInvoke(() => StartBInsideFrame(pump));
                Evidence.Phase("b.posted", new { InsideOwnedFrame = pump.Active });
            });
            watchdog.Start();
            Dispatcher.PushFrame(pump.Frame);
        }
        finally
        {
            pump.Active = false;
            watchdog.Stop();
            pump.ReleaseTimer?.Stop();
            if (bootstrap?.Status == DispatcherOperationStatus.Pending) bootstrap.Abort();
            if (startB?.Status == DispatcherOperationStatus.Pending) startB.Abort();
        }
        pump.Failure?.Throw();
        if (!pump.BCompleted || pump.BRoot == null || !pump.BRoot.IsCompletedSuccessfully)
            throw new InvalidOperationException("Nested loop exited without B's successful terminal state.");
        Evidence.Phase("pump.exit", new { BCompleted = pump.BCompleted, OnlyOwnedFrameStopped = true });
    }

    [RuntimeAsyncMethodGeneration(false)]
    private static async void StartBInsideFrame(NestedPump pump)
    {
        try
        {
            pump.A.AssertAfterAwait(3);
            if (!pump.Active) throw new InvalidOperationException("B's start callback must be inside A's active nested frame.");
            Evidence.Phase("b.callback", new { InsideOwnedFrame = pump.Active });
            var b = new RunState("b", true, pump.A.WorkMs, pump.A.Dispatcher, pump.A.UiThread, pump.A.GateDelayMs) { Pump = pump };
            pump.BRoot = NestedBLevel1Async(b);
            if (pump.BRoot.IsCompleted || b.Gate.Task.IsCompleted || Volatile.Read(ref b.EnteredLevels) != 7)
                throw new InvalidOperationException("B must suspend on its independent initially closed gate.");
            Evidence.Phase("b.suspension.verified", new { RootIncomplete = true, GateIncomplete = true, EnteredLevels = b.EnteredLevels,
                IndependentGate = !ReferenceEquals(b.Gate, pump.A.Gate), InsideOwnedFrame = pump.Active });
            pump.ReleaseTimer = new DispatcherTimer(DispatcherPriority.Background, b.Dispatcher) { Interval = TimeSpan.FromMilliseconds(b.GateDelayMs) };
            pump.ReleaseTimer.Tick += (_, _) =>
            {
                pump.ReleaseTimer.Stop();
                b.AssertAfterAwait(3);
                if (!pump.Active || pump.BRoot.IsCompleted || b.Gate.Task.IsCompleted)
                    throw new InvalidOperationException("B's gate release escaped its live nested frame or suspension.");
                Evidence.Phase("b.gate.release", new { InsideOwnedFrame = pump.Active });
                b.Gate.SetResult(true);
            };
            pump.ReleaseTimer.Start();
            WorkResult result = await pump.BRoot;
            if (!pump.Active || !pump.BRoot.IsCompletedSuccessfully)
                throw new InvalidOperationException("B did not finish normally inside the nested frame.");
            pump.BCompleted = true;
            Evidence.Phase("b.finished", new { InsideOwnedFrame = pump.Active, RootSuccess = true, result.Iterations });
            pump.Frame.Continue = false;
        }
        catch (Exception error)
        {
            // Transfer faults across the synchronous pump boundary; A rethrows after PushFrame returns.
            pump.Failure = ExceptionDispatchInfo.Capture(error);
            Evidence.Phase("b.failure", new { Error = error.ToString() });
            pump.Frame.Continue = false;
        }
        finally { pump.ReleaseTimer?.Stop(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult WorkCpuLeaf(int workMs)
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
        var runtime = new[] { "ReentrantALevel1Async", "ReentrantALevel2Async", "ReentrantALevel3Async",
            "NestedBLevel1Async", "NestedBLevel2Async", "NestedBLevel3Async" }.Select(name =>
        {
            MethodInfo method = typeof(Runner).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            int flags = (int)method.GetMethodImplementationFlags();
            if ((flags & 0x2000) == 0 || method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                throw new InvalidOperationException($"{name} is not genuine runtime-async V2.");
            byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
            return new { Method = name, Flags = flags, RuntimeAsync0x2000 = true, IlHex = Convert.ToHexString(il), IlSha256 = Convert.ToHexString(SHA256.HashData(il)) };
        }).ToArray();
        MethodInfo entry = typeof(ScenarioWindow).GetMethod("OnScenarioClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var classic = entry.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (((int)entry.GetMethodImplementationFlags() & 0x2000) != 0 || classic == null)
            throw new InvalidOperationException("The WPF event-entry wrapper must remain explicitly classic.");
        MethodInfo nestedEntry = typeof(Runner).GetMethod("StartBInsideFrame", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (((int)nestedEntry.GetMethodImplementationFlags() & 0x2000) != 0 || nestedEntry.GetCustomAttribute<AsyncStateMachineAttribute>() == null)
            throw new InvalidOperationException("B's scoped callback coordinator must remain classic.");
        Evidence.Phase("codegen.verified", new
        {
            RuntimeMethods = runtime,
            WpfEventEntry = new { Method = entry.Name, RuntimeAsync0x2000 = false, ClassicStateMachine = classic.StateMachineType.FullName },
            NestedClassicEntry = nestedEntry.Name,
            Scope = "Two native three-level chains; classic event/callback coordinators; synchronous physical nested pump helper."
        });
    }
}

internal sealed class NestedPump
{
    internal NestedPump(RunState a) { A = a; }
    internal RunState A { get; }
    internal DispatcherFrame Frame { get; } = new();
    internal bool Active;
    internal bool BCompleted;
    internal Task<WorkResult>? BRoot;
    internal ExceptionDispatchInfo? Failure;
    internal DispatcherTimer? ReleaseTimer;
}

internal sealed class RunState
{
    internal RunState(string mode, bool captureUi, int workMs, Dispatcher dispatcher, uint uiThread, int gateDelayMs)
    {
        Mode = mode; CaptureUi = captureUi; WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread; GateDelayMs = gateDelayMs;
    }
    internal void AssertAfterAwait(int level)
    {
        bool onUi = Dispatcher.CheckAccess() && Evidence.OsThreadId == UiThread;
        bool dispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
        Evidence.Phase($"level{level}.after-await", new { Mode, CaptureUi, OnUi = onUi, DispatcherContext = dispatcherContext });
        if (CaptureUi ? !onUi || !dispatcherContext : onUi || Dispatcher.CheckAccess())
            throw new InvalidOperationException($"Await-context/thread assertion failed at level {level}.");
    }
    internal string Mode { get; }
    internal void WorkBegin(string window, string leaf, params string[] expected) =>
        Evidence.Phase("sampling.begin", new { Window = window, Leaf = leaf, WorkMs, Scenario = "nested-ui",
            Operation = Mode, InsideOwnedFrame = Pump?.Active ?? false, ExpectedMethods = expected });
    internal void WorkEnd(string window, WorkResult result) =>
        Evidence.Phase("sampling.end", new { Window = window, Operation = Mode, result.Iterations });
    internal NestedPump? Pump { get; set; }
    internal int GateDelayMs { get; }
    internal bool CaptureUi { get; }
    internal int WorkMs { get; }
    internal Dispatcher Dispatcher { get; }
    internal uint UiThread { get; }
    internal TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int EnteredLevels;
}

internal readonly record struct WorkResult(ulong Checksum, long Iterations);

internal sealed record Options(string? AutoMode, string? StartFile, bool SmokeStart, bool AutoClose, int WorkMs, int GateDelayMs, int FlushMs)
{
    internal static Options Parse(string[] args)
    {
        string? mode = null, start = null;
        bool smoke = false, close = false;
        int work = 600, delay = 400, flush = 3000;
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
        if (mode != null && mode != "nested-ui")
            throw new ArgumentException("Select nested-ui.");
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
