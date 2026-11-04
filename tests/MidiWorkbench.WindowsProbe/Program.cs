using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Midi.Windows;

// Explicit opt-in manual acceptance probe. This fixed allowlist cannot address hardware ports.
if (!OperatingSystem.IsWindows() || args.Length is < 2 or > 4 || args[0] != "--approved-virtual")
{
    Console.Error.WriteLine("Windows only. Usage: --approved-virtual REPORT.json [SECONDS] [--expect-capture-overflow] (sends MIDI to Drum, Pad1, Pad2, Lead virtual ports)");
    return 1;
}
string[] names = ["Drum", "Pad1", "Pad2", "Lead"];
var seconds = args.Length >= 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 8;
if (seconds is < 8 or > 300) throw new ArgumentOutOfRangeException(nameof(seconds));
var bars = (seconds + 1) / 2;
var steps = bars * 8;
var expectOverflow = args.Length == 4 && args[3] == "--expect-capture-overflow";
if (args.Length == 4 && !expectOverflow) throw new ArgumentException("Unknown probe option");
if (expectOverflow && bars * 1408 <= 100000) throw new ArgumentException("Expected-overflow mode needs at least 144 seconds of dense output");
using IMidiBackend backend = new WinMmBackend();
var endpoints = backend.Endpoints();
foreach (var name in names)
    if (!endpoints.Any(e => e.Id == $"winmm-out:{name}#1") || !endpoints.Any(e => e.Id == $"winmm-in:{name}#1"))
        throw new InvalidOperationException($"Required approved virtual port '{name}' is unavailable.");
var arrivals = names.Select(_ => new ConcurrentQueue<TimedMidi>()).ToArray();
var inputs = names.Select((name, index) => backend.OpenInput($"winmm-in:{name}#1", arrivals[index].Enqueue)).ToArray();
var outputs = names.Select(name => new MeasuredOutput(backend.OpenOutput($"winmm-out:{name}#1"), steps * 32 + bars * 96 + 512)).ToArray();
var reports = new List<object>();
var failed = false;
try
{
    var planned = new List<PlannedMessage>();
    for (var port = 0; port < names.Length; port++)
    {
        planned.Add(new(0, $"pedal{port}", names[port], (byte)(0xB0 | port), 64, 127));
        for (var step = 0; step < steps; step++)
        for (var note = 0; note < 16; note++)
        {
            var owner = $"{port}/{step}/{note}";
            planned.Add(new(step * 480, owner, names[port], (byte)(0x90 | port), (byte)(48 + note), 100));
            planned.Add(new(step * 480 + 360, owner, names[port], (byte)(0x80 | port), (byte)(48 + note), 0));
        }
    }
    var rig = new Rig(Rig.CurrentSchemaVersion, names.Select(n => new RigEndpoint($"winmm-out:{n}#1")).ToImmutableArray(),
        names.Select(n => new DeviceRoute(n, $"winmm-out:{n}#1")).ToImmutableArray());
    var plan = RouteResolver.Resolve(new(bars * 3840L, planned.OrderBy(e => e.Tick).ThenBy(e => e.Status & 0xF0).ToImmutableArray(), names.ToImmutableArray()), rig);
    using (var host = new TransportHost(new HostOptions(Wait: WindowsMidi.CreateWait())))
    {
        host.Start(plan, outputs, 120, false).Wait(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(bars * 2 + 15);
        while (host.Status.Running && DateTime.UtcNow < deadline) Thread.Sleep(10);
        if (host.Status.Running) throw new TimeoutException("Native probe did not finish.");
        host.Stop().Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(200);
        var capture = host.Capture();
        for (var i = 0; i < names.Length; i++)
        {
            var received = arrivals[i].ToArray();
            var clocks = received.Where(e => e.Status == 0xF8).ToArray();
            var attacks = received.Where(e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0).ToArray();
            var releases = received.Where(e => (e.Status & 0xF0) == 0x80 || (e.Status & 0xF0) == 0x90 && e.Data2 == 0).ToArray();
            var dispatches = outputs[i].Snapshot();
            var noteDispatches = dispatches.Where(e => (e.Message.Status & 0xF0) == 0x90 && e.Message.Data2 > 0).ToArray();
            var clockDispatches = dispatches.Where(e => e.Message.Status == 0xF8).ToArray();
            var deadlineToDispatch = noteDispatches.Select((e, index) => 1000 * (e.Entered - capture.Origin - index / 16 * .25)).ToArray();
            var dispatchToArrival = attacks.Zip(noteDispatches, (a, d) => 1000 * (a.Seconds - d.Entered)).ToArray();
            var adapterCall = dispatches.Select(e => 1000 * (e.Returned - e.Entered)).ToArray();
            var clockDeadlineToDispatch = clockDispatches.Select((e, index) => 1000 * (e.Entered - capture.Origin - index / 48d)).ToArray();
            var latencySamples = attacks.Zip(noteDispatches, (a, s) => 1000 * (a.Seconds - s.Message.Seconds)).ToArray();
            var latency = latencySamples.Order().ToArray();
            var spacing = clocks.Zip(clocks.Skip(1), (a, b) => Math.Abs(1000 * (b.Seconds - a.Seconds - 1d / 48))).Order().ToArray();
            var held = new Dictionary<(byte, byte), int>(); var unmatched = 0;
            foreach (var m in received)
            {
                var key = ((byte)(m.Status & 15), m.Data1);
                if ((m.Status & 0xF0) == 0x90 && m.Data2 > 0) held[key] = held.GetValueOrDefault(key) + 1;
                if ((m.Status & 0xF0) == 0x80 || (m.Status & 0xF0) == 0x90 && m.Data2 == 0)
                { if (held.GetValueOrDefault(key) == 0) unmatched++; else held[key]--; }
            }
            var matches = attacks.Length == noteDispatches.Length && attacks.Zip(noteDispatches).All(x => x.First.Status == x.Second.Message.Status && x.First.Data1 == x.Second.Message.Data1 && x.First.Data2 == x.Second.Message.Data2);
            var passed = attacks.Length == steps * 16 && releases.Length == steps * 16 && clocks.Length == bars * 96 && unmatched == 0 && held.Values.All(n => n == 0)
                && matches && capture.Complete == !expectOverflow && (!expectOverflow || capture.Entries.Length == 100000)
                && host.Status.Error is null && host.Status.LateNoteOns == 0 && host.Status.SkippedClocks == 0
                && received.Any(e => (e.Status & 0xF0) == 0xB0 && e.Data1 == 64 && e.Data2 == 0);
            reports.Add(new { Endpoint = names[i], Passed = passed, Attacks = attacks.Length, Releases = releases.Length, Clocks = clocks.Length,
                UnmatchedReleases = unmatched, Held = held.Values.Sum(), MatchesSubmittedNotes = matches, LatencyMedianMs = Quantile(latency, .5), LatencyP99Ms = Quantile(latency, .99),
                LatencyMaxMs = Quantile(latency, 1), ClockSpacingP99Ms = Quantile(spacing, .99), ClockSpacingMaxMs = Quantile(spacing, 1),
                FirstChordLatencyMaxMs = Quantile(latencySamples.Take(16).Order().ToArray(), 1),
                AfterFirstChordLatencyP99Ms = Quantile(latencySamples.Skip(16).Order().ToArray(), .99),
                DeadlineToDispatch = Stats(deadlineToDispatch), DispatchToArrival = Stats(dispatchToArrival), AdapterCall = Stats(adapterCall),
                ClockDeadlineToDispatch = Stats(clockDeadlineToDispatch),
                FirstChordDeadlineToDispatch = Stats(deadlineToDispatch.Take(16)), AfterFirstChordDeadlineToDispatch = Stats(deadlineToDispatch.Skip(16)),
                host.Status.LateNoteOns, host.Status.SkippedClocks, host.Status.Error, capture.Complete,
                CaptureEntries = capture.Entries.Length, ExpectedCaptureOverflow = expectOverflow });
            var tracePath = Path.ChangeExtension(Path.GetFullPath(args[1]), $"{names[i]}.dispatch.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
            using (var trace = new StreamWriter(tracePath))
            {
                trace.WriteLine("status,data1,data2,captured_host_time,adapter_entered,adapter_returned");
                foreach (var e in dispatches) trace.WriteLine(FormattableString.Invariant($"{e.Message.Status},{e.Message.Data1},{e.Message.Data2},{e.Message.Seconds:R},{e.Entered:R},{e.Returned:R}"));
            }
            if (!passed)
            {
                failed = true;
                Console.Error.WriteLine($"Virtual native acceptance failed for {names[i]}: {attacks.Length}/{releases.Length}/{clocks.Length}");
            }
        }
        var clockStreams = arrivals.Select(q => q.Where(m => m.Status == 0xF8).ToArray()).ToArray();
        var skew = Enumerable.Range(0, clockStreams.Min(s => s.Length)).Select(i => 1000 * (clockStreams.Max(s => s[i].Seconds) - clockStreams.Min(s => s[i].Seconds))).Order().ToArray();
        reports.Add(new { InterPortClockSkewMedianMs = Quantile(skew, .5), InterPortClockSkewP99Ms = Quantile(skew, .99), InterPortClockSkewMaxMs = Quantile(skew, 1) });
        var dispatchClocks = outputs.Select(q => q.Snapshot().Where(m => m.Message.Status == 0xF8).ToArray()).ToArray();
        var dispatchSkew = Enumerable.Range(0, dispatchClocks.Min(s => s.Length)).Select(i => 1000 * (dispatchClocks.Max(s => s[i].Entered) - dispatchClocks.Min(s => s[i].Entered))).ToArray();
        reports.Add(new { InterPortClockDispatchSkew = Stats(dispatchSkew), Origin = capture.Origin });
    }
    // Shutdown while notes and sustain are held, followed by reopening every native output.
    foreach (var queue in arrivals) while (queue.TryDequeue(out _)) { }
    var shutdown = new TransportHost(new HostOptions(Wait: WindowsMidi.CreateWait()));
    shutdown.Start(plan, outputs, 120, true).Wait(TimeSpan.FromSeconds(5));
    Thread.Sleep(50); shutdown.Dispose(); shutdown.Dispose(); Thread.Sleep(100);
    for (var i = 0; i < names.Length; i++)
    {
        var messages = arrivals[i].ToArray();
        var ons = messages.Count(e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0);
        var offs = messages.Count(e => (e.Status & 0xF0) == 0x80 || (e.Status & 0xF0) == 0x90 && e.Data2 == 0);
        var shutdownPassed = ons == 16 && offs == 16 && messages.Any(e => (e.Status & 0xF0) == 0xB0 && e.Data1 == 64 && e.Data2 == 0);
        if (!shutdownPassed) { failed = true; Console.Error.WriteLine($"Shutdown note/pedal check failed on {names[i]}"); }
        reports.Add(new { Endpoint = names[i], ShutdownAttacks = ons, ShutdownReleases = offs, Passed = shutdownPassed });
        outputs[i].Dispose();
        using var reopened = backend.OpenOutput($"winmm-out:{names[i]}#1");
    }
    var result = new { TimestampUtc = DateTimeOffset.UtcNow, Platform = Environment.OSVersion.ToString(),
        Scope = "Approved virtualMIDI ports only; host callback arrival and submission timing, not physical/acoustic timing. No unplug/reconnect test.", Results = reports };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllText(args[1], json); Console.WriteLine(json);
    return failed ? 2 : 0;
}
finally { foreach (var output in outputs) output.Dispose(); foreach (var input in inputs) input.Dispose(); }

static double Quantile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 : sorted[(int)Math.Round((sorted.Length - 1) * fraction)];
static object Stats(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return new { Count = sorted.Length, MedianMs = Quantile(sorted, .5), P99Ms = Quantile(sorted, .99), MaxMs = Quantile(sorted, 1) };
}
