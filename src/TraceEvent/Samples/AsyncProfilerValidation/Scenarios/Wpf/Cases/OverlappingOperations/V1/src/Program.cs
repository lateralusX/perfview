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
        Title = "Overlapping gated classic operations on one WPF dispatcher";
        Width = 690;
        Height = 300;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Start A, then B in separate real UI callbacks; release gates A0, B0, A1, B1. UI CPU is serial.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _capture = new Button { Content = "Run gated operations A / B / A / B", Tag = "overlap-abab", Margin = new Thickness(0, 0, 0, 8) };
        _noCapture = new Button { Content = "Repeat the same bounded A / B sequence", Tag = "overlap-abab", Margin = new Thickness(0, 0, 0, 12) };
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
        _status.Text = "Two suspended classic operations, same UI dispatcher, independent gates; bounded serial CPU windows.";
        var a = new RunState("A", _options.WorkMs, Dispatcher, _uiThread);
        var b = new RunState("B", _options.WorkMs, Dispatcher, _uiThread);
        Task<WorkResult>? rootA = null, rootB = null;
        Evidence.Phase("scenario.begin", new { Scenario = mode, AwaitPolicy = "bare UI awaits", ReleaseOrder = "A0,B0,A1,B1" });
        await Dispatcher.InvokeAsync(() =>
        {
            AssertUiContext();
            Evidence.Phase("operation.callback", new { Operation = "A", Mechanism = "separate Dispatcher.InvokeAsync callback" });
            rootA = Runner.OperationALevel1Async(a);
            a.ProveSuspended(rootA);
        }).Task.ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            AssertUiContext();
            Evidence.Phase("operation.callback", new { Operation = "B", Mechanism = "separate Dispatcher.InvokeAsync callback", AStillIncomplete = !rootA!.IsCompleted });
            rootB = Runner.OperationBLevel1Async(b);
            b.ProveSuspended(rootB);
            if (rootA!.IsCompleted || a.Gates.Any(g => g.Task.IsCompleted))
                throw new InvalidOperationException("A must still be suspended on independent closed gates when B starts.");
            Evidence.Phase("suspension.verified", new { BothRootsIncomplete = true, AllFourGatesClosed = true,
                DistinctGateInstances = !ReferenceEquals(a.Gates[0], b.Gates[0]) && !ReferenceEquals(a.Gates[1], b.Gates[1]),
                ChildTasksStoredForInspection = false });
            int next = 0;
            var release = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
            release.Tick += (_, _) =>
            {
                AssertUiContext();
                RunState current = next % 2 == 0 ? a : b;
                RunState other = next % 2 == 0 ? b : a;
                Task<WorkResult> root = next % 2 == 0 ? rootA! : rootB!;
                Task<WorkResult> otherRoot = next % 2 == 0 ? rootB! : rootA!;
                int round = next / 2;
                if (current.SuspendedRound != round) return;
                if (root.IsCompleted || current.Gates[round].Task.IsCompleted)
                    throw new InvalidOperationException("Selected operation must be suspended on its still-closed gate.");
                if (next == 0 && (otherRoot.IsCompleted || other.Gates.Any(g => g.Task.IsCompleted)))
                    throw new InvalidOperationException("B must remain wholly suspended while A0 runs.");
                if (next == 1 && (otherRoot.IsCompleted || other.Gates[1].Task.IsCompleted))
                    throw new InvalidOperationException("A must remain suspended on gate1 while B0 runs.");
                Evidence.Phase("gate.release", new { Sequence = next, Operation = current.Operation, Round = round,
                    SelectedRootIncomplete = true, OtherRootIncomplete = !otherRoot.IsCompleted,
                    OtherRound0Closed = !other.Gates[0].Task.IsCompleted, OtherRound1Closed = !other.Gates[1].Task.IsCompleted });
                current.Gates[round].SetResult(true);
                if (++next == 4) release.Stop();
            };
            release.Start();
        }).Task.ConfigureAwait(false);
        WorkResult resultA = await rootA!.ConfigureAwait(false);
        WorkResult resultB = await rootB!.ConfigureAwait(false);
        var result = new WorkResult(resultA.Checksum ^ resultB.Checksum, resultA.Iterations + resultB.Iterations);
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
    internal static async Task<WorkResult> OperationALevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await OperationALevel2Async(state);
        state.AssertAfterAwait(1);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> OperationALevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await OperationALevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> OperationALevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        state.SuspendedRound = 0;
        await state.Gates[0].Task;
        state.AssertAfterAwait(3);
        state.WorkBegin(0, nameof(OperationACpuLeaf));
        WorkResult first = OperationACpuLeaf(state.WorkMs);
        state.WorkEnd(0, first);
        state.SuspendedRound = 1;
        await state.Gates[1].Task;
        state.AssertAfterAwait(3);
        state.WorkBegin(1, nameof(OperationACpuLeaf));
        WorkResult second = OperationACpuLeaf(state.WorkMs);
        state.WorkEnd(1, second);
        return second;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> OperationBLevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        WorkResult result = await OperationBLevel2Async(state);
        state.AssertAfterAwait(1);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> OperationBLevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await OperationBLevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> OperationBLevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        state.SuspendedRound = 0;
        await state.Gates[0].Task;
        state.AssertAfterAwait(3);
        state.WorkBegin(0, nameof(OperationBCpuLeaf));
        WorkResult first = OperationBCpuLeaf(state.WorkMs);
        state.WorkEnd(0, first);
        state.SuspendedRound = 1;
        await state.Gates[1].Task;
        state.AssertAfterAwait(3);
        state.WorkBegin(1, nameof(OperationBCpuLeaf));
        WorkResult second = OperationBCpuLeaf(state.WorkMs);
        state.WorkEnd(1, second);
        return second;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult OperationACpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult OperationBCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

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

internal sealed class RunState
{
    internal RunState(string operation, int workMs, Dispatcher dispatcher, uint uiThread)
    {
        Operation = operation; WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread;
    }
    internal void AssertAfterAwait(int level)
    {
        bool onUi = Dispatcher.CheckAccess() && Evidence.OsThreadId == UiThread;
        bool dispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
        Evidence.Phase("operation.after-await", new { Operation, Level = level, OnUi = onUi, DispatcherContext = dispatcherContext });
        if (!onUi || !dispatcherContext)
            throw new InvalidOperationException($"Await-context/thread assertion failed at level {level}.");
    }
    internal void ProveSuspended(Task<WorkResult> root)
    {
        if (root.IsCompleted || EnteredLevels != 7 || Gates.Any(g => g.Task.IsCompleted))
            throw new InvalidOperationException($"Operation {Operation} did not enter all three levels on closed gates.");
        Evidence.Phase("operation.suspension.verified", new { Operation, RootIncomplete = true, EnteredLevels, BothGatesClosed = true });
    }
    internal void WorkBegin(int round, string leaf)
    {
        string[] expected = new[] { $"Operation{Operation}Level3Async", $"Operation{Operation}Level2Async", $"Operation{Operation}Level1Async" };
        Evidence.Phase("sampling.begin", new { Window = $"{Operation}-R{round}", Operation, Round = round,
            Leaf = leaf, WorkMs, Scenario = "overlap-abab", ExpectedMethods = expected });
    }
    internal void WorkEnd(int round, WorkResult result) =>
        Evidence.Phase("sampling.end", new { Window = $"{Operation}-R{round}", Operation, Round = round, result.Iterations });
    internal string Operation { get; }
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
        int work = 500, delay = 250, flush = 3000;
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
        if (mode != null && mode != "overlap-abab")
            throw new ArgumentException("Select overlap-abab.");
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
