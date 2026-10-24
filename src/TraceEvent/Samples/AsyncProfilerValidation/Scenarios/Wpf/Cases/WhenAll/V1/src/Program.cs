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
        Title = "WPF V1 Task.WhenAll fan-out and join";
        Width = 690;
        Height = 300;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "A and B start together on closed gates. A finishes while B remains gated; then B finishes and the parent resumes on UI.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _capture = new Button { Content = "Run gated A / B / WhenAll parent", Tag = "whenall-ui", Margin = new Thickness(0, 0, 0, 8) };
        _noCapture = new Button { Content = "Repeat the same controlled join", Tag = "whenall-ui", Margin = new Thickness(0, 0, 0, 12) };
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
        _status.Text = $"Ready. PID {Environment.ProcessId}, UI OS thread {_uiThread}. Branch work runs off UI; post-join parent work briefly uses UI.";
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
        _status.Text = "Controlled overlapping branch lifetimes, not simultaneous CPU. Parent's bare WhenAll await captures UI.";
        var state = new JoinState(_options.WorkMs, Dispatcher, _uiThread);
        Evidence.Phase("scenario.begin", new { Scenario = mode, BranchAwaitPolicy = "literal ConfigureAwait(false)",
            ParentAwaitPolicy = "bare await Task.WhenAll captures UI", MaterializedBranchTasks = true });
        Task<WorkResult> root = Runner.WhenAllParentAsync(state);
        if (root.IsCompleted || state.ARoot == null || state.BRoot == null || state.Join == null ||
            state.ARoot.IsCompleted || state.BRoot.IsCompleted || state.Join.IsCompleted ||
            state.A.Gate.Task.IsCompleted || state.B.Gate.Task.IsCompleted ||
            state.A.EnteredLevels != 7 || state.B.EnteredLevels != 7)
            throw new InvalidOperationException("Both branches and the parent must be waiting on independent closed gates and WhenAll.");
        Task<WorkResult> aRoot = state.ARoot, bRoot = state.BRoot;
        Task<WorkResult[]> joined = state.Join;
        Evidence.Phase("suspension.verified", new
        {
            RootIncomplete = !root.IsCompleted,
            BothBranchRootsIncomplete = true, JoinIncomplete = true, BothGatesClosed = true,
            AEntered = state.A.EnteredLevels, BEntered = state.B.EnteredLevels,
            IndependentGates = !ReferenceEquals(state.A.Gate, state.B.Gate),
            BranchTasksMaterializedForRealFanout = true,
            RunContinuationsAsynchronously = true
        });
        var release = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
        int releaseStage = 0;
        release.Tick += (_, _) =>
        {
            AssertUiContext();
            if (releaseStage == 0)
            {
                Evidence.Phase("a.gate.release", new { BStillGated = !state.B.Gate.Task.IsCompleted,
                    JoinIncomplete = !joined.IsCompleted, ParentAfterJoinCount = state.AfterJoinCount });
                state.A.Gate.SetResult(true);
                releaseStage = 1;
                return;
            }
            if (!aRoot.IsCompleted) return;
            if (!aRoot.IsCompletedSuccessfully)
                throw new InvalidOperationException("Branch A faulted or was cancelled unexpectedly.", aRoot.Exception);
            if (bRoot.IsCompleted || state.B.Gate.Task.IsCompleted || joined.IsCompleted || root.IsCompleted || state.AfterJoinCount != 0)
                throw new InvalidOperationException("Parent must still await WhenAll after only A finishes.");
            Evidence.Phase("join.after-a-only", new { ACompletedSuccessfully = true, BStillGated = true,
                BRootIncomplete = true, JoinIncomplete = true, ParentRootIncomplete = true, ParentAfterJoinCount = state.AfterJoinCount });
            Evidence.Phase("b.gate.release");
            release.Stop();
            state.B.Gate.SetResult(true);
        };
        Evidence.Phase("gate.delay.begin", new { _options.GateDelayMs });
        release.Start();
        WorkResult result = await root.ConfigureAwait(false);
        if (state.A.CompletionCount != 1 || state.B.CompletionCount != 1 || state.AfterJoinCount != 1 || result.Checksum != 46)
            throw new InvalidOperationException("Expected A/B results, completions and one parent join continuation.");
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
    internal static async Task<WorkResult> WhenAllParentAsync(JoinState state)
    {
        state.AssertUi();
        // Materialized branch Tasks are the actual fan-out boundary, not a direct classic-await chain.
        Task<WorkResult> a = FanoutALevel1Async(state.A);
        Task<WorkResult> b = FanoutBLevel1Async(state.B);
        state.ARoot = a;
        state.BRoot = b;
        state.A.ProveSuspended(a);
        state.B.ProveSuspended(b);
        Task<WorkResult[]> joined = Task.WhenAll(a, b);
        state.Join = joined;
        if (joined.IsCompleted) throw new InvalidOperationException("WhenAll must initially wait for both gated branches.");
        Evidence.Phase("join.wait", new { RealTaskWhenAll = true, BothBranchesIncomplete = true, ParentContextCaptured = true });
        WorkResult[] results = await joined;
        state.AssertUi();
        if (!a.IsCompletedSuccessfully || !b.IsCompletedSuccessfully || results.Length != 2 ||
            results[0].Checksum != 17 || results[1].Checksum != 29 ||
            state.A.CompletionCount != 1 || state.B.CompletionCount != 1 ||
            Interlocked.Increment(ref state.AfterJoinCount) != 1)
            throw new InvalidOperationException("WhenAll results or exactly-once completions did not match.");
        Evidence.Phase("join.completed", new { BothBranchesSucceeded = true, AResult = results[0].Checksum, BResult = results[1].Checksum, state.AfterJoinCount });
        Evidence.Phase("sampling.begin", new { Window = "parent-after-join", Leaf = nameof(JoinParentCpuLeaf), state.WorkMs,
            Scenario = "whenall-ui", ExpectedMethods = new[] { nameof(WhenAllParentAsync) } });
        WorkResult work = JoinParentCpuLeaf(state.WorkMs);
        Evidence.Phase("sampling.end", new { Window = "parent-after-join", work.Iterations });
        return new WorkResult(results[0].Checksum + results[1].Checksum, results[0].Iterations + results[1].Iterations + work.Iterations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> FanoutALevel1Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await FanoutALevel2Async(state).ConfigureAwait(false);
        state.AssertAfterAwait(1);
        state.MarkCompleted(result);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FanoutALevel2Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await FanoutALevel3Async(state).ConfigureAwait(false);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FanoutALevel3Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        await state.Gate.Task.ConfigureAwait(false);
        state.AssertAfterAwait(3);
        state.WorkBegin(nameof(BranchACpuLeaf));
        WorkResult work = BranchACpuLeaf(state.WorkMs);
        state.WorkEnd(work);
        return new WorkResult(state.ExpectedChecksum, work.Iterations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> FanoutBLevel1Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await FanoutBLevel2Async(state).ConfigureAwait(false);
        state.AssertAfterAwait(1);
        state.MarkCompleted(result);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FanoutBLevel2Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await FanoutBLevel3Async(state).ConfigureAwait(false);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FanoutBLevel3Async(BranchState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        await state.Gate.Task.ConfigureAwait(false);
        state.AssertAfterAwait(3);
        state.WorkBegin(nameof(BranchBCpuLeaf));
        WorkResult work = BranchBCpuLeaf(state.WorkMs);
        state.WorkEnd(work);
        return new WorkResult(state.ExpectedChecksum, work.Iterations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult BranchACpuLeaf(int workMs) => WorkCpuLeaf(workMs);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult BranchBCpuLeaf(int workMs) => WorkCpuLeaf(workMs);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult JoinParentCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

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

    internal static void VerifyCodegen() => ClassicCodegen.Verify();
}

internal sealed class JoinState
{
    internal JoinState(int workMs, Dispatcher dispatcher, uint uiThread)
    {
        WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread;
        A = new BranchState("A", 17, workMs, dispatcher, uiThread);
        B = new BranchState("B", 29, workMs, dispatcher, uiThread);
    }
    internal void AssertUi()
    {
        if (!Dispatcher.CheckAccess() || Evidence.OsThreadId != UiThread || SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("WhenAll parent must start/resume on the real UI context.");
    }
    internal BranchState A { get; }
    internal BranchState B { get; }
    internal int WorkMs { get; }
    internal Dispatcher Dispatcher { get; }
    internal uint UiThread { get; }
    internal Task<WorkResult>? ARoot;
    internal Task<WorkResult>? BRoot;
    internal Task<WorkResult[]>? Join;
    internal int AfterJoinCount;
}

internal sealed class BranchState
{
    internal BranchState(string operation, ulong expectedChecksum, int workMs, Dispatcher dispatcher, uint uiThread)
    {
        Operation = operation; ExpectedChecksum = expectedChecksum; WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread;
    }
    internal void AssertAfterAwait(int level)
    {
        bool onUi = Dispatcher.CheckAccess() && Evidence.OsThreadId == UiThread;
        bool dispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
        Evidence.Phase("branch.after-await", new { Operation, Level = level, OnUi = onUi, DispatcherContext = dispatcherContext });
        if (onUi || Dispatcher.CheckAccess() || SynchronizationContext.Current != null)
            throw new InvalidOperationException($"Branch {Operation} level{level} must actually resume off UI with null context.");
    }
    internal void ProveSuspended(Task<WorkResult> root)
    {
        if (root.IsCompleted || Gate.Task.IsCompleted || EnteredLevels != 7)
            throw new InvalidOperationException($"Branch {Operation} did not enter/suspend on its closed gate.");
        Evidence.Phase("branch.suspension.verified", new { Operation, RootIncomplete = true, GateIncomplete = true, EnteredLevels });
    }
    internal void WorkBegin(string leaf) =>
        Evidence.Phase("sampling.begin", new { Window = $"branch-{Operation}", Leaf = leaf, WorkMs, Scenario = "whenall-ui",
            ExpectedMethods = new[] { $"Fanout{Operation}Level3Async", $"Fanout{Operation}Level2Async", $"Fanout{Operation}Level1Async" } });
    internal void WorkEnd(WorkResult work) =>
        Evidence.Phase("sampling.end", new { Window = $"branch-{Operation}", work.Iterations });
    internal void MarkCompleted(WorkResult result)
    {
        if (result.Checksum != ExpectedChecksum || Interlocked.Increment(ref CompletionCount) != 1)
            throw new InvalidOperationException($"Branch {Operation} result or completion count mismatch.");
        Evidence.Phase("branch.completed", new { Operation, Result = result.Checksum, CompletionCount });
    }
    internal string Operation { get; }
    internal ulong ExpectedChecksum { get; }
    internal int WorkMs { get; }
    internal Dispatcher Dispatcher { get; }
    internal uint UiThread { get; }
    internal TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int EnteredLevels;
    internal int CompletionCount;
}

internal readonly record struct WorkResult(ulong Checksum, long Iterations);

internal sealed record Options(string? AutoMode, string? StartFile, bool SmokeStart, bool AutoClose, int WorkMs, int GateDelayMs, int FlushMs)
{
    internal static Options Parse(string[] args)
    {
        string? mode = null, start = null;
        bool smoke = false, close = false;
        int work = 600, delay = 250, flush = 3000;
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
        if (mode != null && mode != "whenall-ui")
            throw new ArgumentException("Select whenall-ui.");
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
