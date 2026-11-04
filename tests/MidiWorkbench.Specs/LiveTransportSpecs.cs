using MidiWorkbench.Domain;
using MidiWorkbench.Engine;

namespace MidiWorkbench.Specs;

public static class LiveTransportSpecs
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        ReplaceQueuedInsideLookahead();
        ReplacementUsesEarlierQuantum();
        CancelReplacementPreservesPlayback();
        ReplacementPreservesThru();
        ClockContinuityAcrossReplacement();
    }
    private static void ReplaceQueuedInsideLookahead()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0;
            var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
            var first = Build.Plan(new MidiNote("first", 0, 3000, 60, 90)) with { Tag = "first" };
            var second = Build.Plan(new MidiNote("second", 0, 480, 62, 90)) with { Tag = "second" };
            var latest = Build.Plan(new MidiNote("latest", 0, 480, 64, 90)) with { Tag = "latest" };
            host.Start(first, [port], 120, true); host.Step(0);
            now = .1; var firstQueue = host.QueuePlan(second, [port], 960); host.Step(now);
            now = .49; host.Step(now);
            Check(host.Status.ActivePlanTag == "first", "Lookahead changed section early");
            now = .495; var secondQueue = host.QueuePlan(latest, [port], 960); host.Step(now);
            Check(firstQueue.IsCompletedSuccessfully && secondQueue.IsCompletedSuccessfully, "Queue rejected");
            now = .5; host.Step(now);
            Check(host.Status.ActivePlanTag == "latest", "Replacement missed the still-pending boundary");
            Check(port.Sent.Count(x => x.Message is { Status: 0x90, Data1: 64 }) == 1 &&
                !port.Sent.Any(x => x.Message is { Status: 0x90, Data1: 62 }), "Outdated candidate played");
            Check(port.Sent.Count(x => x.Message.Status == 0xFA) == 1 && !port.Sent.Any(x => x.Message.Status == 0xFC), "Clock restarted");
        }
    }
    private static void ReplacementUsesEarlierQuantum()
    {
        double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .01));
        var first = Build.Plan(new MidiNote("a", 0, 3000, 60, 90)) with { Tag = "first" };
        var second = Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" };
        host.Start(first, [port], 120, true); host.Step(0);
        now = .1; host.QueuePlan(second, [port], 1920); host.Step(now);
        now = .2; host.QueuePlan(second, [port], 960); host.Step(now);
        now = .5; host.Step(now);
        Check(host.Status.ActivePlanTag == "second", "Shorter quantization retained the later boundary");
    }
    private static void CancelReplacementPreservesPlayback()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .1));
            var first = Build.Plan(new MidiNote("a", 0, 3000, 60, 90)) with { Tag = "first" };
            var second = Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" };
            var latest = Build.Plan(new MidiNote("c", 0, 480, 64, 90)) with { Tag = "latest" };
            host.Start(first, [port], 120, true); host.Step(0);
            now = .1; host.QueuePlan(second, [port], 960); host.Step(now);
            now = .49; host.Step(now);
            now = .495; host.QueuePlan(latest, [port], 960); host.Step(now);
            now = .499; var cancel = host.CancelQueuedPlan(); host.Step(now);
            now = .5; host.Step(now);
            Check(cancel.IsCompletedSuccessfully && host.Status.ActivePlanTag == "first", "Cancel changed active plan");
            Check(!port.Sent.Any(s => s.Message is { Status: 0x90, Data1: 62 or 64 }) && port.Count(0x80) == 0, "Cancel changed sounding notes");
            Check(port.Sent.Count(s => s.Message.Status == 0xFA) == 1 && !port.Sent.Any(s => s.Message.Status == 0xFC), "Cancel restarted clock");
        }
    }
    private static void ReplacementPreservesThru()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false));
            var first = Build.Plan(new MidiNote("a", 0, 3000, 60, 90)) with { Tag = "first" };
            var second = Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" };
            host.Arm(port, 1); host.Start(first, [port], 120, true); host.Step(0);
            now = .1; host.PostInput(new(now, 0x90, 60, 100)); host.QueuePlan(second, [port], 960); host.Step(now);
            now = .5; host.Step(now);
            Check(host.Status.ActivePlanTag == "second" && !port.Sent.Any(s => s.Message is { Status: 0x80, Data1: 60 }), "Transition cut off held thru note");
            now = .51; host.PostInput(new(now, 0x80, 60, 0)); host.Step(now);
            Check(port.Sent.Count(s => s.Message is { Status: 0x80, Data1: 60 }) == 1, "Thru note did not release");
        }
    }

    private static void ClockContinuityAcrossReplacement()
    {
        foreach (var scheduled in new[] { false, true })
        {
            double now = 0; var backend = new FakeBackend(() => now); var port = backend.Add("memory", scheduled);
            using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .01));
            var first = Build.Plan(new MidiNote("a", 0, 3000, 60, 90)) with { Tag = "first" };
            var second = Build.Plan(new MidiNote("b", 0, 480, 62, 90)) with { Tag = "second" };
            var latest = Build.Plan(new MidiNote("c", 0, 480, 64, 90)) with { Tag = "latest" };
            host.Start(first, [port], 120, true);
            for (var i = 0; i <= 600; i++)
            {
                now = i / 1000d;
                if (i == 100) host.QueuePlan(second, [port], 960);
                if (i == 495) host.QueuePlan(latest, [port], 960);
                host.Step(now);
            }
            var pulses = port.Sent.Select(x => x.Message).Where(m => m.Status == 0xF8 && m.Seconds <= .6).ToArray();
            Check(pulses.Length == 29 && pulses.Zip(pulses.Skip(1)).All(p => Math.Abs((p.Second.Seconds - p.First.Seconds) - 1d / 48) < .0011), "Clock lost continuity at replacement");
            Check(port.Sent.Count(x => x.Message.Status == 0xFA) == 1 && !port.Sent.Any(x => x.Message.Status == 0xFC), "Clock restarted");
            Check(port.Sent.Count(x => x.Message is { Status: 0x80, Data1: 60 }) == 1 && port.Sent.Count(x => x.Message is { Status: 0x90, Data1: 64 }) == 1, "Transition note release or attack missing");
        }
    }

}
