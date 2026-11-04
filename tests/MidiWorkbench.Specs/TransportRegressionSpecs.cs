using System.Collections.Immutable;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;

namespace MidiWorkbench.Specs;

public static class TransportRegressionSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        SharedOwnership();
        InputBudgetAndOverflow();
        CancelledCapture();
        StalledQueue();
        ClockOnlyEndpoint();
        UrgentStop();
        QuantizedPlan();
        StopWaitsForTail();
        ReplayControllers();
        JournalOverflow();
        CancelQueued();
        ProjectWideTransition();
        ScheduledOrderingAndPanic();
        CancelAtLookahead();
        ScheduledThruPanic();
        MultiPortPanicAndFault();
        MutedSectionContinuity();
        RealtimeWithChannelOverride();
        DroppedAttackDoesNotSuppressOverlap();
        HostSharedGeneratedOwnership();
        ActiveTagWaitsForBoundary();
        CancelledPreparedPortDisconnect();
        NeededPortsRemainMonitored();
    }
    private static void SharedOwnership()
    {
        double now = 0;
        var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Arm(port, 1, destination: "Keyboard"); host.Start(Build.Plan(new MidiNote("a", 0, 960, 60, 90)), [port], 120, false); host.Step(0);
        host.PostInput(new(.1, 0x90, 60, 100)); now = .1; host.Step(now);
        now = .5; host.Step(now);
        Check(port.Count(0x90) == 1 && port.Count(0x80) == 0, "Generated release cut off thru owner");
        host.PostInput(new(.51, 0xB0, 64, 127)); host.Step(.51);
        host.Arm(null, 1); host.Step(.52);
        Check(port.Sent.Any(s => s.Message is { Status: 0xB0, Data1: 64, Data2: 0 }) && port.Count(0x80) == 1, "Disarm did not release pedal and note");
        host.Stop(); host.Step(.6);
        var capture = host.Capture();
        Check(capture.Entries.Any(e => !e.Input && e.Status == 0xB0 && e.Data1 == 64 && e.Destination >= 0 && capture.Destinations[e.Destination] == "Keyboard"), "Thru destination or output missing from capture");
    }
    private static void InputBudgetAndOverflow()
    {
        var backend = new FakeBackend(() => 0); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, InputCapacity: 4, InputBudget: 1));
        host.Start(Build.Plan(), [port], 120, false); host.Step(0);
        for (var i = 0; i < 5; i++) host.PostInput(new(0, 0x90, (byte)(60 + i), 100));
        host.Step(.01);
        Check(host.Status.DroppedInputs == 1 && !host.Status.CaptureComplete, "Take input overflow not reported");
        Check(host.Status.Captured == 3, "Input work was not bounded"); // Start, clock, one input.
        host.Stop(); host.Step(.02); Check(!host.Capture().Complete, "Capture lost overflow");
        host.Start(Build.Plan(), [port], 120, false); host.Step(.03);
        Check(host.Status.CaptureComplete, "Overflow carried into next take");
    }
    private static void CancelledCapture()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .2));
        host.Start(Build.Plan(new MidiNote("a", 0, 960, 60, 90)), [port], 120, false); host.Step(0);
        now = .01; host.Panic(); host.Step(now);
        var capture = host.Capture();
        Check(!capture.Entries.Any(e => !e.Input && e.Seconds > .01), "Cancelled scheduled output remained captured");
        Check(capture.Entries.Any(e => !e.Input && e.Status == 0x80 && e.Data1 == 60), "Panic release missing");
    }
    private static void StalledQueue()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .2));
        host.Start(Build.Plan(new MidiNote("a", 0, 120, 60, 90), new MidiNote("b", 240, 120, 62, 90)), [port], 120, false); host.Step(0);
        now = 1; host.Step(now);
        Check(!port.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 }), "Stale queued attack burst after stall");
        Check(port.Sent.Any(s => s.Message is { Status: 0x80, Data1: 60, Seconds: 1 }), "Stalled release or actual timestamp lost");
        Check(host.Status.LateNoteOns > 0 && host.Status.SkippedClocks > 0, "Dropped backlog not counted");
    }
    private static void ClockOnlyEndpoint()
    {
        var rig = new Rig(Rig.CurrentSchemaVersion, [new RigEndpoint("clock")], ImmutableArray<DeviceRoute>.Empty);
        var plan = RouteResolver.Resolve(new PlaybackPlan(3840, [], []), rig);
        Check(plan.Endpoints.SequenceEqual(["clock"]) && plan.ClockPorts.SequenceEqual([0]), "Clock-only endpoint omitted");
    }
    private static void UrgentStop()
    {
        var backend = new FakeBackend(() => 0); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(Build.Plan(), [port], 120, false); host.Step(0);
        for (var i = 0; i < 64; i++) host.SetTempo(120);
        var stop = host.Stop(); host.Step(.1);
        Check(stop.IsCompletedSuccessfully && !host.Status.Running, "Stop blocked behind a full command queue");
        try { host.Arm(port, 1, "memory"); throw new Exception("Cyclic thru accepted"); } catch (ArgumentException) { }
    }
    private static void QuantizedPlan()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        var first = Build.Plan(new MidiNote("a", 0, 3000, 60, 90));
        var second = Build.Plan(new MidiNote("b", 0, 480, 62, 90));
        host.Start(first, [port], 120, true); host.Step(0);
        var queued = host.QueuePlan(second, [port], 960); host.Step(.1);
        Check(queued.IsCompletedSuccessfully, "Queue failed");
        for (var i = 101; i <= 800; i++) { now = i / 1000d; host.Step(now); }
        var off = port.Sent.Single(s => s.Message is { Status: 0x80, Data1: 60 });
        var on = port.Sent.Single(s => s.Message is { Status: 0x90, Data1: 62 });
        Check(Math.Abs(off.Message.Seconds - .5) < .002 && Math.Abs(on.Message.Seconds - .5) < .002, "Transition missed quantized boundary");
        Check(port.Sent.Count(s => s.Message.Status == 0xFA) == 1 && !port.Sent.Any(s => s.Message.Status == 0xFC), "Transition restarted clock");
    }

    private static void StopWaitsForTail()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .05));
        host.Start(Build.Plan(new MidiNote("a", 0, 3000, 60, 90)), [port], 120, false); host.Step(0);
        now = .01; var stop = host.Stop(); host.Step(now);
        Check(!stop.IsCompleted && host.Status.OutputPending, "Stop completed before queued release");
        now = .1; host.Step(now);
        Check(stop.IsCompletedSuccessfully && !host.Status.OutputPending, "Stop did not settle");
        Check(host.Capture().Entries.Any(e => !e.Input && e.Status == 0x80 && e.Data1 == 60 && e.Seconds == .1), "Submitted stop tail missing from capture");
    }
    private static void ReplayControllers()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        var plan = Build.Plan() with { Events = [new ResolvedMessage(0, 0, 0, 0, 0xB0, 64, 127), new ResolvedMessage(100, 0, 0, 1, 0xC0, 7, 0)] };
        host.Start(plan, [port], 120, false); host.Step(0);
        now = .1; host.Step(now);
        Check(port.Sent.Any(s => s.Message is { Status: 0xB0, Data1: 64, Data2: 127 }) && port.Sent.Any(s => s.Message is { Status: 0xC0, Data1: 7 }), "Controller or program event lost");
    }

    private static void JournalOverflow()
    {
        var backend = new FakeBackend(() => 0); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(Build.Plan(), [port], 120, true); host.Step(0);
        for (var batch = 0; batch < 66; batch++)
        {
            for (var i = 0; i < 64; i++) host.SetTempo(120);
            host.Step(.01);
        }
        Check(!host.Status.CaptureComplete, "Journal overflow was not reported");
        host.Panic(); host.Step(.02);
        Check(!host.Capture().Complete, "Journal overflow lost from take");
    }
    private static void CancelQueued()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(Build.Plan(new MidiNote("a", 0, 960, 60, 90)), [port], 120, true); host.Step(0);
        host.QueuePlan(Build.Plan(new MidiNote("b", 0, 480, 62, 90)), [port], 960); host.Step(.1);
        host.CancelQueuedPlan(); host.Step(.2);
        for (var i = 201; i <= 700; i++) { now = i / 1000d; host.Step(now); }
        Check(!port.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 }), "Cancelled plan played");
    }

    private static void ProjectWideTransition()
    {
        double now = 0; var backend = new FakeBackend(() => now);
        var a = backend.Add("A", false); var b = backend.Add("B", true); var c = backend.Add("C", true);
        var rig = new Rig(Rig.CurrentSchemaVersion, [new("A"), new("B", SendClock: false), new("C")], [new("left", "A"), new("right", "B")]);
        var left = PlanCompiler.Compile(Build.Section(1, Build.Part("left-part", "left", 1, new MidiNote("a", 0, 3000, 60, 90))));
        var right = PlanCompiler.Compile(Build.Section(1, Build.Part("right-part", "right", 1, new MidiNote("b", 0, 480, 62, 90))));
        var first = RouteResolver.Resolve(left, rig, ["right", "left"]) with { Tag = "first" };
        var second = RouteResolver.Resolve(right, rig, ["left", "right"]) with { Tag = "second" };
        Check(first.Endpoints.SequenceEqual(second.Endpoints) && first.Destinations.SequenceEqual(second.Destinations), "Project route map is unstable");
        Check(first.Endpoints.SequenceEqual(["A", "B", "C"]), "Silent or clock-only endpoints omitted");
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .01));
        host.Start(first, [a, b, c], 120, true); host.Step(0);
        now = .1; var queue = host.QueuePlan(second, [a, b, c], 960); host.Step(now);
        Check(queue.IsCompletedSuccessfully, "Different sections could not queue");
        for (var i = 101; i < 200; i++) { now = i / 1000d; host.Step(now); }
        now = .2; host.SetTempo(240); host.Step(now);
        for (var i = 201; i <= 350; i++) { now = i / 1000d; host.Step(now); }
        Check(host.Status.Tick > 900 && host.Status.ActivePlanTag == "first", "Status switched section before boundary");
        for (var i = 351; i <= 355; i++) { now = i / 1000d; host.Step(now); }
        Check(host.Status.Tick >= 0 && host.Status.Tick < 5 && host.Status.ActivePlanTag == "second", "Status did not publish the new section at its boundary");
        for (var i = 356; i <= 365; i++) { now = i / 1000d; host.Step(now); }
        Check(host.Status.Tick >= 0 && host.Status.Tick < 60, "Status did not advance in new section");
        Check(a.Sent.Any(s => s.Message is { Status: 0x80, Data1: 60 }) && b.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 }), "Transition note routing failed");
        foreach (var port in new[] { a, b, c })
        {
            var expectedStarts = port == b ? 0 : 1;
            Check(port.Sent.Count(s => s.Message.Status == 0xFA) == expectedStarts && !port.Sent.Any(s => s.Message.Status == 0xFC), "Clock restarted during transition");
            Check(port.Sent.Zip(port.Sent.Skip(1)).All(pair => pair.First.Message.Seconds <= pair.Second.Message.Seconds + 1e-9), "Port timestamps reversed");
        }
        var stop = host.Stop(); now = .5; host.Step(now); now = .6; host.Step(now);
        Check(stop.IsCompletedSuccessfully && host.Status.ActivePlanTag is null, "Stop did not clear active section");
        var capture = host.Capture();
        Check(capture.Entries.Any(e => !e.Input && e.Status == 0x90 && e.Data1 == 62 && capture.Destinations[e.Destination] == "right"), "New-section destination lost in capture");
        Check(capture.TempoChanges.Any(t => Math.Abs(t.Seconds - .21) < .001 && t.Bpm == 240), "Effective tempo time not captured");
    }
    private static void ScheduledOrderingAndPanic()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
        host.Start(Build.Plan(new MidiNote("a", 10, 500, 60, 90), new MidiNote("b", 600, 100, 62, 90)), [port], 120, true); host.Step(0);
        for (var i = 1; i <= 20; i++) { now = i / 1000d; host.Step(now); }
        host.SetTempo(300); host.Step(now);
        for (var i = 21; i <= 180; i++) { now = i / 1000d; host.Step(now); }
        Check(port.Sent.Zip(port.Sent.Skip(1)).All(pair => pair.First.Message.Seconds <= pair.Second.Message.Seconds + 1e-9), "Scheduled clock/note timestamps reversed across tempo change");
        Check(port.Sent.Any(s => s.Message.Seconds > now), "Test did not submit future scheduled output");
        host.Panic(); host.Step(now);
        var cap = host.Capture();
        Check(!cap.Entries.Any(e => !e.Input && e.Seconds > now), "Scheduled cancel left future capture entries");
        Check(port.Sent.Any(s => s.Message is { Status: 0x80, Data1: 60 }), "Panic failed to release scheduled-port note");
        Check(!port.Sent.Any(s => s.Message.Seconds > now), "Scheduled output survived cancellation");
    }
    private static void CancelAtLookahead()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
        host.Start(Build.Plan(new MidiNote("a", 0, 3000, 60, 90)) with { Tag = "first" }, [port], 120, true); host.Step(0);
        now = .1; host.QueuePlan(Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" }, [port], 960); host.Step(now);
        for (var i = 101; i <= 490; i++) { now = i / 1000d; host.Step(now); }
        Check(host.Status.Tick > 900, "Status switched during lookahead");
        now = .495; var cancel = host.CancelQueuedPlan(); host.Step(now);
        Check(cancel.IsCompletedSuccessfully, "Launch could not be cancelled before boundary");
        for (var i = 496; i <= 520; i++) { now = i / 1000d; host.Step(now); }
        Check(!port.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 }) && host.Status.ActivePlanTag == "first", "Cancelled lookahead launch played or changed the active tag");
    }

    private static void ScheduledThruPanic()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .2));
        host.Arm(port, 1, destination: "Test");
        host.Start(Build.Plan(new MidiNote("a", 0, 480, 60, 90)), [port], 120, true); host.Step(0);
        now = .1; host.PostInput(new(now, 0x90, 60, 100)); host.PostInput(new(now, 0xB0, 64, 127)); host.Step(now);
        now = .12; host.Panic(); host.Step(now);
        Check(port.Count(0x90) == 1 && port.Count(0x80) == 1, "Scheduled-port thru ownership was not balanced on panic");
        Check(port.Sent.Any(s => s.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), "Scheduled-port panic left sustain down");
        var cap = host.Capture();
        Check(cap.Entries.Count(e => !e.Input && e.Status == 0x80 && e.Data1 == 60) == 1, "Panic release capture incorrect");
        Check(!port.Sent.Any(s => s.Message.Seconds > now), "Future output survived scheduled-port panic");
    }

    private static void MultiPortPanicAndFault()
    {
        double now = 0; var backend = new FakeBackend(() => now);
        var a = backend.Add("A", false); var b = backend.Add("B", true); var c = backend.Add("C", true);
        var section = Build.Section(1,
            Build.Part("a", "a", 1, new MidiNote("a", 0, 3000, 60, 90)),
            Build.Part("b", "b", 1, new MidiNote("b", 0, 3000, 62, 90)),
            Build.Part("c", "c", 1, new MidiNote("c", 0, 3000, 64, 90)));
        var plan = Build.Plan(section, Build.Rig(("a", "A"), ("b", "B"), ("c", "C")));
        using (var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1)))
        {
            host.Start(plan, [a, b, c], 120, true); host.Step(0);
            now = .01;
            Check(b.Sent.Any(s => s.Message.Seconds > now) && c.Sent.Any(s => s.Message.Seconds > now), "Scheduled clocks were not submitted");
            host.Panic(); host.Step(now);
            foreach (var port in new[] { a, b, c })
            {
                Check(port.Count(0x90) == 1 && port.Count(0x80) == 1, "Panic did not release every endpoint");
                Check(port.Sent.Count(s => s.Message.Status == 0xFA) == 1 && port.Sent.Count(s => s.Message.Status == 0xFC) == 1, "Panic clock sequence incorrect");
                Check(!port.Sent.Any(s => s.Message.Seconds > now), "Scheduled endpoint retained cancelled output");
            }
            Check(!host.Capture().Entries.Any(e => !e.Input && e.Seconds > now), "Cancelled multi-port output remained captured");
        }
        now = 0; backend = new FakeBackend(() => now);
        a = backend.Add("A", false); b = backend.Add("B", true); c = backend.Add("C", true);
        using var faultHost = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .01));
        faultHost.Start(plan, [a, b, c], 120, true); faultHost.Step(0);
        b.Disconnect(); now = .02; faultHost.Step(now); now = .1; faultHost.Step(now);
        Check(!faultHost.Status.Running && faultHost.Status.Error?.Contains("B") == true, "Scheduled endpoint fault not reported");
        Check(a.Count(0x80) == 1 && c.Count(0x80) == 1, "Fault did not release other ports");
    }

    private static void MutedSectionContinuity()
    {
        double now = 0; var backend = new FakeBackend(() => now);
        var a = backend.Add("A", false); var b = backend.Add("B", true);
        var rig = Build.Rig(("left", "A"), ("right", "B"));
        var first = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1,
            Build.Part("one", "left", 1, new MidiNote("a", 0, 3000, 60, 90)))), rig, ["left", "right"]);
        var silent = first with { Events = [] };
        var next = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1,
            Build.Part("two", "right", 1, new MidiNote("b", 0, 480, 62, 90)))), rig, ["left", "right"]);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(first, [a, b], 120, true); host.Step(0);
        now = .1; var mute = host.QueuePlan(silent, [a, b], 960); host.Step(now);
        for (var i = 101; i <= 700; i++) { now = i / 1000d; host.Step(now); }
        Check(mute.IsCompletedSuccessfully && a.Count(0x80) == 1, "Silent section did not release prior notes");
        Check(host.Status.Running && !a.Sent.Any(s => s.Message.Status == 0xFC), "Silent section stopped clock");
        var resume = host.QueuePlan(next, [a, b], 960); host.Step(now);
        for (var i = 701; i <= 1050; i++) { now = i / 1000d; host.Step(now); }
        Check(resume.IsCompletedSuccessfully && b.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 }), "Silent section failed to resume on another endpoint");
        foreach (var port in new[] { a, b })
            Check(port.Sent.Count(s => s.Message.Status == 0xFA) == 1 && port.Sent.Count(s => s.Message.Status == 0xF8) > 40 && !port.Sent.Any(s => s.Message.Status == 0xFC), "Clock was interrupted by silent section");
    }

    private static void RealtimeWithChannelOverride()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        var rig = new Rig(Rig.CurrentSchemaVersion, [new("memory", SendClock: false)], [new("synth", "memory", 3)]);
        var playback = new PlaybackPlan(960,
            [new(0, "start", "synth", 0xFA, 0, 0), new(10, "clock", "synth", 0xF8, 0, 0),
             new(20, "stop", "synth", 0xFC, 0, 0), new(30, "note", "synth", 0x90, 60, 90),
             new(40, "note", "synth", 0x80, 60, 0)], ["synth"]);
        var plan = RouteResolver.Resolve(playback, rig, ["synth"]);
        Check(plan.Events.Select(e => e.Status).SequenceEqual(new byte[] { 0xFA, 0xF8, 0xFC, 0x92, 0x82 }), "Channel override corrupted realtime status");
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(plan, [port], 120, false);
        for (var i = 0; i <= 40; i++) { now = i / 1000d; host.Step(now); }
        Check(port.Sent.Select(s => s.Message.Status).SequenceEqual(new byte[] { 0xFA, 0xF8, 0xFC, 0x92, 0x82 }), "Realtime replay changed status on output");
    }

    private static void DroppedAttackDoesNotSuppressOverlap()
    {
        foreach (var scheduled in new[] { false, true })
        foreach (var alreadyQueued in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
            var tick = alreadyQueued ? 150 : 300;
            var plan = Build.Plan(new MidiNote("old", 100, alreadyQueued ? 100 : 900, 60, 70), new MidiNote("new", tick, 300, 60, 100));
            host.Start(plan, [port], 120, false); host.Step(0);
            var stallEnd = alreadyQueued ? 90 : 150;
            now = stallEnd / 1000d; host.Step(now);
            for (var i = stallEnd + 1; i <= 550; i++) { now = i / 1000d; host.Step(now); }
            Check(host.Status.LateNoteOns >= 1, "Stale attack not counted");
            Check(port.Sent.Count(s => s.Message is { Status: 0x90, Data1: 60, Data2: 100 }) == 1, "Stale attack suppressed the later overlapping note");
            var releases = port.Sent.Where(s => s.Message is { Status: 0x80, Data1: 60 }).ToArray();
            Check(releases.Length == 1 && releases[0].Message.Seconds >= (tick + 300) / 1920d - .002, "Dropped owner's release cut off the later note");
        }
    }

    private static void HostSharedGeneratedOwnership()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false));
            host.Start(Build.Plan(new MidiNote("first", 0, 480, 60, 80), new MidiNote("second", 100, 700, 60, 100)), [port], 120, false);
            for (var i = 0; i <= 500; i++) { now = i / 1000d; host.Step(now); }
            var noteEvents = port.Sent.Where(s => (s.Message.Status & 0xF0) is 0x80 or 0x90).ToArray();
            Check(noteEvents.Length == 2 && noteEvents[0].Message.Status == 0x90 && noteEvents[1].Message.Status == 0x80 && noteEvents[1].Message.Seconds >= .416, "Generated overlap was cut off or retriggered");
        }
    }
    private static void ActiveTagWaitsForBoundary()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
        host.Start(Build.Plan(new MidiNote("a", 0, 960, 60, 90)) with { Tag = "first" }, [port], 120, true); host.Step(0);
        now = .1; host.QueuePlan(Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" }, [port], 960); host.Step(now);
        now = .49; host.Step(now);
        Check(host.Status.ActivePlanTag == "first", "Lookahead changed the active tag before host time");
        now = .5; host.Step(now);
        Check(host.Status.ActivePlanTag == "second" && host.Status.Tick == 0, "Boundary did not publish the active tag and position");
    }

    private static void CancelledPreparedPortDisconnect()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now);
            var a = backend.Add("A", false); var b = backend.Add("B", scheduled);
            var rig = new Rig(Rig.CurrentSchemaVersion, [new("A"), new("B", SendClock: false)], [new("a", "A"), new("b", "B")]);
            var first = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1, Build.Part("a", "a", 1, new MidiNote("a", 0, 3000, 60, 90)))), rig, ["a", "b"]);
            var next = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1, Build.Part("b", "b", 1, new MidiNote("b", 0, 960, 62, 90)))), rig, ["a", "b"]);
            using var host = new TransportHost(new HostOptions(Background: false));
            host.Start(first, [a, b], 120, true); host.Step(0);
            now = .1; host.QueuePlan(next, [a, b], 960); host.Step(now);
            now = .2; host.CancelQueuedPlan(); host.Step(now);
            b.Disconnect(); now = .3; host.Step(now);
            Check(host.Status.Running && host.Status.Error is null, "Cancelled prepared port stopped the active section");
            a.Disconnect(); now = .4; host.Step(now);
            Check(!host.Status.Running && host.Status.Error?.Contains("A") == true, "Active-port disconnect was ignored");
        }
    }

    private static void NeededPortsRemainMonitored()
    {
        double now = 0; var backend = new FakeBackend(() => now);
        var a = backend.Add("A", false); var b = backend.Add("B", true);
        var rig = new Rig(Rig.CurrentSchemaVersion, [new("A", false, 100), new("B", false)], [new("a", "A"), new("b", "B")]);
        var first = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1, Build.Part("a", "a", 1, new MidiNote("a", 0, 3000, 60, 90)))), rig, ["a", "b"]);
        var next = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1, Build.Part("b", "b", 1, new MidiNote("b", 0, 960, 62, 90)))), rig, ["a", "b"]);
        using (var host = new TransportHost(new HostOptions(Background: false)))
        {
            host.Start(first, [a, b], 120, true); host.Step(0); now = .1; host.Step(now);
            now = .11; host.QueuePlan(next, [a, b], 960); host.Step(now);
            now = .5; host.Step(now);
            Check(host.Status.Running && host.Status.OutputPending, "Transition did not leave an offset release pending");
            a.Disconnect(); now = .51; host.Step(now);
            Check(!host.Status.Running && host.Status.Error?.Contains("A") == true, "Port with pending release was not monitored");
        }
        now = 0; backend = new FakeBackend(() => now); a = backend.Add("A", false); b = backend.Add("B", true);
        using (var host = new TransportHost(new HostOptions(Background: false)))
        {
            host.Start(first, [a, b], 120, true); host.Step(0);
            now = .1; host.QueuePlan(next, [a, b], 960); host.Step(now);
            b.Disconnect(); now = .11; host.Step(now);
            Check(!host.Status.Running && host.Status.Error?.Contains("B") == true, "Queued target disconnect was not monitored");
        }
        now = 0; backend = new FakeBackend(() => now); a = backend.Add("A", false); b = backend.Add("B", true);
        var clockRig = new Rig(Rig.CurrentSchemaVersion, [new("A", false), new("B", true)], [new("a", "A")]);
        var clockPlan = RouteResolver.Resolve(PlanCompiler.Compile(Build.Section(1, Build.Part("a", "a", 1, new MidiNote("a", 0, 960, 60, 90)))), clockRig);
        using var clockHost = new TransportHost(new HostOptions(Background: false));
        clockHost.Start(clockPlan, [a, b], 120, true); clockHost.Step(0);
        b.Disconnect(); now = .01; clockHost.Step(now);
        Check(!clockHost.Status.Running && clockHost.Status.Error?.Contains("B") == true, "Clock-only endpoint disconnect was ignored");
    }

}
