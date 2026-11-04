using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Application;

public sealed record SessionStatus(bool Running, double Tick, int CapturedEvents, bool CaptureComplete, long LateNotes,
    long SkippedClocks, long DroppedInputs, string? Error, long Revision)
{
    public Section? PlayingSection { get; init; }
}
public enum GenerationPhase { Idle, Preparing, Ready, Cancelled, Failed }
public enum AuditionPhase { Idle, Queued, Playing, Returning }
public sealed record AuditionProgress(AuditionPhase Phase, string? SectionId = null);
public sealed record GenerationProgress(long RequestId, GenerationPhase Phase, string? SectionId = null, string? Error = null);
public sealed record SessionOptions(IMidiBackend? Backend = null, string? RigPath = null, HostOptions? Host = null, string? TakeDirectory = null);

// Owns project state and device lifetimes. Playback runs on TransportHost's worker; nothing here blocks it.
public sealed class WorkbenchSession : IDisposable
{
    public const string NewPhrase = "+phrase";
    public const string PhraseDestination = "Phrase";
    private readonly Lock gate = new();
    private readonly IMidiBackend backend;
    private readonly TransportHost host;
    private readonly string? rigPath;
    private readonly string? takeDirectory;
    private readonly Dictionary<string, IMidiOutputPort> outputs = new(StringComparer.Ordinal);
    private Dictionary<string, PortSlot> planPorts = new(StringComparer.Ordinal);
    private sealed class PortSlot(string id) : IMidiOutputPort
    {
        private IMidiOutputPort? port;
        public string EndpointId => id;
        public bool ScheduledSend => Volatile.Read(ref port)?.ScheduledSend ?? false;
        public bool Faulted => Volatile.Read(ref port)?.Faulted ?? false;
        public bool IsBound => Volatile.Read(ref port) is not null;
        public void Bind(IMidiOutputPort value) => Interlocked.CompareExchange(ref port, value, null);
        public void Submit(TimedMidi message) => (Volatile.Read(ref port) ?? throw new MidiPortException($"Output '{id}' is not open.")).Submit(message);
        public void Cancel(double after) => Volatile.Read(ref port)?.Cancel(after);
        public void Dispose() { }
    }
    private IMidiInputPort? input;
    private SongProject project;
    private SongBank? bank;
    public SongBank? Bank { get { lock (gate) return bank; } }
    private Rig rig;
    private string? armed;
    private long committedTake, revision;
    private string? notice;
    private string selectedSectionId;
    private SongProject? preparedFrom;
    private Section? prepared;
    private CancellationTokenSource? generationCancellation;
    private readonly SemaphoreSlim generationWorker = new(1, 1);
    private long generationRequest;
    private bool disposed;
    private GenerationProgress generationStatus = new(0, GenerationPhase.Idle);
    public GenerationProgress GenerationStatus { get { lock (gate) return generationStatus; } }
    public Section? Candidate { get { lock (gate) return prepared; } }
    private void InvalidateGeneration()
    {
        generationRequest++;
        generationCancellation?.Cancel();
        generationCancellation = null;
        prepared = null; preparedFrom = null;
        generationStatus = new(generationRequest, GenerationPhase.Idle);
    }
    private void EnsureOpen() { ObjectDisposedException.ThrowIf(disposed, this); }
    private readonly Stack<SongProject> undo = new(), redo = new();
    private readonly List<PerformanceTake> takes = new();
    private long archivedTake;
    private Meter? captureMeter;
    private string? captureProjectId, captureProjectName, captureInput, captureDestination, captureEndpoint;
    private readonly Dictionary<string, Section> planSections = new(StringComparer.Ordinal);
    private Section? PlayingSectionFor(TransportStatus status) =>
        status.Running && status.ActivePlanTag is { } tag && planSections.TryGetValue(tag, out var section) ? section : null;
    public Section? PlayingSection { get { lock (gate) return PlayingSectionFor(host.Status); } }
    private ResolvedPlan TagSection(ResolvedPlan plan, Section section, string? tag = null)
    {
        tag ??= "plan-" + Guid.NewGuid().ToString("N");
        if (!host.Status.Running) planSections.Clear();
        planSections[tag] = section;
        return plan with { Tag = tag };
    }
    private readonly Dictionary<string, string> auditionTags = new(StringComparer.Ordinal);
    private string? auditionTag, auditionSectionId;
    private bool auditionQueued, auditionReturning;
    private string? ActiveSection(TransportStatus status) => PlayingSectionFor(status)?.Id;
    public string? PlayingSectionId { get { lock (gate) return PlayingSectionFor(host.Status)?.Id; } }
    public AuditionProgress AuditionStatus
    {
        get { lock (gate) {
            var status = host.Status;
            if (!status.Running) return new(AuditionPhase.Idle);
            if (auditionQueued && status.ActivePlanTag != auditionTag) return new(AuditionPhase.Queued, auditionSectionId);
            if (status.ActivePlanTag is { } tag && auditionTags.TryGetValue(tag, out var id))
                return new(auditionReturning ? AuditionPhase.Returning : AuditionPhase.Playing, id);
            return new(AuditionPhase.Idle);
        } }
    }
    private Dictionary<string, string> captureEndpointDestinations = new(StringComparer.Ordinal);
    public string SelectedSectionId { get { lock (gate) return selectedSectionId; } }
    public Section SelectedSection { get { lock (gate) return project.Sections[SectionIndex]; } }
    public bool CanUndo { get { lock (gate) return undo.Count > 0; } }
    public bool CanRedo { get { lock (gate) return redo.Count > 0; } }
    public IReadOnlyList<PerformanceTake> Takes { get { lock (gate) return takes.ToArray(); } }
    private int SectionIndex => Array.FindIndex(project.Sections.ToArray(), s => s.Id == selectedSectionId);
    private void RequireStopped() { if (Running) throw new InvalidOperationException("Stop playback first."); }
    private void Apply(SongProject value)
    {
        value.Validate(); CancelDiscardedAudition(returnPlaying: true);
        undo.Push(project); redo.Clear(); project = value; revision++;
        InvalidateGeneration();
    }
    public void SelectSection(string id)
    {
        lock (gate) { if (Running && armed is not null) throw new InvalidOperationException("Stop recording before selecting a section."); if (!project.Sections.Any(s => s.Id == id)) throw new ArgumentException("Unknown section."); if (id != selectedSectionId) { CancelDiscardedAudition(returnPlaying: true); InvalidateGeneration(); } if (!Running) { CommitRecording(); Await(host.Arm(null, 1)); armed = null; } selectedSectionId = id; }
    }
    public string AddSection()
    {
        lock (gate) { RequireStopped(); CommitRecording(); var source = project.Sections[SectionIndex];
            var id = "section-" + Guid.NewGuid().ToString("N");
            var section = new SectionGenerator().Generate(source.Outline with { Name = "Section" }) with { Id = id };
            Apply(project with { Sections = project.Sections.Add(section) }); selectedSectionId = id; return id; }
    }
    public string DuplicateSection(string id)
    {
        lock (gate) { RequireStopped(); CommitRecording(); var section = project.Sections.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown section.");
            var next = "section-" + Guid.NewGuid().ToString("N");
            Apply(project with { Sections = project.Sections.Add(section with { Id = next, Outline = section.Outline with { Name = section.Outline.Name + " copy" } }) }); selectedSectionId = next; return next; }
    }
    public void RemoveSection(string id)
    {
        lock (gate) { RequireStopped(); CommitRecording(); if (project.Sections.Length == 1) throw new InvalidOperationException("Keep at least one section.");
            if (!project.Sections.Any(s => s.Id == id)) throw new ArgumentException("Unknown section.");
            Apply(project with { Sections = project.Sections.Where(s => s.Id != id).ToImmutableArray(), Arrangement = project.Arrangement.Where(e => e.SectionId != id).ToImmutableArray() });
            if (selectedSectionId == id) selectedSectionId = project.Sections[0].Id; }
    }
    public void MoveSection(string id, int index)
    {
        lock (gate) { RequireStopped(); CommitRecording(); var old = Array.FindIndex(project.Sections.ToArray(), s => s.Id == id);
            if (old < 0 || index < 0 || index >= project.Sections.Length) throw new ArgumentOutOfRangeException(nameof(index));
            Apply(project with { Sections = project.Sections.RemoveAt(old).Insert(index, project.Sections[old]) }); }
    }
    public void SetArrangement(IEnumerable<ArrangementEntry> entries)
    { lock (gate) { RequireStopped(); CommitRecording(); Apply(project with { Arrangement = entries.ToImmutableArray() }); } }
    public void Undo() => History(undo, redo);
    public void Redo() => History(redo, undo);
    private void History(Stack<SongProject> from, Stack<SongProject> to)
    {
        lock (gate) { RequireStopped(); CommitRecording(); if (from.Count == 0) return; to.Push(project); project = from.Pop();
            if (!project.Sections.Any(s => s.Id == selectedSectionId)) selectedSectionId = project.Sections[0].Id;
            InvalidateGeneration(); revision++; }
    }

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
        takeDirectory = options.TakeDirectory;
        if (takeDirectory is not null)
        {
            Directory.CreateDirectory(takeDirectory);
            foreach (var file in Directory.EnumerateFiles(takeDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                try { takes.Add(ProjectStore.LoadTake(file)); }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) { notice = $"Take not loaded: {ex.Message}"; }
        }
        project = CreateDemo();
        selectedSectionId = project.Sections[0].Id;
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

    public Section PrepareGeneration(Outline outline, DrummerSettings? drummer = null, bool basicDrums = false) =>
        PrepareGenerationAsync(outline, drummer, basicDrums).GetAwaiter().GetResult();
    public Task<Section> PrepareGenerationAsync(Outline outline, DrummerSettings? drummer = null, bool basicDrums = false,
        CancellationToken cancellationToken = default) => PrepareAsync((section, token) =>
    {
        token.ThrowIfCancellationRequested();
        var result = new SectionGenerator().Generate(outline, section, drummer, basicDrums);
        token.ThrowIfCancellationRequested();
        return result with { Parts = result.Parts.Select(p => section.Parts.FirstOrDefault(old => old.Id == p.Id) is { } old
            ? p with { Muted = old.Muted, Solo = old.Solo } : p).ToImmutableArray() };
    }, cancellationToken);
    public Task<Section> PrepareVariationAsync(uint seed, DrummerSettings? drummer = null, bool basicDrums = false,
        CancellationToken cancellationToken = default)
    {
        return PrepareAsync((section, token) =>
        {
            token.ThrowIfCancellationRequested();
            var generators = new Dictionary<string, GeneratorSettings>(StringComparer.Ordinal);
            foreach (var part in section.Parts)
            {
                token.ThrowIfCancellationRequested();
                if (part.Pattern.Kind == PatternKind.Fixed || part.Pattern.Recipe is not { Generator: PatternGenerator.GeneratorId } recipe) continue;
                PatternGenerator.ValidateRecipe(recipe);
                generators.Add(part.Id, recipe.Settings! with { Seed = VariationSeed(seed, part.Id) });
            }
            var result = new SectionGenerator().Generate(section.Outline with { Seed = seed }, section, drummer, basicDrums, generators);
            token.ThrowIfCancellationRequested();
            return result with { Parts = result.Parts.Select(p => section.Parts.FirstOrDefault(old => old.Id == p.Id) is { } old
                ? p with { Muted = old.Muted, Solo = old.Solo } : p).ToImmutableArray() };
        }, cancellationToken);
    }
    private static uint VariationSeed(uint seed, string partId)
    {
        unchecked
        {
            var hash = 2166136261u ^ seed;
            foreach (var c in partId) { hash ^= c; hash *= 16777619u; }
            return hash;
        }
    }
    public Task<Section> PreparePartVariationAsync(string partId, GeneratorSettings settings, uint seed, CancellationToken cancellationToken = default) =>
        PreparePartGenerationAsync(partId, settings with { Seed = seed }, cancellationToken);
    public Section PreparePartGeneration(string partId, GeneratorSettings settings) =>
        PreparePartGenerationAsync(partId, settings).GetAwaiter().GetResult();
    public Task<Section> PreparePartGenerationAsync(string partId, GeneratorSettings settings, CancellationToken cancellationToken = default) =>
        PrepareAsync((section, token) =>
        {
            token.ThrowIfCancellationRequested();
            var part = section.Parts.FirstOrDefault(p => p.Id == partId) ?? throw new ArgumentException("Unknown part.");
            var pattern = new PatternGenerator().Generate(section.Outline, settings, part.Pattern);
            token.ThrowIfCancellationRequested();
            return section with { Parts = section.Parts.Select(p => p.Id == partId ? p with { Pattern = pattern } : p).ToImmutableArray() };
        }, cancellationToken);
    private async Task<Section> PrepareAsync(Func<Section, CancellationToken, Section> generate, CancellationToken cancellationToken)
    {
        SongProject snapshot; Section section; long request; CancellationTokenSource source; CancellationToken token;
        lock (gate)
        {
            EnsureOpen();
            if (!Running) CommitRecording();
            CancelDiscardedAudition(returnPlaying: false);
            InvalidateGeneration();
            snapshot = project; section = project.Sections[SectionIndex]; request = generationRequest;
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            generationCancellation = source; token = source.Token;
            generationStatus = new(request, GenerationPhase.Preparing, section.Id);
        }
        try
        {
            var computation = Task.Run(async () =>
            {
                await generationWorker.WaitAsync(token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return generate(section, token); }
                finally { generationWorker.Release(); }
            });
            _ = computation.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await computation.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            (snapshot with { Sections = snapshot.Sections.SetItem(Array.FindIndex(snapshot.Sections.ToArray(), s => s.Id == section.Id), result) }).Validate();
            lock (gate)
            {
                if (disposed || request != generationRequest || !ReferenceEquals(project, snapshot) || selectedSectionId != section.Id || token.IsCancellationRequested)
                    throw new OperationCanceledException("Generation was superseded.");
                prepared = result; preparedFrom = snapshot; generationCancellation = null;
                generationStatus = new(request, GenerationPhase.Ready, section.Id); return result;
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (request == generationRequest)
                {
                    generationCancellation = null;
                    generationStatus = new(request, ex is OperationCanceledException ? GenerationPhase.Cancelled : GenerationPhase.Failed,
                        section.Id, ex is OperationCanceledException ? null : ex.Message);
                }
            }
            throw;
        }
        finally { source.Dispose(); }
    }
    public void CancelGeneration()
    {
        lock (gate)
        {
            if (generationCancellation is null) return;
            var id = generationRequest; var sectionId = generationStatus.SectionId;
            generationRequest++; generationCancellation.Cancel(); generationCancellation = null;
            generationStatus = new(id, GenerationPhase.Cancelled, sectionId);
        }
    }
    public void AcceptGeneration() => AcceptGeneration(0);
    // The project changes when acceptance succeeds; cancelling its queued launch does not undo the edit.
    public void AcceptGeneration(long quantumTicks)
    {
        lock (gate)
        {
            EnsureOpen();
            if (prepared is null || !ReferenceEquals(preparedFrom, project) || prepared.Id != selectedSectionId) throw new InvalidOperationException("No current candidate.");
            var candidate = project with { Sections = project.Sections.SetItem(SectionIndex, prepared) };
            candidate.Validate();
            if (Running)
            {
                if (armed is not null) throw new InvalidOperationException("Stop recording before accepting a candidate.");
                if (ActiveSection(host.Status) != prepared.Id) throw new InvalidOperationException("Live acceptance requires the playing section; arrangement playback is not supported.");
                if (quantumTicks <= 0) throw new ArgumentOutOfRangeException(nameof(quantumTicks), "Choose a beat or bar boundary.");
                var temporary = candidate with { Sections = [prepared], Arrangement = [] };
                var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), prepared);
                QueueResolved(plan, quantumTicks);
                auditionQueued = false;
                auditionReturning = host.Status.ActivePlanTag is { } activeTag && auditionTags.ContainsKey(activeTag);
            }
            Apply(candidate);
        }
    }
    public void AuditionCandidate(long quantumTicks)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!Running || armed is not null) throw new InvalidOperationException("Play a section without recording to audition.");
            if (prepared is null || !ReferenceEquals(preparedFrom, project) || prepared.Id != selectedSectionId) throw new InvalidOperationException("No current candidate.");
            if (ActiveSection(host.Status) != prepared.Id) throw new InvalidOperationException("Audition requires the playing section; arrangement playback is not supported.");
            if (quantumTicks <= 0) throw new ArgumentOutOfRangeException(nameof(quantumTicks));
            var temporary = project with { Sections = [prepared], Arrangement = [] };
            var tag = "audition-" + Guid.NewGuid().ToString("N");
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), prepared, tag);
            QueueResolved(plan, quantumTicks);
            var currentTag = host.Status.ActivePlanTag;
            foreach (var old in auditionTags.Keys.Where(key => key != currentTag).ToArray()) auditionTags.Remove(old);
            auditionTags[tag] = prepared.Id; auditionTag = tag; auditionSectionId = prepared.Id;
            auditionQueued = true; auditionReturning = false;
        }
    }
    public void ReturnFromAudition(long quantumTicks)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!Running) throw new InvalidOperationException("Playback is stopped.");
            if (quantumTicks <= 0) throw new ArgumentOutOfRangeException(nameof(quantumTicks));
            var activeTag = host.Status.ActivePlanTag;
            if (activeTag is null || !auditionTags.TryGetValue(activeTag, out var id))
            {
                if (!auditionQueued) throw new InvalidOperationException("No audition is playing or queued.");
                Await(host.CancelQueuedPlan()); auditionQueued = false; auditionTag = null; auditionSectionId = null; return;
            }
            var section = project.Sections.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Audition section is unavailable.");
            var temporary = project with { Sections = [section], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), section);
            QueueResolved(plan, quantumTicks);
            auditionQueued = false; auditionReturning = true;
        }
    }
    private void CancelDiscardedAudition(bool returnPlaying)
    {
        if (!Running) { auditionQueued = false; return; }
        var tag = host.Status.ActivePlanTag;
        if (auditionQueued && tag != auditionTag)
        {
            Await(host.CancelQueuedPlan()); auditionQueued = false;
        }
        if (returnPlaying && tag is not null && auditionTags.TryGetValue(tag, out var id) && !auditionReturning)
        {
            var section = project.Sections.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Audition section is unavailable.");
            var temporary = project with { Sections = [section], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), section);
            var beat = MusicalTime.Ppq * 4L / section.Outline.Meter.Denominator;
            QueueResolved(plan, beat);
            auditionQueued = false; auditionReturning = true;
        }
    }
    public void DiscardGeneration()
    {
        lock (gate) { CancelDiscardedAudition(returnPlaying: true); InvalidateGeneration(); }
    }
    public void Generate(Outline outline, DrummerSettings? drummer = null, bool basicDrums = false)
    { PrepareGeneration(outline, drummer, basicDrums); AcceptGeneration(); }
    public void RegeneratePart(string partId, GeneratorSettings settings)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording();
            var section = project.Sections[SectionIndex];
            var part = section.Parts.FirstOrDefault(p => p.Id == partId) ?? throw new ArgumentException("Unknown part.");
            var pattern = new PatternGenerator().Generate(section.Outline, settings, part.Pattern);
            ChangePart(partId, p => p with { Pattern = pattern });
        }
    }
    public void SetPatternKind(string partId, PatternKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown pattern kind.");
        ChangePart(partId, p => p with { Pattern = p.Pattern with { Kind = kind } });
    }
    public void SetPartMute(string partId, bool muted) => ChangePerformancePart(partId, p => p with { Muted = muted });
    public void SetPartSolo(string partId, bool solo) => ChangePerformancePart(partId, p => p with { Solo = solo });
    private void ChangePerformancePart(string partId, Func<Part, Part> change)
    {
        lock (gate)
        {
            if (!Running) { ChangePart(partId, change); return; }
            if (armed is not null) throw new InvalidOperationException("Stop recording before changing parts.");
            if (PlayingSectionId != selectedSectionId) throw new InvalidOperationException("Select the playing section before changing parts.");
            var section = project.Sections[SectionIndex];
            if (!section.Parts.Any(p => p.Id == partId)) throw new ArgumentException("Unknown part.");
            var updated = section with { Parts = section.Parts.Select(p => p.Id == partId ? change(p) : p).ToImmutableArray() };
            var candidate = project with { Sections = project.Sections.SetItem(SectionIndex, updated) };
            candidate.Validate();
            var temporary = candidate with { Sections = [updated], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), updated);
            QueueResolved(plan, section.Outline.Meter.BarTicks);
            auditionQueued = false;
            auditionReturning = host.Status.ActivePlanTag is { } activeTag && auditionTags.ContainsKey(activeTag);
            Apply(candidate);
        }
    }
    public void EditNotes(string partId, IEnumerable<MidiNote> upserts, IEnumerable<string> deleteIds)
    {
        ArgumentNullException.ThrowIfNull(upserts); ArgumentNullException.ThrowIfNull(deleteIds);
        lock (gate)
        {
            RequireStopped();
            var section = project.Sections[SectionIndex];
            var part = section.Parts.FirstOrDefault(p => p.Id == partId) ?? throw new ArgumentException("Unknown part.");
            var pattern = NoteSelectionEdits.Apply(part.Pattern, upserts, deleteIds, section.Outline.LengthTicks);
            if (ReferenceEquals(pattern, part.Pattern)) return;
            var changed = section with { Parts = section.Parts.Select(p => p.Id == partId ? p with { Pattern = pattern } : p).ToImmutableArray() };
            Apply(project with { Sections = project.Sections.SetItem(SectionIndex, changed) });
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
            var section = project.Sections[SectionIndex];
            if (!section.Parts.Any(p => p.Id == partId)) throw new ArgumentException("Unknown part.");
            var candidate = project with { Sections = project.Sections.SetItem(SectionIndex, section with
                { Parts = section.Parts.Select(p => p.Id == partId ? change(p) : p).ToImmutableArray() }) };
            Apply(candidate);
        }
    }
    public void SetTempo(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        lock (gate) { Await(host.SetTempo(bpm)); Apply(project with { Bpm = bpm }); }
    }

    private ResolvedPlan ResolveProjectPlan(PlaybackPlan plan)
    {
        var routed = rig.Routes.Select(r => r.Destination).ToHashSet(StringComparer.Ordinal);
        return RouteResolver.Resolve(plan, rig, project.Sections.SelectMany(s => s.Parts)
            .Select(p => p.Destination).Where(routed.Contains));
    }
    private IMidiOutputPort[] PlanPorts(ResolvedPlan plan, bool start)
    {
        if (start) planPorts = new(StringComparer.Ordinal);
        var required = plan.Events.Select(e => e.Port).Concat(plan.ClockPorts).ToHashSet();
        var result = new IMidiOutputPort[plan.Endpoints.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var id = plan.Endpoints[i];
            if (!planPorts.TryGetValue(id, out var slot)) planPorts[id] = slot = new(id);
            if (required.Contains(i))
            {
                if (slot.Faulted) throw new InvalidOperationException($"Output '{id}' disconnected.");
                if (!slot.IsBound) slot.Bind(Output(id));
            }
            result[i] = slot;
        }
        return result;
    }
    private void ResetAudition()
    {
        auditionTags.Clear(); auditionTag = null; auditionSectionId = null;
        auditionQueued = false; auditionReturning = false;
    }
    private void QueueResolved(ResolvedPlan plan, long quantumTicks)
    {
        Await(host.QueuePlan(plan, PlanPorts(plan, false), quantumTicks));
        var active = host.Status.ActivePlanTag;
        foreach (var old in planSections.Keys.Where(key => key != active && key != plan.Tag).ToArray()) planSections.Remove(old);
    }
    private void SnapshotCaptureSource()
    {
        ResetAudition();
        captureProjectId = project.Id; captureProjectName = project.Name; captureInput = rig.Input;
        captureEndpointDestinations = rig.Routes.GroupBy(r => r.EndpointId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Destination, StringComparer.Ordinal);
        captureDestination = armed == NewPhrase ? PhraseDestination : project.Sections[SectionIndex].Parts.FirstOrDefault(p => p.Id == armed)?.Destination;
        captureEndpoint = rig.Routes.FirstOrDefault(r => r.Destination == captureDestination)?.EndpointId;
    }
    public void Start(bool loop = false)
    {
        lock (gate)
        {
            if (Running) return;
            CommitRecording();
            ArchiveTake();
            var playback = armed is null ? ArrangementCompiler.Compile(project) : ArrangementCompiler.Compile(project with { Sections = [project.Sections[SectionIndex]], Arrangement = [] });
            var plan = ResolveProjectPlan(playback);
            if (armed is not null) plan = TagSection(plan, project.Sections[SectionIndex]);
            var ports = PlanPorts(plan, true);
            notice = null;
            captureMeter = armed is null ? project.Sections.First(s => s.Id == (project.Arrangement.IsEmpty ? project.Sections[0].Id : project.Arrangement[0].SectionId)).Outline.Meter : project.Sections[SectionIndex].Outline.Meter;
            SnapshotCaptureSource();
            Await(host.Start(plan, ports, project.Bpm, loop));
        }
    }
    public void StartSection(bool repeat = false) => StartSection(SelectedSectionId, repeat);
    public void StartSection(string id, bool repeat = false)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording(); ArchiveTake();
            var section = project.Sections.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown section.");
            if (armed is not null && id != selectedSectionId) throw new InvalidOperationException("Recording target belongs to the selected section.");
            var temporary = project with { Sections = [section], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), section);
            var ports = PlanPorts(plan, true);
            captureMeter = section.Outline.Meter; SnapshotCaptureSource(); notice = null;
            Await(host.Start(plan, ports, project.Bpm, repeat));
        }
    }
    public void StartArrangement(bool loop = false)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording(); ArchiveTake();
            if (armed is not null) throw new InvalidOperationException("Disarm before arrangement playback.");
            var plan = ResolveProjectPlan(ArrangementCompiler.Compile(project));
            var ports = PlanPorts(plan, true); notice = null;
            captureMeter = project.Sections.First(s => s.Id == (project.Arrangement.IsEmpty ? project.Sections[0].Id : project.Arrangement[0].SectionId)).Outline.Meter;
            SnapshotCaptureSource();
            Await(host.Start(plan, ports, project.Bpm, loop));
        }
    }
    public void StartCandidate(bool loop = false)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording(); ArchiveTake();
            if (prepared is null || !ReferenceEquals(preparedFrom, project) || prepared.Id != selectedSectionId) throw new InvalidOperationException("No current candidate.");
            if (armed is not null) throw new InvalidOperationException("Disarm before audition.");
            var temporary = project with { Sections = [prepared], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), prepared);
            captureMeter = prepared.Outline.Meter;
            SnapshotCaptureSource();
            Await(host.Start(plan, PlanPorts(plan, true), project.Bpm, loop));
        }
    }
    public void QueueSection(string id, long quantumTicks)
    {
        lock (gate)
        {
            if (!Running) throw new InvalidOperationException("Start playback first.");
            if (armed is not null) throw new InvalidOperationException("Cannot change sections while recording.");
            var section = project.Sections.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown section.");
            var temporary = project with { Sections = [section], Arrangement = [] };
            var plan = TagSection(ResolveProjectPlan(ArrangementCompiler.Compile(temporary)), section);
            QueueResolved(plan, quantumTicks); auditionQueued = false;
        }
    }
    public void CancelQueuedSection() { lock (gate) { Await(host.CancelQueuedPlan()); auditionQueued = false; auditionReturning = false; } }
    public void Stop()
    {
        lock (gate) { Await(host.Stop()); ResetAudition(); CommitRecording(); ArchiveTake(); }
    }
    public void Panic()
    {
        lock (gate) { Await(host.Panic()); ResetAudition(); CommitRecording(); ArchiveTake(); }
    }

    // Arms a part (or NewPhrase) for recording and thru from the rig input; null disarms.
    public void Arm(string? target)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording();
            if (target is null) { Await(host.Arm(null, 1)); armed = null; return; }
            if (rig.Input is null) throw new InvalidOperationException("Choose an input in Routing before recording.");
            var (destination, channel) = target == NewPhrase ? (PhraseDestination, 1)
                : project.Sections[SectionIndex].Parts.FirstOrDefault(p => p.Id == target) is { } part ? (part.Destination, part.Channel)
                : throw new ArgumentException("Unknown part.");
            var route = rig.Routes.FirstOrDefault(r => r.Destination == destination)
                ?? throw new InvalidOperationException($"No route for '{destination}'. Set it in Routing.");
            if (rig.Input == MemoryBackend.LoopbackId && route.EndpointId == MemoryBackend.LoopbackId) throw new InvalidOperationException("Loopback input and thru output would create feedback.");
            Await(host.Arm(Output(route.EndpointId), route.Channel ?? channel, destination: destination));
            armed = target;
        }
    }
    private void CommitRecording()
    {
        var status = host.Status;
        if (armed is null || status.Running || status.Take <= committedTake) return;
        var capture = host.Capture();
        var notes = PhraseRecorder.ToNotes(capture, $"rec-{Guid.NewGuid():N}");
        if (notes.IsEmpty) { committedTake = capture.Take; return; }
        var section = project.Sections[SectionIndex];
        Section changed;
        string? createdPart = null;
        if (armed == NewPhrase)
        {
            var number = 1;
            while (section.Parts.Any(p => p.Id == $"phrase-{number}")) number++;
            var id = $"phrase-{number}";
            changed = section with { Parts = section.Parts.Add(new(id, $"Phrase {number}", PartRole.Phrase, PhraseDestination, 1,
                new($"{id}-pattern", PatternKind.Fixed, notes))) };
            createdPart = id;
        }
        else if (!section.Parts.Any(p => p.Id == armed)) throw new InvalidOperationException("Recording target no longer exists.");
        else changed = section with { Parts = section.Parts.Select(p => p.Id != armed ? p : p with { Pattern = p.Pattern with
            {
                Kind = p.Pattern.Kind == PatternKind.Fixed ? PatternKind.Fixed : PatternKind.Hybrid,
                Notes = p.Pattern.Notes.AddRange(notes).OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray()
            } }).ToImmutableArray() };
        var candidate = project with { Sections = project.Sections.SetItem(SectionIndex, changed) };
        Apply(candidate); if (createdPart is not null) armed = createdPart; committedTake = capture.Take;
    }

    public SessionStatus Status()
    {
        lock (gate)
        {
            var s = host.Status;
            if (!s.Running && armed is not null && s.Take > committedTake)
            {
                try { CommitRecording(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { notice = ex.Message; }
            }
            if (!s.Running && s.Take > archivedTake) ArchiveTake();
            return new(s.Running, s.Tick, s.Captured, s.CaptureComplete, s.LateNoteOns, s.SkippedClocks, s.DroppedInputs, notice ?? s.Error, revision)
                { PlayingSection = PlayingSectionFor(s) };
        }
    }

    public void SetRig(Rig value)
    {
        value.Validate();
        lock (gate)
        {
            Await(host.Stop()); CommitRecording(); ArchiveTake(); Await(host.Arm(null, 1)); armed = null;
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

    public void NewBank(string name)
    {
        var value = new SongBank(SongBank.CurrentSchemaVersion, name, [], []); value.Validate();
        lock (gate) bank = value;
    }
    public void LoadBank(string path)
    {
        var value = ProjectStore.LoadBank(path); lock (gate) bank = value;
    }
    public void SaveBank(string path)
    {
        SongBank value; lock (gate) value = bank ?? throw new InvalidOperationException("No bank open.");
        ProjectStore.SaveBank(path, value);
    }
    public string AddCurrentToBank()
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording();
            var current = bank ?? throw new InvalidOperationException("No bank open.");
            var id = Guid.NewGuid().ToString("N");
            bank = current with { Songs = current.Songs.Add(new(id, project)), Order = current.Order.Add(id) }; return id;
        }
    }
    public void SetBankOrder(IEnumerable<string> order)
    {
        lock (gate)
        {
            var current = bank ?? throw new InvalidOperationException("No bank open.");
            var value = current with { Order = order.ToImmutableArray() }; value.Validate(); bank = value;
        }
    }
    public void OpenBankSong(string id)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording(); ArchiveTake();
            var song = bank?.Songs.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown bank song.");
            Await(host.Arm(null, 1)); armed = null; project = song.Project; selectedSectionId = project.Sections[0].Id;
            undo.Clear(); redo.Clear(); InvalidateGeneration(); revision++;
        }
    }
    public void UpdateBankSong(string id)
    {
        lock (gate)
        {
            RequireStopped(); CommitRecording(); var current = bank ?? throw new InvalidOperationException("No bank open.");
            var index = Array.FindIndex(current.Songs.ToArray(), s => s.Id == id);
            if (index < 0) throw new ArgumentException("Unknown bank song.");
            bank = current with { Songs = current.Songs.SetItem(index, new(id, project)) };
        }
    }
    public void Save(string path) => ProjectStore.Save(path, Project);
    public void Load(string path)
    {
        var value = ProjectStore.Load(path);
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before opening a project.");
            CommitRecording(); ArchiveTake(); Await(host.Arm(null, 1));
            project = value; selectedSectionId = value.Sections[0].Id; armed = null; undo.Clear(); redo.Clear(); InvalidateGeneration(); revision++;
        }
    }
    public void ExportMidi(string path)
    {
        var p = Project; ProjectStore.WriteAtomic(path, MidiFile.FromSection(p.Sections.First(s => s.Id == SelectedSectionId), p.Bpm));
    }
    public void ExportArrangementMidi(string path)
    {
        SongProject snapshot; lock (gate) snapshot = project;
        var plan = ArrangementCompiler.Compile(snapshot);
        var entries = snapshot.Arrangement.IsEmpty ? snapshot.Sections.Select(s => new ArrangementEntry(s.Id)) : snapshot.Arrangement;
        var lookup = snapshot.Sections.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var meters = new List<(long Tick, Meter Meter)>(); long offset = 0;
        foreach (var entry in entries)
        for (var i = 0; i < entry.Repeats; i++)
        {
            var section = lookup[entry.SectionId];
            if (meters.Count == 0 || meters[^1].Meter != section.Outline.Meter) meters.Add((offset, section.Outline.Meter));
            offset = checked(offset + section.Outline.LengthTicks);
        }
        var tracks = plan.Events.GroupBy(e => e.Destination).Select(g => new SmfTrack(g.Key,
            g.Select(e => new SmfEvent(e.Tick, e.Status, e.Data1, e.Data2)).ToArray())).ToArray();
        ProjectStore.WriteAtomic(path, MidiFile.Build(meters, [(0, snapshot.Bpm)], tracks));
    }
    public PerformanceTake Take()
    {
        TransportCapture capture;
        string? inputId;
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("Stop playback before saving the take.");
            if (host.Status.OutputPending) Await(host.Stop());
            capture = host.Capture(); inputId = captureInput;
        }
        if (capture.Take == 0) throw new InvalidOperationException("Play a section before saving a take.");
        lock (gate) return CaptureTake(capture, inputId);
    }
    private PerformanceTake CaptureTake(TransportCapture capture, string? inputId)
    {
        var result = ToTake(capture, inputId) with { Meter = captureMeter, SourceProjectId = captureProjectId, SourceProjectName = captureProjectName };
        if (captureDestination is not null && captureEndpoint is not null)
            result = result with { Outputs = result.Outputs.Select(e => e.Destination is null && e.Endpoint == captureEndpoint && e.Status < 0xF0 ? e with { Destination = captureDestination } : e).ToImmutableArray() };
        result = result with { Outputs = result.Outputs.Select(e => e.Destination is null && e.Status < 0xF0 && captureEndpointDestinations.TryGetValue(e.Endpoint, out var destination) ? e with { Destination = destination } : e).ToImmutableArray() };
        return result;
    }
    private void ArchiveTake()
    {
        if (host.Status.OutputPending) Await(host.Stop());
        var capture = host.Capture();
        if (capture.Take > archivedTake)
        {
            var take = CaptureTake(capture, captureInput);
            if (takeDirectory is not null) ProjectStore.SaveTake(Path.Combine(takeDirectory, $"{take.StartedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"), take);
            takes.Add(take); archivedTake = capture.Take;
        }
    }
    public PerformanceTake LoadTake(string path)
    {
        var take = ProjectStore.LoadTake(path); lock (gate) { takes.Add(take); return take; }
    }
    public void ReplayTake(PerformanceTake take) => PlayTake(take);
    public void ReplayTake(PerformanceTake take, IReadOnlyDictionary<string, string> endpointDestinations) => PlayTake(take, endpointDestinations);
    public void ReplayTake(PerformanceTake take, IReadOnlyDictionary<string, string> endpointDestinations, IReadOnlyDictionary<string, string> clockEndpoints) => PlayTake(take, endpointDestinations, clockEndpoints);
    public void PlayTake(PerformanceTake take, IReadOnlyDictionary<string, string>? endpointDestinations = null, IReadOnlyDictionary<string, string>? clockEndpoints = null)
    {
        ProjectStore.ValidateTake(take);
        lock (gate)
        {
            RequireStopped(); CommitRecording(); ArchiveTake();
            var events = new List<PlannedMessage>();
            var realtime = new List<(int Order, long Tick, string Endpoint, byte Status, byte Data1, byte Data2)>();
            var musicalOrder = new List<int>();
            var order = 0;
            var open = new Dictionary<(string Destination, int Channel, int Pitch), Queue<string>>();
            var serial = 0;
            foreach (var e in take.Outputs.OrderBy(e => e.Seconds))
            {
                var eventOrder = order++;
                var tick = checked((long)Math.Round(e.Seconds * take.Bpm * MusicalTime.Ppq / 60));
                if (e.Status >= 0xF8)
                {
                    string? endpoint = null;
                    if (clockEndpoints is not null && clockEndpoints.TryGetValue(e.Endpoint, out var mapped)) endpoint = mapped;
                    else if (rig.Endpoints.Any(x => x.Id == e.Endpoint && x.SendClock)) endpoint = e.Endpoint;
                    else
                    {
                        var candidates = rig.Endpoints.Where(x => x.SendClock).Select(x => x.Id).ToArray();
                        if (candidates.Length == 1) endpoint = candidates[0];
                    }
                    if (endpoint is null || !rig.Endpoints.Any(x => x.Id == endpoint && x.SendClock))
                        throw new InvalidOperationException($"Choose a clock output for captured endpoint '{e.Endpoint}'.");
                    realtime.Add((eventOrder, tick, endpoint, e.Status, e.Data1, e.Data2));
                    continue;
                }
                var destination = e.Destination;
                if (destination is null)
                {
                    if (endpointDestinations is not null && endpointDestinations.TryGetValue(e.Endpoint, out var selected)) destination = selected;
                    else
                    {
                        var candidates = rig.Routes.Where(r => r.EndpointId == e.Endpoint).Select(r => r.Destination).ToArray();
                        if (candidates.Length != 1) throw new InvalidOperationException($"Choose a destination for captured endpoint '{e.Endpoint}'.");
                        destination = candidates[0];
                    }
                }
                if (!rig.Routes.Any(r => r.Destination == destination)) throw new InvalidOperationException($"No route for '{destination}'.");
                var kind = e.Status & 0xF0; var key = (destination, e.Status & 15, (int)e.Data1);
                string owner;
                var status = e.Status;
                if (kind == 0x90 && e.Data2 > 0)
                {
                    owner = "take-" + serial++;
                    if (!open.TryGetValue(key, out var queue)) open[key] = queue = new();
                    queue.Enqueue(owner);
                }
                else if (kind == 0x80 || kind == 0x90 && e.Data2 == 0)
                {
                    if (!open.TryGetValue(key, out var queue) || queue.Count == 0) throw new InvalidOperationException("Take contains a note release without its attack.");
                    owner = queue.Dequeue(); status = (byte)(0x80 | (e.Status & 15));
                }
                else owner = "message-" + serial++;
                events.Add(new(tick, owner, destination, status, e.Data1, e.Data2)); musicalOrder.Add(eventOrder);
            }
            var end = Math.Max(take.Outputs.Select(e => e.Seconds).DefaultIfEmpty(0).Max(), take.Actions.Select(a => a.Seconds).DefaultIfEmpty(0).Max());
            var length = Math.Max(1, checked((long)Math.Ceiling(end * take.Bpm * MusicalTime.Ppq / 60)) + 1);
            var plan = new PlaybackPlan(length, events.ToImmutableArray(), events.Select(e => e.Destination).Distinct(StringComparer.Ordinal).ToImmutableArray());
            var resolved = RouteResolver.Resolve(plan, rig);
            // Realtime events belong to physical clock outputs, not logical instrument routes.
            var endpoints = resolved.Endpoints.ToList();
            var offsets = resolved.OffsetSeconds.ToList();
            var ordered = resolved.Events.Select((e, i) => (Order: musicalOrder[i], Event: e)).ToList();
            foreach (var e in realtime)
            {
                var port = endpoints.IndexOf(e.Endpoint);
                if (port < 0)
                {
                    port = endpoints.Count; endpoints.Add(e.Endpoint);
                    offsets.Add(rig.Endpoints.First(x => x.Id == e.Endpoint).OffsetMilliseconds / 1000);
                }
                ordered.Add((e.Order, new ResolvedMessage(e.Tick, port, -1, -1, e.Status, e.Data1, e.Data2)));
            }
            resolved = resolved with { ClockPorts = [], Endpoints = endpoints.ToImmutableArray(), OffsetSeconds = offsets.ToImmutableArray(),
                Events = ordered.OrderBy(x => x.Event.Tick).ThenBy(x => x.Order).Select(x => x.Event).ToImmutableArray() };
            Await(host.Arm(null, 1)); armed = null;
            ResetAudition();
            captureMeter = take.Meter; captureProjectId = take.SourceProjectId; captureProjectName = take.SourceProjectName; captureInput = rig.Input; captureDestination = null; captureEndpoint = null;
            captureEndpointDestinations = rig.Routes.GroupBy(r => r.EndpointId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Destination, StringComparer.Ordinal);
            Await(host.Start(resolved, PlanPorts(resolved, true), take.Bpm, false));
        }
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
        ProjectStore.WriteAtomic(path, MidiFile.FromTake(take, take.Meter ?? SelectedSection.Outline.Meter, map.TickAt, map.Changes));
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
        lock (gate) { if (disposed) return; disposed = true; InvalidateGeneration(); }
        try { lock (gate) { Await(host.Stop()); CommitRecording(); ArchiveTake(); } }
        finally
        {
            host.Dispose();
            input?.Dispose();
            foreach (var port in outputs.Values) port.Dispose();
            backend.Dispose();
        }
    }
}
