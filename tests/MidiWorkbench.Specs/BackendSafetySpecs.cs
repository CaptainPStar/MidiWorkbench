using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Application;

namespace MidiWorkbench.Specs;

public static class BackendSafetySpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        SustainCleanup(false, false);
        SustainCleanup(true, false);
        SustainCleanup(false, true);
        SustainCleanup(true, true);
        PanicDuringStopTail();
        DisposeCancelsScheduledOutput();
        InputLossReleasesThru();
        PreStartInputIsNotCaptured();
        SharedPedalOwnership();
        PanicDiscardsInputBacklog();
        InvalidStartPreservesCapture();
        ReopenFailedThru();
        InputDisconnectStopsWithoutUiPolling();
        SessionInputReconnect();
        OfflineUnarmedInputDoesNotBlockPlayback();
        OutputFaultTakesPriorityOverOfflineInputNotice();
    }
    private static ResolvedPlan PedalPlan() => Build.Plan(new MidiNote("n", 0, 960, 60, 100)) with
    {
        Events = [new(0, 0, 0, 5, 0xB0, 64, 127), new(0, 0, 0, 0, 0x90, 60, 100), new(960, 0, 0, 0, 0x80, 60, 0)]
    };
    private static void SustainCleanup(bool scheduled, bool panic)
    {
        double now = 0;
        var port = new FakeBackend(() => now).Add("memory", scheduled);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.Start(PedalPlan(), [port], 120, true); host.Step(now);
        now = .005;
        if (panic) host.Panic(); else host.Stop();
        host.Step(now); now = .02; host.Step(now);
        Check(port.Sent.Any(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), $"{scheduled}/{panic}: sustain remained down");
        Check(port.Count(0x90) == port.Count(0x80), $"{scheduled}/{panic}: unbalanced notes");
    }
    private static void PanicDuringStopTail()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now, LookaheadSeconds: .1));
        host.Start(Build.Plan(new MidiNote("n", 0, 3000, 60, 100)), [port], 120, true); host.Step(now);
        now = .01; var stop = host.Stop(); host.Step(now);
        Check(!stop.IsCompleted, "Test failed to queue a stop tail");
        now = .02; host.Panic(); host.Step(now);
        Check(port.Count(0x80) == 1 && port.Sent.Single(x => x.Message.Status == 0x80).Message.Seconds == now, "Panic cancelled the sole release after Stop");
        Check(stop.IsCompletedSuccessfully && !host.Status.OutputPending, "Panic did not settle Stop waiter");
    }
    private static void DisposeCancelsScheduledOutput()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", true);
        var host = new TransportHost(new HostOptions(Background: false, Clock: () => now, LookaheadSeconds: .2));
        host.Start(PedalPlan(), [port], 120, true); host.Step(now);
        Check(port.Sent.Any(x => x.Message.Seconds > now), "Test failed to submit future output");
        now = .01; host.Dispose(); host.Dispose();
        Check(!port.Sent.Any(x => x.Message.Seconds > now), "Dispose left scheduled output alive");
        Check(port.Count(0x80) == 1 && port.Sent.Any(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), "Dispose left a held note/pedal");
        try { host.Stop(); throw new Exception("Disposed host accepted commands"); } catch (ObjectDisposedException) { }
    }
    private static void InputLossReleasesThru()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now, InputCapacity: 2));
        host.Arm(port, 1); host.Start(Build.Plan(), [port], 120, true); host.Step(now);
        host.PostInput(new(now, 0x90, 67, 100)); host.PostInput(new(now, 0xB0, 64, 127)); host.Step(now);
        host.PostInput(new(now, 0xB0, 1, 1)); host.PostInput(new(now, 0xB0, 1, 2)); host.PostInput(new(now, 0x80, 67, 0));
        now = .01; host.Step(now);
        Check(port.Count(0x80) == 1 && port.Sent.Any(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), "Dropped input release left thru stuck");
        Check(!host.Status.CaptureComplete && host.Status.DroppedInputs == 1, "Input loss was not reported");
        host.PostInput(new(now, 0x90, 67, 100)); host.Step(now);
        Check(port.Count(0x90) == 2, "Thru failed to resume after loss recovery");
    }
    private static void PreStartInputIsNotCaptured()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.PostInput(new(0, 0x90, 67, 100)); now = 1;
        host.Start(Build.Plan(), [port], 120, true); host.Step(now);
        host.Panic(); host.Step(now);
        Check(host.Capture().Entries.All(x => x.Seconds >= 0), "Pre-start input produced an invalid negative take timestamp");
    }
    private static void SharedPedalOwnership()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.Arm(port, 1); host.Start(PedalPlan(), [port], 120, true); host.Step(now);
        host.PostInput(new(now, 0xB0, 64, 127)); host.Step(now);
        now = .005; host.Stop(); host.Step(now); now = .02; host.Step(now);
        Check(!port.Sent.Any(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), "Generated Stop cut off the live sustain owner");
        host.PostInput(new(now, 0xB0, 64, 0)); host.Step(now);
        Check(port.Sent.Count(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }) == 1, "Final pedal owner did not release sustain");
    }
    private static void PanicDiscardsInputBacklog()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.Arm(port, 1); host.Step(now);
        host.PostInput(new(now, 0x90, 60, 100)); host.Panic(); host.Step(now);
        Check(port.Count(0x90) == 0, "Panic replayed queued keyboard attacks after emergency cleanup");
    }
    private static void InvalidStartPreservesCapture()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.Start(Build.Plan(), [port], 120, true); host.Step(now);
        try { host.Start(Build.Plan(), [port], double.NaN, true); throw new Exception("Invalid tempo accepted"); }
        catch (ArgumentOutOfRangeException) { }
        host.Panic(); host.Step(now);
        Check(host.Capture().Take == 1, "Invalid Start corrupted capture publication");
    }
    private static void ReopenFailedThru()
    {
        double now = 0; var backend = new FakeBackend(() => now); var failed = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.Arm(failed, 1); host.Step(now); failed.Disconnect();
        host.PostInput(new(now, 0x90, 60, 100)); host.Step(now);
        var reopened = backend.Add("memory", false);
        host.Arm(reopened, 1); host.Step(now);
        host.PostInput(new(now, 0x90, 60, 100)); host.Step(now);
        Check(reopened.Count(0x90) == 1, "Failed thru ownership suppressed notes on the reopened endpoint");
    }
    private static void InputDisconnectStopsWithoutUiPolling()
    {
        double now = 0; var port = new FakeBackend(() => now).Add("memory", false); var input = new DisconnectableInput();
        using var host = new TransportHost(new HostOptions(Background: false, Clock: () => now));
        host.WatchInput(input); host.Arm(port, 1); host.Start(PedalPlan(), [port], 120, true); host.Step(now);
        host.PostInput(new(now, 0x90, 67, 100)); host.Step(now);
        input.Disconnect(); now = .01; host.Step(now);
        Check(!host.Status.Running && !host.Status.CaptureComplete && host.Status.Error?.Contains("keyboard") == true, "Input disconnect did not stop/mark incomplete independently of UI polling");
        Check(port.Count(0x90) == port.Count(0x80) && port.Sent.Any(x => x.Message is { Status: 0xB0, Data1: 64, Data2: 0 }), "Input disconnect left generated or thru notes/pedal held");
        Check(!host.Capture().Complete, "Lost input was hidden in saved capture");
    }
    private static void SessionInputReconnect()
    {
        using var backend = new DisconnectableBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "keyboard" });
        session.Arm(WorkbenchSession.NewPhrase); session.StartSection(true);
        backend.Input!.Disconnect();
        Thread.Sleep(30);
        var status = session.Status();
        Check(!status.Running && !status.CaptureComplete && session.Armed is null && session.Takes.Last().Complete == false, "Disconnected session retained arm or archived a complete take");
        session.Arm(WorkbenchSession.NewPhrase);
        Check(backend.Opens == 2 && backend.Input is { Faulted: false }, "Rearm failed to reopen input");
        session.StartSection(true); session.Stop();
        Check(session.Takes.Last().Complete, "Reconnected input inherited the prior take's fault");
    }
    private static void OfflineUnarmedInputDoesNotBlockPlayback()
    {
        using var backend = new DisconnectableBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "keyboard" });
        backend.Input!.Disconnect(); backend.Available = false;
        session.StartSection(true);
        Check(session.Status().Running && session.Armed is null, "Offline unarmed keyboard blocked output playback");
        session.Stop();
        try { session.Arm(WorkbenchSession.NewPhrase); throw new Exception("Recording armed without its input"); }
        catch (MidiPortException) { }
    }
    private static void OutputFaultTakesPriorityOverOfflineInputNotice()
    {
        using var backend = new DisconnectableBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "keyboard" });
        backend.Input!.Disconnect(); backend.Available = false;
        session.StartSection(true);
        Check(session.Status().Error?.Contains("keyboard") == true, "Offline input notice was not established");
        backend.OutputFailed = true;
        Check(SpinWait.SpinUntil(() => !session.Status().Running, TimeSpan.FromSeconds(2)), "Output fault did not stop playback");
        var error = session.Status().Error;
        Check(error?.Contains("memory") == true && !error.Contains("keyboard"), "Optional input notice masked the active output fault");
    }
    private sealed class DisconnectableInput : IMidiInputPort
    {
        private volatile bool faulted;
        public string EndpointId => "keyboard";
        public bool Faulted => faulted;
        public void Disconnect() => faulted = true;
        public void Dispose() => faulted = true;
    }
    private sealed class DisconnectableBackend : IMidiBackend
    {
        private readonly MemoryBackend memory = new();
        public DisconnectableInput? Input { get; private set; }
        public int Opens { get; private set; }
        public bool Available { get; set; } = true;
        public volatile bool OutputFailed;
        public event Action? EndpointsChanged { add { } remove { } }
        public IReadOnlyList<MidiEndpoint> Endpoints() => [..memory.Endpoints(), new("keyboard", "Keyboard", true, false)];
        public IMidiOutputPort OpenOutput(string endpointId) => new FaultableOutput(memory.OpenOutput(endpointId), this);
        public IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received)
        { if (endpointId != "keyboard" || !Available) throw new MidiPortException("Input 'keyboard' is unavailable"); Opens++; return Input = new(); }
        public void Dispose() { Input?.Dispose(); memory.Dispose(); }
        private sealed class FaultableOutput(IMidiOutputPort inner, DisconnectableBackend backend) : IMidiOutputPort
        {
            public string EndpointId => inner.EndpointId;
            public bool ScheduledSend => inner.ScheduledSend;
            public bool Faulted => backend.OutputFailed || inner.Faulted;
            public void Submit(TimedMidi message) => inner.Submit(message);
            public void Cancel(double after) => inner.Cancel(after);
            public void Dispose() => inner.Dispose();
        }
    }
}
