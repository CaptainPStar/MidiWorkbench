using System.Collections.Immutable;
using System.Threading.Channels;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

public sealed record TransportStatus(bool Running, double Tick, double Bpm, long LateNoteOns, long SkippedClocks,
    int Captured, bool CaptureComplete, long DroppedInputs, string? Error, long Take);
public readonly record struct CaptureEntry(double Seconds, bool Input, int Port, int Destination, byte Status, byte Data1, byte Data2);
// Entry seconds are relative to transport start. Input port index -1 is the rig input.
// Origin is the host-clock time of transport start.
public sealed record TransportCapture(long Take, double Origin, DateTimeOffset StartedAt, double Bpm, bool Complete, bool Loop, long LengthTicks,
    ImmutableArray<CaptureEntry> Entries, ImmutableArray<PerformanceAction> Actions, ImmutableArray<TempoChange> TempoChanges,
    ImmutableArray<string> Endpoints, ImmutableArray<string> Destinations);
public sealed record HostOptions(int CaptureCapacity = 100000, double LookaheadSeconds = .01, int PendingCapacity = 8192,
    int InputCapacity = 4096, bool Background = true, IHostWait? Wait = null, Func<double>? Clock = null);

// Owns the Transport and the opened output ports while playing. Callers talk to it only through bounded queues and snapshots.
public sealed class TransportHost : IDisposable
{
    private enum Kind { Start, Stop, Panic, Tempo, Arm, Exit }
    private sealed record Command(Kind Kind, TaskCompletionSource Done)
    {
        public ResolvedPlan? Plan { get; init; }
        public IMidiOutputPort[] Ports { get; init; } = [];
        public IMidiOutputPort? ThruPort { get; init; }
        public int Channel { get; init; }
        public double Bpm { get; init; }
        public bool Loop { get; init; }
    }
    private readonly HostOptions options;
    private readonly Func<double> clock;
    private readonly Transport transport;
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly Channel<TimedMidi> inputs;
    private readonly CaptureEntry[] entries;
    private readonly PerformanceAction[] actions = new PerformanceAction[4096];
    private readonly TempoChange[] tempos = new TempoChange[4096];
    private int entryCount, actionCount, tempoCount;
    private bool overflow;
    private long sequence, take, droppedInputs;
    private volatile bool running;
    private ResolvedPlan? plan;
    private IMidiOutputPort[] ports = [];
    private Queue<TimedMidi>[] pending = [];
    private bool[] faulted = [];
    private IMidiOutputPort? thru;
    private int thruChannel;
    private readonly HashSet<(byte Channel, byte Pitch)> thruHeld = new();
    private bool thruFaulted, stopRequested, loop, dirty;
    private double startBpm = 120, origin, current, lastPublish = double.NegativeInfinity;
    private DateTimeOffset startedAt;
    private string? error;
    private TransportStatus status = new(false, 0, 120, 0, 0, 0, true, 0, null, 0);
    private readonly Thread? worker;
    private readonly CancellationTokenSource exit = new();

    public TransportHost(HostOptions? options = null)
    {
        this.options = options ?? new HostOptions();
        clock = this.options.Clock ?? (() => HostTime.Now);
        entries = new CaptureEntry[this.options.CaptureCapacity];
        inputs = Channel.CreateBounded<TimedMidi>(new BoundedChannelOptions(this.options.InputCapacity) { SingleReader = true });
        transport = new(Dispatch);
        if (!this.options.Background) return;
        worker = new Thread(Run) { IsBackground = true, Name = "MidiWorkbench transport", Priority = ThreadPriority.Highest };
        worker.Start();
    }
    public TransportStatus Status => Volatile.Read(ref status);
    public Task Start(ResolvedPlan value, IReadOnlyList<IMidiOutputPort> outputs, double bpm, bool repeat)
    {
        if (outputs.Count != value.Endpoints.Length || outputs.Where((p, i) => p.EndpointId != value.Endpoints[i]).Any())
            throw new ArgumentException("Ports do not match the plan endpoints.");
        return Post(Kind.Start, c => c with { Plan = value, Ports = outputs.ToArray(), Bpm = bpm, Loop = repeat });
    }
    public Task Stop() => Post(Kind.Stop);
    public Task Panic() => Post(Kind.Panic);
    public Task SetTempo(double bpm) => Post(Kind.Tempo, c => c with { Bpm = bpm });
    // Thru forwards rig input to one port/channel; null disarms.
    public Task Arm(IMidiOutputPort? port, int channel) => Post(Kind.Arm, c => c with { ThruPort = port, Channel = channel });
    // Driver threads call this; it never blocks.
    public void PostInput(TimedMidi message)
    {
        if (!inputs.Writer.TryWrite(message)) Interlocked.Increment(ref droppedInputs);
    }
    private Task Post(Kind kind, Func<Command, Command>? configure = null)
    {
        var command = new Command(kind, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (configure is not null) command = configure(command);
        if (!commands.Writer.TryWrite(command)) throw new InvalidOperationException("Transport is busy; retry.");
        return command.Done.Task;
    }

    public TransportCapture Capture()
    {
        var before = Interlocked.Read(ref sequence);
        if (running || before % 2 == 1) throw new InvalidOperationException("Stop playback before reading the take.");
        var p = plan;
        var result = new TransportCapture(Volatile.Read(ref take), origin, startedAt, startBpm, !overflow, loop, p?.LengthTicks ?? 0,
            entries.AsSpan(0, Volatile.Read(ref entryCount)).ToImmutableArray(), actions.AsSpan(0, actionCount).ToImmutableArray(),
            tempos.AsSpan(0, tempoCount).ToImmutableArray(), p?.Endpoints ?? [], p?.Destinations ?? []);
        if (Interlocked.Read(ref sequence) != before || running) throw new InvalidOperationException("Playback restarted while reading the take.");
        return result;
    }

    public void Step(double now)
    {
        current = now;
        while (commands.Reader.TryRead(out var command))
        {
            // Publish before completing so callers observe the command's effect; publish again after this step's pump.
            try { Execute(command, now); Publish(now); dirty = true; command.Done.TrySetResult(); }
            catch (Exception ex) { command.Done.TrySetException(ex); }
        }
        for (var i = 0; i < ports.Length; i++)
        {
            if (!faulted[i] && ports[i].Faulted) Fault(i, "endpoint disconnected");
            while (!faulted[i] && pending[i].TryPeek(out var m) && m.Seconds <= now) { pending[i].Dequeue(); Submit(i, m); }
        }
        while (inputs.Reader.TryRead(out var input)) Receive(input, now);
        var wasRunning = transport.Running;
        transport.Pump(now, now + options.LookaheadSeconds);
        if (stopRequested) { stopRequested = false; transport.Stop(now); Act(now, "stop", "fault"); }
        else if (wasRunning && !transport.Running) Act(now, "end");
        running = transport.Running;
        if (dirty || now - lastPublish >= .01 || running != status.Running) Publish(now);
    }
    private void Execute(Command c, double now)
    {
        switch (c.Kind)
        {
            case Kind.Start:
                if (transport.Running) { transport.Stop(now); Act(now, "stop"); }
                Interlocked.Increment(ref sequence);
                plan = c.Plan!; ports = c.Ports; loop = c.Loop;
                pending = ports.Select(_ => new Queue<TimedMidi>()).ToArray();
                faulted = new bool[ports.Length];
                entryCount = 0; actionCount = 0; tempoCount = 0; overflow = false; error = null; stopRequested = false;
                origin = now; startedAt = DateTimeOffset.UtcNow; take++;
                tempos[tempoCount++] = new(0, c.Bpm); startBpm = c.Bpm;
                Act(now, "start", loop ? "loop" : null);
                transport.Start(plan, now, c.Bpm, loop);
                Interlocked.Increment(ref sequence);
                break;
            case Kind.Stop:
                if (transport.Running) { transport.Stop(now); Act(now, "stop"); }
                break;
            case Kind.Panic:
                foreach (var p in ports.Where(p => p.ScheduledSend)) Try(() => p.Cancel(now));
                foreach (var q in pending) q.Clear();
                var wasRunning = transport.Running;
                transport.Panic(now);
                ReleaseThru(now);
                if (wasRunning) Act(now, "panic");
                break;
            case Kind.Tempo:
                transport.SetTempo(now, c.Bpm);
                if (transport.Running && tempoCount < tempos.Length) tempos[tempoCount++] = new(now - origin, c.Bpm);
                Act(now, "tempo", c.Bpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Kind.Arm:
                ReleaseThru(now);
                thru = c.ThruPort; thruChannel = c.Channel - 1; thruFaulted = false;
                Act(now, "arm", thru?.EndpointId);
                break;
            case Kind.Exit:
                transport.Stop(now); ReleaseThru(now); exit.Cancel();
                break;
        }
        running = transport.Running;
        dirty = true;
    }
    private void Receive(TimedMidi input, double now)
    {
        if (transport.Running) Record(new(input.Seconds - origin, true, -1, -1, input.Status, input.Data1, input.Data2));
        if (thru is null || thruFaulted || input.Status is < 0x80 or >= 0xF0) return;
        var kind = input.Status & 0xF0;
        var status = (byte)(kind | thruChannel);
        if (kind == 0x90 && input.Data2 > 0) thruHeld.Add(((byte)thruChannel, input.Data1));
        else if (kind is 0x80 or 0x90) thruHeld.Remove(((byte)thruChannel, input.Data1));
        try { thru.Submit(new(now, status, input.Data1, input.Data2)); }
        catch (MidiPortException ex) { thruFaulted = true; error = $"Thru output '{thru.EndpointId}' failed: {ex.Message}"; }
    }
    private void ReleaseThru(double now)
    {
        if (thru is not null && !thruFaulted)
            foreach (var (channel, pitch) in thruHeld) Try(() => thru.Submit(new(now, (byte)(0x80 | channel), pitch, 0)));
        thruHeld.Clear();
    }
    private void Dispatch(int port, int destination, TimedMidi m)
    {
        Record(new(m.Seconds - origin, false, port, destination, m.Status, m.Data1, m.Data2));
        if (faulted[port]) return;
        if (ports[port].ScheduledSend || m.Seconds <= current && pending[port].Count == 0) { Submit(port, m); return; }
        if (pending[port].Count >= options.PendingCapacity) { Fault(port, "output backlog exceeded"); return; }
        pending[port].Enqueue(m);
    }
    private void Submit(int port, TimedMidi m)
    {
        try { ports[port].Submit(m); }
        catch (MidiPortException ex) { Fault(port, ex.Message); }
    }
    private void Fault(int port, string message)
    {
        faulted[port] = true; pending[port].Clear();
        error = $"Output '{ports[port].EndpointId}' failed: {message}. Reconnect it and press Play.";
        stopRequested = true;
        Act(current, "fault", ports[port].EndpointId);
    }
    private void Record(CaptureEntry entry)
    {
        if (entryCount >= entries.Length) { overflow = true; return; }
        entries[entryCount] = entry; Volatile.Write(ref entryCount, entryCount + 1);
    }
    private void Act(double now, string kind, string? value = null)
    {
        if (actionCount < actions.Length) actions[actionCount++] = new(now - origin, kind, value);
    }
    private static void Try(Action action)
    {
        try { action(); } catch (MidiPortException) { }
    }
    private void Publish(double now)
    {
        lastPublish = now; dirty = false;
        Volatile.Write(ref status, new(transport.Running, transport.Position(now), transport.Bpm, transport.LateNoteOns, transport.SkippedClocks,
            entryCount, !overflow, Interlocked.Read(ref droppedInputs), error, take));
    }
    private void Run()
    {
        var wait = options.Wait ?? new SleepWait();
        while (!exit.IsCancellationRequested)
        {
            try { Step(clock()); }
            catch (Exception ex)
            {
                error = ex.Message;
                try { transport.Panic(clock()); } catch (Exception) { }
                Publish(clock());
            }
            wait.Wait(TimeSpan.FromMilliseconds(1));
        }
    }
    public void Dispose()
    {
        if (worker is null) { Step(clock()); transport.Stop(clock()); ReleaseThru(clock()); }
        else if (!exit.IsCancellationRequested)
        {
            try { Post(Kind.Exit); } catch (InvalidOperationException) { exit.Cancel(); }
            if (!worker.Join(TimeSpan.FromSeconds(2))) exit.Cancel();
            worker.Join(TimeSpan.FromSeconds(2));
        }
        options.Wait?.Dispose();
        exit.Dispose();
    }
}
