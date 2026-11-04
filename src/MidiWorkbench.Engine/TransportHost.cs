using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

public sealed record TransportStatus(bool Running, double Tick, double Bpm, long LateNoteOns, long SkippedClocks,
    int Captured, bool CaptureComplete, long DroppedInputs, string? Error, long Take, bool OutputPending = false, string? ActivePlanTag = null, string? QueuedPlanTag = null);
public readonly record struct CaptureEntry(double Seconds, bool Input, int Port, int Destination, byte Status, byte Data1, byte Data2);
// Entry seconds are relative to transport start. Input port index -1 is the rig input.
// Origin is the host-clock time of transport start.
public sealed record TransportCapture(long Take, double Origin, DateTimeOffset StartedAt, double Bpm, bool Complete, bool Loop, long LengthTicks,
    ImmutableArray<CaptureEntry> Entries, ImmutableArray<PerformanceAction> Actions, ImmutableArray<TempoChange> TempoChanges,
    ImmutableArray<string> Endpoints, ImmutableArray<string> Destinations);
public sealed record HostOptions(int CaptureCapacity = 100000, double LookaheadSeconds = .01, int PendingCapacity = 8192,
    int InputCapacity = 4096, bool Background = true, IHostWait? Wait = null, Func<double>? Clock = null, int InputBudget = 256);

// Owns the Transport and the opened output ports while playing. Callers talk to it only through bounded queues and snapshots.
public sealed class TransportHost : IDisposable
{
    private static readonly Lazy<bool> prepared = new(() => { PrepareScheduler(); return true; });
    private enum Kind { Start, Stop, Panic, Tempo, Arm, WatchInput, InputFault, QueuePlan, CancelQueuedPlan, Exit }
    private sealed record Command(Kind Kind, TaskCompletionSource Done)
    {
        public ResolvedPlan? Plan { get; init; }
        public IMidiOutputPort[] Ports { get; init; } = [];
        public IMidiOutputPort? ThruPort { get; init; }
        public int Channel { get; init; }
        public double Bpm { get; init; }
        public bool Loop { get; init; }
        public long QuantumTicks { get; init; }
        public string? Destination { get; init; }
        public IMidiInputPort? Input { get; init; }
    }
    private readonly HostOptions options;
    private readonly Func<double> clock;
    private readonly Transport transport;
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly Channel<TimedMidi> inputs;
    private readonly ConcurrentQueue<Command> urgent = new();
    private readonly CaptureEntry[] entries;
    private readonly PerformanceAction[] actions = new PerformanceAction[4096];
    private readonly TempoChange[] tempos = new TempoChange[4096];
    private int entryCount, actionCount, tempoCount;
    private bool overflow;
    private long takeDroppedInputs;
    private ImmutableArray<string> captureEndpoints = [], captureDestinations = [];
    private readonly List<TaskCompletionSource> stopWaiters = new();
    private string? thruDestination;
    private readonly Dictionary<(string Endpoint, byte Channel, byte Pitch), int> ownership = new();
    private readonly Dictionary<(int Port, int Owner), (int Channel, int Pitch)> generatedOwners = new();
    private readonly Dictionary<(int Port, int Channel, int Pitch), int> generatedCounts = new();
    private readonly Dictionary<(string Endpoint, byte Channel), int> pedal = new();
    private long sequence, take, droppedInputs, recoveredDroppedInputs, publishedSectionRevision;
    private int disposed;
    private volatile bool running;
    private ResolvedPlan? plan;
    private IMidiOutputPort[] ports = [];
    private readonly record struct Pending(int Destination, TimedMidi Message, int Owner = -1);
    private Queue<Pending>[] pending = [];
    private bool[] faulted = [];
    private double[] scheduledUntil = [];
    private IMidiOutputPort? thru;
    private IMidiInputPort? watchedInput;
    private int thruChannel;
    private readonly HashSet<(byte Channel, byte Pitch)> thruHeld = new();
    private bool thruFaulted, stopRequested, loop, dirty, collectingOutput;
    private int pendingCursor;
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
        transport = new(Dispatch, DispatchOwned);
        if (!this.options.Background) return;
        worker = new Thread(Run) { IsBackground = true, Name = "MidiWorkbench transport", Priority = ThreadPriority.Highest };
        worker.Start();
    }
    public TransportStatus Status => Volatile.Read(ref status);
    public Task Start(ResolvedPlan value, IReadOnlyList<IMidiOutputPort> outputs, double bpm, bool repeat)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        if (outputs.Count != value.Endpoints.Length || outputs.Where((p, i) => p.EndpointId != value.Endpoints[i]).Any())
            throw new ArgumentException("Ports do not match the plan endpoints.");
        return Post(Kind.Start, c => c with { Plan = value, Ports = outputs.ToArray(), Bpm = bpm, Loop = repeat });
    }
    public Task QueuePlan(ResolvedPlan value, IReadOnlyList<IMidiOutputPort> outputs, long quantumTicks)
    {
        if (outputs.Count != value.Endpoints.Length || outputs.Where((p, i) => p.EndpointId != value.Endpoints[i]).Any())
            throw new ArgumentException("Ports do not match the plan endpoints.");
        return Post(Kind.QueuePlan, c => c with { Plan = value, Ports = outputs.ToArray(), QuantumTicks = quantumTicks });
    }
    public Task CancelQueuedPlan() => Post(Kind.CancelQueuedPlan);
    public Task Stop() => Post(Kind.Stop);
    public Task Panic() => Post(Kind.Panic);
    public Task SetTempo(double bpm) => Post(Kind.Tempo, c => c with { Bpm = bpm });
    public Task WatchInput(IMidiInputPort? input) => Post(Kind.WatchInput, c => c with { Input = input });
    public Task InputDisconnected(string endpointId) => Post(Kind.InputFault, c => c with { Destination = endpointId });
    // Thru forwards rig input to one port/channel; null disarms.
    public Task Arm(IMidiOutputPort? port, int channel, string? inputEndpointId = null, string? destination = null)
    {
        if (port is not null && port.EndpointId == inputEndpointId) throw new ArgumentException("Input and thru output must differ.");
        return Post(Kind.Arm, c => c with { ThruPort = port, Channel = channel, Destination = destination });
    }
    // Driver threads call this; it never blocks.
    public void PostInput(TimedMidi message)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!inputs.Writer.TryWrite(message)) { Interlocked.Increment(ref droppedInputs); if (running) Interlocked.Increment(ref takeDroppedInputs); }
    }
    private Task Post(Kind kind, Func<Command, Command>? configure = null)
    {
        if (kind != Kind.Exit) ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var command = new Command(kind, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (configure is not null) command = configure(command);
        if (!commands.Writer.TryWrite(command))
        {
            if (kind is Kind.Stop or Kind.Panic or Kind.InputFault or Kind.Exit) urgent.Enqueue(command);
            else throw new InvalidOperationException("Transport is busy; retry.");
        }
        return command.Done.Task;
    }

    public TransportCapture Capture()
    {
        var before = Interlocked.Read(ref sequence);
        if (running || before % 2 == 1) throw new InvalidOperationException("Stop playback before reading the take.");
        var p = plan;
        var result = new TransportCapture(Volatile.Read(ref take), origin, startedAt, startBpm, !overflow && Interlocked.Read(ref takeDroppedInputs) == 0, loop, p?.LengthTicks ?? 0,
            entries.AsSpan(0, Volatile.Read(ref entryCount)).ToImmutableArray(), actions.AsSpan(0, actionCount).ToImmutableArray(),
            tempos.AsSpan(0, tempoCount).ToImmutableArray(), captureEndpoints, captureDestinations);
        if (Interlocked.Read(ref sequence) != before || running) throw new InvalidOperationException("Playback restarted while reading the take.");
        return result;
    }

    public void Step(double now)
    {
        current = now;
        void Process(Command command)
        {
            try
            {
                Execute(command, now); Publish(now); dirty = true;
                if (command.Kind == Kind.Stop && pending.Any(q => q.Count != 0)) stopWaiters.Add(command.Done);
                else command.Done.TrySetResult();
            }
            catch (Exception ex) { command.Done.TrySetException(ex); }
        }
        for (var budget = 64; budget > 0 && commands.Reader.TryRead(out var command); budget--) Process(command);
        for (var budget = 64; budget > 0 && urgent.TryDequeue(out var command); budget--) Process(command);
        if (watchedInput is { Faulted: true } lost)
            Execute(new Command(Kind.InputFault, new TaskCompletionSource()) { Destination = lost.EndpointId }, now);
        for (var i = 0; i < ports.Length; i++)
            if (!faulted[i] && NeedsPort(i, now) && ports[i].Faulted) Fault(i, "endpoint disconnected");
        DrainPending(now);
        var losses = Interlocked.Read(ref droppedInputs);
        if (losses != recoveredDroppedInputs && thru is not null)
        {
            // Once a release is lost, queued key state cannot be trusted. Drop the bounded
            // backlog and release only our thru ownership before accepting fresh input.
            for (var i = 0; i < options.InputCapacity && inputs.Reader.TryRead(out _); i++) { }
            ReleaseThru(now);
            error = "MIDI input overflow; held input notes released. The take is incomplete.";
            dirty = true;
        }
        recoveredDroppedInputs = losses;
        for (var inputBudget = Math.Max(1, options.InputBudget); inputBudget > 0 && inputs.Reader.TryRead(out var input); inputBudget--) Receive(input, now);
        var wasRunning = transport.Running;
        collectingOutput = true;
        try { transport.Pump(now, now + options.LookaheadSeconds); }
        finally { collectingOutput = false; }
        DrainPending(now);
        if (stopRequested) { stopRequested = false; transport.Stop(now); Act(now, "stop", "fault"); }
        else if (wasRunning && !transport.Running) Act(now, "end");
        running = transport.Running;
        if (dirty || now - lastPublish >= .01 || running != status.Running || transport.SectionRevision != publishedSectionRevision || pending.Any(q => q.Count != 0) != status.OutputPending) Publish(now);
        if (stopWaiters.Count != 0 && !pending.Any(q => q.Count != 0))
        {
            foreach (var waiter in stopWaiters) waiter.TrySetResult();
            stopWaiters.Clear();
        }
    }
    private void Execute(Command c, double now)
    {
        switch (c.Kind)
        {
            case Kind.Start:
                if (transport.Running) { transport.Stop(now); Act(now, "stop"); }
                FlushPending();
                Interlocked.Increment(ref sequence);
                plan = c.Plan!; ports = c.Ports; loop = c.Loop;
                pending = ports.Select(_ => new Queue<Pending>()).ToArray();
                captureEndpoints = plan.Endpoints; captureDestinations = plan.Destinations;
                if (thruDestination is not null && !captureDestinations.Contains(thruDestination)) captureDestinations = captureDestinations.Add(thruDestination);
                if (thru is not null && !captureEndpoints.Contains(thru.EndpointId)) captureEndpoints = captureEndpoints.Add(thru.EndpointId);
                faulted = new bool[ports.Length]; scheduledUntil = new double[ports.Length];
                entryCount = 0; actionCount = 0; tempoCount = 0; overflow = false; Interlocked.Exchange(ref takeDroppedInputs, 0); error = null; stopRequested = false;
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
                // Do not replay input attacks which predate this emergency command.
                for (var i = 0; i < options.InputCapacity && inputs.Reader.TryRead(out _); i++) { }
                var cancelled = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < ports.Length; i++)
                {
                    if (!NeedsPort(i, now) || !ports[i].ScheduledSend || faulted[i]) continue;
                    try { ports[i].Cancel(now); scheduledUntil[i] = now; cancelled.Add(ports[i].EndpointId); }
                    catch (MidiPortException ex) { Fault(i, $"cancellation failed: {ex.Message}"); }
                }
                RemoveCancelledCapture(now, cancelled);
                foreach (var q in pending) while (q.TryDequeue(out var item))
                    if (IsAttackOrClock(item.Message)) transport.CountLate(item.Message);
                var wasRunning = transport.Running;
                transport.Panic(now);
                ReleaseThru(now);
                // Stop has already forgotten its planned notes. Panic must still release
                // attacks actually submitted before Stop's pending tail was cancelled.
                for (var i = 0; i < ports.Length; i++) ReleaseSubmitted(i, now);
                if (wasRunning) Act(now, "panic");
                break;
            case Kind.Tempo:
                transport.SetTempo(now, c.Bpm);
                if (transport.Running) { if (tempoCount < tempos.Length) tempos[tempoCount++] = new(transport.LastTempoChangeTime - origin, c.Bpm); else overflow = true; }
                Act(now, "tempo", c.Bpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Kind.WatchInput:
                watchedInput = c.Input;
                break;
            case Kind.InputFault:
                Execute(new Command(Kind.Panic, new TaskCompletionSource()), now);
                watchedInput = null; thru = null; thruDestination = null;
                overflow = true;
                error = $"Input '{c.Destination}' disconnected. The take is incomplete. Reconnect it and arm again.";
                Act(now, "input-fault", c.Destination);
                break;
            case Kind.Arm:
                if (c.ThruPort is not null && c.Channel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(c.Channel));
                ReleaseThru(now);
                thru = c.ThruPort; thruChannel = c.Channel - 1; thruFaulted = false; thruDestination = c.Destination;
                if (take > 0 && thruDestination is not null && !captureDestinations.Contains(thruDestination)) captureDestinations = captureDestinations.Add(thruDestination);
                if (take > 0 && thru is not null && !captureEndpoints.Contains(thru.EndpointId)) captureEndpoints = captureEndpoints.Add(thru.EndpointId);
                Act(now, "arm", thru?.EndpointId);
                break;
            case Kind.QueuePlan:
                if (c.Ports.Length != ports.Length || c.Ports.Where((p, i) => !ReferenceEquals(p, ports[i])).Any())
                    throw new ArgumentException("Queued plan must use the active port instances.");
                transport.QueuePlan(c.Plan!, c.QuantumTicks, now); Act(now, "queue");
                break;
            case Kind.CancelQueuedPlan:
                transport.CancelQueuedPlan(now); Act(now, "cancel-queue");
                break;
            case Kind.Exit:
                Execute(new Command(Kind.Panic, new TaskCompletionSource()), now);
                exit.Cancel();
                break;
        }
        running = transport.Running;
        dirty = true;
    }
    private bool NeedsPort(int port, double now)
    {
        var id = ports[port].EndpointId;
        if (transport.NeedsPort(port) || pending[port].Count != 0 || scheduledUntil[port] > now || thru?.EndpointId == id) return true;
        foreach (var key in ownership.Keys) if (key.Endpoint == id) return true;
        foreach (var key in pedal.Keys) if (key.Endpoint == id) return true;
        return false;
    }
    private void DrainPending(double now)
    {
        while (true)
        {
            var selected = -1;
            var earliest = double.PositiveInfinity;
            // Merge ordered port queues by deadline. Equal deadlines rotate between
            // ports so a chord on one device cannot delay every other device's clock.
            for (var n = 0; n < ports.Length; n++)
            {
                var i = (pendingCursor + n) % ports.Length;
                if (faulted[i] || !pending[i].TryPeek(out var candidate)) continue;
                var m = candidate.Message;
                if (m.Seconds > now && (!ports[i].ScheduledSend || thru?.EndpointId == ports[i].EndpointId || IsNote(m))) continue;
                if (m.Seconds >= earliest) continue;
                selected = i; earliest = m.Seconds;
            }
            if (selected < 0) return;
            var item = pending[selected].Dequeue();
            pendingCursor = (selected + 1) % ports.Length;
            if (now - item.Message.Seconds > Transport.LateLimitSeconds && IsAttackOrClock(item.Message))
                transport.CountLate(item.Message);
            else Submit(selected, item.Destination, item.Message, item.Owner);
        }
    }
    private int ThruDestinationIndex() => thruDestination is null ? -1 : captureDestinations.IndexOf(thruDestination);
    private void Receive(TimedMidi input, double now)
    {
        if (transport.Running && input.Seconds >= origin) Record(new(input.Seconds - origin, true, -1, -1, input.Status, input.Data1, input.Data2));
        if (thru is null || thruFaulted || input.Status is < 0x80 or >= 0xF0) return;
        var kind = input.Status & 0xF0;
        var m = new TimedMidi(now, (byte)(kind | thruChannel), input.Data1, input.Data2);
        var key = ((byte)thruChannel, input.Data1);
        if (kind == 0x90 && input.Data2 > 0) { if (!thruHeld.Add(key)) return; }
        else if (kind is 0x80 or 0x90) { if (!thruHeld.Remove(key)) return; }
        SendOwned(thru, ThruDestinationIndex(), m, 2);
    }
    private void ReleaseThru(double now)
    {
        if (thru is not null && !thruFaulted)
        {
            var pedalKey = (thru.EndpointId, (byte)thruChannel);
            if (pedal.ContainsKey(pedalKey)) SendOwned(thru, ThruDestinationIndex(), new(now, (byte)(0xB0 | thruChannel), 64, 0), 2);
            foreach (var (channel, pitch) in thruHeld)
                SendOwned(thru, ThruDestinationIndex(), new(now, (byte)(0x80 | channel), pitch, 0), 2);
        }
        thruHeld.Clear();
        if (thru is not null)
        {
            // A failed native submission can leave bookkeeping even though releasing
            // through the old handle is impossible. A reopened endpoint must start clean.
            foreach (var key in ownership.Keys.Where(k => k.Endpoint == thru.EndpointId).ToArray())
            {
                var remaining = ownership[key] & ~2;
                if (remaining == 0) ownership.Remove(key); else ownership[key] = remaining;
            }
            foreach (var key in pedal.Keys.Where(k => k.Endpoint == thru.EndpointId).ToArray())
            {
                var remaining = pedal[key] & ~2;
                if (remaining == 0) pedal.Remove(key); else pedal[key] = remaining;
            }
        }
    }
    private static bool IsNote(TimedMidi m) => (m.Status & 0xF0) is 0x80 or 0x90;
    private static bool IsAttackOrClock(TimedMidi m) => m.Status == 0xF8 || ((m.Status & 0xF0) == 0x90 && m.Data2 > 0);
    private void Dispatch(int port, int destination, TimedMidi m) => DispatchCore(port, destination, -1, m);
    private void DispatchOwned(int port, int destination, int owner, TimedMidi m) => DispatchCore(port, destination, owner, m);
    private void DispatchCore(int port, int destination, int owner, TimedMidi m)
    {
        if (faulted[port]) return;
        // Arbitrate notes at their deadline so live input shares the same ownership state.
        var sharedNote = IsNote(m);
        var liveThru = thru?.EndpointId == ports[port].EndpointId;
        if (!collectingOutput && pending[port].Count == 0 && ((!sharedNote && !liveThru && ports[port].ScheduledSend) || m.Seconds <= current))
        { Submit(port, destination, m, owner); return; }
        if (pending[port].Count >= options.PendingCapacity) { Fault(port, "output backlog exceeded"); return; }
        pending[port].Enqueue(new(destination, m, owner));
    }
    private void FlushPending()
    {
        for (var i = 0; i < pending.Length; i++)
            while (!faulted[i] && pending[i].TryDequeue(out var item))
                if (!IsAttackOrClock(item.Message)) Submit(i, item.Destination, item.Message, item.Owner);
                else transport.CountLate(item.Message);
    }
    private void Submit(int port, int destination, TimedMidi m, int owner = -1)
    {
        if (!ports[port].ScheduledSend || m.Seconds < current) m = m with { Seconds = current };
        var kind = m.Status & 0xF0;
        var note = (port, Channel: m.Status & 15, Pitch: (int)m.Data1);
        var attack = kind == 0x90 && m.Data2 > 0;
        var release = kind == 0x80 || kind == 0x90 && m.Data2 == 0;
        if (attack && owner >= 0)
        {
            if (!generatedOwners.TryAdd((port, owner), (note.Channel, note.Pitch))) return;
            generatedCounts.TryGetValue(note, out var count);
            generatedCounts[note] = count + 1;
            if (count != 0) return;
        }
        else if (release && owner >= 0)
        {
            if (!generatedOwners.TryGetValue((port, owner), out var key) || key != (note.Channel, note.Pitch)) return;
            generatedOwners.Remove((port, owner));
            var remaining = generatedCounts[note] - 1;
            if (remaining > 0) { generatedCounts[note] = remaining; return; }
            generatedCounts.Remove(note);
        }
        else if (release)
        {
            foreach (var key in generatedOwners.Where(x => x.Key.Port == port && x.Value == (note.Channel, note.Pitch)).Select(x => x.Key).ToArray())
                generatedOwners.Remove(key);
            generatedCounts.Remove(note);
        }
        SendOwned(ports[port], destination, m, 1, port);
    }
    private void SendOwned(IMidiOutputPort output, int destination, TimedMidi m, int owner, int port = -1)
    {
        var kind = m.Status & 0xF0;
        var key = (output.EndpointId, (byte)(m.Status & 15), m.Data1);
        ownership.TryGetValue(key, out var owners);
        var attack = kind == 0x90 && m.Data2 > 0;
        var release = kind == 0x80 || kind == 0x90 && m.Data2 == 0;
        if (attack) { ownership[key] = owners | owner; if (owners != 0) return; }
        if (release)
        {
            if ((owners & owner) == 0) return;
            owners &= ~owner;
            if (owners != 0) { ownership[key] = owners; return; }
            ownership.Remove(key);
        }
        if (kind == 0xB0 && m.Data1 == 64)
        {
            var pedalKey = (output.EndpointId, (byte)(m.Status & 15));
            pedal.TryGetValue(pedalKey, out var pedalOwners);
            if (m.Data2 >= 64) pedal[pedalKey] = pedalOwners | owner;
            else
            {
                pedalOwners &= ~owner;
                if (pedalOwners != 0) { pedal[pedalKey] = pedalOwners; return; }
                pedal.Remove(pedalKey);
            }
        }
        try
        {
            output.Submit(m);
            if (port >= 0 && output.ScheduledSend && m.Seconds > current)
                scheduledUntil[port] = Math.Max(scheduledUntil[port], m.Seconds);
            if (take > 0 && (owner != 2 || transport.Running || running))
            {
                var index = captureEndpoints.IndexOf(output.EndpointId);
                if (index >= 0) Record(new(m.Seconds - origin, false, index, destination, m.Status, m.Data1, m.Data2));
            }
        }
        catch (MidiPortException ex)
        {
            if (port >= 0) { if (!faulted[port]) Fault(port, ex.Message); }
            else { thruFaulted = true; error = $"Thru output '{output.EndpointId}' failed: {ex.Message}"; }
        }
    }
    private void RemoveCancelledCapture(double now, HashSet<string> cancelled)
    {
        var write = 0;
        for (var read = 0; read < entryCount; read++)
        {
            var e = entries[read];
            if (!e.Input && e.Seconds > now - origin && e.Port >= 0 && e.Port < captureEndpoints.Length &&
                cancelled.Contains(captureEndpoints[e.Port])) continue;
            entries[write++] = e;
        }
        entryCount = write;
    }
    private void ReleaseSubmitted(int port, double now)
    {
        var output = ports[port];
        foreach (var key in pedal.Keys.Where(k => k.Endpoint == output.EndpointId).ToArray())
        {
            var owners = pedal[key];
            if ((owners & 1) != 0) SendOwned(output, -1, new(now, (byte)(0xB0 | key.Channel), 64, 0), 1, port);
            if ((owners & 2) != 0) SendOwned(output, -1, new(now, (byte)(0xB0 | key.Channel), 64, 0), 2, port);
        }
        foreach (var key in ownership.Keys.Where(k => k.Endpoint == output.EndpointId).ToArray())
        {
            var owners = ownership[key];
            if ((owners & 1) != 0) SendOwned(output, -1, new(now, (byte)(0x80 | key.Channel), key.Pitch, 0), 1, port);
            if ((owners & 2) != 0) SendOwned(output, -1, new(now, (byte)(0x80 | key.Channel), key.Pitch, 0), 2, port);
        }
        foreach (var key in generatedOwners.Keys.Where(k => k.Port == port).ToArray()) generatedOwners.Remove(key);
        foreach (var key in generatedCounts.Keys.Where(k => k.Port == port).ToArray()) generatedCounts.Remove(key);
    }
    private void Fault(int port, string message)
    {
        // A host backlog is not necessarily a broken device: release notes already submitted.
        faulted[port] = true;
        ReleaseSubmitted(port, current);
        while (pending[port].TryDequeue(out var item)) if (IsAttackOrClock(item.Message)) transport.CountLate(item.Message);
        overflow = true;
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
        if (actionCount < actions.Length) actions[actionCount++] = new(now - origin, kind, value); else overflow = true;
    }
    private void Publish(double now)
    {
        lastPublish = now; dirty = false; publishedSectionRevision = transport.SectionRevision;
        Volatile.Write(ref status, new(transport.Running, transport.Position(now), transport.Bpm, transport.LateNoteOns, transport.SkippedClocks,
            entryCount, !overflow && Interlocked.Read(ref takeDroppedInputs) == 0, Interlocked.Read(ref droppedInputs), error, take, pending.Any(q => q.Count != 0), transport.ActivePlanTag, transport.QueuedPlanTag));
    }
    private void Run()
    {
        // Compile and exercise cold managed paths against silent ports before sampling
        // the first real transport origin. User commands wait in the normal queue.
        _ = prepared.Value;
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
            var duration = NextWait(clock());
            if (duration > TimeSpan.Zero && !exit.IsCancellationRequested) wait.Wait(duration);
        }
    }
    private TimeSpan NextWait(double now)
    {
        var seconds = .001;
        for (var i = 0; i < pending.Length; i++)
            if (!faulted[i] && pending[i].TryPeek(out var item)) seconds = Math.Min(seconds, item.Message.Seconds - now);
        // A due message gets another iteration immediately; future messages cap the
        // ordinary one-millisecond poll. No spin loop or process-wide timer change.
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }
    private static void PrepareScheduler()
    {
        using var warmup = new TransportHost(new HostOptions(Background: false, CaptureCapacity: 128,
            PendingCapacity: 128, InputCapacity: 16, InputBudget: 16, Clock: () => .5));
        var outputs = new IMidiOutputPort[] { new PreparationPort("prepare-a"), new PreparationPort("prepare-b") };
        var value = new ResolvedPlan(960,
            [new(0, 0, 0, 0, 0x90, 60, 100), new(0, 1, 0, 1, 0x90, 64, 100), new(0, 0, 0, 4, 0xB0, 64, 127),
             new(240, 0, 0, 0, 0x80, 60, 0), new(240, 1, 0, 1, 0x80, 64, 0), new(480, 0, 0, 2, 0x90, 62, 100),
             new(720, 0, 0, 2, 0x80, 62, 0)],
            ["prepare-a", "prepare-b"], ["prepare"], [0, 1], [0, 0]);
        warmup.Arm(outputs[0], 1);
        warmup.Start(value, outputs, 120, true); warmup.Step(0);
        warmup.PostInput(new(.01, 0x90, 60, 100)); warmup.PostInput(new(.01, 0xB0, 64, 127)); warmup.Step(.01);
        warmup.SetTempo(140); warmup.Step(.02);
        warmup.QueuePlan(value, outputs, 240); warmup.Step(.03);
        warmup.Step(.15); warmup.Step(.25);
        warmup.PostInput(new(.26, 0x80, 60, 0)); warmup.Step(.26);
        warmup.Stop(); warmup.Step(.27); warmup.Panic(); warmup.Step(.28);
    }
    private sealed class PreparationPort(string id) : IMidiOutputPort
    {
        public string EndpointId => id;
        public bool ScheduledSend => false;
        public bool Faulted => false;
        public void Submit(TimedMidi message) { }
        public void Cancel(double after) { }
        public void Dispose() { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (worker == Thread.CurrentThread)
        {
            Execute(new Command(Kind.Exit, new TaskCompletionSource()), clock());
            return;
        }
        if (worker is null) { Step(clock()); Execute(new Command(Kind.Exit, new TaskCompletionSource()), clock()); Publish(clock()); foreach (var waiter in stopWaiters) waiter.TrySetResult(); stopWaiters.Clear(); }
        else if (!exit.IsCancellationRequested)
        {
            try { Post(Kind.Exit); } catch (InvalidOperationException) { exit.Cancel(); }
            if (!worker.Join(TimeSpan.FromSeconds(2))) exit.Cancel();
            worker.Join(TimeSpan.FromSeconds(2));
        }
        if (worker is null || !worker.IsAlive) { options.Wait?.Dispose(); exit.Dispose(); }
    }
}
