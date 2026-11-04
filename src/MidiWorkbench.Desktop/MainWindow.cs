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

public sealed partial class MainWindow : Window
{
    private static readonly string Settings = Environment.GetEnvironmentVariable("MIDIWORKBENCH_SETTINGS_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
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
    private readonly TextBlock selectedCount = new(), queuedStatus = new();
    private readonly TextBlock performanceStatus = new() { MaxWidth = 360, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock takeHistoryHint = new() { Text = "Recent takes; Load take opens older recordings.", IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock status = new(), beat = new() { FontSize = 20, MinWidth = 120 }, activity = new(), candidateLabel = new();
    private readonly TextBox sectionName = new(), scale = new();
    private readonly TextBlock bankName = new() { Text = "No bank", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
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
    private readonly ListBox notes = new() { MaxHeight = 130, SelectionMode = SelectionMode.Multiple };
    private readonly NumericUpDown pitch = Number(0, 127, 60), velocity = Number(1, 127, 90), tick = Number(1, 1000001, 1), duration = Number(1m / MusicalTime.Ppq, 1000000, .25m);
    private readonly List<Control> stoppedControls = new();
    private readonly HashSet<Control> pendingButtons = new();
    private Func<Task<string?>>? takeSavePicker = null;
    private readonly List<Control> noteEditControls = new();
    private readonly Button play, playSection, undo, redo, accept, discard, audition, returnAudition, cancelGeneration, prepareSection, varySection, cancelQueued;
    private readonly ComboBox candidateBoundary = new() { ItemsSource = new[] { "Bar", "Beat" }, SelectedIndex = 0 };
    private readonly Control candidateTools;
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
    private Outline? loadedOutline;
    private (int Persona, double Complexity, double Fills, double Swing, bool HalfTime)? loadedDrummer;
    private (int, double, double, double, bool) CurrentDrummer() => (drummer.SelectedIndex, complexity.Value, fills.Value, swing.Value, halfTime.IsChecked == true);

    public MainWindow()
    {
        Title = "MidiWorkbench"; Width = 1380; Height = 900; MinWidth = 1000; MinHeight = 650;
        tick.Increment = duration.Increment = .25m;
        tick.FormatString = duration.FormatString = "0.####";
        Tip(tick, "Start in quarter-note beats, counted from 1. One quarter note is 960 ticks; snap is set separately.");
        Tip(duration, "Length in quarter-note beats. 0.25 is a sixteenth note, in any meter.");
        var backend = WindowsMidi.CreateBackend() is { } windows ? new CompositeBackend(new MemoryBackend(), windows) : (IMidiBackend)new MemoryBackend();
        session = new(new SessionOptions(backend, Path.Combine(Settings, "rig.json"), new HostOptions(Wait: WindowsMidi.CreateWait()), Path.Combine(Settings, "takes")));
        savedProject = session.Project;
        routing = new(session, ex => { status.Text = ex?.Message ?? "Routing applied."; if (ex is null) Reload(); });
        arrangement = new(session, action => _ = GuardDraftChange(() => { action(); ClearCandidate(); Reload(); }));
        session.EndpointsChanged += () => Dispatcher.UIThread.Post(() => { if (!closing) { routing.Rebuild(preserveDraft: true); RefreshOutputStatus(); } });
        var panic = Button("Panic", () => { session.Panic(); Reload(); }, emergency: true); Tip(panic, "Release notes and pedals immediately (Ctrl+Shift+Space)");
        panic.Classes.Add("danger");
        undo = AsyncButton("Undo", () => GuardDraftChange(() => { session.Undo(); ClearCandidate(); Reload(); }), true);
        redo = AsyncButton("Redo", () => GuardDraftChange(() => { session.Redo(); ClearCandidate(); Reload(); }), true);
        Tip(undo, "Undo the last project edit (Ctrl+Z)"); Tip(redo, "Redo the last undone edit (Ctrl+Shift+Z)");
        play = Button("Play song", StartSong);
        play.Classes.Add("primary");
        cancelQueued = Button("Cancel queue", session.CancelQueuedSection);
        var transport = Wrap(play,
            Button("Stop", () => { session.Stop(); Reload(); RefreshTakes(); }, emergency: true), panic, loop, Label("BPM", tempo),
            Button("Set", () => session.SetTempo((double)(tempo.Value ?? 120))), beat, pulse, queuedStatus, cancelQueued);
        Tip(loop, "Repeat the current playback range"); Tip(tempo, "Quarter-note beats per minute");
        var files = Wrap(AsyncButton("Open", Open, true, true), AsyncButton("Save", Save, modal: true), AsyncButton("Save as", SaveAs, modal: true), AsyncButton("Rename song", RenameSong, true, true), AsyncButton("Save take", SaveTake, true, true),
            AsyncButton("Export MIDI", Export, modal: true), undo, redo);

        sections.SelectionChanged += async (_, _) =>
        {
            if (loading || sections.SelectedItem is not Choice { Id: { } id } || id == session.SelectedSectionId) return;
            loading = true;
            try { sections.SelectedItem = sections.ItemsSource?.OfType<Choice>().FirstOrDefault(c => c.Id == session.SelectedSectionId); }
            finally { loading = false; }
            await GuardDraftChange(() => { session.SelectSection(id); ClearCandidate(); currentPart = null; Reload(); });
        };
        var sectionTools = Wrap(AsyncButton("Add", () => GuardDraftChange(() => { session.AddSection(); ClearCandidate(); Reload(); }), true),
            AsyncButton("Duplicate", () => GuardDraftChange(() => { session.DuplicateSection(session.SelectedSectionId); ClearCandidate(); Reload(); }), true),
            AsyncButton("Remove", () => GuardDraftChange(() => { session.RemoveSection(session.SelectedSectionId); ClearCandidate(); Reload(); }), true),
            AsyncButton("Up", () => GuardDraftChange(() => MoveSection(-1)), true), AsyncButton("Down", () => GuardDraftChange(() => MoveSection(1)), true));
        Tip(sectionTools, "Reorder sections; the song follows this order unless a custom song order is set");
        drummer.SelectionChanged += (_, _) => { if (!loading && drummer.SelectedIndex > 0) ShowDrummer(DrumPersonas.All[drummer.SelectedIndex - 1].Id, null); };
        Tip(complexity, "Drum activity; other parts use density");
        accept = Button("Accept", AcceptCandidate);
        accept.Classes.Add("primary");
        discard = Button("Discard", () => { ClearCandidate(); RefreshNotes(); roll.RevealMaterial(); });
        audition = Button("Audition", AuditionCandidate);
        returnAudition = Button("Return", () => session.ReturnFromAudition(CandidateQuantum()));
        cancelGeneration = Button("Cancel preparation", session.CancelGeneration);
        prepareSection = AsyncButton("Preview section", PrepareSectionAsync);
        prepareSection.Classes.Add("primary");
        varySection = AsyncButton("Vary", PrepareVariationAsync);
        Tip(varySection, "Vary the accepted section with the next seed. Use Preview section to apply edited outline controls.");
        Tip(candidateBoundary, "Boundary for live audition, return or acceptance");
        AutomationProperties.SetName(candidateBoundary, "Candidate boundary");
        Tip(accept, "Commit the candidate. During playback, queue it at the chosen boundary. Cancelling a queued launch does not undo the accepted edit.");
        var outlineSettings = Column(Label("Sections", sections), sectionTools,
            new Expander { Header = "Section settings", IsExpanded = true, Content = Column(new TextBlock { Text = "Apply changes with Preview section, then Accept.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Label("Name", sectionName),
                Wrap(Label("Bars", bars), Label("Meter", Wrap(numerator, denominator))),
                Wrap(PitchLabel("Root note", root), Label("Seed", seed)), Label("Scale intervals", scale),
                AmountLabel("Density", density), AmountLabel("Intensity", intensity), AmountLabel("Variation", variation)) },
            new Expander { Header = "Drummer", Content = Column(Label("Persona", drummer), Label("Complexity", complexity), Label("Fills", fills), Label("Swing", swing), halfTime) },
            new Expander { Header = "Part generator", Content = generators });
        var outline = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        outline.Children.Add(new ScrollViewer { Content = outlineSettings });
        candidateTools = Wrap(candidateBoundary, audition, returnAudition, accept, discard);
        var candidateActions = Column(Wrap(prepareSection, varySection, cancelGeneration), candidateLabel, candidateTools);
        Grid.SetRow(candidateActions, 1); outline.Children.Add(candidateActions);
        Tip(scale, "Pitch-class intervals from the root, separated by commas");
        generators.Error += ex => status.Text = ex.Message;
        generators.Prepare = PreparePartAsync;

        parts.SelectionChanged += async (_, _) =>
        {
            if (loading || parts.SelectedItem is not Choice { Id: { } id } || id == currentPart) return;
            loading = true;
            try { parts.SelectedItem = parts.ItemsSource?.OfType<Choice>().FirstOrDefault(c => c.Id == currentPart); }
            finally { loading = false; }
            await GuardDraftChange(() => { loading = true; try { parts.SelectedItem = parts.ItemsSource?.OfType<Choice>().FirstOrDefault(c => c.Id == id); } finally { loading = false; } currentPart = null; RefreshNotes(true); }, generatorOnly: true);
        };
        notes.SelectionChanged += (_, _) =>
        {
            if (!syncingNotes)
            {
                roll.SelectMany(notes.SelectedItems?.OfType<NoteChoice>().Select(n => n.Note.Id) ?? []);
                roll.RevealSelection();
            }
        };
        roll.SelectionChanged += SelectNote;
        roll.CommitEdit = CommitNoteEdit;
        roll.Error += ex => status.Text = ex.Message;
        mode.SelectionChanged += (_, _) => { if (!loading) RefreshNotes(true); };
        snap.SelectionChanged += (_, _) => roll.SnapTicks = snap.SelectedIndex switch { 0 => 960, 1 => 480, 2 => 240, 3 => 120, 4 => 320, 5 => 160, _ => 1 };
        zoom.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) roll.Zoom = zoom.Value; };
        velocity.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) roll.NewVelocity = (int)(velocity.Value ?? 90); };
        duration.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) roll.NewDuration = BeatsToTicks(duration.Value ?? .25m); };
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
        var fields = Wrap(PitchLabel("Pitch", pitch), Label("Velocity", velocity), Label("Start beat", tick), Label("Length (beats)", duration), locked);
        Grid.SetRow(fields, 2); editor.Children.Add(fields);
        var apply = Button("Apply", ApplyNote, true);
        Tip(apply, "For a selection, pitch/tick/duration changes are relative. Velocity and lock apply to all selected notes.");
        var edits = Wrap(apply, Button("Add note", AddNote, true), Button("Delete", DeleteNote, true),
            Button("All", roll.SelectAll), Button("Copy", roll.Copy), Button("Paste", roll.Paste, true), Button("Duplicate", roll.Duplicate, true), selectedCount); Grid.SetRow(edits, 3); editor.Children.Add(edits);
        noteEditControls.Add(fields);
        noteEditControls.AddRange(edits.Children.OfType<Button>().Where(b => b.Content is not ("All" or "Copy")));
        var noteList = new Expander { Header = "Notes", Content = notes }; Grid.SetRow(noteList, 4); editor.Children.Add(noteList);
        stoppedControls.Add(mute); stoppedControls.Add(solo); stoppedControls.Add(fields); stoppedControls.Add(patternKind);
        AutomationProperties.SetName(notes, "Note list");

        record.SelectionChanged += (_, _) => { if (!loading) Try(() => { try { session.Arm((record.SelectedItem as Choice)?.Id); } finally { Reload(); } }); };
        Tip(record, "Arms the routing input with MIDI thru. Recording plays the selected section; Loop repeats it. Stop keeps recorded notes.");
        var takeTools = Wrap(AsyncButton("Load take", LoadTake, true, true), AsyncButton("Save selected", SaveSelectedTake, true, true), Button("Recall last", () => { session.Stop(); var take = session.Take(); RefreshTakes(take); }, true),
            Button("Replay", () => { if (takes.SelectedIndex < 0 || takes.SelectedIndex >= takeItems.Length) throw new InvalidOperationException("Choose a take first."); playback = "take"; replayingTake = takeItems[takes.SelectedIndex]; session.PlayTake(replayingTake); }, true));
        playSection = Button("Play section", PlaySection, true);
        stoppedControls.Add(record); stoppedControls.Add(arrangement);
        workspace = new(new()
        {
            ["Outline"] = outline, ["Patterns"] = editor,
            ["Perform"] = new ScrollViewer { Content = Column(activity, Label("Record into", record),
                playSection, Wrap(launchSection, launchBoundary, Button("Queue", QueueSection)),
                arrangement, Label("Takes", takes), takeHistoryHint, takeTools, new Expander { Header = "Bank", Content = BuildBankPane() }) }, ["Routing"] = routing
        });
        workspace.AttachShortcuts(HandleShortcuts);
        Tip(launchSection, "Section to launch; choosing it does not change playback");
        Tip(launchBoundary, "Queue the section at the next bar or beat of the playing meter");
        AutomationProperties.SetName(launchSection, "Launch section"); AutomationProperties.SetName(launchBoundary, "Launch boundary");
        AutomationProperties.SetName(patternKind, "Pattern kind");
        files.Children.Add(Button("Reset layout", workspace.Reset));
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        transport.Margin = new Thickness(12, 10, 4, 0); files.Margin = new Thickness(12, 0, 4, 0);
        body.Children.Add(transport);
        Grid.SetRow(files, 1); body.Children.Add(files);
        AutomationProperties.SetName(performanceStatus, "Playback status");
        var outputBar = Wrap(outputStatus, Button("Routing", () => workspace.Show("Routing")), performanceStatus);
        outputBar.Margin = new Thickness(12, 0, 4, 0); Grid.SetRow(outputBar, 2); body.Children.Add(outputBar);
        Grid.SetRow(workspace, 3); body.Children.Add(workspace);
        status.Margin = new Thickness(12, 6); status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Grid.SetRow(status, 4); body.Children.Add(status); Content = body;
        AutomationProperties.SetName(status, "Status");
        Reload();
        if (File.Exists(layout)) Try(() => workspace.Restore(File.ReadAllText(layout)));
        Opened += (_, _) => { Try(workspace.OpenFloatingWindows); timer.Start(); };
        timer.Tick += (_, _) => TryTransport();
        AddHandler(KeyDownEvent, HandleShortcuts, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += OnClosing;
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
    private static Control AmountLabel(string text, Slider slider)
    {
        var value = new TextBlock { Text = $"{slider.Value:P0}", MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
        slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) value.Text = $"{slider.Value:P0}"; };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(slider); Grid.SetColumn(value, 1); row.Children.Add(value);
        AutomationProperties.SetName(slider, text);
        return Label(text, row);
    }
    private static Control PitchLabel(string text, NumericUpDown number)
    {
        static string Name(decimal? value) { var n = (int)(value ?? 60); return $"{new[] { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" }[n % 12]}{n / 12 - 1}"; }
        var label = new TextBlock { Text = $"{text} · {Name(number.Value)}" };
        number.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) label.Text = $"{text} · {Name(number.Value)}"; };
        AutomationProperties.SetName(number, text); Tip(number, "MIDI note number. Middle C is 60 (C4).");
        return new StackPanel { Spacing = 3, Children = { label, number } };
    }
    private static long BeatsToTicks(decimal value) => (long)decimal.Round(value * MusicalTime.Ppq, 0, MidpointRounding.AwayFromZero);
    private Button Button(string title, Action action, bool stopped = false, bool emergency = false)
    {
        var button = new Button { Content = title }; button.Click += (_, _) => { if (emergency || !documentDialog && !closePending) Try(action); }; if (stopped) stoppedControls.Add(button); return button;
    }
    private Button AsyncButton(string title, Func<Task> action, bool stopped = false, bool modal = false)
    {
        var button = new Button { Content = title }; button.Click += async (_, _) =>
        {
            if (pendingButtons.Contains(button) || documentDialog || closePending) return;
            pendingButtons.Add(button); if (modal) documentDialog = true; button.IsEnabled = false;
            try { await action(); } catch (Exception ex) { status.Text = ex.Message; }
            finally { pendingButtons.Remove(button); if (modal) documentDialog = false; button.IsEnabled = !stopped || !session.IsRunning; }
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
    private Task PrepareSectionAsync()
    {
        var outline = ReadOutline();
        DrummerSettings? settings = drummer.SelectedIndex > 0 ? new(DrumPersonas.All[drummer.SelectedIndex - 1].Id, complexity.Value, fills.Value, swing.Value, halfTime.IsChecked == true) : null;
        return PrepareAsync(() => session.PrepareGenerationAsync(outline, settings, basicDrums: settings is null));
    }
    private Task PreparePartAsync(GeneratorSettings settings)
    {
        if (SelectedPart() is not { } part) throw new InvalidOperationException("Choose a part first.");
        if (generators.UseSource) settings = settings with { SourceNotes = part.Pattern.Notes, SourceLengthTicks = DisplaySection.Outline.LengthTicks };
        return PrepareAsync(() => session.PreparePartGenerationAsync(part.Id, settings));
    }
    private Task PrepareVariationAsync()
    {
        var next = unchecked((candidate?.Outline.Seed ?? session.SelectedSection.Outline.Seed) + 1);
        seed.Value = next;
        return PrepareAsync(() => session.PrepareVariationAsync(next));
    }
    private async Task PrepareAsync(Func<Task<Section>> prepare)
    {
        try
        {
            var pending = prepare(); candidate = session.Candidate; RefreshNotes(); UpdateCandidate();
            var result = await pending;
            if (!closing && ReferenceEquals(session.Candidate, result) && session.SelectedSectionId == result.Id)
            { candidate = result; RefreshNotes(); roll.RevealMaterial(); }
        }
        catch (OperationCanceledException) { }
        finally { if (!closing) UpdateCandidate(); }
    }
    private long CandidateQuantum()
    {
        var meter = PlayingPosition(session.Status()).Section.Outline.Meter;
        return candidateBoundary.SelectedIndex == 1 ? MusicalTime.Ppq * 4L / meter.Denominator : meter.BarTicks;
    }
    private void AcceptCandidate()
    {
        session.AcceptGeneration(session.Status().Running ? CandidateQuantum() : 0);
        candidate = null; Reload(); roll.RevealMaterial();
    }
    private void AuditionCandidate()
    {
        if (candidate is null) return;
        auditionSection = candidate;
        if (session.Status().Running) session.AuditionCandidate(CandidateQuantum());
        else { auditionSection = candidate; playback = "candidate"; session.StartCandidate(loop.IsChecked == true); }
    }
    private void PlaySection()
    {
        auditionSection = session.SelectedSection; playback = "section"; session.StartSection(session.SelectedSectionId, loop.IsChecked == true);
    }
    private void QueueSection()
    {
        if (launchSection.SelectedItem is not Choice { Id: { } id }) throw new InvalidOperationException("Choose a section to launch.");
        var meter = PlayingPosition(session.Status()).Section.Outline.Meter;
        session.QueueSection(id, launchBoundary.SelectedIndex == 1 ? MusicalTime.Ppq * 4L / meter.Denominator : meter.BarTicks);
    }
    private void ClearCandidate() { candidate = null; session.DiscardGeneration(); UpdateCandidate(); }
    private Section DisplaySection => candidate ?? session.SelectedSection;
    private void UpdateCandidate()
    {
        var progress = session.GenerationStatus; var running = session.IsRunning;
        var preparing = progress.Phase == GenerationPhase.Preparing;
        candidateTools.IsVisible = candidate is not null || preparing || session.AuditionStatus.Phase != AuditionPhase.Idle;
        candidateLabel.IsVisible = progress.Phase != GenerationPhase.Idle;
        candidateLabel.Text = progress.Phase switch
        {
            GenerationPhase.Preparing => "Preparing…", GenerationPhase.Ready => "Preview ready · not yet saved", GenerationPhase.Cancelled => "Cancelled",
            GenerationPhase.Failed => progress.Error, _ => ""
        };
        cancelGeneration.IsVisible = preparing;
        prepareSection.IsEnabled = varySection.IsEnabled = !preparing;
        generators.IsPreparing = preparing;
        accept.IsEnabled = candidate is not null && (!running || session.PlayingSectionId == candidate.Id && session.Armed is null);
        discard.IsEnabled = candidate is not null || preparing;
        audition.IsEnabled = candidate is not null && (!running || session.PlayingSectionId == candidate.Id && session.Armed is null);
        returnAudition.IsEnabled = running && session.AuditionStatus.Phase is AuditionPhase.Queued or AuditionPhase.Playing;
        roll.ReadOnly = candidate is not null || running;
        foreach (var control in noteEditControls) control.IsEnabled = !running && candidate is null;
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
            if (!ReferenceEquals(loadedOutline, o))
            {
                sectionName.Text = o.Name; bars.Value = o.Bars; numerator.Value = o.Meter.Numerator; denominator.SelectedItem = o.Meter.Denominator;
                root.Value = o.Root; scale.Text = string.Join(',', o.Scale); seed.Value = o.Seed;
                density.Value = o.Density; intensity.Value = o.Intensity; variation.Value = o.Variation;
                var drums = section.Parts.FirstOrDefault(p => p.Role == PartRole.Drums)?.Pattern.Recipe;
                if (drums?.Drummer is { } d) { drummer.SelectedIndex = (int)d.Persona + 1; ShowDrummer(d.Persona, d); } else if (drums is not null) drummer.SelectedIndex = 0;
                loadedDrummer = CurrentDrummer();
                loadedOutline = o;
            }
            tempo.Value = (decimal)project.Bpm;
            var selected = (parts.SelectedItem as Choice)?.Id;
            var partChoices = DisplaySection.Parts.Select(p => new Choice(p.Id, p.Name)).ToArray(); parts.ItemsSource = partChoices;
            parts.SelectedItem = partChoices.FirstOrDefault(p => p.Id == selected) ?? partChoices[0];
            RefreshNotes(currentPart is null);
            var targets = new[] { new Choice(null, "Off") }.Concat(section.Parts.Select(p => new Choice(p.Id, p.Name))).Append(new Choice(WorkbenchSession.NewPhrase, "New part")).ToArray();
            record.ItemsSource = targets; record.SelectedItem = targets.FirstOrDefault(t => t.Id == session.Armed) ?? targets[0];
            arrangement.Refresh(); RefreshTakes();
            RefreshBank();
            var destinations = string.Join('\n', WorkbenchSession.Destinations(project));
            if (destinations != routedDestinations) { routedDestinations = destinations; routing.Rebuild(preserveDraft: true); }
        }
        finally { loading = false; }
        RefreshOutputStatus(); RefreshDocumentTitle();
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
            roll.Show(p, DisplaySection.Outline, mode.SelectedIndex == 1, DisplaySection.Id);
            syncingNotes = true;
            notes.ItemsSource = p?.Pattern.Notes.Select(n => new NoteChoice(n)).ToArray();
            SyncNoteList();
            syncingNotes = false;
            mute.IsChecked = p?.Muted == true; solo.IsChecked = p?.Solo == true; patternKind.SelectedItem = p?.Pattern.Kind;
            var settings = p?.Pattern.Recipe?.Settings;
            if (changed || !ReferenceEquals(loadedGeneratorSettings, settings)) { generators.Load(settings, DisplaySection.Outline.Seed); loadedGeneratorSettings = settings; }
            generators.SetSectionDensity(session.SelectedSection.Outline.Density);
            currentPart = p?.Id;
            if (changed || center) Dispatcher.UIThread.Post(() => rollScroll.Offset = new Vector(rollScroll.Offset.X, roll.SuggestedOffset), DispatcherPriority.Loaded);
        }
        finally { loading = wasLoading; syncingNotes = false; }
        revision = session.Revision;
        UpdateCandidate();
    }
    private sealed record NoteChoice(MidiNote Note)
    {
        public override string ToString() => $"{Note.Tick}   {NoteEditor.PitchLabel(Note.Pitch)}   v{Note.Velocity}   {Note.Duration}{(Note.Locked ? "   locked" : "")}";
    }
    private void SyncNoteList()
    {
        var ids = roll.SelectedIds.ToHashSet(StringComparer.Ordinal);
        notes.SelectedItems?.Clear();
        if (notes.ItemsSource is IEnumerable<NoteChoice> choices)
            foreach (var choice in choices.Where(n => ids.Contains(n.Note.Id))) notes.SelectedItems?.Add(choice);
        selectedCount.Text = ids.Count == 0 ? "" : $"{ids.Count} selected";
    }
    private void SelectNote(MidiNote? n)
    {
        if (n is not null)
        { pitch.Value = n.Pitch; velocity.Value = n.Velocity; tick.Value = 1m + (decimal)n.Tick / MusicalTime.Ppq; duration.Value = (decimal)n.Duration / MusicalTime.Ppq; locked.IsChecked = n.Locked; }
        syncingNotes = true;
        try { SyncNoteList(); } finally { syncingNotes = false; }
    }
    private MidiNote Edited(string id) => new(id, BeatsToTicks((tick.Value ?? 1) - 1), BeatsToTicks(duration.Value ?? .25m), (int)(pitch.Value ?? 60), (int)(velocity.Value ?? 90), locked.IsChecked == true);
    private bool CommitNoteEdit(NoteEdit edit)
    {
        try
        {
            if (candidate is not null) throw new InvalidOperationException("Accept or discard the candidate before editing.");
            if (SelectedPart() is not { } part) return false;
            session.EditNotes(part.Id, edit.Upserts, edit.DeleteIds); RefreshNotes(); status.Text = ""; return true;
        }
        catch (Exception ex) { status.Text = ex.Message; return false; }
    }
    private void ApplyNote() { if (roll.SelectedId is { } id) roll.ApplyFields(Edited(id)); }
    private void AddNote() => roll.Add(Edited(Guid.NewGuid().ToString("N")) with { Locked = true });
    private void DeleteNote() => roll.DeleteSelection();
    private void TryTransport()
    {
        try { RefreshTransport(); }
        catch (Exception ex)
        {
            // Status may retry a failed take archive. Keep recovery controls usable
            // without retrying the archive just to determine the transport state.
            var running = session.IsRunning;
            foreach (var control in stoppedControls) control.IsEnabled = !running && !pendingButtons.Contains(control);
            play.IsEnabled = !running; roll.ReadOnly = running || candidate is not null;
            if (!running) performanceStatus.IsVisible = false;
            play.Content = session.Armed is null ? "Play song" : "Record section";
            playSection.Content = session.Armed is null ? "Play section" : "Record section";
            undo.IsEnabled = !running && session.CanUndo && !pendingButtons.Contains(undo);
            redo.IsEnabled = !running && session.CanRedo && !pendingButtons.Contains(redo);
            UpdateCandidate();
            patternKind.IsEnabled = mute.IsEnabled = solo.IsEnabled = !running && candidate is null;
            status.Text = ex is IOException or UnauthorizedAccessException
                ? $"Take archive failed: {ex.Message}. Use Save take to save it elsewhere."
                : ex.Message;
        }
    }
    private (Section Section, double Position) PlayingPosition(SessionStatus transport)
    {
        var tick = transport.Tick;
        if (transport.PlayingSection is { } sounding) return (sounding, tick);
        var project = session.Project;
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
        RefreshDocumentTitle();
        if (s.Revision != revision) { candidate = null; Reload(); }
        var (section, position) = PlayingPosition(s);
        var playingTake = playback == "take" && s.PlayingSection is null;
        var meter = playingTake ? replayingTake?.Meter ?? section.Outline.Meter : section.Outline.Meter;
        var unit = MusicalTime.Ppq * 4d / meter.Denominator;
        beat.Text = s.Running ? $"{(long)(position / meter.BarTicks) + 1}:{(int)(position / unit) % meter.Numerator + 1}  {meter.Numerator}/{meter.Denominator}" : "Stopped";
        Tip(beat, s.Running ? section.Outline.Name : "Bar and beat within the playing section");
        var auditionPhase = session.AuditionStatus.Phase;
        var acceptedSounding = session.Project.Sections.FirstOrDefault(item => item.Id == section.Id);
        var previewPlaying = playback == "candidate" && ReferenceEquals(section, auditionSection) && !ReferenceEquals(section, acceptedSounding)
            || auditionPhase is AuditionPhase.Playing or AuditionPhase.Returning;
        var sounding = playingTake ? "Playing: take" : previewPlaying ? $"Playing preview: {section.Outline.Name}" : $"Playing: {section.Outline.Name}";
        var target = (record.SelectedItem as Choice)?.Label ?? "part";
        performanceStatus.Text = (session.Armed is not null ? $"Recording: {target} · " : "") + sounding;
        performanceStatus.IsVisible = s.Running;
        Tip(performanceStatus, performanceStatus.Text);
        pulse.Update(meter, position, s.Running);
        roll.Playhead = s.Running && !playingTake && ReferenceEquals(section, DisplaySection) ? position : -1;
        foreach (var c in stoppedControls) c.IsEnabled = !s.Running && !pendingButtons.Contains(c);
        sections.IsEnabled = !s.Running || session.Armed is null;
        play.Content = session.Armed is null ? "Play song" : "Record section";
        playSection.Content = session.Armed is null ? "Play section" : "Record section";
        Tip(playSection, session.Armed is null ? "Play the selected section" : "Record input into the selected section; Stop keeps recorded notes");
        play.IsEnabled = !s.Running;
        Tip(play, session.Armed is null ? "Play the song order (F5 play/stop; F8 stop)" : "Record the routing input into the selected section (F5 play/stop; F8 stop)");
        roll.ReadOnly = s.Running || candidate is not null;
        undo.IsEnabled = !s.Running && session.CanUndo && !pendingButtons.Contains(undo); redo.IsEnabled = !s.Running && session.CanRedo && !pendingButtons.Contains(redo);
        if (!ReferenceEquals(candidate, session.Candidate)) { candidate = session.Candidate; RefreshNotes(); }
        UpdateCandidate();
        var auditionProgress = session.AuditionStatus;
        cancelQueued.IsEnabled = s.QueuedSection is not null;
        queuedStatus.Text = auditionProgress.Phase switch
        {
            AuditionPhase.Queued => "Audition queued", AuditionPhase.Playing => "Audition", AuditionPhase.Returning => "Return queued",
            _ => s.QueuedSection is { } queued ? $"Queued: {queued.Outline.Name}" : ""
        };
        patternKind.IsEnabled = !s.Running && candidate is null;
        mute.IsEnabled = solo.IsEnabled = candidate is null && (!s.Running || session.Armed is null && s.PlayingSection?.Id == session.SelectedSectionId);
        activity.Text = $"{s.CapturedEvents} events · {s.LateNotes} late" + (s.DroppedInputs > 0 ? $" · {s.DroppedInputs} inputs dropped" : "") + (s.SkippedClocks > 0 ? $" · {s.SkippedClocks} clocks skipped" : "");
        if (transportWasRunning && !s.Running) { Reload(); RefreshTakes(); }
        transportWasRunning = s.Running;
        if (s.Error is not null) status.Text = s.Error;
        else if (!s.CaptureComplete) status.Text = "Take incomplete: capture limit or dropped MIDI input. Save this take before starting another.";
    }
    private void RefreshTakes(PerformanceTake? add = null)
    {
        var selected = takes.SelectedIndex >= 0 && takes.SelectedIndex < takeItems.Length ? takeItems[takes.SelectedIndex] : null;
        var archived = session.Takes.ToList();
        if (add is not null && !archived.Contains(add)) archived.Add(add);
        if (selected is not null && !archived.Contains(selected)) archived.Add(selected);
        takeItems = archived.ToArray(); takes.ItemsSource = takeItems.Select((t, i) => $"{i + 1}. {t.StartedAt:HH:mm:ss} · {t.Outputs.Length} out / {t.Inputs.Length} in{(t.Complete ? "" : " · incomplete")}").ToArray();
        takes.SelectedIndex = add is not null ? Array.IndexOf(takeItems, add) : selected is not null ? Array.IndexOf(takeItems, selected) : takeItems.Length - 1;
        takeHistoryHint.IsVisible = session.HasOlderTakes;
    }
    private async Task Open()
    {
        RequireStoppedForPicker();
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open project", AllowMultiple = false });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path && await ConfirmProjectReplacement())
        { session.Load(path); savedProject = session.Project; projectPath = path; candidate = null; currentPart = null; Reload(); status.Text = $"Opened {Path.GetFileName(path)}"; }
    }
    private async Task Save()
    {
        await SaveProject(false);
    }
    private async Task SaveAs() { await SaveProject(true); }
    private static readonly FilePickerFileType JsonType = new("Take (JSON)") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType MidiType = new("Standard MIDI file") { Patterns = ["*.mid"] };
    private async Task LoadTake()
    {
        RequireStoppedForPicker();
        var folder = session.TakeDirectory is { } directory && Directory.Exists(directory) ? await StorageProvider.TryGetFolderFromPathAsync(directory) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Load take", AllowMultiple = false, FileTypeFilter = [JsonType], SuggestedStartLocation = folder });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) RefreshTakes(session.LoadTake(path));
    }
    private async Task SaveTake()
    {
        RequireStoppedForPicker();
        var path = takeSavePicker is null
            ? (await StorageProvider.SaveFilePickerAsync(new() { Title = "Save take", SuggestedFileName = "take.json", FileTypeChoices = [JsonType, MidiType] }))?.TryGetLocalPath()
            : await takeSavePicker();
        if (path is not null) { session.SaveTake(path); status.Text = $"Saved {Path.GetFileName(path)}"; }
    }
    private async Task SaveSelectedTake()
    {
        RequireStoppedForPicker();
        var take = SelectedTake();
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save selected take", SuggestedFileName = "take.json", FileTypeChoices = [JsonType, MidiType] });
        if (file?.TryGetLocalPath() is { } path) session.SaveTake(path, take);
    }
    private PerformanceTake SelectedTake() => takes.SelectedIndex >= 0 && takes.SelectedIndex < takeItems.Length
        ? takeItems[takes.SelectedIndex] : throw new InvalidOperationException("Choose a take first.");
    private async Task Export()
    {
        RequireStoppedForPicker();
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export song as MIDI", SuggestedFileName = "song.mid", FileTypeChoices = [MidiType] });
        if (file?.TryGetLocalPath() is { } path) session.ExportArrangementMidi(path);
    }
    private Control BuildBankPane()
    {
        AutomationProperties.SetName(bankSongs, "Bank song"); AutomationProperties.SetName(bankOrder, "Bank order");
        Tip(bankSongs, "Select a saved bank song; Open loads it without starting playback");
        var pane = Column(Label("Bank", bankName), Wrap(AsyncButton("New bank", NewBank, true, true), AsyncButton("Rename bank", RenameBank, true, true),
            AsyncButton("Load bank", LoadBank, true, true), AsyncButton("Save bank", SaveBank, true, true)),
            Label("Song", bankSongs), Wrap(Button("Add current", () => { session.AddCurrentToBank(); RefreshBank(); }, true),
                AsyncButton("Open", async () => { if ((bankSongs.SelectedItem as Choice)?.Id is { } id && await ConfirmProjectReplacement()) { session.OpenBankSong(id); savedProject = session.Project; projectPath = null; candidate = null; currentPart = null; Reload(); } }, true, true),
                Button("Update", () => { if ((bankSongs.SelectedItem as Choice)?.Id is { } id) { session.UpdateBankSong(id); RefreshBank(); } }, true)),
            Label("Set order", bankOrder), Wrap(Button("Append", () => { if (session.Bank is { } bank && (bankSongs.SelectedItem as Choice)?.Id is { } id) { session.SetBankOrder(bank.Order.Add(id)); RefreshBank(); } }, true),
                Button("Remove", () => { if (session.Bank is { } bank && bankOrder.SelectedIndex >= 0) { session.SetBankOrder(bank.Order.RemoveAt(bankOrder.SelectedIndex)); RefreshBank(); } }, true),
                Button("Up", () => MoveBankSong(-1), true), Button("Down", () => MoveBankSong(1), true)));
        stoppedControls.Add(pane); return pane;
    }
    private void RefreshBank()
    {
        var bank = session.Bank; var selected = (bankSongs.SelectedItem as Choice)?.Id; var index = bankOrder.SelectedIndex;
        if (bank is null) { bankName.Text = "No bank"; bankSongs.ItemsSource = null; bankOrder.ItemsSource = null; return; }
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
        RequireStoppedForPicker();
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Load bank", AllowMultiple = false, FileTypeFilter = [JsonType] });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path && await ConfirmBankReplacement()) { session.LoadBank(path); savedBank = session.Bank; RefreshBank(); }
    }
    private async Task SaveBank()
    {
        RequireStoppedForPicker();
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save bank", SuggestedFileName = "bank.json", FileTypeChoices = [JsonType] });
        if (file?.TryGetLocalPath() is { } path) { session.SaveBank(path); savedBank = session.Bank; status.Text = $"Saved {Path.GetFileName(path)}"; }
    }
}
