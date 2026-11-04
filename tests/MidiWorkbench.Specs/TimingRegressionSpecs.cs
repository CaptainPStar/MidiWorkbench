using System.Collections.Immutable;
using MidiWorkbench.Engine;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Specs;

public static class TimingRegressionSpecs
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        EqualDeadlinePortsShareDispatch();
        EarlierDeadlinePrecedesAnotherPortsChord();
        EmptySchedulerIterationsDoNotAllocate();
        StartupPreparationIsSilentAndSeparate();
        PendingDeadlineCapsWorkerWait();
    }
    private static void EqualDeadlinePortsShareDispatch()
    {
        var sent = new List<(int Port, TimedMidi Message)>();
        var outputs = Enumerable.Range(0, 4).Select(i => new OrderedPort(i, sent)).ToArray();
        var events = Enumerable.Range(0, 4).SelectMany(port => Enumerable.Range(0, 16)
            .Select(n => new ResolvedMessage(480, port, port, port * 16 + n, 0x90, (byte)(48 + n), 100))).ToImmutableArray();
        var plan = new ResolvedPlan(3840, events, outputs.Select(p => p.EndpointId).ToImmutableArray(), ["a", "b", "c", "d"], [0, 1, 2, 3], [0, 0, 0, 0]);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(plan, outputs, 120, true);
        for (var ms = 0; ms < 250; ms++) host.Step(ms / 1000d);
        sent.Clear(); host.Step(.25);
        Check(sent.Take(4).All(s => s.Message.Status == 0xF8) && sent.Take(4).Select(s => s.Port).Distinct().Count() == 4,
            "One port's chord delayed another port's equal-deadline clock");
        var notes = sent.Where(s => s.Message.Status == 0x90).ToArray();
        for (var n = 0; n < 16; n++)
            Check(notes.Skip(n * 4).Take(4).Select(s => s.Port).Distinct().Count() == 4,
                "Equal-deadline chord drained one port before servicing its peers");
    }
    private static void EarlierDeadlinePrecedesAnotherPortsChord()
    {
        var sent = new List<(int Port, TimedMidi Message)>();
        var outputs = Enumerable.Range(0, 2).Select(i => new OrderedPort(i, sent)).ToArray();
        var events = new[] { new ResolvedMessage(480, 0, 0, 0, 0x90, 60, 100), new ResolvedMessage(460, 1, 1, 1, 0x90, 62, 100) }.OrderBy(e => e.Tick).ToImmutableArray();
        var plan = new ResolvedPlan(3840, events, ["port0", "port1"], ["a", "b"], [], [0, 0]);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .3));
        host.Start(plan, outputs, 120, true); host.Step(0); sent.Clear(); host.Step(.25);
        Check(sent.Count == 2 && sent[0].Port == 1 && sent[1].Port == 0, "Port index overrode musical deadline order");
    }
    private static void EmptySchedulerIterationsDoNotAllocate()
    {
        var sent = new List<(int Port, TimedMidi Message)>();
        var outputs = Enumerable.Range(0, 4).Select(i => new OrderedPort(i, sent)).ToArray();
        var plan = new ResolvedPlan(3840, [], outputs.Select(p => p.EndpointId).ToImmutableArray(), [], [], [0, 0, 0, 0]);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(plan, outputs, 120, true); host.Step(0);
        for (var i = 0; i < 100; i++) host.Step(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) host.Step(0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated <= 256, $"Empty scheduler iterations allocated {allocated} bytes, creating avoidable GC pressure");
    }
    private static void StartupPreparationIsSilentAndSeparate()
    {
        using var backend = new MemoryBackend();
        using var output = backend.OpenOutput("memory");
        using var host = new TransportHost();
        var start = host.Start(Build.Plan(new MidiNote("real", 0, 960, 72, 100)), [output], 120, true);
        Check(start.Wait(TimeSpan.FromSeconds(2)), "Preparation blocked the first Start command");
        Thread.Sleep(30);
        Check(host.Stop().Wait(TimeSpan.FromSeconds(2)), "Prepared transport failed to stop");
        var capture = host.Capture();
        Check(capture.Take == 1 && capture.Endpoints.SequenceEqual(["memory"]) && capture.Destinations.SequenceEqual(["Test"]),
            "Preparation contaminated real take identity or routing");
        Check(capture.Entries.Where(e => (e.Status & 0xF0) is 0x80 or 0x90).All(e => e.Data1 == 72), "Synthetic preparation notes leaked into the user's take");
        Check(backend.MemorySnapshot().Where(e => (e.Status & 0xF0) == 0x90).All(e => e.Data1 == 72), "Preparation emitted notes through the real backend");
    }
    private static void PendingDeadlineCapsWorkerWait()
    {
        using var backend = new MemoryBackend();
        using var output = backend.OpenOutput("loopback");
        using var wait = new ObservedWait();
        using var host = new TransportHost(new HostOptions(Wait: wait, Clock: () => 0));
        var plan = Build.Plan(new MidiNote("soon", 1, 960, 72, 100)) with { Endpoints = ["loopback"] };
        Check(host.Start(plan, [output], 120, true).Wait(TimeSpan.FromSeconds(2)), "Start command stalled");
        Check(SpinWait.SpinUntil(() => wait.ShortWaitSeen, TimeSpan.FromSeconds(1)), "Worker slept the full poll interval across a nearer MIDI deadline");
    }
    private sealed class ObservedWait : IHostWait
    {
        private int shortWait;
        public bool ShortWaitSeen => Volatile.Read(ref shortWait) != 0;
        public void Wait(TimeSpan duration)
        {
            if (duration > TimeSpan.Zero && duration < TimeSpan.FromMilliseconds(1)) Volatile.Write(ref shortWait, 1);
            Thread.Sleep(1);
        }
        public void Dispose() { }
    }
    private sealed class OrderedPort(int index, List<(int Port, TimedMidi Message)> sent) : IMidiOutputPort
    {
        public string EndpointId { get; } = $"port{index}";
        public bool ScheduledSend => false;
        public bool Faulted => false;
        public void Submit(TimedMidi message) => sent.Add((index, message));
        public void Cancel(double after) { }
        public void Dispose() { }
    }
}
