using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Application;

public sealed record SessionStatus(bool Running, double Tick, int CapturedEvents, bool CaptureComplete, long LateNotes,
    long SkippedClocks, long DroppedInputs, string? Error, long Revision);
public sealed record SessionOptions(IMidiBackend? Backend = null, string? RigPath = null, HostOptions? Host = null);

// Owns project state and device lifetimes. Playback runs on TransportHost's worker; nothing here blocks it.
public sealed class WorkbenchSession : IDisposable
{
    public const string NewPhrase = "+phrase";
    public const string PhraseDestination = "Phrase";
    private readonly Lock gate = new();
    private readonly IMidiBackend backend;
    private readonly TransportHost host;
    private readonly string? rigPath;
    private readonly Dictionary<string, IMidiOutputPort> outputs = new(StringComparer.Ordinal);
    private IMidiInputPort? input;
    private SongProject project;
    private Rig rig;
    private string? armed;
    private long committedTake, revision;
    private string? notice;
    public SongProject Project { get { lock (gate) return project; } }
    public Rig Rig { get { lock (gate) return rig; } }
    public string? Armed { get { lock (gate) return armed; } }
    public long Revision { get { lock (gate) return revision; } }
    public event Action? EndpointsChanged { add => backend.EndpointsChanged += value; remove => backend.EndpointsChanged -= value; }

    public WorkbenchSession(SessionOptions? options = null)
    {
        options ??= new();
        backend = options.Backend ?? new MemoryBackend();
        rigPath = options.RigPath;
        project = CreateDemo();
        rig = Rig.Memory(Destinations(project));
        if (rigPath is not null && File.Exists(rigPath))
        {
            try { rig = ProjectStore.LoadRig(rigPath); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
            { notice = $"Rig not loaded: {ex.Message}"; }
        }
        host = new(options.Host);
        try { OpenInput(); } catch (MidiPortException ex) { notice = ex.Message; }
    }
    public static SongProject CreateDemo()
    {
        var outline = new Outline("Opening", 8, new Meter(4, 4), 40, [0, 1, 3, 5, 7, 8, 10], .35, .5, .25, 1);
        return new(SongProject.CurrentSchemaVersion, "Untitled", 120, [new SectionGenerator().Generate(outline)]);
    }
    public static IEnumerable<string> Destinations(SongProject project) =>
        project.Sections.SelectMany(s => s.Parts).Select(p => p.Destination).Append(PhraseDestination).Distinct(StringComparer.Ordinal);
    public IReadOnlyList<MidiEndpoint> Endpoints() => backend.Endpoints();
    private bool Running => host.Status.Running;

    public void Generate(Outline outline, DrummerSettings? drummer = null, bool basicDrums = false)
    {
        SongProject snapshot;
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before generating in this build.");
            snapshot = project;
        }
        var section = new SectionGenerator().Generate(outline, snapshot.Sections[0], drummer, basicDrums);
        var candidate = snapshot with { Sections = snapshot.Sections.SetItem(0, section) };
        candidate.Validate();
        lock (gate)
        {
            if (Running || !ReferenceEquals(project, snapshot)) throw new InvalidOperationException("Project changed during generation; retry.");
            project = candidate; revision++;
        }
    }
    public void EditNote(string partId, MidiNote note) => ChangePart(partId, part => part with
    {
        Pattern = part.Pattern with { Kind = part.Pattern.Kind == PatternKind.Fixed ? PatternKind.Fixed : PatternKind.Hybrid,
            Notes = part.Pattern.Notes.Where(n => n.Id != note.Id).Append(note).OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray() }
    });
    // Fixed preserves deletions across regeneration until explicit generator conversion exists.
    public void DeleteNote(string partId, string noteId) => ChangePart(partId, part => part with
    {
        Pattern = part.Pattern with { Kind = PatternKind.Fixed, Notes = part.Pattern.Notes.Where(n => n.Id != noteId).ToImmutableArray() }
    });
    private void ChangePart(string partId, Func<Part, Part> change)
    {
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before editing in this build.");
            var section = project.Sections[0];
            if (!section.Parts.Any(p => p.Id == partId)) throw new ArgumentException("Unknown part.");
            var candidate = project with { Sections = project.Sections.SetItem(0, section with
                { Parts = section.Parts.Select(p => p.Id == partId ? change(p) : p).ToImmutableArray() }) };
            candidate.Validate(); project = candidate; revision++;
        }
    }
    public void SetTempo(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        lock (gate) { project = project with { Bpm = bpm }; Await(host.SetTempo(bpm)); }
    }

    public void Start(bool loop = false)
    {
        lock (gate)
        {
            if (Running) return;
            CommitRecording();
            var plan = RouteResolver.Resolve(PlanCompiler.Compile(project.Sections[0]), rig);
            var ports = plan.Endpoints.Select(Output).ToArray();
            notice = null;
            Await(host.Start(plan, ports, project.Bpm, loop));
        }
    }
    public void Stop()
    {
        lock (gate) { Await(host.Stop()); CommitRecording(); }
    }
    public void Panic()
    {
        lock (gate) { Await(host.Panic()); CommitRecording(); }
    }

    // Arms a part (or NewPhrase) for recording and thru from the rig input; null disarms.
    public void Arm(string? target)
    {
        lock (gate)
        {
            if (target is null) { Await(host.Arm(null, 1)); armed = null; return; }
            if (rig.Input is null) throw new InvalidOperationException("Choose an input in Routing before recording.");
            var (destination, channel) = target == NewPhrase ? (PhraseDestination, 1)
                : project.Sections[0].Parts.FirstOrDefault(p => p.Id == target) is { } part ? (part.Destination, part.Channel)
                : throw new ArgumentException("Unknown part.");
            var route = rig.Routes.FirstOrDefault(r => r.Destination == destination)
                ?? throw new InvalidOperationException($"No route for '{destination}'. Set it in Routing.");
            Await(host.Arm(Output(route.EndpointId), route.Channel ?? channel));
            armed = target;
        }
    }
    private void CommitRecording()
    {
        var status = host.Status;
        if (armed is null || status.Running || status.Take <= committedTake) return;
        var capture = host.Capture();
        committedTake = capture.Take;
        var notes = PhraseRecorder.ToNotes(capture, $"rec{capture.Take}");
        if (notes.IsEmpty) return;
        var section = project.Sections[0];
        Section changed;
        if (armed == NewPhrase)
        {
            var number = 1;
            while (section.Parts.Any(p => p.Id == $"phrase-{number}")) number++;
            var id = $"phrase-{number}";
            changed = section with { Parts = section.Parts.Add(new(id, $"Phrase {number}", PartRole.Phrase, PhraseDestination, 1,
                new($"{id}-pattern", PatternKind.Fixed, notes))) };
            armed = id;
        }
        else changed = section with { Parts = section.Parts.Select(p => p.Id != armed ? p : p with { Pattern = p.Pattern with
            {
                Kind = p.Pattern.Kind == PatternKind.Fixed ? PatternKind.Fixed : PatternKind.Hybrid,
                Notes = p.Pattern.Notes.AddRange(notes).OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray()
            } }).ToImmutableArray() };
        var candidate = project with { Sections = project.Sections.SetItem(0, changed) };
        candidate.Validate(); project = candidate; revision++;
    }

    public SessionStatus Status()
    {
        lock (gate)
        {
            var s = host.Status;
            if (!s.Running && armed is not null && s.Take > committedTake)
            {
                try { CommitRecording(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { notice = ex.Message; committedTake = s.Take; }
            }
            return new(s.Running, s.Tick, s.Captured, s.CaptureComplete, s.LateNoteOns, s.SkippedClocks, s.DroppedInputs, notice ?? s.Error, revision);
        }
    }

    public void SetRig(Rig value)
    {
        value.Validate();
        lock (gate)
        {
            Await(host.Stop()); Await(host.Arm(null, 1)); armed = null;
            rig = value;
            if (rigPath is not null) ProjectStore.SaveRig(rigPath, value);
            var stale = outputs.Where(o => !value.Endpoints.Any(e => e.Id == o.Key)).ToArray();
            foreach (var (id, _) in stale) outputs.Remove(id);
            // Release messages from Stop can still be queued inside the lookahead.
            if (stale.Length > 0) _ = Task.Delay(250).ContinueWith(_ => { foreach (var (_, port) in stale) port.Dispose(); }, TaskScheduler.Default);
            input?.Dispose(); input = null;
            OpenInput();
        }
    }
    private void OpenInput()
    {
        if (rig.Input is { } id) input = backend.OpenInput(id, host.PostInput);
    }
    private IMidiOutputPort Output(string endpointId)
    {
        if (outputs.TryGetValue(endpointId, out var port) && !port.Faulted) return port;
        port?.Dispose(); outputs.Remove(endpointId);
        try { port = backend.OpenOutput(endpointId); }
        catch (MidiPortException ex) { throw new InvalidOperationException($"Output '{endpointId}' unavailable: {ex.Message}", ex); }
        outputs[endpointId] = port; return port;
    }

    public void Save(string path) => ProjectStore.Save(path, Project);
    public void Load(string path)
    {
        var value = ProjectStore.Load(path);
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before opening a project.");
            project = value; armed = null; revision++;
        }
        Await(host.Arm(null, 1));
    }
    public void ExportMidi(string path)
    {
        var p = Project; ProjectStore.WriteAtomic(path, MidiFile.FromSection(p.Sections[0], p.Bpm));
    }
    public PerformanceTake Take()
    {
        TransportCapture capture;
        string? inputId;
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before saving the take.");
            capture = host.Capture(); inputId = rig.Input;
        }
        if (capture.Take == 0) throw new InvalidOperationException("Play a section before saving a take.");
        return ToTake(capture, inputId);
    }
    public static PerformanceTake ToTake(TransportCapture capture, string? inputId)
    {
        CapturedMidi Map(CaptureEntry e) => new(e.Seconds, e.Input ? inputId ?? "input" : capture.Endpoints[e.Port],
            e.Destination >= 0 ? capture.Destinations[e.Destination] : null, e.Status, e.Data1, e.Data2);
        return new(PerformanceTake.CurrentSchemaVersion, capture.StartedAt, capture.Bpm, capture.Complete,
            capture.Entries.Where(e => !e.Input).Select(Map).ToImmutableArray(), capture.Entries.Where(e => e.Input).Select(Map).ToImmutableArray(),
            capture.Actions, capture.TempoChanges);
    }
    // .mid writes a Standard MIDI File; anything else writes the JSON take.
    public void SaveTake(string path)
    {
        var take = Take();
        if (!path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)) { ProjectStore.SaveTake(path, take); return; }
        var map = new TempoMap(take.Bpm, take.TempoChanges);
        ProjectStore.WriteAtomic(path, MidiFile.FromTake(take, Project.Sections[0].Outline.Meter, map.TickAt, map.Changes));
    }

    private static void Await(Task task)
    {
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Transport did not respond.");
        }
        catch (AggregateException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Throw(ex.InnerException); }
    }
    public void Dispose()
    {
        host.Dispose();
        input?.Dispose();
        foreach (var port in outputs.Values) port.Dispose();
        backend.Dispose();
    }
}
