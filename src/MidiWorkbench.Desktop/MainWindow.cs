using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Midi.Windows;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Desktop;

public sealed class MainWindow : Window
{
    private static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.Create), "MidiWorkbench");
    private readonly WorkbenchSession session;
    private readonly DockWorkspace workspace;
    private readonly RoutingPane routing;
    private readonly ArrangementPane arrangement;
    private readonly GeneratorPane generators = new();
    private readonly NoteEditor roll = new();
    private readonly ScrollViewer rollScroll;
    private readonly BeatPulse pulse = new() { Width = 220 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly TextBlock status = new(), beat = new() { FontSize = 20, MinWidth = 120 }, activity = new(), candidateLabel = new();
    private readonly TextBox sectionName = new(), scale = new();
    private readonly TextBox bankName = new() { Text = "Bank" };
    private readonly ComboBox bankSongs = new() { MinWidth = 180 };
    private readonly ListBox bankOrder = new() { MinHeight = 90, MaxHeight = 200 };
    private readonly ComboBox sections = new() { MinWidth = 180 }, parts = new() { MinWidth = 140 };
    private readonly ComboBox launchSection = new() { MinWidth = 180 }, launchBoundary = new() { ItemsSource = new[] { "Bar", "Beat" }, SelectedIndex = 0 },
        patternKind = new() { ItemsSource = Enum.GetValues<PatternKind>() };
    private readonly NumericUpDown bars = Number(1, 256, 8), numerator = Number(1, 32, 4), root = Number(24, 84, 40),
        seed = Number(0, uint.MaxValue, 1), tempo = Number(20, 400, 120);
    private readonly ComboBox denominator = new() { ItemsSource = new[] { 1, 2, 4, 8, 16, 32 }, SelectedItem = 4 };
    private readonly Slider density = Amount(.35), intensity = Amount(.5), variation = Amount(.25), complexity = Amount(.3), fills = Amount(.3),
        swing = new() { Minimum = 0, Maximum = .45, MinWidth = 140 };
    private readonly ComboBox drummer = new() { ItemsSource = new[] { "Basic" }.Concat(DrumPersonas.All.Select(p => p.Name)).ToArray(), SelectedIndex = 1 };
    private readonly CheckBox halfTime = new() { Content = "Half-time" }, loop = new() { Content = "Loop" },
        mute = new() { Content = "Mute" }, solo = new() { Content = "Solo" }, locked = new() { Content = "Lock" };
    private readonly ComboBox record = new() { MinWidth = 160 }, takes = new() { MinWidth = 240 },
        mode = new() { ItemsSource = new[] { "Piano roll", "Drum grid" }, SelectedIndex = 0 },
        snap = new() { ItemsSource = new[] { "1/4", "1/8", "1/16", "1/32", "1/8T", "1/16T", "Off" }, SelectedIndex = 2 };
    private readonly Slider zoom = new() { Minimum = 24, Maximum = 240, Value = 96, Width = 110 };
    private readonly ListBox notes = new() { MaxHeight = 130 };
    private readonly NumericUpDown pitch = Number(0, 127, 60), velocity = Number(1, 127, 90), tick = Number(0, 1000000000, 0), duration = Number(1, 1000000000, 240);
    private readonly List<Control> stoppedControls = new();
    private readonly Button play, undo, redo, accept, discard, audition;
    private readonly string layout = Path.Combine(Settings, "layout.json");
    private Section? candidate;
    private PerformanceTake[] takeItems = [];
    private long revision;
    private string? routedDestinations, currentPart;
    private bool closing, loading, syncingNotes, transportWasRunning;
    private string playback = "song";
    private Section? auditionSection;
    private PerformanceTake? replayingTake;
    private GeneratorSettings? loadedGeneratorSettings;

    public MainWindow()
    {
        Title = "MidiWorkbench"; Width = 1380; Height = 900; MinWidth = 1000; MinHeight = 650;
        var backend = WindowsMidi.CreateBackend() is { } windows ? new CompositeBackend(new MemoryBackend(), windows) : (IMidiBackend)new MemoryBackend();
        session = new(new SessionOptions(backend, Path.Combine(Settings, "rig.json"), new HostOptions(Wait: WindowsMidi.CreateWait()), Path.Combine(Settings, "takes")));
        routing = new(session, ex => { status.Text = ex?.Message ?? ""; if (ex is null) Reload(); });
        arrangement = new(session, action => Try(() => { action(); ClearCandidate(); Reload(); }));
        session.EndpointsChanged += () => Dispatcher.UIThread.Post(routing.Rebuild);
        var panic = Button("Panic", () => { session.Panic(); Reload(); }); Tip(panic, "Release notes immediately and send All Notes Off");
        undo = Button("Undo", () => { session.Undo(); ClearCandidate(); Reload(); }, true);
        redo = Button("Redo", () => { session.Redo(); ClearCandidate(); Reload(); }, true);
        Tip(undo, "Undo the last project edit (Ctrl+Z)"); Tip(redo, "Redo the last undone edit (Ctrl+Shift+Z)");
        play = Button("Play song", () => { playback = session.Armed is null ? "song" : "section"; auditionSection = session.SelectedSection; if (session.Armed is null) session.StartArrangement(loop.IsChecked == true); else session.StartSection(session.SelectedSectionId, loop.IsChecked == true); });
        var transport = Row(play,
            Button("Stop", () => { session.Stop(); Reload(); RefreshTakes(); }), panic, loop, Label("BPM", tempo),
            Button("Set", () => session.SetTempo((double)(tempo.Value ?? 120))), beat, pulse);
        Tip(loop, "Repeat the current playback range"); Tip(tempo, "Quarter-note beats per minute");
        var files = Row(AsyncButton("Open", Open, true), AsyncButton("Save", Save), AsyncButton("Save take", SaveTake, true),
            AsyncButton("Export MIDI", Export), undo, redo);

        sections.SelectionChanged += (_, _) => { if (!loading && sections.SelectedItem is Choice { Id: { } id }) Try(() => { session.SelectSection(id); ClearCandidate(); currentPart = null; Reload(); }); };
        var sectionTools = Wrap(Button("Add", () => { session.AddSection(); ClearCandidate(); Reload(); }, true),
            Button("Duplicate", () => { session.DuplicateSection(session.SelectedSectionId); ClearCandidate(); Reload(); }, true),
            Button("Remove", () => { session.RemoveSection(session.SelectedSectionId); ClearCandidate(); Reload(); }, true),
            Button("Up", () => MoveSection(-1), true), Button("Down", () => MoveSection(1), true));
        Tip(sectionTools, "Reorder sections; the song follows this order unless a custom song order is set");
        drummer.SelectionChanged += (_, _) => { if (!loading && drummer.SelectedIndex > 0) ShowDrummer(DrumPersonas.All[drummer.SelectedIndex - 1].Id, null); };
        Tip(complexity, "Drum activity; other parts use density");
        accept = Button("Accept", () => { session.AcceptGeneration(); candidate = null; Reload(); }, true);
        discard = Button("Discard", () => { ClearCandidate(); Reload(); }, true);
        audition = Button("Audition", AuditionCandidate, true);
        var outline = Column(Label("Sections", sections), sectionTools, Label("Name", sectionName), Label("Bars", bars), Label("Meter", Row(numerator, denominator)),
            Label("Root", root), Label("Scale", scale), Label("Density", density), Label("Intensity", intensity), Label("Variation", variation), Label("Seed", seed),
            new Expander { Header = "Drummer", Content = Column(Label("Persona", drummer), Label("Complexity", complexity), Label("Fills", fills), Label("Swing", swing), halfTime) },
            Wrap(Button("Preview section", PrepareSection, true), Button("Vary", () => { seed.Value = ((seed.Value ?? 1) + 1) % 4294967296m; PrepareSection(); }, true)),
            candidateLabel, Wrap(audition, accept, discard), new Expander { Header = "Part generator", Content = generators });
        Tip(scale, "Pitch-class intervals from the root, separated by commas");
        stoppedControls.Add(generators);
        generators.Error += ex => status.Text = ex.Message;
        generators.Prepare += settings => Try(() => PreparePart(settings));

        parts.SelectionChanged += (_, _) => { if (!loading) { currentPart = null; RefreshNotes(true); } };
        notes.SelectionChanged += (_, _) => { if (!syncingNotes && notes.SelectedIndex >= 0 && SelectedPart() is { } p && notes.SelectedIndex < p.Pattern.Notes.Length) roll.Select(p.Pattern.Notes[notes.SelectedIndex].Id); };
        roll.SelectionChanged += SelectNote;
        roll.NoteEdited += n => Try(() => { if (SelectedPart() is { } p) { session.EditNote(p.Id, n); RefreshNotes(); roll.Select(n.Id); } });
        roll.NoteDeleted += id => Try(() => { if (SelectedPart() is { } p) { session.DeleteNote(p.Id, id); RefreshNotes(); } });
        mode.SelectionChanged += (_, _) => { if (!loading) RefreshNotes(true); };
        snap.SelectionChanged += (_, _) => roll.SnapTicks = snap.SelectedIndex switch { 0 => 960, 1 => 480, 2 => 240, 3 => 120, 4 => 320, 5 => 160, _ => 1 };
        zoom.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) roll.Zoom = zoom.Value; };
        velocity.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) roll.NewVelocity = (int)(velocity.Value ?? 90); };
        duration.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) roll.NewDuration = (long)(duration.Value ?? 240); };
        mute.IsCheckedChanged += (_, _) => { if (!loading && candidate is null && SelectedPart() is { } p) Try(() => { session.SetPartMute(p.Id, mute.IsChecked == true); RefreshNotes(); }); };
        solo.IsCheckedChanged += (_, _) => { if (!loading && candidate is null && SelectedPart() is { } p) Try(() => { session.SetPartSolo(p.Id, solo.IsChecked == true); RefreshNotes(); }); };
        patternKind.SelectionChanged += (_, _) => { if (!loading && candidate is null && SelectedPart() is { } p && patternKind.SelectedItem is PatternKind kind) Try(() => { session.SetPatternKind(p.Id, kind); RefreshNotes(); }); };
        Tip(patternKind, "Fixed material survives all regeneration. Hybrid preserves locked notes; Generative replaces unlocked material.");
        Tip(snap, "Snap note starts and drag changes to this rhythmic grid; T means triplets"); Tip(zoom, "Horizontal zoom");
        AutomationProperties.SetName(parts, "Part"); AutomationProperties.SetName(mode, "Editor mode"); AutomationProperties.SetName(snap, "Snap"); AutomationProperties.SetName(zoom, "Zoom");
        roll.HorizontalAlignment = HorizontalAlignment.Left; roll.VerticalAlignment = VerticalAlignment.Top;
        rollScroll = new ScrollViewer { Content = roll, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(8) };
        editor.Children.Add(Wrap(parts, mode, Label("Snap", snap), Label("Zoom", zoom), mute, solo, patternKind));
        Grid.SetRow(rollScroll, 1); editor.Children.Add(rollScroll);
        var fields = Wrap(Label("Pitch", pitch), Label("Velocity", velocity), Label("Tick", tick), Label("Duration", duration), locked);
        Grid.SetRow(fields, 2); editor.Children.Add(fields);
        var edits = Wrap(Button("Apply", ApplyNote, true), Button("Add note", AddNote, true), Button("Delete", DeleteNote, true)); Grid.SetRow(edits, 3); editor.Children.Add(edits);
        var noteList = new Expander { Header = "Notes", Content = notes }; Grid.SetRow(noteList, 4); editor.Children.Add(noteList);
        stoppedControls.Add(mute); stoppedControls.Add(solo); stoppedControls.Add(fields); stoppedControls.Add(patternKind);
        AutomationProperties.SetName(notes, "Note list");

        record.SelectionChanged += (_, _) => { if (!loading) Try(() => { try { session.Arm((record.SelectedItem as Choice)?.Id); } finally { Reload(); } }); };
        Tip(record, "Arms the routing input with MIDI thru. Recording plays and loops the selected section; Stop commits locked notes.");
        var takeTools = Wrap(AsyncButton("Load take", LoadTake, true), Button("Recall last", () => { session.Stop(); var take = session.Take(); RefreshTakes(take); }, true),
            Button("Replay", () => { if (takes.SelectedIndex < 0 || takes.SelectedIndex >= takeItems.Length) throw new InvalidOperationException("Choose a take first."); playback = "take"; replayingTake = takeItems[takes.SelectedIndex]; session.PlayTake(replayingTake); }, true));
        stoppedControls.Add(record); stoppedControls.Add(arrangement);
        workspace = new(new()
        {
            ["Outline"] = new ScrollViewer { Content = outline }, ["Patterns"] = editor,
            ["Perform"] = new ScrollViewer { Content = Column(activity, Label("Record into", record),
                Button("Play section", PlaySection, true), Wrap(launchSection, launchBoundary, Button("Queue", QueueSection), Button("Cancel queue", session.CancelQueuedSection)),
                arrangement, Label("Takes", takes), takeTools, new Expander { Header = "Bank", Content = BuildBankPane() }) }, ["Routing"] = routing
        });
        Tip(launchSection, "Section to launch; choosing it does not change playback");
        Tip(launchBoundary, "Queue the section at the next bar or beat of the playing meter");
        AutomationProperties.SetName(launchSection, "Launch section"); AutomationProperties.SetName(launchBoundary, "Launch boundary");
        AutomationProperties.SetName(patternKind, "Pattern kind");
        files.Children.Add(Button("Reset layout", workspace.Reset));
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        body.Children.Add(new ScrollViewer { Content = transport, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetRow(files, 1); body.Children.Add(files); Grid.SetRow(workspace, 2); body.Children.Add(workspace);
        status.Margin = new Thickness(12, 6); Grid.SetRow(status, 3); body.Children.Add(status); Content = body;
        AutomationProperties.SetName(status, "Status");
        Reload();
        if (File.Exists(layout)) Try(() => workspace.Restore(File.ReadAllText(layout)));
        Opened += (_, _) => { Try(workspace.OpenFloatingWindows); timer.Start(); };
        timer.Tick += (_, _) => TryTransport();
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key != Key.Z || e.Source is TextBox or NumericUpDown) return;
            Try(() => { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) session.Redo(); else session.Undo(); ClearCandidate(); Reload(); }); e.Handled = true;
        };
        Closing += (_, _) =>
        {
            if (closing) return;
            Try(session.Stop);
            try { ProjectStore.WriteAtomic(layout, workspace.Save()); } catch (Exception ex) { status.Text = $"Layout not saved: {ex.Message}"; }
            closing = true;
        };
        Closed += (_, _) => { timer.Stop(); workspace.Dispose(); session.Dispose(); };
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, Increment = 1, MinWidth = 105, MaxWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private static Slider Amount(double value) => new() { Minimum = 0, Maximum = 1, Value = value, MinWidth = 140 };
    private static void Tip(Control control, string text) => ToolTip.SetTip(control, text);
    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) row.Children.Add(c); return row;
    }
    private static WrapPanel Wrap(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in controls) { c.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(c); } return panel;
    }
    private static StackPanel Column(params Control[] controls)
    {
        var col = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        foreach (var c in controls) col.Children.Add(c); return col;
    }
    private static StackPanel Label(string text, Control control)
    {
        AutomationProperties.SetName(control, text);
        return new StackPanel { Spacing = 3, Children = { new TextBlock { Text = text }, control } };
    }
    private Button Button(string title, Action action, bool stopped = false)
    {
        var button = new Button { Content = title }; button.Click += (_, _) => Try(action); if (stopped) stoppedControls.Add(button); return button;
    }
    private Button AsyncButton(string title, Func<Task> action, bool stopped = false)
    {
        var button = new Button { Content = title }; button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); status.Text = ""; } catch (Exception ex) { status.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        }; if (stopped) stoppedControls.Add(button); return button;
    }
    private void Try(Action action)
    {
        try { action(); status.Text = ""; } catch (Exception ex) { status.Text = ex.Message; }
    }
    private void MoveSection(int delta)
    {
        var index = Array.FindIndex(session.Project.Sections.ToArray(), s => s.Id == session.SelectedSectionId);
        if (index + delta < 0 || index + delta >= session.Project.Sections.Length) return;
        session.MoveSection(session.SelectedSectionId, index + delta); ClearCandidate(); Reload();
    }
    private void ShowDrummer(DrumPersona persona, DrummerSettings? stored)
    {
        var s = stored ?? DrumPersonas.Settings(persona); complexity.Value = s.Complexity; fills.Value = s.Fills; swing.Value = s.Swing; halfTime.IsChecked = s.HalfTime;
    }
    private Outline ReadOutline() => new(sectionName.Text ?? "Section", (int)(bars.Value ?? 8),
        new Meter((int)(numerator.Value ?? 4), (int)(denominator.SelectedItem ?? 4)), (int)(root.Value ?? 40),
        (scale.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToImmutableArray(),
        density.Value, intensity.Value, variation.Value, (uint)(seed.Value ?? 1));
    private void PrepareSection()
    {
        DrummerSettings? settings = drummer.SelectedIndex > 0 ? new(DrumPersonas.All[drummer.SelectedIndex - 1].Id, complexity.Value, fills.Value, swing.Value, halfTime.IsChecked == true) : null;
        candidate = session.PrepareGeneration(ReadOutline(), settings, basicDrums: settings is null); RefreshNotes(); UpdateCandidate();
    }
    private void PreparePart(GeneratorSettings settings)
    {
        if (SelectedPart() is not { } part) throw new InvalidOperationException("Choose a part first.");
        if (generators.UseSource) settings = settings with { SourceNotes = part.Pattern.Notes, SourceLengthTicks = DisplaySection.Outline.LengthTicks };
        candidate = session.PreparePartGeneration(part.Id, settings); RefreshNotes(); UpdateCandidate();
    }
    private void AuditionCandidate()
    {
        if (candidate is null) return;
        auditionSection = candidate; playback = "candidate"; session.StartCandidate(loop.IsChecked == true);
    }
    private void PlaySection()
    {
        auditionSection = session.SelectedSection; playback = "section"; session.StartSection(session.SelectedSectionId, loop.IsChecked == true);
    }
    private void QueueSection()
    {
        if (launchSection.SelectedItem is not Choice { Id: { } id }) throw new InvalidOperationException("Choose a section to launch.");
        var meter = PlayingPosition(session.Status().Tick, session.PlayingSectionId).Section.Outline.Meter;
        session.QueueSection(id, launchBoundary.SelectedIndex == 1 ? MusicalTime.Ppq * 4L / meter.Denominator : meter.BarTicks);
    }
    private void ClearCandidate() { candidate = null; session.DiscardGeneration(); UpdateCandidate(); }
    private Section DisplaySection => candidate ?? session.SelectedSection;
    private void UpdateCandidate()
    {
        candidateLabel.Text = candidate is null ? "" : "Candidate";
        accept.IsEnabled = discard.IsEnabled = audition.IsEnabled = candidate is not null && !session.Status().Running;
        roll.ReadOnly = candidate is not null || session.Status().Running;
    }
    private void Reload()
    {
        loading = true;
        try
        {
            var project = session.Project; var section = session.SelectedSection; var o = section.Outline; revision = session.Revision;
            var choices = project.Sections.Select((s, i) => new Choice(s.Id, $"{i + 1}. {s.Outline.Name}")).ToArray();
            sections.ItemsSource = choices; sections.SelectedItem = choices.First(c => c.Id == section.Id);
            var launch = (launchSection.SelectedItem as Choice)?.Id;
            launchSection.ItemsSource = choices; launchSection.SelectedItem = choices.FirstOrDefault(c => c.Id == launch) ?? choices[0];
            sectionName.Text = o.Name; bars.Value = o.Bars; numerator.Value = o.Meter.Numerator; denominator.SelectedItem = o.Meter.Denominator;
            root.Value = o.Root; scale.Text = string.Join(',', o.Scale); seed.Value = o.Seed;
            density.Value = o.Density; intensity.Value = o.Intensity; variation.Value = o.Variation; tempo.Value = (decimal)project.Bpm;
            var drums = section.Parts.FirstOrDefault(p => p.Role == PartRole.Drums)?.Pattern.Recipe;
            if (drums?.Drummer is { } d) { drummer.SelectedIndex = (int)d.Persona + 1; ShowDrummer(d.Persona, d); } else if (drums is not null) drummer.SelectedIndex = 0;
            var selected = (parts.SelectedItem as Choice)?.Id;
            var partChoices = DisplaySection.Parts.Select(p => new Choice(p.Id, p.Name)).ToArray(); parts.ItemsSource = partChoices;
            parts.SelectedItem = partChoices.FirstOrDefault(p => p.Id == selected) ?? partChoices[0];
            RefreshNotes(currentPart is null);
            var targets = new[] { new Choice(null, "Off") }.Concat(section.Parts.Select(p => new Choice(p.Id, p.Name))).Append(new Choice(WorkbenchSession.NewPhrase, "New part")).ToArray();
            record.ItemsSource = targets; record.SelectedItem = targets.FirstOrDefault(t => t.Id == session.Armed) ?? targets[0];
            arrangement.Refresh(); RefreshTakes();
            RefreshBank();
            var destinations = string.Join('\n', WorkbenchSession.Destinations(project));
            if (destinations != routedDestinations) { routedDestinations = destinations; routing.Rebuild(); }
        }
        finally { loading = false; }
        UpdateCandidate();
    }
    private Part? SelectedPart() => DisplaySection.Parts.FirstOrDefault(p => p.Id == (parts.SelectedItem as Choice)?.Id);
    private void RefreshNotes(bool center = false)
    {
        var p = SelectedPart(); var changed = currentPart != p?.Id;
        var wasLoading = loading; loading = true;
        try
        {
            if (changed) mode.SelectedIndex = p?.Role == PartRole.Drums ? 1 : 0;
            roll.Show(p, DisplaySection.Outline, mode.SelectedIndex == 1);
            syncingNotes = true;
            notes.ItemsSource = p?.Pattern.Notes.Select(n => $"{n.Tick}   {NoteEditor.PitchLabel(n.Pitch)}   v{n.Velocity}   {n.Duration}{(n.Locked ? "   locked" : "")}").ToArray();
            notes.SelectedIndex = p is null ? -1 : Array.FindIndex(p.Pattern.Notes.ToArray(), n => n.Id == roll.SelectedId);
            syncingNotes = false;
            mute.IsChecked = p?.Muted == true; solo.IsChecked = p?.Solo == true; patternKind.SelectedItem = p?.Pattern.Kind;
            var settings = p?.Pattern.Recipe?.Settings;
            if (changed || !ReferenceEquals(loadedGeneratorSettings, settings)) { generators.Load(settings, DisplaySection.Outline.Seed); loadedGeneratorSettings = settings; }
            currentPart = p?.Id;
            if (changed || center) Dispatcher.UIThread.Post(() => rollScroll.Offset = new Vector(rollScroll.Offset.X, roll.SuggestedOffset), DispatcherPriority.Loaded);
        }
        finally { loading = wasLoading; syncingNotes = false; }
        revision = session.Revision;
        UpdateCandidate();
    }
    private MidiNote? SelectedNote() => SelectedPart()?.Pattern.Notes.FirstOrDefault(n => n.Id == roll.SelectedId);
    private void SelectNote(MidiNote? n)
    {
        if (n is null) return;
        pitch.Value = n.Pitch; velocity.Value = n.Velocity; tick.Value = n.Tick; duration.Value = n.Duration; locked.IsChecked = n.Locked;
        syncingNotes = true;
        if (SelectedPart() is { } p) notes.SelectedIndex = Array.FindIndex(p.Pattern.Notes.ToArray(), v => v.Id == n.Id);
        syncingNotes = false;
    }
    private MidiNote Edited(string id) => new(id, (long)(tick.Value ?? 0), (long)(duration.Value ?? 240), (int)(pitch.Value ?? 60), (int)(velocity.Value ?? 90), locked.IsChecked == true);
    private void ApplyNote()
    {
        if (candidate is not null) throw new InvalidOperationException("Accept or discard the candidate before editing.");
        if (SelectedPart() is not { } p || SelectedNote() is not { } n) return;
        session.EditNote(p.Id, Edited(n.Id)); RefreshNotes(); roll.Select(n.Id);
    }
    private void AddNote()
    {
        if (candidate is not null) throw new InvalidOperationException("Accept or discard the candidate before editing.");
        if (SelectedPart() is not { } p) return;
        var note = Edited(Guid.NewGuid().ToString("N")) with { Locked = true }; session.EditNote(p.Id, note); RefreshNotes(); roll.Select(note.Id);
    }
    private void DeleteNote()
    {
        if (candidate is not null) throw new InvalidOperationException("Accept or discard the candidate before editing.");
        if (SelectedPart() is not { } p || SelectedNote() is not { } n) return;
        session.DeleteNote(p.Id, n.Id); RefreshNotes();
    }
    private void TryTransport()
    {
        try { RefreshTransport(); } catch (Exception ex) { status.Text = ex.Message; }
    }
    private (Section Section, double Position) PlayingPosition(double tick, string? activeSectionId)
    {
        var project = session.Project;
        if (activeSectionId is not null)
        {
            if (playback == "candidate" && auditionSection is { } preview && preview.Id == activeSectionId) return (preview, tick);
            if (project.Sections.FirstOrDefault(s => s.Id == activeSectionId) is { } active) return (active, tick);
        }
        var section = session.SelectedSection;
        if (playback == "song" && session.Armed is null)
        {
            var entries = project.Arrangement.IsEmpty ? project.Sections.Select(s => new ArrangementEntry(s.Id)) : project.Arrangement;
            foreach (var entry in entries)
            {
                section = project.Sections.First(s => s.Id == entry.SectionId);
                var length = section.Outline.LengthTicks * entry.Repeats;
                if (tick < length) return (section, tick % section.Outline.LengthTicks);
                tick -= length;
            }
        }
        else if (playback is "candidate" or "section" && auditionSection is { } preview) section = preview;
        return (section, tick);
    }
    private void RefreshTransport()
    {
        var s = session.Status();
        if (s.Revision != revision) { candidate = null; Reload(); }
        var activeSectionId = session.PlayingSectionId;
        var (section, position) = PlayingPosition(s.Tick, activeSectionId);
        var playingTake = playback == "take" && activeSectionId is null;
        var meter = playingTake ? replayingTake?.Meter ?? section.Outline.Meter : section.Outline.Meter;
        var unit = MusicalTime.Ppq * 4d / meter.Denominator;
        beat.Text = s.Running ? $"{(long)(position / meter.BarTicks) + 1}:{(int)(position / unit) % meter.Numerator + 1}  {meter.Numerator}/{meter.Denominator}" : "Stopped";
        Tip(beat, s.Running ? section.Outline.Name : "Bar and beat within the playing section");
        pulse.Update(meter, position, s.Running);
        roll.Playhead = s.Running && !playingTake && section.Id == DisplaySection.Id ? position : -1;
        foreach (var c in stoppedControls) c.IsEnabled = !s.Running;
        sections.IsEnabled = !s.Running || session.Armed is null;
        play.Content = session.Armed is null ? "Play song" : "Record section";
        Tip(play, session.Armed is null ? "Play the song order" : "Record the routing input into the selected section");
        roll.ReadOnly = s.Running || candidate is not null;
        undo.IsEnabled = !s.Running && session.CanUndo; redo.IsEnabled = !s.Running && session.CanRedo;
        accept.IsEnabled = discard.IsEnabled = audition.IsEnabled = candidate is not null && !s.Running;
        mute.IsEnabled = solo.IsEnabled = patternKind.IsEnabled = !s.Running && candidate is null;
        activity.Text = $"{s.CapturedEvents} events · {s.LateNotes} late" + (s.DroppedInputs > 0 ? $" · {s.DroppedInputs} inputs dropped" : "") + (s.SkippedClocks > 0 ? $" · {s.SkippedClocks} clocks skipped" : "");
        if (transportWasRunning && !s.Running) { Reload(); RefreshTakes(); }
        transportWasRunning = s.Running;
        if (!s.CaptureComplete) status.Text = "Capture full. Save this take and start a new one."; else if (s.Error is not null) status.Text = s.Error;
    }
    private void RefreshTakes(PerformanceTake? add = null)
    {
        var selected = takes.SelectedIndex >= 0 && takes.SelectedIndex < takeItems.Length ? takeItems[takes.SelectedIndex] : null;
        var archived = session.Takes.ToList();
        if (add is not null && !archived.Contains(add)) archived.Add(add);
        if (selected is not null && !archived.Contains(selected)) archived.Add(selected);
        takeItems = archived.ToArray(); takes.ItemsSource = takeItems.Select((t, i) => $"{i + 1}. {t.StartedAt:HH:mm:ss} · {t.Outputs.Length} out / {t.Inputs.Length} in{(t.Complete ? "" : " · incomplete")}").ToArray();
        takes.SelectedIndex = add is not null ? Array.IndexOf(takeItems, add) : selected is not null ? Array.IndexOf(takeItems, selected) : takeItems.Length - 1;
    }
    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open project", AllowMultiple = false });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) { session.Load(path); candidate = null; currentPart = null; Reload(); }
    }
    private async Task Save()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save project", SuggestedFileName = "song.midiwork.json" });
        if (file?.TryGetLocalPath() is { } path) session.Save(path);
    }
    private static readonly FilePickerFileType JsonType = new("Take (JSON)") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType MidiType = new("Standard MIDI file") { Patterns = ["*.mid"] };
    private async Task LoadTake()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Load take", AllowMultiple = false, FileTypeFilter = [JsonType] });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) RefreshTakes(session.LoadTake(path));
    }
    private async Task SaveTake()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save take", SuggestedFileName = "take.json", FileTypeChoices = [JsonType, MidiType] });
        if (file?.TryGetLocalPath() is { } path) session.SaveTake(path);
    }
    private async Task Export()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export song as MIDI", SuggestedFileName = "song.mid", FileTypeChoices = [MidiType] });
        if (file?.TryGetLocalPath() is { } path) session.ExportArrangementMidi(path);
    }
    private Control BuildBankPane()
    {
        AutomationProperties.SetName(bankSongs, "Bank song"); AutomationProperties.SetName(bankOrder, "Bank order");
        Tip(bankSongs, "Select a saved bank song; Open loads it without starting playback");
        var pane = Column(Label("Name", bankName), Wrap(Button("New", () => { session.NewBank(bankName.Text ?? "Bank"); RefreshBank(); }, true),
            AsyncButton("Load bank", LoadBank, true), AsyncButton("Save bank", SaveBank, true)),
            Label("Song", bankSongs), Wrap(Button("Add current", () => { session.AddCurrentToBank(); RefreshBank(); }, true),
                Button("Open", () => { if ((bankSongs.SelectedItem as Choice)?.Id is { } id) { session.OpenBankSong(id); candidate = null; currentPart = null; Reload(); } }, true),
                Button("Update", () => { if ((bankSongs.SelectedItem as Choice)?.Id is { } id) { session.UpdateBankSong(id); RefreshBank(); } }, true)),
            Label("Set order", bankOrder), Wrap(Button("Append", () => { if (session.Bank is { } bank && (bankSongs.SelectedItem as Choice)?.Id is { } id) { session.SetBankOrder(bank.Order.Add(id)); RefreshBank(); } }, true),
                Button("Remove", () => { if (session.Bank is { } bank && bankOrder.SelectedIndex >= 0) { session.SetBankOrder(bank.Order.RemoveAt(bankOrder.SelectedIndex)); RefreshBank(); } }, true),
                Button("Up", () => MoveBankSong(-1), true), Button("Down", () => MoveBankSong(1), true)));
        stoppedControls.Add(pane); return pane;
    }
    private void RefreshBank()
    {
        var bank = session.Bank; var selected = (bankSongs.SelectedItem as Choice)?.Id; var index = bankOrder.SelectedIndex;
        if (bank is null) { bankSongs.ItemsSource = null; bankOrder.ItemsSource = null; return; }
        bankName.Text = bank.Name;
        var songs = bank.Songs.Select(s => new Choice(s.Id, s.Project.Name)).ToArray(); bankSongs.ItemsSource = songs;
        bankSongs.SelectedItem = songs.FirstOrDefault(s => s.Id == selected) ?? songs.FirstOrDefault();
        var lookup = bank.Songs.ToDictionary(s => s.Id);
        bankOrder.ItemsSource = bank.Order.Select((id, i) => $"{i + 1}. {lookup[id].Project.Name}").ToArray();
        bankOrder.SelectedIndex = bank.Order.IsEmpty ? -1 : Math.Clamp(index, 0, bank.Order.Length - 1);
    }
    private void MoveBankSong(int delta)
    {
        if (session.Bank is not { } bank) return;
        var index = bankOrder.SelectedIndex; var next = index + delta;
        if (index < 0 || next < 0 || next >= bank.Order.Length) return;
        var order = bank.Order.ToArray(); (order[index], order[next]) = (order[next], order[index]);
        session.SetBankOrder(order); RefreshBank(); bankOrder.SelectedIndex = next;
    }
    private async Task LoadBank()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Load bank", AllowMultiple = false, FileTypeFilter = [JsonType] });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) { session.LoadBank(path); RefreshBank(); }
    }
    private async Task SaveBank()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save bank", SuggestedFileName = "bank.json", FileTypeChoices = [JsonType] });
        if (file?.TryGetLocalPath() is { } path) session.SaveBank(path);
    }
}
