using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
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
    // Without Create, a missing folder yields "" and settings would land in the working directory.
    private static readonly string Settings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MidiWorkbench");
    private readonly WorkbenchSession session;
    private readonly DockWorkspace workspace;
    private readonly RoutingPane routing;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly TextBlock status = new();
    private readonly TextBlock beat = new() { FontSize = 28 };
    private readonly TextBlock activity = new();
    private readonly TextBox sectionName = new();
    private readonly TextBox scale = new();
    private readonly NumericUpDown bars = Number(1, 256, 8);
    private readonly NumericUpDown numerator = Number(1, 32, 4);
    private readonly ComboBox denominator = new() { ItemsSource = new[] { 1, 2, 4, 8, 16, 32 }, SelectedItem = 4 };
    private readonly NumericUpDown root = Number(24, 84, 40);
    private readonly NumericUpDown seed = Number(0, uint.MaxValue, 1);
    private readonly NumericUpDown tempo = Number(20, 400, 120);
    private readonly Slider density = Amount(.35), intensity = Amount(.5), variation = Amount(.25);
    private readonly ComboBox drummer = new() { ItemsSource = new[] { "Basic" }.Concat(DrumPersonas.All.Select(p => p.Name)).ToArray(), SelectedIndex = 1 };
    private readonly Slider complexity = Amount(.3), fills = Amount(.3), swing = new() { Minimum = 0, Maximum = .45, Value = 0, MinWidth = 180 };
    private readonly CheckBox halfTime = new() { Content = "Half-time" };
    private readonly CheckBox loop = new() { Content = "Loop" };
    private readonly ComboBox record = new() { MinWidth = 160 };
    private readonly ComboBox parts = new();
    private readonly ListBox notes = new();
    private readonly NumericUpDown pitch = Number(0, 127, 60), velocity = Number(1, 127, 90);
    private readonly NumericUpDown tick = Number(0, 10000000, 0), duration = Number(1, 10000000, 480);
    private readonly CheckBox locked = new() { Content = "Lock" };
    private readonly string layout = Path.Combine(Settings, "layout.json");
    private long revision;
    private string? routedDestinations;
    private bool closing, loading;

    public MainWindow()
    {
        Title = "MidiWorkbench"; Width = 1280; Height = 800; MinWidth = 900; MinHeight = 600;
        var backend = WindowsMidi.CreateBackend() is { } windows ? new CompositeBackend(new MemoryBackend(), windows) : (IMidiBackend)new MemoryBackend();
        session = new(new SessionOptions(backend, Path.Combine(Settings, "rig.json"), new HostOptions(Wait: WindowsMidi.CreateWait())));
        routing = new(session, ex => status.Text = ex?.Message ?? "");
        session.EndpointsChanged += () => Dispatcher.UIThread.Post(routing.Rebuild);

        var panic = Button("Panic", () => session.Panic());
        ToolTip.SetTip(panic, "Release all notes now and send All Notes Off");
        ToolTip.SetTip(loop, "Repeat the section");
        var toolbar = Row(Button("Play", () => session.Start(loop.IsChecked == true)), Button("Stop", () => { session.Stop(); Reload(); }), panic, loop,
            tempo, Button("Set BPM", () => session.SetTempo((double)(tempo.Value ?? 120))),
            AsyncButton("Open", Open), AsyncButton("Save", Save), AsyncButton("Save take", SaveTake), AsyncButton("Export MIDI", Export));
        ToolTip.SetTip(tempo, "Quarter-note BPM");

        drummer.SelectionChanged += (_, _) => { if (!loading && drummer.SelectedIndex > 0) ShowDrummer(DrumPersonas.All[drummer.SelectedIndex - 1].Id, null); };
        ToolTip.SetTip(complexity, "Drums use complexity instead of density");
        var outline = Column(Label("Section", sectionName), Label("Bars", bars), Label("Meter", Row(numerator, denominator)),
            Label("Root MIDI note", root), Label("Scale intervals", scale), Label("Density", density),
            Label("Intensity", intensity), Label("Variation", variation), Label("Seed", seed),
            Label("Drums", drummer), Label("Complexity", complexity), Label("Fills", fills), Label("Swing", swing), halfTime,
            Row(Button("Generate", Generate), Button("Vary", () => { seed.Value = ((seed.Value ?? 1) + 1) % 4294967296m; Generate(); })));

        parts.SelectionChanged += (_, _) => RefreshNotes();
        notes.SelectionChanged += (_, _) => SelectNote();
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        editor.Children.Add(parts); Grid.SetRow(notes, 1); editor.Children.Add(notes);
        var fields = Row(Label("Pitch", pitch), Label("Velocity", velocity), Label("Tick", tick), Label("Duration", duration), locked);
        Grid.SetRow(fields, 2); editor.Children.Add(fields);
        var apply = Row(Button("Apply note", ApplyNote), Button("Add note", AddNote), Button("Delete note", DeleteNote));
        Grid.SetRow(apply, 3); editor.Children.Add(apply);

        record.SelectionChanged += (_, _) => { if (!loading) Try(() => session.Arm((record.SelectedItem as Choice)?.Id)); };
        ToolTip.SetTip(record, "Records the routing input while playing and plays it through to the part");

        workspace = new(new()
        {
            ["Outline"] = new ScrollViewer { Content = outline },
            ["Patterns"] = editor,
            ["Perform"] = Column(beat, activity, Label("Record into", record)),
            ["Routing"] = routing
        });
        toolbar.Children.Add(Button("Reset layout", () => workspace.Reset()));
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetRow(workspace, 1); body.Children.Add(workspace);
        status.Margin = new Thickness(12, 6); Grid.SetRow(status, 2); body.Children.Add(status); Content = body;
        Reload();
        if (File.Exists(layout)) Try(() => workspace.Restore(File.ReadAllText(layout)));
        Opened += (_, _) => { Try(workspace.OpenFloatingWindows); timer.Start(); };
        timer.Tick += (_, _) => RefreshTransport();
        Closing += (_, e) =>
        {
            if (closing) return;
            Try(session.Stop);
            try { ProjectStore.WriteAtomic(layout, workspace.Save()); }
            catch (Exception ex) { status.Text = $"Layout not saved: {ex.Message}"; }
            closing = true;
        };
        Closed += (_, _) => { timer.Stop(); workspace.Dispose(); session.Dispose(); };
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, Increment = 1, MinWidth = 140 };
    private static Slider Amount(double value) => new() { Minimum = 0, Maximum = 1, Value = value, MinWidth = 180 };
    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) row.Children.Add(c); return row;
    }
    private static StackPanel Column(params Control[] controls)
    {
        var col = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        foreach (var c in controls) col.Children.Add(c); return col;
    }
    private static StackPanel Label(string text, Control control) => Column(new TextBlock { Text = text }, control);
    private Button Button(string title, Action action)
    {
        var button = new Button { Content = title }; button.Click += (_, _) => Try(action); return button;
    }
    private Button AsyncButton(string title, Func<Task> action)
    {
        var button = new Button { Content = title }; button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); status.Text = ""; } catch (Exception ex) { status.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        }; return button;
    }
    private void Try(Action action)
    {
        try { action(); status.Text = ""; } catch (Exception ex) { status.Text = ex.Message; }
    }

    private void ShowDrummer(DrumPersona persona, DrummerSettings? stored)
    {
        var s = stored ?? DrumPersonas.Settings(persona);
        complexity.Value = s.Complexity; fills.Value = s.Fills; swing.Value = s.Swing; halfTime.IsChecked = s.HalfTime;
    }
    private void Generate()
    {
        var outline = new Outline(sectionName.Text ?? "Section", (int)(bars.Value ?? 8),
            new Meter((int)(numerator.Value ?? 4), (int)(denominator.SelectedItem ?? 4)), (int)(root.Value ?? 40),
            (scale.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToImmutableArray(),
            density.Value, intensity.Value, variation.Value, (uint)(seed.Value ?? 1));
        DrummerSettings? settings = drummer.SelectedIndex > 0
            ? new(DrumPersonas.All[drummer.SelectedIndex - 1].Id, complexity.Value, fills.Value, swing.Value, halfTime.IsChecked == true)
            : null;
        session.Generate(outline, settings, basicDrums: settings is null); Reload();
    }
    private void Reload()
    {
        loading = true;
        try
        {
            var project = session.Project; var section = project.Sections[0]; var o = section.Outline;
            revision = session.Revision;
            sectionName.Text = o.Name; bars.Value = o.Bars; numerator.Value = o.Meter.Numerator; denominator.SelectedItem = o.Meter.Denominator;
            root.Value = o.Root; scale.Text = string.Join(",", o.Scale); seed.Value = o.Seed;
            density.Value = o.Density; intensity.Value = o.Intensity; variation.Value = o.Variation; tempo.Value = (decimal)project.Bpm;
            var drums = section.Parts.FirstOrDefault(p => p.Role == PartRole.Drums)?.Pattern.Recipe;
            if (drums?.Drummer is { } d) { drummer.SelectedIndex = (int)d.Persona + 1; ShowDrummer(d.Persona, d); }
            else if (drums is not null) drummer.SelectedIndex = 0;
            var selected = parts.SelectedIndex;
            parts.ItemsSource = section.Parts.Select(p => p.Name).ToArray();
            parts.SelectedIndex = selected >= 0 && selected < section.Parts.Length ? selected : 0;
            RefreshNotes();
            var targets = new[] { new Choice(null, "Off") }.Concat(section.Parts.Select(p => new Choice(p.Id, p.Name)))
                .Append(new Choice(WorkbenchSession.NewPhrase, "New part")).ToArray();
            record.ItemsSource = targets;
            record.SelectedItem = targets.FirstOrDefault(t => t.Id == session.Armed) ?? targets[0];
            // Rebuilding discards unapplied routing edits, so only do it when destinations change.
            var destinations = string.Join("\n", WorkbenchSession.Destinations(project));
            if (destinations != routedDestinations) { routedDestinations = destinations; routing.Rebuild(); }
        }
        finally { loading = false; }
    }
    private Part? SelectedPart() => parts.SelectedIndex >= 0 && parts.SelectedIndex < session.Project.Sections[0].Parts.Length ? session.Project.Sections[0].Parts[parts.SelectedIndex] : null;
    private void RefreshNotes()
    {
        notes.ItemsSource = SelectedPart()?.Pattern.Notes.Select(n => $"{n.Tick,6}   {n.Pitch,3}   {n.Velocity,3}   {n.Duration,5}{(n.Locked ? "   locked" : "")}").ToArray();
    }
    private MidiNote? SelectedNote() => SelectedPart() is {} p && notes.SelectedIndex >= 0 && notes.SelectedIndex < p.Pattern.Notes.Length ? p.Pattern.Notes[notes.SelectedIndex] : null;
    private void SelectNote()
    {
        if (SelectedNote() is not {} n) return;
        pitch.Value = n.Pitch; velocity.Value = n.Velocity; tick.Value = n.Tick; duration.Value = n.Duration; locked.IsChecked = n.Locked;
    }
    private MidiNote Edited(string id) => new(id, (long)(tick.Value ?? 0), (long)(duration.Value ?? 480), (int)(pitch.Value ?? 60), (int)(velocity.Value ?? 90), locked.IsChecked == true);
    private void ApplyNote()
    {
        if (SelectedPart() is not {} p || SelectedNote() is not {} n) return;
        session.EditNote(p.Id, Edited(n.Id)); RefreshNotes();
    }
    private void AddNote()
    {
        if (SelectedPart() is not {} p) return;
        session.EditNote(p.Id, Edited(Guid.NewGuid().ToString("N")) with { Locked = true }); RefreshNotes();
    }
    private void DeleteNote()
    {
        if (SelectedPart() is not {} p || SelectedNote() is not {} n) return;
        session.DeleteNote(p.Id, n.Id); RefreshNotes();
    }
    private void RefreshTransport()
    {
        var s = session.Status(); var meter = session.Project.Sections[0].Outline.Meter;
        if (s.Revision != revision) Reload();
        var unit = MusicalTime.Ppq * 4d / meter.Denominator;
        beat.Text = s.Running ? $"{(long)(s.Tick / meter.BarTicks) + 1} : {(int)(s.Tick / unit) % meter.Numerator + 1}" : "Stopped";
        activity.Text = $"{s.CapturedEvents} events · {s.LateNotes} late notes" + (s.DroppedInputs > 0 ? $" · {s.DroppedInputs} inputs dropped" : "");
        if (!s.CaptureComplete) status.Text = "Capture full. Save this take and start a new one.";
        else if (s.Error is not null) status.Text = s.Error;
    }
    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open project", AllowMultiple = false });
        if (files.Count == 1 && files[0].TryGetLocalPath() is {} path) { session.Load(path); Reload(); }
    }
    private async Task Save()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save project", SuggestedFileName = "song.midiwork.json" });
        if (file?.TryGetLocalPath() is {} path) session.Save(path);
    }
    private static readonly FilePickerFileType JsonType = new("Take (JSON)") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType MidiType = new("Standard MIDI file") { Patterns = ["*.mid"] };
    private async Task SaveTake()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
            { Title = "Save take", SuggestedFileName = "take.json", FileTypeChoices = [JsonType, MidiType] });
        if (file?.TryGetLocalPath() is {} path) session.SaveTake(path);
    }
    private async Task Export()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
            { Title = "Export section as MIDI file", SuggestedFileName = "section.mid", FileTypeChoices = [MidiType] });
        if (file?.TryGetLocalPath() is {} path) session.ExportMidi(path);
    }
}
