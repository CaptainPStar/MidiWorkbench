using System.Diagnostics;
using System.Collections.Immutable;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Application;

public sealed record SessionStatus(bool Running, double Tick, int CapturedEvents, bool CaptureComplete, long LateNotes, string? Error);

public sealed class WorkbenchSession : IDisposable
{
    private readonly object gate = new();
    private readonly MemoryMidiSink sink = new();
    private readonly Transport transport;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private DateTimeOffset captureStart;
    private double captureOrigin;
    private double initialBpm;
    private readonly List<TempoChange> tempoChanges = new();
    private string? error;
    private SongProject project;
    public SongProject Project { get { lock (gate) return project; } }
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public WorkbenchSession()
    {
        project = CreateDemo(); transport = new(sink);
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public static SongProject CreateDemo()
    {
        var outline = new Outline("Opening", 8, new Meter(4, 4), 40, [0, 1, 3, 5, 7, 8, 10], .35, .5, .25, 1);
        return new(1, "Untitled", 120, [new SectionGenerator().Generate(outline)]);
    }
    public void Generate(Outline outline)
    {
        SongProject snapshot;
        lock (gate)
        {
            if (transport.Running) throw new InvalidOperationException("Stop playback before generating in this foundation build.");
            snapshot = project;
        }
        var section = new SectionGenerator().Generate(outline, snapshot.Sections[0]);
        var candidate = snapshot with { Sections = snapshot.Sections.SetItem(0, section) };
        candidate.Validate();
        lock (gate)
        {
            if (transport.Running || !ReferenceEquals(project, snapshot)) throw new InvalidOperationException("Project changed during generation; retry.");
            project = candidate;
        }
    }
    public void EditNote(string partId, MidiNote note)
    {
        ChangePart(partId, part => part with
        {
            Pattern = part.Pattern with { Kind = part.Pattern.Kind == PatternKind.Fixed ? PatternKind.Fixed : PatternKind.Hybrid,
                Notes = part.Pattern.Notes.Where(n => n.Id != note.Id).Append(note).OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray() }
        });
    }
    public void DeleteNote(string partId, string noteId)
    {
        ChangePart(partId, part => part with
        {
            // Fixed preserves deletions across regeneration until explicit generator conversion exists.
            Pattern = part.Pattern with { Kind = PatternKind.Fixed, Notes = part.Pattern.Notes.Where(n => n.Id != noteId).ToImmutableArray() }
        });
    }
    private void ChangePart(string partId, Func<Part, Part> change)
    {
        lock (gate)
        {
            if (transport.Running) throw new InvalidOperationException("Stop playback before editing in this foundation build.");
            var section = project.Sections[0];
            if (!section.Parts.Any(p => p.Id == partId)) throw new ArgumentException("Unknown part.");
            var candidate = project with { Sections = project.Sections.SetItem(0, section with
                { Parts = section.Parts.Select(p => p.Id == partId ? change(p) : p).ToImmutableArray() }) };
            candidate.Validate(); project = candidate;
        }
    }
    public void SetTempo(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        lock (gate)
        {
            var candidate = project with { Bpm = bpm };
            var now = Now;
            if (transport.Running) tempoChanges.Add(new(now - captureOrigin, bpm));
            transport.SetTempo(now, bpm); project = candidate;
        }
    }
    public void Start()
    {
        lock (gate)
        {
            if (transport.Running) return;
            var plan = PlanCompiler.Compile(project.Sections[0]);
            sink.Clear(); captureStart = DateTimeOffset.UtcNow; captureOrigin = Now;
            initialBpm = project.Bpm; tempoChanges.Clear(); tempoChanges.Add(new(0, initialBpm));
            transport.Start(plan, captureOrigin, project.Bpm); error = null;
        }
    }
    public void Stop() { lock (gate) transport.Stop(Now); }
    public SessionStatus Status()
    {
        lock (gate) return new(transport.Running, transport.Position(Now), sink.Count, !sink.Overflowed, transport.LateNoteOns, error);
    }
    public void Save(string path) => ProjectStore.Save(path, Project);
    public void Load(string path)
    {
        var value = ProjectStore.Load(path);
        lock (gate)
        {
            if (transport.Running) throw new InvalidOperationException("Stop playback before opening a project.");
            project = value;
        }
    }
    public void SaveTake(string path)
    {
        PerformanceTake take;
        lock (gate)
        {
            if (transport.Running) throw new InvalidOperationException("Stop playback before saving the take.");
            if (sink.Count == 0) throw new InvalidOperationException("Play a section before saving a take.");
            take = new(1, captureStart, initialBpm, !sink.Overflowed,
                sink.Snapshot().Select(e => e with { Seconds = e.Seconds - captureOrigin }).ToImmutableArray())
                { TempoChanges = tempoChanges.ToImmutableArray() };
        }
        ProjectStore.SaveTake(path, take);
    }
    private void Run()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { lock (gate) transport.Pump(Now); }
            catch (Exception ex)
            {
                lock (gate) { error = ex.Message; transport.Stop(Now); }
            }
            cancellation.Token.WaitHandle.WaitOne(2);
        }
    }
    public void Dispose()
    {
        cancellation.Cancel(); worker.GetAwaiter().GetResult(); Stop(); cancellation.Dispose();
    }
}
