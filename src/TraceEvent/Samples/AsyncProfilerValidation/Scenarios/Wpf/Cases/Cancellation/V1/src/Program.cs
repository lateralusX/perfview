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
        Title = "WPF V1 cancellation followed by fresh operation";
        Width = 690;
        Height = 300;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Cancel a suspended classic chain; sample its handler/finally; then start an independent fresh chain on the same UI thread.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _capture = new Button { Content = "Cancel, clean up, then run fresh operation", Tag = "cancel-fresh-ui", Margin = new Thickness(0, 0, 0, 8) };
        _noCapture = new Button { Content = "Repeat the same cancellation / fresh sequence", Tag = "cancel-fresh-ui", Margin = new Thickness(0, 0, 0, 12) };
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
        _status.Text = "Cancellation handler/finally and fresh-operation CPU windows deliberately freeze the UI briefly.";
        Evidence.Phase("scenario.begin", new { Scenario = mode, CaptureUi = true, AwaitPolicy = "bare UI awaits", Sequence = "cancel suspended operation, finish cleanup, fresh UI callback" });
        CancellationToken cancelledToken;
        using (var cancelled = new RunState("cancelled", true, _options.WorkMs, Dispatcher, _uiThread))
        {
            cancelledToken = cancelled.Token;
            Task<WorkResult> root = Runner.CancelledLevel1Async(cancelled);
            cancelled.ProveSuspended(root);
            Evidence.Phase("suspension.verified", new { RootIncomplete = true, GateIncomplete = true, EnteredLevels = 7, ChildTasksStoredForInspection = false });
            var cancelTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
            cancelTimer.Tick += (_, _) =>
            {
                cancelTimer.Stop();
                AssertUiContext();
                cancelled.ProveSuspended(root);
                Evidence.Phase("cancel.request", new { RootIncomplete = true, GateIncomplete = true });
                cancelled.Cancellation.Cancel();
                if (!cancelled.Gate.Task.IsCanceled || !cancelled.Token.IsCancellationRequested)
                    throw new InvalidOperationException("Cancellation did not cancel the suspended gate.");
                Evidence.Phase("cancel.request.completed", new { GateIsCanceled = true, TokenRequested = true });
            };
            cancelTimer.Start();
            try
            {
                await root.ConfigureAwait(false);
                throw new InvalidOperationException("Cancelled operation unexpectedly completed successfully.");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == cancelled.Token && cancelled.Token.IsCancellationRequested)
            {
                if (!root.IsCanceled || root.IsFaulted || cancelled.CancellationObserved != 1 || cancelled.HandlerCount != 1 || cancelled.FinallyCount != 1)
                    throw new InvalidOperationException("Expected token, cancelled root and once-only cleanup were not proved.");
                Evidence.Phase("cancel.operation.finished", new { RootIsCanceled = root.IsCanceled, RootIsFaulted = root.IsFaulted,
                    TokenMatches = error.CancellationToken == cancelled.Token, cancelled.CancellationObserved, cancelled.HandlerCount, cancelled.FinallyCount });
            }
        }
        WorkResult result;
        using (var fresh = new RunState("fresh", false, _options.WorkMs, Dispatcher, _uiThread))
        {
            if (fresh.Token == cancelledToken || fresh.Token.IsCancellationRequested)
                throw new InvalidOperationException("Fresh operation needs a distinct, noncancelled token.");
            Task<WorkResult>? freshRoot = null;
            await Dispatcher.InvokeAsync(() =>
            {
                AssertUiContext();
                Evidence.Phase("fresh.callback", new { SameUiOsThread = Evidence.OsThreadId == _uiThread, TokenIndependent = fresh.Token != cancelledToken });
                freshRoot = Runner.FreshLevel1Async(fresh);
                fresh.ProveSuspended(freshRoot);
                var release = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(_options.GateDelayMs) };
                release.Tick += (_, _) =>
                {
                    release.Stop();
                    AssertUiContext();
                    fresh.ProveSuspended(freshRoot);
                    Evidence.Phase("fresh.gate.release", new { GateIncomplete = true, RootIncomplete = true, TokenNotCancelled = !fresh.Token.IsCancellationRequested });
                    fresh.Gate.SetResult(true);
                };
                release.Start();
            }).Task.ConfigureAwait(false);
            result = await freshRoot!.ConfigureAwait(false);
            if (!freshRoot.IsCompletedSuccessfully || freshRoot.IsCanceled || fresh.Token.IsCancellationRequested)
                throw new InvalidOperationException("Fresh operation must complete successfully without cancellation.");
            Evidence.Phase("fresh.operation.finished", new { RootSuccess = true, RootIsCanceled = false, TokenNotCancelled = true });
        }
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
    internal static async Task<WorkResult> CancelledLevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        Evidence.Phase("level1.enter");
        try
        {
            await CancelledLevel2Async(state);
            throw new InvalidOperationException("The cancelled child unexpectedly returned.");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == state.Token && state.Token.IsCancellationRequested)
        {
            state.AssertAfterAwait(1);
            if (++state.CancellationObserved != 1 || ++state.HandlerCount != 1)
                throw new InvalidOperationException("Cancellation observation or handler ran more than once.");
            Evidence.Phase("cancel.observed", new { TokenMatches = error.CancellationToken == state.Token, TokenRequested = state.Token.IsCancellationRequested });
            state.WorkBegin("cancel-handler", nameof(CancelHandlerCpuLeaf), nameof(CancelledLevel1Async));
            WorkResult handled = CancelHandlerCpuLeaf(state.WorkMs);
            state.WorkEnd("cancel-handler", handled);
            throw;
        }
        finally
        {
            state.AssertAfterAwait(1);
            if (++state.FinallyCount != 1) throw new InvalidOperationException("Finally ran more than once.");
            Evidence.Phase("cancel.finally");
            state.WorkBegin("cancel-finally", nameof(CancelFinallyCpuLeaf), nameof(CancelledLevel1Async));
            WorkResult cleanup = CancelFinallyCpuLeaf(state.WorkMs);
            state.WorkEnd("cancel-finally", cleanup);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> CancelledLevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        Evidence.Phase("level2.enter");
        WorkResult result = await CancelledLevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> CancelledLevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        Evidence.Phase("level3.enter");
        await state.Gate.Task;
        state.AssertAfterAwait(3);
        throw new InvalidOperationException("A normally released gate is not expected in the cancellation chain.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult CancelHandlerCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult CancelFinallyCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkResult FreshCpuLeaf(int workMs) => WorkCpuLeaf(workMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> FreshLevel1Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 1);
        WorkResult result = await FreshLevel2Async(state);
        state.AssertAfterAwait(1);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FreshLevel2Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 2);
        WorkResult result = await FreshLevel3Async(state);
        state.AssertAfterAwait(2);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WorkResult> FreshLevel3Async(RunState state)
    {
        Interlocked.Or(ref state.EnteredLevels, 4);
        await state.Gate.Task;
        state.AssertAfterAwait(3);
        state.WorkBegin("fresh", nameof(FreshCpuLeaf), nameof(FreshLevel3Async), nameof(FreshLevel2Async), nameof(FreshLevel1Async));
        WorkResult result = FreshCpuLeaf(state.WorkMs);
        state.WorkEnd("fresh", result);
        return result;
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

    internal static void VerifyCodegen() => ClassicCodegen.Verify();
}

internal sealed class RunState : IDisposable
{
    internal RunState(string mode, bool cancellationAware, int workMs, Dispatcher dispatcher, uint uiThread)
    {
        Mode = mode; WorkMs = workMs; Dispatcher = dispatcher; UiThread = uiThread;
        Token = Cancellation.Token;
        if (cancellationAware)
            _registration = Token.Register(() =>
            {
                if (!Gate.TrySetCanceled(Token)) throw new InvalidOperationException("Cancellation gate was not still closed.");
                Evidence.Phase("cancel.gate.cancelled", new { TokenRequested = Token.IsCancellationRequested, GateIsCanceled = Gate.Task.IsCanceled });
            });
    }
    internal void AssertAfterAwait(int level)
    {
        bool onUi = Dispatcher.CheckAccess() && Evidence.OsThreadId == UiThread;
        bool dispatcherContext = SynchronizationContext.Current is DispatcherSynchronizationContext;
        Evidence.Phase($"level{level}.after-await", new { Mode, OnUi = onUi, DispatcherContext = dispatcherContext });
        if (!onUi || !dispatcherContext)
            throw new InvalidOperationException($"Await-context/thread assertion failed at level {level}.");
    }
    internal string Mode { get; }
    internal void WorkBegin(string window, string leaf, params string[] expected) =>
        Evidence.Phase("sampling.begin", new { Window = window, Leaf = leaf, WorkMs, Scenario = Mode, ExpectedMethods = expected });
    internal void WorkEnd(string window, WorkResult result) =>
        Evidence.Phase("sampling.end", new { Window = window, result.Iterations });
    internal void ProveSuspended(Task<WorkResult> root)
    {
        if (root.IsCompleted || Gate.Task.IsCompleted || Volatile.Read(ref EnteredLevels) != 7)
            throw new InvalidOperationException("Expected all three entered classic levels suspended on an initially closed gate.");
        Evidence.Phase("operation.suspension.verified", new { Operation = Mode, RootIncomplete = true, GateIncomplete = true, EnteredLevels, ChildTasksStoredForInspection = false });
    }
    public void Dispose()
    {
        _registration.Dispose();
        Cancellation.Dispose();
        Evidence.Phase("operation.resources.disposed", new { Operation = Mode, RegistrationDisposed = true, SourceDisposed = true });
    }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal CancellationToken Token { get; }
    internal int WorkMs { get; }
    internal Dispatcher Dispatcher { get; }
    internal uint UiThread { get; }
    internal TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int EnteredLevels;
    internal int CancellationObserved;
    internal int HandlerCount;
    internal int FinallyCount;
    private readonly CancellationTokenRegistration _registration;
}

internal readonly record struct WorkResult(ulong Checksum, long Iterations);

internal sealed record Options(string? AutoMode, string? StartFile, bool SmokeStart, bool AutoClose, int WorkMs, int GateDelayMs, int FlushMs)
{
    internal static Options Parse(string[] args)
    {
        string? mode = null, start = null;
        bool smoke = false, close = false;
        int work = 600, delay = 500, flush = 3000;
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
        if (mode != null && mode != "cancel-fresh-ui")
            throw new ArgumentException("Select cancel-fresh-ui.");
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
