using System.Diagnostics;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiWorkbench.Application;
using MidiWorkbench.Desktop;
using MidiWorkbench.Domain;
using MidiWorkbench.Persistence;

// In-process, offscreen Avalonia input/render checks. Never sends OS mouse or keyboard input.
internal static class Program
{
    private static readonly List<string> Results = [];
    private static MainWindow window = null!;
    private static string evidence = "";

    [STAThread]
    private static int Main(string[] args)
    {
        evidence = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/desktop-ux");
        Directory.CreateDirectory(evidence);
        Environment.SetEnvironmentVariable("MIDIWORKBENCH_SETTINGS_DIRECTORY", Path.Combine(evidence, "settings-" + Guid.NewGuid().ToString("N")));
        try
        {
            AppBuilder.Configure<WorkbenchApp>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            window = new MainWindow(); window.Show(); Pump();
            if (args.Contains("--export-choice-only"))
            {
                CheckMidiExportChoice(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--framing-only") || args.Contains("--framing-group-only"))
            {
                CheckChordFraming(args.Contains("--framing-group-only")); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--new-song-only"))
            {
                CheckNewSongWorkflow(); SetField("closing", true); window.Close(); Pump();
                window = new MainWindow(); window.Show(); Pump(); CheckNaturalCompletionNewSong();
                File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--vary-only"))
            {
                CheckVariationSeedFlows(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--lock-only") || args.Contains("--lock-field-only") || args.Contains("--lock-pointer-only"))
            {
                CheckManualNoteLock(args.Contains("--lock-pointer-only") ? "pointer" : args.Contains("--lock-field-only") ? "field" : null);
                File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--pad-only"))
            {
                CheckPadRegisterWorkflow(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--viewport-only"))
            {
                CheckProgrammaticNoteVisibility(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--draft-only"))
            {
                CheckDiscardedDrafts(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--density-only"))
            {
                CheckGeneratorDensityContext(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            if (args.Contains("--section-rename-only"))
            {
                CheckSectionRename(); File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                SetField("closing", true); window.Close(); Pump(); return 0;
            }
            CheckMidiExportChoice();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckChordFraming();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckNewSongWorkflow();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckNaturalCompletionNewSong();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckVariationSeedFlows();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckManualNoteLock();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckSectionRename();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckProgrammaticNoteVisibility();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckDiscardedDrafts();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckGeneratorDensityContext();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckPadRegisterWorkflow();
            SetField("closing", true); window.Close(); Pump();
            window = new MainWindow(); window.Show(); Pump();
            CheckPlaybackIdentity();
            window = new MainWindow(); window.Show(); Pump();
            CheckGeneratorTiming();
            window = new MainWindow(); window.Show(); Pump();
            CheckSongBankNames();
            window = new MainWindow(); window.Show(); Pump();
            var session = Field<WorkbenchSession>("session");
            Check(!window.Title!.StartsWith('*'), "Initial project is clean");
            Check(Field<TextBlock>("outputStatus").Text!.Contains("Simulation"), "Simulation routing is explicit");
            Capture("initial-1380x900", 1380, 900);
            Capture("initial-1000x650", 1000, 650);
            CheckControlsFit("prepareSection", "varySection", "audition", "returnAudition", "accept", "discard");
            CheckOutlineDraftProtection(session);
            CheckModalGate();
            CheckFloatingShortcuts(session);

            // Preview remains separate, while acceptance creates a dirty, undoable project edit.
            var original = session.Project;
            Click("Preview section");
            Until(() => session.Candidate is not null && Field<Button>("accept").IsEnabled);
            Check(ReferenceEquals(original, session.Project), "Preview preserves accepted project");
            Check(Field<Button>("audition").IsEnabled, "Ready candidate exposes audition");
            foreach (var text in new[] { "Apply", "Add note", "Delete", "Paste", "Duplicate" })
                Check(!Field<List<Control>>("noteEditControls").OfType<Button>().Single(b => Equals(b.Content, text)).IsEffectivelyEnabled,
                    $"Candidate disables {text}");
            Check(!Field<NumericUpDown>("tick").IsEffectivelyEnabled, "Candidate disables note fields");
            Check(FindButton(window, "Copy").IsEffectivelyEnabled && FindButton(window, "All").IsEffectivelyEnabled,
                "Candidate keeps Copy and All available");
            window.Close(); Pump();
            var previewDialog = window.OwnedWindows.Single();
            Check(previewDialog.Title == "Unaccepted changes", "Clean accepted project protects ready preview on close");
            using (var frame = previewDialog.CaptureRenderedFrame())
                frame!.Save(Path.Combine(evidence, "unaccepted-preview-dialog.png"), PngBitmapEncoderOptions.Default);
            Click(previewDialog, "Cancel"); Pump();
            Check(window.IsVisible && session.Candidate is not null, "Cancel close preserves ready preview");
            Capture("candidate-1000x650", 1000, 650);
            Click("Audition"); Until(() => session.Status().Running);
            Check(ReferenceEquals(original, session.Project), "Audition preserves accepted project");
            SendKey(Key.F8);
            Until(() => !session.Status().Running);
            Check(!session.Status().Running, "F8 stops candidate audition");
            Click("Accept");
            Check(!ReferenceEquals(original, session.Project), "Accept commits candidate");
            Check(window.Title!.StartsWith('*'), "Accepted candidate marks project unsaved");
            SendKey(Key.Z, KeyModifiers.Control);
            Check(ReferenceEquals(original, session.Project), "Ctrl+Z restores accepted project");
            SendKey(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
            Check(!ReferenceEquals(original, session.Project), "Ctrl+Shift+Z restores generation edit");

            // Avoid native pickers: assign the already-chosen path, then exercise the actual Save shortcut.
            var path = Path.Combine(evidence, "workflow.midiwork.json");
            SetField("projectPath", path); SendKey(Key.S, KeyModifiers.Control);
            Until(() => File.Exists(path));
            Check(!window.Title!.StartsWith('*'), "Ctrl+S writes project and clears unsaved marker");
            Check(ProjectStore.Load(path).Sections.Length == session.Project.Sections.Length, "Saved project independently reloads");
            SendKey(Key.F5); Until(() => session.Status().Running);
            Check(session.Status().Running, "F5 starts arrangement");
            SendKey(Key.Space, KeyModifiers.Control | KeyModifiers.Shift);
            Until(() => !session.Status().Running);
            Check(!session.Status().Running, "Ctrl+Shift+Space panics transport");
            var availableTakes = Field<PerformanceTake[]>("takeItems");
            Check(availableTakes.Length >= 2, "Several takes are available for explicit selection");
            Field<ComboBox>("takes").SelectedIndex = 0;
            var selectedTake = (PerformanceTake)typeof(MainWindow).GetMethod("SelectedTake", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Check(ReferenceEquals(selectedTake, availableTakes[0]) && !ReferenceEquals(selectedTake, availableTakes[^1]), "Save selected resolves the chosen older take");
            var selectedPath = Path.Combine(evidence, "selected-older-take.json");
            session.SaveTake(selectedPath, selectedTake);
            Check(System.Text.Json.JsonSerializer.Serialize(ProjectStore.LoadTake(selectedPath)) == System.Text.Json.JsonSerializer.Serialize(selectedTake),
                "Saving selected writes the older take, not the latest capture");
            CheckBeatFields(session);

            // Exercise live audition/return at a beat boundary while the accepted section is looping.
            Field<CheckBox>("loop").IsChecked = true;
            Field<ComboBox>("candidateBoundary").SelectedIndex = 1;
            Invoke("PlaySection"); Until(() => session.Status().Running);
            Click("Preview section"); Until(() => session.Candidate is not null && Field<Button>("audition").IsEnabled);
            Click("Audition"); Until(() => session.AuditionStatus.Phase == AuditionPhase.Playing);
            Check(session.AuditionStatus.Phase == AuditionPhase.Playing, "Live audition activates at boundary");
            Until(() => Field<Button>("returnAudition").IsEnabled);
            Click("Return");
            Until(() => session.AuditionStatus.Phase == AuditionPhase.Idle);
            Check(session.AuditionStatus.Phase == AuditionPhase.Idle, "Return restores accepted playback");
            SendKey(Key.F8); Until(() => !session.Status().Running);
            Click("Discard");
            CheckArchiveRecovery(session);

            // Make another real UI edit and verify close protection, focus and cancellation.
            Click("Add note");
            Until(() => window.Title!.StartsWith('*'));
            Check(window.Title!.StartsWith('*'), "Note edit marks project unsaved");
            window.Close(); Pump();
            var dialog = window.OwnedWindows.Single();
            Check(dialog.Title == "Unsaved project", "Dirty close opens save decision");
            Check(dialog.FocusManager?.GetFocusedElement() is Button { Content: "Cancel" }, "Dirty close initially focuses Cancel");
            using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(evidence, "unsaved-dialog.png"), PngBitmapEncoderOptions.Default);
            Click(dialog, "Cancel"); Pump();
            Check(window.IsVisible, "Cancel preserves window and unsaved project");
            Check(window.Title!.StartsWith('*'), "Cancel retains unsaved marker");
            Capture("edited-1380x900", 1380, 900);
            Click("Routing"); Pump();
            Capture("routing-1000x650", 1000, 650);
            CheckRoutingFit();
            CheckRoutingRefreshDraft();
            SendKey(Key.S, KeyModifiers.Control); Pump(); window.Close(); Pump();
            Check(!window.IsVisible, "Saved project closes without prompting");
            CheckEditorAndBenchmark();
            File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
            Console.WriteLine($"PASS {Results.Count(r => r.StartsWith("PASS ", StringComparison.Ordinal))} desktop UX checks; evidence: {evidence}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results.Append("FAIL " + ex));
            Console.Error.WriteLine(ex); return 1;
        }
        finally
        {
            if (window?.IsVisible == true) { SetField("closing", true); window.Close(); Pump(); }
        }
    }

    private static T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void SetField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Results.Add("PASS " + message); Console.WriteLine("PASS " + message);
    }
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        // SetupWithoutStarting intentionally has no native message loop. Run the same refresh
        // used by the production 50ms timer so async transport snapshots reach the controls.
        if (window?.IsVisible == true) Invoke("TryTransport");
        Dispatcher.UIThread.RunJobs();
    }
    private static void Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { Pump(); if (timeout.Elapsed.TotalSeconds > 10) throw new TimeoutException("UI action did not complete"); Thread.Sleep(10); }
        Pump();
    }
    private static void Click(string text) => Click(window, text);
    private static Button FindButton(Window root, string text) => root.GetVisualDescendants().OfType<Button>()
        .Single(b => Equals(b.Content, text) && b.IsEffectivelyVisible);
    private static void Click(Window root, string text)
    {
        var buttons = root.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, text) && b.IsEffectivelyVisible).ToArray();
        if (buttons.Length != 1) throw new InvalidOperationException($"Expected one visible '{text}' button, found {buttons.Length}");
        if (!buttons[0].IsEffectivelyEnabled) throw new InvalidOperationException($"'{text}' is disabled");
        using var frame = root.CaptureRenderedFrame();
        var point = buttons[0].TranslatePoint(new Point(buttons[0].Bounds.Width / 2, buttons[0].Bounds.Height / 2), root)
            ?? throw new InvalidOperationException($"'{text}' is detached");
        root.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(point, MouseButton.Left, RawInputModifiers.None); Pump();
    }
    private static void SendKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    { window.KeyPress(key, (RawInputModifiers)modifiers, PhysicalKey.None, null); window.KeyRelease(key, (RawInputModifiers)modifiers, PhysicalKey.None, null); Pump(); }
    private static void CheckControlsFit(params string[] names)
    {
        foreach (var name in names)
        {
            var control = Field<Control>(name);
            var point = control.TranslatePoint(default, window)!.Value;
            Check(point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width <= window.Bounds.Width
                && point.Y + control.Bounds.Height <= window.Bounds.Height, $"{name} fits minimum window size");
        }
    }
    private static void CheckBeatFields(WorkbenchSession session)
    {
        var part = session.SelectedSection.Parts[0];
        var roll = Field<Control>("roll");
        var select = roll.GetType().GetMethod("Select")!;
        foreach (var ticks in new long[] { 1, 7, 319 })
        {
            var note = new MidiNote("beat-precision-" + ticks, ticks, ticks, 42, 80, true);
            session.EditNotes(part.Id, [note], []); Invoke("Reload");
            select.Invoke(roll, [note.Id]); Click("Apply");
            var actual = session.SelectedSection.Parts[0].Pattern.Notes.Single(n => n.Id == note.Id);
            Check(actual.Tick == ticks && actual.Duration == ticks, $"No-op beat fields preserve {ticks}-tick start and length");
        }
        Field<NumericUpDown>("tick").Value = 1.25m;
        Field<NumericUpDown>("duration").Value = .25m;
        Click("Apply");
        var edited = session.SelectedSection.Parts[0].Pattern.Notes.Single(n => n.Id == "beat-precision-319");
        Check(edited.Tick == 240 && edited.Duration == 240, "Quarter-beat field edits commit 240 ticks");
    }
    private static IReadOnlyList<(long Tick, int Channel, int Pitch, int Velocity)> ReadMidiAttacks(string path)
    {
        var bytes = File.ReadAllBytes(path); var position = 0;
        int Number(int count) { var value = 0; for (var i = 0; i < count; i++) value = checked((value << 8) | bytes[position++]); return value; }
        string Tag() { var value = System.Text.Encoding.ASCII.GetString(bytes, position, 4); position += 4; return value; }
        long Variable() { long value = 0; for (var i = 0; i < 4; i++) { var b = bytes[position++]; value = (value << 7) | (uint)(b & 127); if (b < 128) return value; } throw new InvalidDataException("Invalid MIDI variable integer"); }
        if (Tag() != "MThd" || Number(4) != 6 || Number(2) != 1) throw new InvalidDataException("Invalid MIDI header");
        var count = Number(2); if (Number(2) != 960) throw new InvalidDataException("Unexpected MIDI resolution");
        var attacks = new List<(long, int, int, int)>();
        for (var track = 0; track < count; track++)
        {
            if (Tag() != "MTrk") throw new InvalidDataException("Missing MIDI track");
            var length = Number(4); var end = checked(position + length); long tick = 0; var running = -1;
            while (position < end)
            {
                tick += Variable(); var status = bytes[position];
                if (status >= 128) { position++; running = status < 0xF0 ? status : -1; }
                else if (running >= 0) status = (byte)running;
                else throw new InvalidDataException("Invalid running status");
                if (status == 0xFF) { position++; var size = checked((int)Variable()); position += size; }
                else if (status is 0xF0 or 0xF7) { var size = checked((int)Variable()); position += size; }
                else
                {
                    var kind = status & 0xF0; var a = bytes[position++]; var b = kind is 0xC0 or 0xD0 ? 0 : bytes[position++];
                    if (kind == 0x90 && b > 0) attacks.Add((tick, (status & 15) + 1, a, b));
                }
            }
            if (position != end) throw new InvalidDataException("MIDI track length mismatch");
        }
        return attacks;
    }
    private static void CheckMidiExportChoice()
    {
        var session = Field<WorkbenchSession>("session"); var old = session.SelectedSection; var originalRig = session.Rig;
        var outline = old.Outline with { Bars = 1 };
        var part = new Part("export-lead", "Lead", PartRole.Phrase, "Lead", 2,
            new Pattern("export-pattern", PatternKind.Fixed, [new MidiNote("export-note", 240, 240, 64, 90)]));
        var section = old with { Outline = outline, Parts = [part] };
        var project = session.Project with { Sections = [section], Arrangement = [new(section.Id, 2)] };
        typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [project]);
        session.SetRig(new Rig(Rig.CurrentSchemaVersion, [new("offline-device", false, 35)], [new("Lead", "offline-device", 7)])); Invoke("Reload"); Pump();
        var baseline = session.Project; var rig = session.Rig; var output = "";
        SetField("midiExportPicker", (Func<string, Task<string?>>)(_ => Task.FromResult<string?>(output)));
        void Open() { Click("Export MIDI"); Pump(); }
        Window Dialog() => window.OwnedWindows.Single(w => w.Title == "Export MIDI");
        ComboBox NamedCombo(Window dialog, string name) => dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == name);
        Open(); var initial = Dialog();
        var scope = NamedCombo(initial, "Export scope"); var channels = NamedCombo(initial, "Export channels");
        Check(scope.SelectedIndex == 0 && scope.SelectedItem!.ToString() == "Song order"
            && channels.SelectedIndex == 0 && channels.SelectedItem!.ToString() == "Current routing channels", "Export defaults to song order and current routing channels");
        Check((ToolTip.GetTip(channels)?.ToString() ?? "").Contains("availability and delay"), "Export help explains channel fallback and excludes endpoint availability and delay");
        using (var frame = initial.CaptureRenderedFrame()) frame!.Save(Path.Combine(evidence, "export-options-dialog.png"), PngBitmapEncoderOptions.Default);
        Click(initial, "Cancel"); Pump(); Check(!window.OwnedWindows.Any(w => w.Title == "Export MIDI"), "Cancelling export closes without writing a file");
        void ExportAndCheck(string file, int selectedScope, int selectedChannels, int channel, int count)
        {
            output = Path.Combine(evidence, file); Open(); var dialog = Dialog();
            NamedCombo(dialog, "Export scope").SelectedIndex = selectedScope; NamedCombo(dialog, "Export channels").SelectedIndex = selectedChannels;
            Click(dialog, "Choose file"); Until(() => File.Exists(output));
            var attacks = ReadMidiAttacks(output);
            Check(attacks.Count == count && attacks.All(a => a.Channel == channel && a.Pitch == 64 && a.Velocity == 90)
                && attacks.Select(a => a.Tick).SequenceEqual(count == 2 ? [240L, 4080L] : [240L]),
                $"{(selectedScope == 0 ? "Song" : "Section")} export uses {(selectedChannels == 0 ? "routing" : "project")} channels without shifting notes");
        }
        ExportAndCheck("song-routing.mid", 0, 0, 7, 2); ExportAndCheck("song-stored.mid", 0, 1, 2, 2);
        ExportAndCheck("section-routing.mid", 1, 0, 7, 1); ExportAndCheck("section-stored.mid", 1, 1, 2, 1);
        Check(ReferenceEquals(session.Project, baseline) && ReferenceEquals(session.Rig, rig), "Export choices do not change the project or rig");
        typeof(MainWindow).GetField("midiExportPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        // Rig settings persist between windows; do not leak this offline fixture
        // into subsequent tests that launch the ordinary four-part project.
        session.SetRig(originalRig);
    }
    private static void CheckChordFraming(bool skipMaterialAssertion = false)
    {
        var session = Field<WorkbenchSession>("session"); var section = session.SelectedSection;
        var project = session.Project with { Sections = session.Project.Sections.SetItem(0, section with { Outline = section.Outline with { Density = 1 } }) };
        typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [project]); Invoke("Reload"); Pump();
        var parts = Field<ComboBox>("parts"); parts.SelectedItem = parts.ItemsSource!.Cast<object>().Single(item => item.ToString() == "Pad"); Pump();
        var roll = Field<Control>("roll"); var rollType = roll.GetType(); var scroll = Field<ScrollViewer>("rollScroll");
        var generator = Field<Control>("generators"); var genType = generator.GetType();
        T Member<T>(string name) => (T)genType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        Member<ComboBox>("kind").SelectedItem = GeneratorKind.Pad;
        Member<NumericUpDown>("minPitch").Value = 72; Member<NumericUpDown>("maxPitch").Value = 96;
        rollType.GetMethod("Select")!.Invoke(roll, [null]);
        scroll.Offset = new Vector(0, 26 + (127 - 76) * 22 - 40); Pump();
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null || session.GenerationStatus.Phase == GenerationPhase.Failed);
        var candidate = session.Candidate ?? throw new InvalidOperationException($"Pad preview failed: {session.GenerationStatus.Error}");
        var notes = candidate.Parts.Single(p => p.Role == PartRole.Pad).Pattern.Notes.Where(n => n.Tick == 0).ToArray();
        Check(notes.Length == 3, "High-register Pad preview produces a triad");
        bool AllRowsVisible(IEnumerable<MidiNote> selected) => selected.All(note =>
        {
            var rect = (Rect)rollType.GetMethod("NoteRect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(roll, [note])!;
            return rect.Top >= scroll.Offset.Y + 26 - 1 && rect.Bottom <= scroll.Offset.Y + scroll.Viewport.Height + 1;
        });
        if (!skipMaterialAssertion) Check(AllRowsVisible(notes), "Generated high-register triad is fully visible when its pitch span fits");
        Capture("chord-preview-1380x900", 1380, 900);
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        rollType.GetMethod("SelectMany")!.Invoke(roll, [notes.Select(n => n.Id).ToArray(), notes[0].Id]); Pump();
        Field<NumericUpDown>("pitch").Value = notes[0].Pitch + 12;
        Click("Apply"); Pump();
        var moved = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad).Pattern.Notes.Where(n => notes.Any(old => old.Id == n.Id)).ToArray();
        Check(moved.Length == 3 && moved.All(n => notes.Any(old => old.Id == n.Id && n.Pitch == old.Pitch + 12)), "Group-field Apply transposes all three selected voices");
        Check(AllRowsVisible(moved), "Group-field transpose reveals every selected voice when its pitch span fits");
        Capture("chord-transpose-1380x900", 1380, 900);
        var pad = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad);
        var low = new MidiNote("wide-low", 0, 240, 24, 90); var high = new MidiNote("wide-high", 0, 240, 120, 90);
        session.EditNotes(pad.Id, [low, high], []); Invoke("Reload"); Pump();
        rollType.GetMethod("SelectMany")!.Invoke(roll, [new[] { low.Id, high.Id }, low.Id]); Pump();
        Field<NumericUpDown>("pitch").Value = 25; Click("Apply"); Pump();
        var movedLow = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad).Pattern.Notes.Single(n => n.Id == low.Id);
        Check(movedLow.Pitch == 25 && AllRowsVisible([movedLow]), "A group wider than the viewport falls back to its primary note");
    }
    private static void CheckNewSongWorkflow()
    {
        var session = Field<WorkbenchSession>("session");
        session.NewBank("Retained set"); session.AddCurrentToBank();
        session.SetRig(session.Rig with { Input = "loopback" }); var rig = session.Rig; var bank = session.Bank;
        session.StartSection(repeat: true); Thread.Sleep(80); session.Stop(); var takeCount = session.Takes.Count;
        var oldPath = Path.Combine(evidence, "old-song.midiwork.json"); session.Save(oldPath);
        SetField("savedProject", session.Project); SetField("projectPath", oldPath);
        var oldId = session.Project.Id;
        var oldPart = session.SelectedSection.Parts.First(); var oldNote = new MidiNote("new-song-old-note", 0, 120, 50, 90, true);
        session.EditNotes(oldPart.Id, [oldNote], []); Invoke("Reload"); Pump();
        var newButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "New song"));
        void StartNew() { newButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
        Window Dialog(string title) => window.OwnedWindows.Single(w => w.Title == title);
        void Enter(string value)
        {
            var dialog = Dialog("New song"); dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = value;
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            if (dialog.IsVisible) dialog.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null); Pump();
        }
        session.StartSection(repeat: true); Until(() => session.IsRunning);
        Check(!newButton.IsEffectivelyEnabled, "New song is disabled while playback is running");
        StartNew(); Check(!window.OwnedWindows.Any(w => w.Title == "New song") && session.Project.Id == oldId, "New song cannot open during playback");
        session.Stop(); Invoke("Reload"); Pump();
        Field<NumericUpDown>("pitch").Value = 91; Field<NumericUpDown>("velocity").Value = 47;
        Field<NumericUpDown>("tick").Value = 30; Field<NumericUpDown>("duration").Value = 2;
        Field<CheckBox>("locked").IsChecked = false; Field<ComboBox>("snap").SelectedIndex = 3; Field<CheckBox>("loop").IsChecked = true;
        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Until(() => session.Candidate is not null);
        var preview = session.Candidate; var density = Field<Slider>("density"); density.Value = .91;
        var generator = Field<Control>("generators"); var genType = generator.GetType();
        var genSeed = (NumericUpDown)genType.GetField("seed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        genSeed.Value = (genSeed.Value ?? 0) + 11; var draftSeed = genSeed.Value;
        Field<Slider>("complexity").Value = .89;
        StartNew(); Enter("  ");
        Check(Dialog("New song").IsVisible && session.Project.Id == oldId, "Blank New song name is rejected");
        Click(Dialog("New song"), "Cancel"); Pump();
        Check(session.Project.Id == oldId && ReferenceEquals(session.Candidate, preview) && density.Value == .91 && genSeed.Value == draftSeed,
            "Cancelling New song name preserves project, preview and drafts");
        StartNew(); Enter("Fresh Study"); Click(Dialog("Unsaved project"), "Cancel"); Pump();
        Check(session.Project.Id == oldId && ReferenceEquals(session.Candidate, preview) && density.Value == .91,
            "Cancelling the unsaved-project confirmation preserves the old project and preview");
        StartNew(); Enter("Fresh Study"); Click(Dialog("Unsaved project"), "Save"); Pump();
        Check(ProjectStore.Load(oldPath).Sections.SelectMany(s => s.Parts).SelectMany(p => p.Pattern.Notes).Any(n => n.Id == oldNote.Id),
            "Saving on replacement confirmation writes the old project to its existing path");
        Click(Dialog("Unaccepted changes"), "Cancel"); Pump();
        Check(session.Project.Id == oldId && ReferenceEquals(session.Candidate, preview), "Cancelling draft discard after Save keeps the old project and preview");
        StartNew(); Enter(" Fresh Study "); Click(Dialog("Unaccepted changes"), "Discard preview"); Pump();
        var fresh = session.Project; var freshId = fresh.Id;
        Check(freshId != oldId && fresh.Name == "Fresh Study" && Field<string?>("projectPath") is null && window.Title!.StartsWith("* Fresh Study", StringComparison.Ordinal),
            "New song has a fresh identity, trimmed name, no path and an unsaved indicator");
        Check(fresh.Bpm == 120 && fresh.Sections.Length == 1 && fresh.Sections[0].Outline is { Bars: 4, Root: 48 }
            && fresh.Sections[0].Outline.Meter is { Numerator: 4, Denominator: 4 }
            && fresh.Sections[0].Outline.Scale.SequenceEqual([0, 2, 4, 5, 7, 9, 11])
            && fresh.Sections[0].Parts.Length == 4 && fresh.Sections[0].Parts.All(p => p.Pattern.Kind == PatternKind.Generative && p.Pattern.Notes.IsEmpty),
            "New song starts as an empty four-part C-major project");
        Check(!session.IsRunning && session.Armed is null && !session.CanUndo && !session.CanRedo && session.Candidate is null
            && !(bool)typeof(MainWindow).GetMethod("HasUnacceptedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!,
            "New song clears transport, history, candidate and unapplied drafts");
        Check(ReferenceEquals(session.Rig, rig) && ReferenceEquals(session.Bank, bank) && session.Takes.Count >= takeCount,
            "New song retains the rig, bank and take history");
        Check(density.Value == fresh.Sections[0].Outline.Density && !(bool)genType.GetProperty("HasDraft")!.GetValue(generator)!
            && Field<CheckBox>("locked").IsChecked == true && Field<NumericUpDown>("pitch").Value == 60
            && Field<NumericUpDown>("velocity").Value == 90 && Field<NumericUpDown>("tick").Value == 1
            && Field<NumericUpDown>("duration").Value == .25m && Field<ComboBox>("snap").SelectedIndex == 2
            && Field<CheckBox>("loop").IsChecked == false && Field<Control>("roll").GetType().GetProperty("SelectedId")!.GetValue(Field<Control>("roll")) is null,
            "New song resets visible musical and manual-note defaults");
        Field<NumericUpDown>("pitch").Value = 60; Click("Add note");
        var newNote = session.SelectedSection.Parts.SelectMany(p => p.Pattern.Notes).Single();
        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Until(() => session.Candidate is not null);
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(session.SelectedSection.Parts.SelectMany(p => p.Pattern.Notes).Any(n => n.Id == newNote.Id && n.Locked),
            "A new song can be edited and generated while retaining a locked note");
        var newPath = Path.Combine(evidence, "fresh-song.midiwork.json"); SetField("projectPath", newPath); SendKey(Key.S, KeyModifiers.Control); Until(() => File.Exists(newPath));
        session.Load(newPath); Invoke("Reload"); Pump();
        Check(session.Project.Id == freshId && session.Project.Name == "Fresh Study" && session.SelectedSection.Parts.SelectMany(p => p.Pattern.Notes).Any(n => n.Id == newNote.Id),
            "Saving and reopening the new song preserve its identity and edits");
        Capture("new-song-toolbar-1000x650", 1000, 650);
    }
    private static void CheckNaturalCompletionNewSong()
    {
        var session = Field<WorkbenchSession>("session");
        var oldSection = session.SelectedSection;
        var shortSection = new MidiWorkbench.Generation.SectionGenerator().Generate(oldSection.Outline with { Bars = 1 }, oldSection);
        typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session,
            [session.Project with { Bpm = 400, Sections = [shortSection] }]);
        session.SetRig(session.Rig with { Input = "loopback" });
        var path = Path.Combine(evidence, "natural-completion-old.midiwork.json"); session.Save(path); SetField("savedProject", session.Project); SetField("projectPath", path);
        var oldId = session.Project.Id; session.Arm(WorkbenchSession.NewPhrase); session.StartSection();
        var backend = (MidiWorkbench.Engine.IMidiBackend)typeof(WorkbenchSession).GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        using (var keys = backend.OpenOutput("loopback"))
        {
            keys.Submit(new(0, 0x90, 67, 100)); Thread.Sleep(35); keys.Submit(new(0, 0x80, 67, 0));
        }
        var deadline = Stopwatch.StartNew();
        while (session.IsRunning) { if (deadline.Elapsed.TotalSeconds > 5) throw new TimeoutException("Recording did not end naturally"); Thread.Sleep(10); }
        Check(ReferenceEquals(Field<SongProject>("savedProject"), session.Project), "Naturally finished recording is still pending before UI refresh");
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "New song"));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        var dialog = window.OwnedWindows.Single(w => w.Title == "New song");
        Check(session.Project.Id == oldId && session.SelectedSection.Parts.Any(p => p.Role == PartRole.Phrase && p.Pattern.Notes.Any(n => n.Pitch == 67))
            && session.Takes.Any(t => t.Inputs.Any(e => e.Data1 == 67)), "New song surfaces naturally finished recorded notes and raw take before confirmation");
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Next";
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null); Pump();
        var unsaved = window.OwnedWindows.Single(w => w.Title == "Unsaved project"); Click(unsaved, "Cancel"); Pump();
        Check(session.Project.Id == oldId && session.SelectedSection.Parts.Any(p => p.Role == PartRole.Phrase && p.Pattern.Notes.Any(n => n.Pitch == 67))
            && session.Takes.Any(t => t.Inputs.Any(e => e.Data1 == 67)), "Cancel retains the naturally completed recording and take");
        session.Arm(WorkbenchSession.NewPhrase); session.StartSection();
        using (var keys = backend.OpenOutput("loopback"))
        {
            keys.Submit(new(0, 0x90, 72, 100)); Thread.Sleep(35); keys.Submit(new(0, 0x80, 72, 0));
        }
        deadline.Restart();
        while (session.IsRunning) { if (deadline.Elapsed.TotalSeconds > 5) throw new TimeoutException("Recording did not end naturally"); Thread.Sleep(10); }
        var directory = session.TakeDirectory!; var held = directory + "-held-new-song-test";
        Directory.Move(directory, held); File.WriteAllText(directory, "injected archive failure");
        try
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(!window.OwnedWindows.Any(w => w.Title == "New song") && session.Project.Id == oldId
                && session.Take().Inputs.Any(e => e.Data1 == 72), "Archive failure blocks New song while keeping the current raw take recoverable");
        }
        finally { File.Delete(directory); Directory.Move(held, directory); }
    }
    private static void CheckVariationSeedFlows()
    {
        var session = Field<WorkbenchSession>("session"); var seed = Field<NumericUpDown>("seed");
        var acceptedSeed = session.SelectedSection.Outline.Seed;
        void VaryTo(uint expected)
        {
            Field<Button>("varySection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => session.Candidate?.Outline.Seed == expected && Field<Button>("discard").IsEnabled);
        }
        void Discard() { Field<Button>("discard").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
        VaryTo(unchecked(acceptedSeed + 1)); Discard();
        var noteButtons = Field<List<Control>>("noteEditControls");
        var duplicate = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Duplicate") && !noteButtons.Contains(b));
        duplicate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!window.OwnedWindows.Any(w => w.Title == "Unaccepted changes") && session.Project.Sections.Length == 2,
            "Vary then Discard leaves no seed draft or duplicate-section guard");
        acceptedSeed = session.SelectedSection.Outline.Seed;
        VaryTo(unchecked(acceptedSeed + 1)); var first = session.Candidate;
        VaryTo(unchecked(acceptedSeed + 2));
        Check(!ReferenceEquals(first, session.Candidate) && session.Candidate!.Outline.Seed == unchecked(acceptedSeed + 2),
            "Repeated Vary advances successive candidate seeds");
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(session.SelectedSection.Outline.Seed == unchecked(acceptedSeed + 2) && seed.Value == session.SelectedSection.Outline.Seed,
            "Accept stores and displays the varied seed");
        var manual = (decimal)session.SelectedSection.Outline.Seed + 11; seed.Value = manual;
        VaryTo(unchecked(session.SelectedSection.Outline.Seed + 1));
        Check(seed.Value == manual, "Vary preserves a genuinely edited seed draft");
        Discard();
        Check(seed.Value == manual && (bool)typeof(MainWindow).GetMethod("HasUnacceptedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!,
            "Discard preserves a genuine seed draft and its guard");
    }
    private static void CheckManualNoteLock(string? baseline = null)
    {
        var session = Field<WorkbenchSession>("session"); var parts = Field<ComboBox>("parts");
        parts.SelectedItem = parts.ItemsSource!.Cast<object>().Single(item => item.ToString() == "Bass"); Pump();
        var lockBox = Field<CheckBox>("locked"); var roll = Field<Control>("roll"); var rollType = roll.GetType();
        var scroll = Field<ScrollViewer>("rollScroll");
        void SetLock(bool value)
        {
            if (lockBox.IsChecked == value) return;
            var point = lockBox.TranslatePoint(new Point(8, lockBox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None); window.MouseUp(point, MouseButton.Left, RawInputModifiers.None); Pump();
            Check(lockBox.IsChecked == value, $"Lock checkbox can be set to {value}");
        }
        MidiNote Selected()
        {
            var id = (string)rollType.GetProperty("SelectedId")!.GetValue(roll)!;
            return session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes.Single(n => n.Id == id);
        }
        MidiNote AddField(bool value, int pitch, decimal beat)
        {
            SetLock(value); Field<NumericUpDown>("pitch").Value = pitch; Field<NumericUpDown>("tick").Value = beat;
            Click("Add note"); Pump(); var note = Selected();
            Check(note.Locked == value && lockBox.IsChecked == value, $"Field-added note and selected Lock both show {value}"); return note;
        }
        MidiNote AddPointer(bool value, int pitch, double x)
        {
            SetLock(value); scroll.Offset = new Vector(0, 900); Pump();
            var point = roll.TranslatePoint(new Point(106 + x, 26 + (127 - pitch) * 22 + 8), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None); window.MouseUp(point, MouseButton.Left, RawInputModifiers.None); Pump();
            var note = Selected();
            Check(note.Pitch == pitch && note.Locked == value && lockBox.IsChecked == value, $"Pointer-added note and selected Lock both show {value}"); return note;
        }
        if (baseline == "field") { AddField(false, 84, 3); return; }
        if (baseline == "pointer") { AddPointer(false, 80, 120); return; }
        Check(lockBox.IsChecked == true, "Manual note entry starts with Lock checked");
        var fieldLocked = AddField(true, 84, 3); var fieldUnlocked = AddField(false, 85, 5);
        var pointerLocked = AddPointer(true, 80, 120); var pointerUnlocked = AddPointer(false, 81, 360);
        var part = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass);
        Check(part.Pattern.Kind != PatternKind.Fixed, "Manual lock test uses a regenerable pattern");
        SendKey(Key.Z, KeyModifiers.Control);
        Check(!session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes.Any(n => n.Id == pointerUnlocked.Id), "Undo removes the last unlocked pointer note");
        SendKey(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Check(session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes.Any(n => n.Id == pointerUnlocked.Id && !n.Locked), "Redo restores the unlocked pointer note");
        rollType.GetMethod("Select")!.Invoke(roll, [fieldLocked.Id]); Pump(); SetLock(false); Click("Apply"); Pump();
        Check(!session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes.Single(n => n.Id == fieldLocked.Id).Locked,
            "Apply still changes the selected existing note's lock");
        SendKey(Key.Z, KeyModifiers.Control);
        Check(session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes.Single(n => n.Id == fieldLocked.Id).Locked,
            "Undo restores the existing note's lock");
        rollType.GetMethod("SelectMany")!.Invoke(roll, [new[] { fieldLocked.Id, fieldUnlocked.Id }, fieldLocked.Id]); Pump();
        roll.Focus(); SendKey(Key.C, KeyModifiers.Control); SendKey(Key.V, KeyModifiers.Control);
        var pasted = (ImmutableArray<MidiNote>)rollType.GetProperty("SelectedNotes")!.GetValue(roll)!;
        Check(pasted.Length == 2 && pasted.Count(n => n.Locked) == 1 && pasted.Count(n => !n.Locked) == 1,
            "Copy and paste preserve each source note's lock state");
        SendKey(Key.Z, KeyModifiers.Control);
        var path = Path.Combine(evidence, "manual-lock-project.midiwork.json"); SetField("projectPath", path); SendKey(Key.S, KeyModifiers.Control); Until(() => File.Exists(path));
        session.Load(path); Invoke("Reload"); Pump();
        var saved = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes;
        Check(saved.Single(n => n.Id == fieldLocked.Id).Locked && !saved.Single(n => n.Id == fieldUnlocked.Id).Locked
            && saved.Single(n => n.Id == pointerLocked.Id).Locked && !saved.Single(n => n.Id == pointerUnlocked.Id).Locked,
            "Save and reopen preserve the checked and unchecked manual notes");
        var generator = Field<Control>("generators"); var type = generator.GetType();
        var preview = (Button)type.GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        preview.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null || session.GenerationStatus.Phase == GenerationPhase.Failed);
        var regenerated = (session.Candidate ?? throw new InvalidOperationException($"Preview failed: {session.GenerationStatus.Error}"))
            .Parts.Single(p => p.Role == PartRole.Bass).Pattern.Notes;
        Check(regenerated.Any(n => n.Id == fieldLocked.Id) && regenerated.Any(n => n.Id == pointerLocked.Id)
            && !regenerated.Any(n => n.Id == fieldUnlocked.Id || n.Id == pointerUnlocked.Id),
            "Scoped regeneration preserves locked manual notes and replaces unlocked manual notes");
        Field<Button>("discard").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    }
    private static void CheckSectionRename()
    {
        var session = Field<WorkbenchSession>("session"); var part = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass);
        var motif = Enumerable.Range(0, 6).Select(i => new MidiNote($"rename-motif-{i}", i * 480L, 240, 48 + i, 80 + i, false)).ToArray();
        session.EditNotes(part.Id, motif, part.Pattern.Notes.Select(n => n.Id)); session.SetPatternKind(part.Id, PatternKind.Generative); Invoke("Reload"); Pump();
        var before = session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern;
        var rename = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Rename section"));
        void StartRename() { rename.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
        Window Dialog(string title) => window.OwnedWindows.Single(w => w.Title == title);
        void EnterName(string value)
        {
            var dialog = Dialog("Rename section"); var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(); input.Text = value;
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            if (dialog.IsVisible) dialog.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
        }
        StartRename(); EnterName("  ");
        Check(Dialog("Rename section").IsVisible && session.SelectedSection.Outline.Name == "Opening", "Blank section name is rejected without changing material");
        Click(Dialog("Rename section"), "Cancel"); Pump();
        Check(session.SelectedSection.Outline.Name == "Opening" && ReferenceEquals(before, session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern),
            "Cancel section rename preserves the accepted motif");
        var scale = Field<TextBox>("scale"); var originalScale = scale.Text; scale.Text = "invalid,scale";
        var density = Field<Slider>("density"); var originalDensity = density.Value; density.Value = .79;
        var complexity = Field<Slider>("complexity"); var originalComplexity = complexity.Value; complexity.Value = .83;
        var generator = Field<Control>("generators"); var generatorSeed = (NumericUpDown)generator.GetType().GetField("seed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var originalGeneratorSeed = generatorSeed.Value; generatorSeed.Value = (originalGeneratorSeed ?? 0) + 7;
        StartRename(); EnterName("  Chorus  ");
        Check(session.SelectedSection.Outline.Name == "Chorus" && session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif),
            "Metadata rename preserves all six unlocked motif notes");
        Check(scale.Text == "invalid,scale" && density.Value == .79 && complexity.Value == .83 && generatorSeed.Value == originalGeneratorSeed + 7,
            "Section rename retains invalid outline, density, drummer and generator drafts");
        scale.Text = originalScale; density.Value = originalDensity; complexity.Value = originalComplexity; generatorSeed.Value = originalGeneratorSeed;
        SendKey(Key.Z, KeyModifiers.Control);
        Check(session.SelectedSection.Outline.Name == "Opening" && session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif),
            "Undo section rename preserves the motif");
        SendKey(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Check(session.SelectedSection.Outline.Name == "Chorus" && session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif),
            "Redo section rename preserves the motif");

        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var preview = session.Candidate;
        StartRename(); EnterName(" Chorus ");
        Check(ReferenceEquals(session.Candidate, preview) && !window.OwnedWindows.Any(w => w.Title == "Unaccepted preview"), "No-op section rename retains the preview without a warning");
        scale.Text = "still,invalid"; density.Value = .71; complexity.Value = .81; generatorSeed.Value = (generatorSeed.Value ?? 0) + 4;
        var changedGeneratorSeed = generatorSeed.Value;
        StartRename(); EnterName("Verse"); Click(Dialog("Unaccepted preview"), "Cancel"); Pump();
        Check(ReferenceEquals(session.Candidate, preview) && session.SelectedSection.Outline.Name == "Chorus" && scale.Text == "still,invalid",
            "Cancelling preview-discard confirmation preserves the section and preview");
        StartRename(); EnterName("Verse"); Click(Dialog("Unaccepted preview"), "Discard preview"); Pump();
        Check(session.Candidate is null && session.SelectedSection.Outline.Name == "Verse"
            && session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif)
            && scale.Text == "still,invalid" && density.Value == .71 && complexity.Value == .81 && generatorSeed.Value == changedGeneratorSeed,
            "Confirmed section rename preserves accepted motif and unrelated drafts while discarding only the preview");
        var parts = Field<ComboBox>("parts"); parts.SelectedItem = parts.ItemsSource!.Cast<object>().Single(item => item.ToString() == "Bass"); Pump();
        if (window.OwnedWindows.Any(w => w.Title == "Unaccepted changes")) { Click(Dialog("Unaccepted changes"), "Discard changes"); Pump(); }
        var previewPart = (Button)generator.GetType().GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        previewPart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null && Field<TextBlock>("candidateLabel").Text!.StartsWith("Preview ready", StringComparison.Ordinal));
        var partPreview = session.Candidate; generatorSeed.Value = (generatorSeed.Value ?? 0) + 9;
        bool GeneratorDirty() => (bool)generator.GetType().GetProperty("HasDraft")!.GetValue(generator)!;
        Check(GeneratorDirty(), "Editing the preview recipe creates a generator draft");
        StartRename(); EnterName("Bridge"); var specificWarning = Dialog("Unaccepted preview");
        Check(specificWarning.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("generator settings") == true),
            "Section rename names candidate-based generator settings in the discard warning");
        Click(specificWarning, "Cancel"); Pump();
        Check(ReferenceEquals(session.Candidate, partPreview) && GeneratorDirty() && session.SelectedSection.Outline.Name == "Verse",
            "Cancel retains candidate-based generator draft and preview");
        StartRename(); EnterName("Bridge"); Click(Dialog("Unaccepted preview"), "Discard preview"); Pump();
        Check(session.Candidate is null && !GeneratorDirty() && session.SelectedSection.Outline.Name == "Bridge"
            && session.SelectedSection.Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif)
            && scale.Text == "still,invalid" && density.Value == .71 && complexity.Value == .81,
            "Confirmed rename discards candidate-based generator draft but preserves accepted motif and unrelated drafts");
        scale.Text = originalScale; density.Value = originalDensity; complexity.Value = originalComplexity;
        var path = Path.Combine(evidence, "renamed-section.midiwork.json"); SetField("projectPath", path); SendKey(Key.S, KeyModifiers.Control); Until(() => File.Exists(path));
        Check(ProjectStore.Load(path).Sections[0].Outline.Name == "Bridge" && ProjectStore.Load(path).Sections[0].Parts.Single(p => p.Id == part.Id).Pattern.Notes.SequenceEqual(motif),
            "Saving and reopening retain the section name and motif");
        session.StartSection(repeat: true); Until(() => session.IsRunning);
        Check(!rename.IsEffectivelyEnabled, "Section rename action is disabled during playback");
        rename.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!window.OwnedWindows.Any(w => w.Title == "Rename section"), "Section rename does not open while playback is running");
        session.Stop(); Pump();
    }
    private static void CheckDiscardedDrafts()
    {
        var session = Field<WorkbenchSession>("session"); var originalOutline = session.SelectedSection.Outline;
        var complexity = Field<Slider>("complexity"); var initialComplexity = complexity.Value;
        var generator = Field<Control>("generators"); var generatorType = generator.GetType();
        var generatorSeed = (NumericUpDown)generatorType.GetField("seed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var originalSeed = generatorSeed.Value;
        var workspace = Field<Control>("workspace"); workspace.GetType().GetMethod("Show")!.Invoke(workspace, ["Perform"]); Pump();
        var arrangement = Field<Control>("arrangement"); var arrangementType = arrangement.GetType();
        var repeats = (NumericUpDown)arrangementType.GetField("repeats", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arrangement)!;
        var set = arrangement.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Set"));
        void SetRepeats() { set.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
        Window Warning() => window.OwnedWindows.Single(w => w.Title == "Unaccepted changes");
        bool HasDraft() => (bool)typeof(MainWindow).GetMethod("HasUnacceptedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        Field<Slider>("density").Value = .88; complexity.Value = .91; generatorSeed.Value = (originalSeed ?? 0) + 7;
        repeats.Value = 2; SetRepeats(); Click(Warning(), "Cancel"); Pump();
        Check(session.Project.Arrangement.IsEmpty && Field<Slider>("density").Value == .88
            && complexity.Value == .91 && generatorSeed.Value == (originalSeed ?? 0) + 7,
            "Cancel arrangement change preserves accepted order and all drafts");
        SetRepeats(); Click(Warning(), "Discard changes"); Pump();
        Check(session.Project.Arrangement.Length == 1 && session.Project.Arrangement[0].Repeats == 2, "Discard permits the requested arrangement change");
        Check(Field<Slider>("density").Value == originalOutline.Density && complexity.Value == initialComplexity && generatorSeed.Value == originalSeed && !HasDraft(),
            "Discard restores outline, drummer and generator drafts to accepted values");
        repeats.Value = 3; SetRepeats();
        Check(!window.OwnedWindows.Any(w => w.Title == "Unaccepted changes") && session.Project.Arrangement[0].Repeats == 3,
            "A second arrangement change does not repeat the discarded-draft prompt");

        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var candidate = session.Candidate;
        Field<Slider>("density").Value = .75; complexity.Value = .87;
        generatorSeed.Value = (generatorSeed.Value ?? 0) + 1;
        var parts = Field<ComboBox>("parts"); var prior = parts.SelectedIndex; parts.SelectedIndex = prior == 0 ? 1 : 0; Pump();
        Check(Warning().GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Discard unapplied generator settings?"),
            "Part-change warning identifies its generator-only discard scope");
        Click(Warning(), "Discard changes"); Pump();
        Check(parts.SelectedIndex != prior && ReferenceEquals(session.Candidate, candidate)
            && Field<Slider>("density").Value == .75 && complexity.Value == .87,
            "Discarding a part-generator draft keeps the preview and unrelated outline and drummer drafts");
        repeats.Value = 4; SetRepeats(); Click(Warning(), "Cancel"); Pump();
        Check(ReferenceEquals(session.Candidate, candidate) && Field<Slider>("density").Value == .75
            && session.Project.Arrangement[0].Repeats == 3, "Cancel preserves preview, unrelated drafts and accepted arrangement");
        SetRepeats(); Click(Warning(), "Discard changes"); Pump();
        Check(session.Candidate is null && Field<Slider>("density").Value == originalOutline.Density && !HasDraft()
            && session.Project.Arrangement[0].Repeats == 4, "General discard clears preview and drafts after the requested action");
        var scale = Field<TextBox>("scale"); var originalScale = scale.Text; scale.Text = "invalid scale";
        repeats.Value = 5; SetRepeats(); Click(Warning(), "Cancel"); Pump();
        Check(scale.Text == "invalid scale" && session.Project.Arrangement[0].Repeats == 4, "Cancel preserves invalid outline input for correction");
        SetRepeats(); Click(Warning(), "Discard changes"); Pump();
        Check(scale.Text == originalScale && !HasDraft() && session.Project.Arrangement[0].Repeats == 5,
            "Discard restores invalid outline input without another prompt");
    }
    private static void CheckGeneratorDensityContext()
    {
        var session = Field<WorkbenchSession>("session"); var generator = Field<Control>("generators"); var type = generator.GetType();
        var context = (TextBlock)type.GetField("densityContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var kind = (ComboBox)type.GetField("kind", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var probability = (Slider)type.GetField("probability", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        kind.SelectedItem = GeneratorKind.Euclidean; probability.Value = 1; Pump();
        Check(context.Text!.Contains("35") && context.Text.Contains("step chance"), "Generator shows the accepted 35% section density as a step-chance factor");
        var tip = ToolTip.GetTip(context)?.ToString() ?? "";
        Check(tip.Contains("accepted section density") && tip.Contains("Preview section") && tip.Contains("per-step probabilities"),
            "Density help explains accepted outline edits and other probability factors");
        var section = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Section settings"));
        var panel = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Part generator"));
        section.IsExpanded = false; panel.IsExpanded = true; Pump();
        generator.GetVisualAncestors().OfType<ScrollViewer>().First().Offset = new Vector(0, 230); Pump();
        Capture("generator-density-35-1000x650", 1000, 650);
        Field<Slider>("density").Value = 1; Pump();
        Check(context.Text!.Contains("35"), "Unapplied section density draft does not change the generator context");
        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null && Field<Button>("accept").IsEnabled);
        Check(context.Text!.Contains("35"), "An unaccepted 100% section preview still shows accepted density 35%");
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(context.Text!.Contains("100"), "Accepting the outline updates generator context to 100%");
    }
    private static void CheckProgrammaticNoteVisibility()
    {
        var session = Field<WorkbenchSession>("session");
        var parts = Field<ComboBox>("parts");
        parts.SelectedItem = parts.ItemsSource!.Cast<object>().Single(item => item.ToString() == "Bass"); Pump();
        var editor = Field<Control>("roll"); var scroll = Field<ScrollViewer>("rollScroll");
        var type = editor.GetType();
        Rect SelectionRect()
        {
            var selected = (string?)type.GetProperty("SelectedId")!.GetValue(editor) ?? throw new InvalidOperationException("No selected note");
            var part = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Bass);
            var note = part.Pattern.Notes.Single(n => n.Id == selected);
            return (Rect)type.GetMethod("NoteRect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [note])!;
        }
        bool Visible(Rect note) => note.Left >= scroll.Offset.X + 106 - 1 && note.Left < scroll.Offset.X + scroll.Viewport.Width
            && note.Top >= scroll.Offset.Y + 26 - 1 && note.Top < scroll.Offset.Y + scroll.Viewport.Height;
        Field<Slider>("zoom").Value = 240; scroll.Offset = new Vector(0, 1500); Pump();
        Field<NumericUpDown>("pitch").Value = 84; Field<NumericUpDown>("tick").Value = 1;
        Click("Add note"); Pump();
        Check(Visible(SelectionRect()), "A field-added C6 note is revealed inside the piano-roll viewport");
        Field<NumericUpDown>("pitch").Value = 85; Field<NumericUpDown>("tick").Value = 28;
        Click("Add note"); Pump();
        Check(Visible(SelectionRect()), "A field-added late note is revealed horizontally at high zoom");
        Field<NumericUpDown>("pitch").Value = 90; Field<NumericUpDown>("tick").Value = 5;
        Click("Apply"); Pump();
        Check(Visible(SelectionRect()), "Applying pitch and time fields reveals the edited selection");
        scroll.Offset = new Vector(0, 0); Pump();
        Field<List<Control>>("noteEditControls").OfType<Button>().Single(b => Equals(b.Content, "Duplicate"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(Visible(SelectionRect()), "Duplicating a selected note reveals the new selection");
        var visibleNote = SelectionRect(); var point = editor.TranslatePoint(new Point(visibleNote.Left + 4, visibleNote.Top + 4), window)!.Value;
        var offset = scroll.Offset;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(point + new Vector(70, 0), RawInputModifiers.LeftMouseButton); Pump();
        Check(scroll.Offset == offset, "Pointer dragging does not scroll the note viewport");
        window.MouseUp(point + new Vector(70, 0), MouseButton.Left, RawInputModifiers.None); Pump();
        Check(scroll.Offset == offset && SelectionRect().Left > visibleNote.Left, "Releasing a real note drag does not jump the viewport");
        var noteId = (string)type.GetProperty("SelectedId")!.GetValue(editor)!;
        var list = Field<ListBox>("notes");
        var choice = list.ItemsSource!.Cast<object>().Single(item => ((MidiNote)item.GetType().GetProperty("Note")!.GetValue(item)!).Id == noteId);
        list.SelectedItem = null; scroll.Offset = new Vector(0, 0); Pump();
        list.SelectedItem = choice; Pump();
        Check(Visible(SelectionRect()), "Selecting a note from the note list reveals it in the editor");
        editor.Focus(); SendKey(Key.C, KeyModifiers.Control); scroll.Offset = new Vector(0, 0); Pump(); SendKey(Key.V, KeyModifiers.Control);
        Check(Visible(SelectionRect()), "Pasting notes reveals the new selection");
        scroll.Offset = new Vector(0, 0); Pump(); editor.Focus(); SendKey(Key.Up);
        Check(Visible(SelectionRect()), "Keyboard note movement reveals the moved selection");
    }
    private static void CheckPadRegisterWorkflow()
    {
        var session = Field<WorkbenchSession>("session");
        Field<NumericUpDown>("root").Value = 36; Field<NumericUpDown>("bars").Value = 2;
        Field<TextBox>("scale").Text = "0,2,3,5,7,8,10"; Field<Slider>("density").Value = 1;
        Click("Preview section"); Until(() => session.Candidate is not null && Field<Button>("accept").IsEnabled); Click("Accept"); Pump();
        var parts = Field<ComboBox>("parts"); parts.SelectedItem = parts.ItemsSource!.Cast<object>().Single(item => item.ToString() == "Pad"); Pump();
        var generator = Field<Control>("generators"); var type = generator.GetType();
        T Member<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        Member<ComboBox>("kind").SelectedItem = GeneratorKind.Pad;
        Member<NumericUpDown>("minPitch").Value = 48; Member<NumericUpDown>("maxPitch").Value = 72;
        Member<TextBox>("chord").Text = "0,2,4"; Member<TextBox>("progression").Text = "0,3";
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null || session.GenerationStatus.Phase == GenerationPhase.Failed);
        var candidate = session.Candidate ?? throw new InvalidOperationException($"Pad preview failed: {session.GenerationStatus.Error}");
        var pattern = candidate.Parts.Single(p => p.Role == PartRole.Pad).Pattern;
        var barTicks = candidate.Outline.Meter.BarTicks;
        var groups = pattern.Notes.GroupBy(n => n.Tick / barTicks).OrderBy(g => g.Key).ToArray();
        Check(groups.Length == 2 && groups.All(g => g.Count() == 3), "Pad preview keeps three voices in each bar when root is below the pitch range");
        Check(groups[0].Select(n => n.Pitch % 12).Order().SequenceEqual([0, 3, 7])
            && groups[1].Select(n => n.Pitch % 12).Order().SequenceEqual([0, 5, 8])
            && pattern.Notes.All(n => n.Pitch is >= 48 and <= 72), "Pad preview preserves both minor-scale chord pitch-class sets in range");
        Check(groups[0].Select(n => n.Pitch).Order().SequenceEqual([48, 51, 55])
            && groups[1].Select(n => n.Pitch).Order().SequenceEqual([53, 56, 60]), "Pad preview voices the two chords in the requested register");
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        var path = Path.Combine(evidence, "pad-register-song.midiwork.json"); SetField("projectPath", path); SendKey(Key.S, KeyModifiers.Control); Until(() => File.Exists(path));
        session.Load(path); Invoke("Reload"); Pump();
        var saved = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad).Pattern;
        Check(saved.Recipe?.Generator == MidiWorkbench.Generation.PatternGenerator.GeneratorId
            && saved.Recipe.Version == "pattern-2" && saved.Recipe.Version == MidiWorkbench.Generation.PatternGenerator.Version
            && saved.Recipe.Settings is { Kind: GeneratorKind.Pad, MinPitch: 48, MaxPitch: 72 }
            && saved.Notes.Select(n => n.Pitch).SequenceEqual(pattern.Notes.Select(n => n.Pitch)),
            "Accept, save and reopen preserve Pad voicing and the current recipe");

        var roll = Field<Control>("roll"); var rollType = roll.GetType(); var scroll = Field<ScrollViewer>("rollScroll");
        bool AnyMaterialVisible(Pattern value)
        {
            var visible = new Rect(scroll.Offset.X + 106, scroll.Offset.Y + 26,
                Math.Max(0, scroll.Viewport.Width - 106), Math.Max(0, scroll.Viewport.Height - 26));
            return value.Notes.Any(note => ((Rect)rollType.GetMethod("NoteRect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(roll, [note])!).Intersects(visible));
        }
        void WaitForPattern(Pattern value) => Until(() => rollType.GetField("part", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(roll) is Part part
            && ReferenceEquals(part.Pattern, value));
        rollType.GetMethod("Select")!.Invoke(roll, [saved.Notes.First().Id]); rollType.GetMethod("RevealSelection")!.Invoke(roll, null); Pump();
        Member<NumericUpDown>("minPitch").Value = 72; Member<NumericUpDown>("maxPitch").Value = 96;
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var high = session.Candidate!.Parts.Single(p => p.Role == PartRole.Pad).Pattern; WaitForPattern(high); Pump();
        Check(AnyMaterialVisible(high), "Changing the Pad preview register reveals generated material");
        var selectedId = (string)rollType.GetProperty("SelectedId")!.GetValue(roll)!;
        Check(Field<NumericUpDown>("pitch").Value == high.Notes.Single(n => n.Id == selectedId).Pitch,
            "Retained selected note fields refresh to the preview pitch");
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(AnyMaterialVisible(session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad).Pattern), "Accept keeps the new Pad register visible");
        scroll.Offset = new Vector(0, 0); Pump(); var manualOffset = scroll.Offset; Pump(); Pump();
        Check(scroll.Offset == manualOffset, "Ordinary transport refresh preserves manual editor scrolling");
        Member<NumericUpDown>("minPitch").Value = 48; Member<NumericUpDown>("maxPitch").Value = 72;
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var low = session.Candidate!.Parts.Single(p => p.Role == PartRole.Pad).Pattern; WaitForPattern(low); Pump();
        Field<Button>("discard").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        var accepted = session.SelectedSection.Parts.Single(p => p.Role == PartRole.Pad).Pattern;
        Check(AnyMaterialVisible(accepted), "Discarding a different-register preview reveals the accepted Pad again");
        rollType.GetMethod("Select")!.Invoke(roll, [null]); Pump();
        var visibleOffset = scroll.Offset;
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var sameRegister = session.Candidate!.Parts.Single(p => p.Role == PartRole.Pad).Pattern; WaitForPattern(sameRegister); Pump();
        Check(AnyMaterialVisible(sameRegister) && scroll.Offset == visibleOffset, "Preview preserves a manual viewport that already contains material when no note is selected");
        Field<Button>("discard").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        rollType.GetMethod("Select")!.Invoke(roll, [null]); scroll.Offset = new Vector(0, 0); Pump();
        Member<Button>("preview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null); var noSelection = session.Candidate!.Parts.Single(p => p.Role == PartRole.Pad).Pattern; WaitForPattern(noSelection); Pump();
        Check(AnyMaterialVisible(noSelection), "Preview reveals generated material when no note is selected and none is visible");
        Field<Button>("discard").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    }
    private static void CheckPlaybackIdentity()
    {
            var session = Field<WorkbenchSession>("session");
            var initial = session.Project;
            var first = new MidiWorkbench.Generation.SectionGenerator().Generate(initial.Sections[0].Outline with { Name = "Verse", Bars = 1 });
            var second = first with { Id = "status-chorus", Outline = first.Outline with { Name = "Chorus" } };
            var project = initial with { Bpm = 400, Sections = [first, second], Arrangement = [new(first.Id), new(second.Id)] };
            typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [project]);
            session.SelectSection(second.Id); Invoke("Reload"); SetField("savedProject", session.Project);
            var label = Field<TextBlock>("performanceStatus");
            Field<CheckBox>("loop").IsChecked = true;
            Invoke("StartSong"); Until(() => session.IsRunning);
            Check(label.Text == "Playing: Verse" && label.IsVisible, "Song shows the sounding arrangement section rather than the editor selection");
            session.Stop(); Invoke("Reload"); Pump();
            Invoke("PlaySection"); Until(() => session.IsRunning);
            Check(label.Text == "Playing: Chorus", "Section playback identifies its sounding section");
            Field<ComboBox>("launchSection").SelectedIndex = 0;
            Field<ComboBox>("launchBoundary").SelectedIndex = 1;
            Invoke("QueueSection");
            Until(() => session.PlayingSectionId == first.Id);
            Check(session.SelectedSectionId == second.Id && label.Text == "Playing: Verse", "Queued launch keeps selected Chorus separate from sounding Verse");
            Capture("playing-1000x650", 1000, 650); Capture("playing-1380x900", 1380, 900);
            session.Stop(); Invoke("Reload"); Pump();
            Check(!label.IsVisible, "Stopped transport clears the sounding label");

            Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => session.Candidate is not null || session.GenerationStatus.Phase == GenerationPhase.Failed);
            if (session.Candidate is null) throw new InvalidOperationException($"Preview failed: {session.GenerationStatus.Error}; {Field<TextBlock>("status").Text}");
            Click("Audition"); Until(() => session.IsRunning);
            Check(label.Text == "Playing preview: Chorus", "Preview playback is identified as a preview");
            Thread.Sleep(200); session.Stop(); Invoke("Reload"); Pump(); Click("Discard");
            var recorded = session.Takes.Last();
            SetField("playback", "take"); SetField("replayingTake", recorded); session.PlayTake(recorded);
            Until(() => session.IsRunning);
            Check(label.Text == "Playing: take", "Take replay is not mislabeled as the selected section");
            session.Stop(); Invoke("Reload"); Pump();

            var selected = session.SelectedSection;
            var longSection = selected with { Outline = selected.Outline with { Name = "Chorus with a deliberately very long section name for a live performance" } };
            typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session,
                [session.Project with { Sections = session.Project.Sections.Replace(selected, longSection) }]);
            session.SetRig(session.Rig with { Input = "loopback" }); session.Arm(WorkbenchSession.NewPhrase); Invoke("Reload"); Pump();
            Check(Equals(Field<Button>("play").Content, "Record section") && Equals(Field<Button>("playSection").Content, "Record section"),
                "Both play actions identify armed recording");
            Invoke("PlaySection"); Until(() => session.IsRunning);
            Check(label.Text!.StartsWith("Recording: New part · Playing: Chorus with a deliberately", StringComparison.Ordinal),
                "Running recording prioritizes its target even with a long sounding section name");
            Capture("recording-1000x650", 1000, 650); Capture("recording-1380x900", 1380, 900);
            session.Stop(); session.Arm(null); Invoke("Reload"); Pump();
            Check(!label.IsVisible && Equals(Field<Button>("play").Content, "Play song") && Equals(Field<Button>("playSection").Content, "Play section"),
                "Stopping and disarming restore play labels");
            SetField("savedProject", session.Project); window.Close(); Pump();
    }
    private static void CheckGeneratorTiming()
    {
        var session = Field<WorkbenchSession>("session");
        var generator = Field<Control>("generators"); var type = generator.GetType();
        T Member<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var load = type.GetMethod("Load")!; var read = type.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var length = Member<ComboBox>("stepLength"); var beats = Member<NumericUpDown>("stepBeats");
        var kind = Member<ComboBox>("kind"); var source = Member<CheckBox>("source");
        var rows = Member<Dictionary<Control, Control>>("fields");
        bool Dirty() => (bool)type.GetProperty("HasDraft")!.GetValue(generator)!;
        GeneratorSettings Read() => (GeneratorSettings)read.Invoke(generator, null)!;
        void Select(string value) => length.SelectedItem = length.ItemsSource!.Cast<object>().Single(item => item.ToString() == value);
        void Load(GeneratorSettings settings) => load.Invoke(generator, [settings, 123u]);
        var settings = new GeneratorSettings(GeneratorKind.StepSequencer)
        {
            Seed = null, PartSeed = 37, Steps = 4, StepTicks = 73,
            StepVelocities = [80, 81, 82, 83], StepProbabilities = [.1, .2, .3, .4], StepRatchets = [1, 2, 3, 4],
            EnabledSteps = [true, false, true, false], ChordDegrees = [1, 3, 5], Progression = [2, 4]
        };
        Load(settings);
        Check(length.SelectedItem!.ToString() == "Custom" && Read().StepTicks == 73 && !Dirty(), "Custom 73-tick timing loads and round-trips without a false draft");
        var preserved = Read();
        Check(preserved.Seed is null && preserved.PartSeed == 37 && preserved.ChordDegrees.SequenceEqual(settings.ChordDegrees)
            && preserved.Progression.SequenceEqual(settings.Progression) && preserved.StepVelocities.SequenceEqual(settings.StepVelocities),
            "Unchanged generator preserves hidden fields, nullable seed and step controls");
        var shortSequence = settings with { Steps = 1, Pulses = 5, Rotation = 7 };
        Load(shortSequence); var shortRead = Read();
        Check(!Dirty() && shortRead.Pulses == 5 && shortRead.Rotation == 7, "Unchanged non-Euclidean recipe preserves hidden rhythm values");
        kind.SelectedItem = GeneratorKind.Euclidean; var euclidean = Read();
        Check(euclidean.Pulses <= euclidean.Steps && euclidean.Rotation < euclidean.Steps, "Switching to Euclidean uses bounded visible rhythm values");
        Load(settings);
        Select("1/8"); Check(Read().StepTicks == 480 && Dirty(), "Eighth-note timing maps to 480 ticks and marks a draft");
        Select("1/8 triplet"); Check(Read().StepTicks == 320, "Eighth-note triplet maps to 320 ticks");
        Select("1/8 dotted"); Check(Read().StepTicks == 720, "Dotted eighth note maps to 720 ticks");
        Select("Custom"); beats.Value = 8m / MusicalTime.Ppq; Check(Read().StepTicks == 8, "Custom timing supports the minimum eight ticks");
        beats.Value = 128; Check(Read().StepTicks == 122880, "Custom timing supports the maximum 128 quarter notes");
        Load(settings); Member<NumericUpDown>("steps").Value = 2;
        var shortened = Read();
        Check(shortened.StepVelocities.Length == 2 && shortened.StepProbabilities.Length == 2 && shortened.StepRatchets.Length == 2 && shortened.EnabledSteps.Length == 2
            && settings.StepVelocities.Length == 4, "Shorter sequence trims candidate step arrays without changing the loaded recipe");
        Load(settings); kind.SelectedItem = GeneratorKind.Pad;
        Check(!rows[length].IsVisible && !rows[beats].IsVisible, "Pad hides step-length controls");
        var note = new MidiNote("source", 0, 120, 60, 90);
        var arp = new GeneratorSettings(GeneratorKind.Arpeggiator) { StepDegrees = [7, 5], SourceNotes = [note], SourceLengthTicks = 960 };
        Load(arp);
        Check(!Dirty() && Read().StepDegrees.SequenceEqual(arp.StepDegrees) && Read().SourceNotes.Length == 1, "Arpeggiator round-trip preserves hidden degrees and source material");
        source.IsChecked = false;
        Check(Read().SourceNotes.IsEmpty && Read().SourceLengthTicks is null, "Disabling source removes stored source material");
        source.IsChecked = true; kind.SelectedItem = GeneratorKind.StepSequencer;
        Check(Read().SourceNotes.IsEmpty && Read().SourceLengthTicks is null, "Non-arpeggiator generators do not retain source material");

        var sectionSettings = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Section settings"));
        var generatorExpander = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Part generator"));
        sectionSettings.IsExpanded = false; generatorExpander.IsExpanded = true; Pump();
        Load(settings); Pump();
        generator.GetVisualAncestors().OfType<ScrollViewer>().First().Offset = new Vector(0, 320); Pump();
        Capture("generator-custom-1380x900", 1380, 900);
        Select("1/8"); length.BringIntoView(); Pump(); Capture("generator-eighth-1380x900", 1380, 900);
        var preview = Member<Button>("preview"); preview.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null || session.GenerationStatus.Phase == GenerationPhase.Failed);
        var candidate = session.Candidate ?? throw new InvalidOperationException($"Part preview failed: {session.GenerationStatus.Error}; {Field<TextBlock>("status").Text}");
        var selectedId = session.SelectedSection.Parts[0].Id;
        Check(candidate.Parts.Single(p => p.Id == selectedId).Pattern.Recipe?.Settings?.StepTicks == 480, "Preview part uses the selected musical step length");
        Field<Button>("accept").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(session.SelectedSection.Parts.Single(p => p.Id == selectedId).Pattern.Recipe?.Settings?.StepTicks == 480, "Accept stores the selected step length in the recipe");
        SetField("savedProject", session.Project); window.Close(); Pump();
    }
    private static void CheckSongBankNames()
    {
        var session = Field<WorkbenchSession>("session");
        Window Dialog(string title) => window.OwnedWindows.Single(w => w.Title == title);
        TextBox Name(Window dialog) => dialog.GetVisualDescendants().OfType<TextBox>().Single();
        void PressDialog(Window target, Key key) { target.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null); if (target.IsVisible) target.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(); }
        void SetNameAndEnter(string title, string value) { var dialog = Dialog(title); Name(dialog).Text = value; PressDialog(dialog, Key.Enter); }
        void Activate(string content)
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content) && b.IsEffectivelyVisible);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        }

        Click("Rename song"); var invalid = Dialog("Rename song");
        using (var frame = invalid.CaptureRenderedFrame()) frame!.Save(Path.Combine(evidence, "rename-song-dialog.png"), PngBitmapEncoderOptions.Default);
        Invoke("StartSong"); Until(() => session.IsRunning); PressDialog(invalid, Key.F8); Until(() => !session.IsRunning);
        Check(!session.IsRunning && invalid.IsVisible, "F8 stops playback while the name dialog stays open");
        Name(invalid).Text = "  "; PressDialog(invalid, Key.Enter);
        Check(invalid.IsVisible && session.Project.Name == "Untitled" && invalid.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "Enter a name."),
            "Blank song name is rejected without changing the project");
        PressDialog(invalid, Key.Escape); Check(!invalid.IsVisible && session.Project.Name == "Untitled", "Escape cancels naming without mutation");
        Click("Rename song"); SetNameAndEnter("Rename song", "  First Song  ");
        Check(session.Project.Name == "First Song" && window.Title!.StartsWith("* First Song", StringComparison.Ordinal), "Renaming trims the song name and marks the project dirty");
        SendKey(Key.Z, KeyModifiers.Control); Check(session.Project.Name == "Untitled", "Undo restores the previous song name");
        SendKey(Key.Z, KeyModifiers.Control | KeyModifiers.Shift); Check(session.Project.Name == "First Song", "Redo restores the song name");
        var songPath = Path.Combine(evidence, "named-project.midiwork.json"); SetField("projectPath", songPath);
        SendKey(Key.S, KeyModifiers.Control); Until(() => File.Exists(songPath));
        Check(ProjectStore.Load(songPath).Name == "First Song" && window.Title!.Contains("First Song (named-project.midiwork.json)"),
            "Saving preserves the song name independently of its filename");

        var workspace = Field<Control>("workspace"); workspace.GetType().GetMethod("Show")!.Invoke(workspace, ["Perform"]); Pump();
        var bankPanel = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Bank")); bankPanel.IsExpanded = true; Pump();
        Activate("New bank");
        using (var frame = Dialog("New bank").CaptureRenderedFrame()) frame!.Save(Path.Combine(evidence, "new-bank-dialog.png"), PngBitmapEncoderOptions.Default);
        SetNameAndEnter("New bank", "Festival");
        Check(session.Bank?.Name == "Festival" && Field<TextBlock>("bankName").Text == "Festival", "Creating a bank uses the requested name");
        Activate("Add current");
        var sectionScale = Field<TextBox>("scale"); var originalScale = sectionScale.Text; sectionScale.Text = "0,2,3,5,7,8,10";
        Click("Rename song"); SetNameAndEnter("Rename song", "Second Song");
        Check(session.Project.Name == "Second Song" && sectionScale.Text == "0,2,3,5,7,8,10", "Song rename preserves unapplied section settings");
        Activate("Add current");
        Check(session.Bank!.Songs.Select(s => s.Project.Name).SequenceEqual(["First Song", "Second Song"])
            && Field<ListBox>("bankOrder").ItemsSource!.Cast<object>().Select(o => o.ToString()).SequenceEqual(["1. First Song", "2. Second Song"]),
            "Two named songs appear distinctly in the bank set order");
        Activate("Rename bank"); SetNameAndEnter("Rename bank", "Tour Set");
        Check(session.Bank!.Name == "Tour Set" && Field<TextBlock>("bankName").Text == "Tour Set" && window.Title!.Contains("Bank unsaved"),
            "Renaming a bank updates its read-only label and dirty indicator");
        bankPanel.GetVisualAncestors().OfType<ScrollViewer>().First().Offset = new Vector(0, 700); Pump();
        Capture("named-bank-1000x650", 1000, 650);
        var bankPath = Path.Combine(evidence, "named-bank.json"); session.SaveBank(bankPath);
        var restored = ProjectStore.LoadBank(bankPath);
        Check(restored.Name == "Tour Set" && restored.Songs.Select(s => s.Project.Name).SequenceEqual(["First Song", "Second Song"])
            && restored.Order.Length == 2, "Bank save and reload preserve its name, song names and order");
        Activate("New bank"); var newBank = Dialog("New bank"); PressDialog(newBank, Key.Escape);
        Check(session.Bank!.Name == "Tour Set" && session.Bank.Songs.Length == 2, "Cancelling new bank preserves the current bank");

        // A real rename invalidates a prepared preview, so cancellation must preserve it.
        workspace.GetType().GetMethod("Show")!.Invoke(workspace, ["Outline"]); Pump();
        Field<Button>("prepareSection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => session.Candidate is not null);
        var preview = session.Candidate;
        Click("Rename song"); SetNameAndEnter("Rename song", "Third Song");
        var warning = Dialog("Unaccepted preview"); Click(warning, "Cancel"); Pump();
        Check(session.Project.Name == "Second Song" && ReferenceEquals(session.Candidate, preview), "Cancelling rename preserves the prepared preview and name");
        Click("Rename song"); SetNameAndEnter("Rename song", "Third Song"); Click(Dialog("Unaccepted preview"), "Discard preview"); Pump();
        Check(session.Project.Name == "Third Song" && session.Candidate is null && sectionScale.Text == "0,2,3,5,7,8,10",
            "Confirmed rename discards only the preview and retains outline draft");
        sectionScale.Text = originalScale;
        Until(() => !Field<bool>("documentDialog"));
        workspace.GetType().GetMethod("Reset")!.Invoke(workspace, null); Pump();
        SetField("savedProject", session.Project); SetField("savedBank", session.Bank!); window.Close(); Pump();
        Check(!window.IsVisible, "Named project and bank close after pending dialog completes");
    }
    private static void CheckOutlineDraftProtection(WorkbenchSession session)
    {
        var outlineSeed = Field<NumericUpDown>("seed"); var originalSeed = outlineSeed.Value;
        outlineSeed.Value = (originalSeed ?? 0) + 7;
        Field<NumericUpDown>("tempo").Value = 121; Click("Set");
        Check(outlineSeed.Value == originalSeed + 7, "Tempo reload preserves unapplied section settings");
        SendKey(Key.Z, KeyModifiers.Control);
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Undo protects unapplied section settings");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        session.Undo(); Invoke("Reload");
        SendKey(Key.F8);
        Check(outlineSeed.Value == originalSeed + 7, "Stop and ordinary reload preserve unapplied section settings");
        Check(!window.Title!.StartsWith('*'), "Undo tempo returns accepted project to clean state");
        window.Close(); Pump();
        var dialog = window.OwnedWindows.Single();
        Check(dialog.Title == "Unaccepted changes", "Clean accepted project protects unapplied outline on close");
        using (var frame = dialog.CaptureRenderedFrame())
            frame!.Save(Path.Combine(evidence, "unaccepted-outline-dialog.png"), PngBitmapEncoderOptions.Default);
        Check(dialog.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible) == 2,
            "Unaccepted-change decision offers discard and cancel");
        Click(dialog, "Cancel"); Pump();
        Check(window.IsVisible && outlineSeed.Value == originalSeed + 7, "Cancel close preserves unapplied outline");
        outlineSeed.Value = originalSeed;
        Check(ReferenceEquals(Field<SongProject>("savedProject"), session.Project), "Draft protection does not mutate accepted project");

        var firstSection = session.SelectedSectionId;
        session.AddSection(); session.SelectSection(firstSection); Invoke("Reload");
        outlineSeed.Value = (originalSeed ?? 0) + 9;
        Field<ComboBox>("sections").SelectedIndex = 1; Pump();
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Changing section protects outline draft");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        Check(session.SelectedSectionId == firstSection && outlineSeed.Value == originalSeed + 9, "Cancel restores section selection and draft");
        outlineSeed.Value = originalSeed; session.Undo(); Invoke("Reload");

        var complexity = Field<Slider>("complexity"); var initialComplexity = complexity.Value;
        complexity.Value = initialComplexity == .9 ? .8 : .9;
        Click("Add");
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Adding a section protects drummer settings");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        Check(complexity.Value == .9 && session.Project.Sections.Length == 1, "Cancel preserves drummer draft and section");
        complexity.Value = initialComplexity;

        var generator = Field<Control>("generators");
        var generatorSeed = (NumericUpDown)generator.GetType().GetField("seed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generator)!;
        var initialSeed = generatorSeed.Value; generatorSeed.Value = (initialSeed ?? 0) + 1;
        var parts = Field<ComboBox>("parts"); var oldPart = parts.SelectedIndex; parts.SelectedIndex = oldPart == 0 ? 1 : 0; Pump();
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Changing part protects generator settings");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        Check(parts.SelectedIndex == oldPart && generatorSeed.Value == initialSeed + 1, "Cancel keeps part and generator draft");
        generatorSeed.Value = initialSeed;
    }
    private static void CheckModalGate()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var create = typeof(MainWindow).GetMethod("AsyncButton", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (Button)create.Invoke(window, ["pending", (Func<Task>)(() => pending.Task), true, true])!;
        var second = (Button)create.Invoke(window, ["other", (Func<Task>)(() => { calls++; return Task.CompletedTask; }), true, true])!;
        first.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!first.IsEnabled, "Timer keeps a pending dialog action disabled");
        second.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(calls == 0, "Shared dialog gate blocks a second file action");
        pending.SetResult(); Until(() => first.IsEnabled);
        second.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(calls == 1, "File actions resume after dialog completes");
    }
    private static void CheckFloatingShortcuts(WorkbenchSession session)
    {
        Field<ComboBox>("sections").Focus();
        SendKey(Key.F5); Until(() => session.Status().Running);
        Check(session.Status().Running, "F5 in docked pane starts once");
        SendKey(Key.F5); Until(() => !session.Status().Running);
        Check(!session.Status().Running, "F5 in docked pane stops once");
        var workspace = Field<Control>("workspace");
        var type = workspace.GetType();
        var factory = (Dock.Model.Core.IFactory)type.GetField("factory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        var host = (Dock.Avalonia.Controls.DockControl)type.GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        static IEnumerable<Dock.Model.Core.IDockable> Walk(Dock.Model.Core.IDockable node)
        {
            yield return node;
            if (node is Dock.Model.Core.IDock dock)
                foreach (var child in dock.VisibleDockables ?? []) foreach (var nested in Walk(child)) yield return nested;
        }
        var outline = Walk(host.Layout!).Single(d => d.Id == "Outline");
        factory.FloatDockable(outline); Pump();
        var sections = Field<ComboBox>("sections");
        var floating = TopLevel.GetTopLevel(sections) as Window;
        Check(floating is not null && floating != window, "Outline actually floats into another window");
        sections.Focus();
        Invoke("StartSong"); Until(() => session.Status().Running);
        floating!.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        floating!.KeyRelease(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        Until(() => !session.Status().Running);
        Check(!session.Status().Running, "F8 in floating pane stops playback");
        Invoke("StartSong"); Until(() => session.Status().Running);
        floating!.KeyPress(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        floating!.KeyRelease(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        Until(() => !session.Status().Running);
        Check(!session.Status().Running, "Panic shortcut in floating pane stops playback");
        Invoke("StartSong"); Until(() => session.Status().Running);
        floating!.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
        floating!.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.None, null); Pump();
        Check(session.Status().Running && Field<TextBlock>("status").Text!.Contains("Stop playback before opening"),
            "First Save refuses a native picker while playback continues");
        SetField("documentDialog", true);
        var confirm = (Task<int>)typeof(MainWindow).GetMethod("ConfirmSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["Test confirmation", "Confirm", null, "Discard"])!;
        Pump(); var dialog = window.OwnedWindows.Single(w => w.Title == "Test confirmation");
        dialog.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        dialog.KeyRelease(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        Until(() => !session.Status().Running);
        Check(!session.Status().Running, "F8 works in an owned confirmation opened from a floating workflow");
        Click(dialog, "Cancel"); Until(() => confirm.IsCompleted); SetField("documentDialog", false);
        type.GetMethod("Reset")!.Invoke(workspace, null); Pump();
        var perform = Walk(host.Layout!).Single(d => d.Id == "Perform"); factory.FloatDockable(perform); Pump();
        var playSection = Field<Button>("playSection"); var performWindow = TopLevel.GetTopLevel(playSection) as Window;
        Check(performWindow is not null && performWindow != window, "Perform pane floats into its own window");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = typeof(MainWindow).GetMethod("AsyncButton", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modal = (Button)create.Invoke(window, ["pending file", (Func<Task>)(() => pending.Task), true, true])!;
        modal.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        playSection.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!session.IsRunning, "A pending file dialog blocks Play section in the floating Perform pane");
        Invoke("StartSong"); Until(() => session.IsRunning); Field<ComboBox>("launchSection").Focus();
        performWindow!.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        performWindow!.KeyRelease(Key.F8, RawInputModifiers.None, PhysicalKey.None, null); Until(() => !session.IsRunning);
        Check(!session.IsRunning, "F8 remains available while a file dialog is pending");
        Invoke("StartSong"); Until(() => session.IsRunning);
        performWindow!.KeyPress(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        performWindow!.KeyRelease(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null); Until(() => !session.IsRunning);
        Check(!session.IsRunning, "Panic remains available while a file dialog is pending");
        pending.SetResult(); Until(() => modal.IsEnabled);
        type.GetMethod("Reset")!.Invoke(workspace, null); Pump();
    }
    private static void CheckArchiveRecovery(WorkbenchSession session)
    {
        var directory = session.TakeDirectory!;
        var held = directory + "-held-for-test";
        var recoveryPath = Path.Combine(evidence, "recovered-take.json");
        Invoke("StartSong"); Until(() => session.IsRunning);
        Directory.Move(directory, held);
        File.WriteAllText(directory, "injected archive failure");
        try
        {
            Click("Stop"); Pump();
            Check(!session.IsRunning && Field<TextBlock>("status").Text!.Contains("Take archive failed"), "Archive failure is visible after stop");
            Check(FindButton(window, "Save take").IsEffectivelyEnabled, "Archive failure leaves Save take enabled");
            SetField("takeSavePicker", (Func<Task<string?>>)(() => Task.FromResult<string?>(recoveryPath)));
            Click("Save take"); Until(() => File.Exists(recoveryPath));
            Check(ProjectStore.LoadTake(recoveryPath).Complete, "Save take action writes recovery JSON outside the failed archive");
            Invoke("StartSong"); Until(() => session.IsRunning);
            Check(session.IsRunning, "Playback restarts after explicit take recovery");
        }
        finally
        {
            typeof(MainWindow).GetField("takeSavePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
            File.Delete(directory); Directory.Move(held, directory);
            if (session.IsRunning) session.Stop();
            Pump();
        }
    }
    private static void CheckRoutingFit()
    {
        var routing = Field<Control>("routing");
        var inputs = routing.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToArray();
        foreach (var control in inputs)
        {
            var point = control.TranslatePoint(default, routing)!.Value;
            Check(point.X >= 0 && point.X + control.Bounds.Width <= routing.Bounds.Width + 1,
                $"Routing {AutomationProperties.GetName(control)} fits pane width");
        }
        foreach (var grid in routing.GetVisualDescendants().OfType<Grid>().Where(g => g.Children.Any(c => c is ComboBox)))
        {
            var children = grid.Children.Where(c => c.IsEffectivelyVisible).ToArray();
            for (var i = 0; i < children.Length; i++)
                for (var j = i + 1; j < children.Length; j++)
                    if (Grid.GetRow(children[i]) == Grid.GetRow(children[j]))
                        Check(!children[i].Bounds.Intersects(children[j].Bounds), "Routing row controls do not overlap");
        }
        var apply = routing.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Apply routing"));
        var applyPosition = apply.TranslatePoint(default, window)!.Value;
        Check(applyPosition.Y + apply.Bounds.Height <= window.Bounds.Height, "Apply routing remains visible at minimum window size");
    }
    private static T Named<T>(Control parent, string name) where T : Control => parent.GetVisualDescendants().OfType<T>()
        .Single(c => AutomationProperties.GetName(c) == name);
    private static void CheckRoutingRefreshDraft()
    {
        var routing = Field<Control>("routing");
        var output = Named<ComboBox>(routing, "Output for Drums");
        output.SelectedItem = output.ItemsSource!.Cast<object>().Single(i => i.ToString() == "Loopback");
        Named<ComboBox>(routing, "Channel for Drums").SelectedIndex = 3;
        Named<CheckBox>(routing, "Clock to Memory").IsChecked = false;
        Named<CheckBox>(routing, "Clock to Loopback").IsChecked = true;
        Named<NumericUpDown>(routing, "Delay for Memory").Value = 17;
        var input = Named<ComboBox>(routing, "Recording input");
        input.SelectedItem = input.ItemsSource!.Cast<object>().Single(i => i.ToString() == "Loopback");
        routing.GetType().GetMethod("Rebuild")!.Invoke(routing, [true]);
        using var frame = window.CaptureRenderedFrame();
        Check(Named<ComboBox>(routing, "Output for Drums").SelectedItem!.ToString() == "Loopback", "Endpoint refresh preserves draft output");
        Check(Named<ComboBox>(routing, "Channel for Drums").SelectedIndex == 3, "Endpoint refresh preserves draft channel");
        Check(Named<CheckBox>(routing, "Clock to Memory").IsChecked == false && Named<CheckBox>(routing, "Clock to Loopback").IsChecked == true,
            "Endpoint refresh preserves draft clock selections");
        Check(Named<NumericUpDown>(routing, "Delay for Memory").Value == 17, "Endpoint refresh preserves draft output delay");
        Check(Named<ComboBox>(routing, "Recording input").SelectedItem!.ToString() == "Loopback", "Endpoint refresh preserves draft input");
        // Add a destination through the normal application edit path, then run the real Reload branch.
        var session = Field<WorkbenchSession>("session");
        var section = session.SelectedSection;
        var newPart = section.Parts[0] with { Id = "routing-refresh-test", Name = "New destination", Destination = "New test destination" };
        var updated = section with { Parts = section.Parts.Add(newPart) };
        var project = session.Project with { Sections = session.Project.Sections.Replace(section, updated) };
        typeof(WorkbenchSession).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, [project]);
        Invoke("Reload"); using var updatedFrame = window.CaptureRenderedFrame();
        Check(Named<ComboBox>(routing, "Output for New test destination").SelectedItem is null,
            "Destination-set change adds an unrouted destination field");
        Check(Named<ComboBox>(routing, "Output for Drums").SelectedItem!.ToString() == "Loopback"
            && Named<ComboBox>(routing, "Channel for Drums").SelectedIndex == 3
            && Named<NumericUpDown>(routing, "Delay for Memory").Value == 17
            && Named<CheckBox>(routing, "Clock to Loopback").IsChecked == true
            && Named<ComboBox>(routing, "Recording input").SelectedItem!.ToString() == "Loopback",
            "Destination-set Reload preserves routing draft fields");
    }
    private static void CheckEditorAndBenchmark()
    {
        var type = typeof(MainWindow).Assembly.GetType("MidiWorkbench.Desktop.NoteEditor")!;
        var editor = (Control)Activator.CreateInstance(type)!;
        editor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        editor.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        var outline = new Outline("Render test", 256, new Meter(), 40, [0, 2, 4, 5, 7, 9, 11], .5, .5, .5, 1);
        var bottom = new MidiNote("bottom", 240, 960, 49, 127);
        var top = new MidiNote("top", 0, 1920, 49, 20);
        var part = new Part("render", "Render", PartRole.Drums, "Drums", 10, new Pattern("render", PatternKind.Fixed, [bottom, top]));
        var show = type.GetMethod("Show")!;
        show.Invoke(editor, [part, outline, true, "render"]);
        var host = new Window { Width = 1000, Height = 200, Content = new ScrollViewer { Content = editor,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } };
        host.Show(); Pump();
        using (var smallFrame = host.CaptureRenderedFrame()) { }
        host.Height = 650; Pump();
        using (var frame = host.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(evidence, "resized-editor.png"), PngBitmapEncoderOptions.Default);
            using (var resizedPixels = frame.Lock())
            {
                var lowerLane = new byte[4]; Marshal.Copy(resizedPixels.Address + 366 * resizedPixels.RowBytes + 200 * 4, lowerLane, 0, 4);
                Check(lowerLane[1] == 38 && Math.Min(lowerLane[0], lowerLane[2]) == 32 && Math.Max(lowerLane[0], lowerLane[2]) == 48,
                    "Stopped editor repaints newly exposed lower lanes after viewport resize");
            }
            frame!.Save(Path.Combine(evidence, "overlap-stacking.png"), PngBitmapEncoderOptions.Default);
            using var pixels = frame.Lock();
            var pixel = new byte[4]; Marshal.Copy(pixels.Address + 35 * pixels.RowBytes + 132 * 4, pixel, 0, 4);
            // The framebuffer can use RGBA or BGRA; either encodes the teal note fill here.
            Check(pixel[1] == 174 && Math.Min(pixel[0], pixel[2]) == 75 && Math.Max(pixel[0], pixel[2]) == 188,
                "Overlapping notes paint in authored stacking order");
        }
        var hit = editor.TranslatePoint(new Point(160, 35), host)!.Value;
        host.MouseDown(hit, MouseButton.Left, RawInputModifiers.None); host.MouseUp(hit, MouseButton.Left, RawInputModifiers.None);
        Check((string?)type.GetProperty("SelectedId")!.GetValue(editor) == "top", "Pointer selects the visually topmost overlapping note");
        var dense = Enumerable.Range(0, 100000).Select(i => new MidiNote("dense-" + i, i * 9L, 7, i % 128, 80)).ToImmutableArray();
        part = part with { Pattern = part.Pattern with { Notes = dense } };
        var indexWatch = Stopwatch.StartNew(); show.Invoke(editor, [part, outline, false, "dense"]); indexWatch.Stop();
        Pump();
        for (var i = 0; i < 3; i++) { editor.InvalidateVisual(); using var frame = host.CaptureRenderedFrame(); }
        var times = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            editor.InvalidateVisual(); var watch = Stopwatch.StartNew();
            using var frame = host.CaptureRenderedFrame(); watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
            if (i == 0) frame!.Save(Path.Combine(evidence, "dense-100000.png"), PngBitmapEncoderOptions.Default);
        }
        times.Sort();
        var measurement = $"100,000 notes; index creation {indexWatch.Elapsed.TotalMilliseconds:F2} ms; 30 invalidated viewport frames median {times[15]:F2} ms, max {times[^1]:F2} ms; 1000x650 offscreen Skia, scale {host.RenderScaling}.";
        Results.Add("MEASURE " + measurement); File.WriteAllText(Path.Combine(evidence, "editor-benchmark.txt"), measurement);
        Check(times.All(double.IsFinite), "Dense 100,000-note viewport renders successfully");
        host.Close(); Pump();
    }
    private static void Capture(string name, double width, double height)
    {
        window.Width = width; window.Height = height; Pump();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null) throw new InvalidOperationException("No rendered frame");
        frame.Save(Path.Combine(evidence, name + ".png"), PngBitmapEncoderOptions.Default);
        File.WriteAllLines(Path.Combine(evidence, name + "-layout.txt"), window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && (c is Button || AutomationProperties.GetName(c) is { Length: > 0 }))
            .Select(c => $"{c.GetType().Name} {AutomationProperties.GetName(c)} {(c as Button)?.Content} local={c.Bounds} window={c.TranslatePoint(default, window)}"));
    }
}



