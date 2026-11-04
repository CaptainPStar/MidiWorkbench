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
    private static void CheckOutlineDraftProtection(WorkbenchSession session)
    {
        var name = Field<TextBox>("sectionName"); var originalName = name.Text;
        name.Text = "Pending section name";
        Field<NumericUpDown>("tempo").Value = 121; Click("Set");
        Check(name.Text == "Pending section name", "Tempo reload preserves unapplied section settings");
        SendKey(Key.Z, KeyModifiers.Control);
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Undo protects unapplied section settings");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        session.Undo(); Invoke("Reload");
        SendKey(Key.F8);
        Check(name.Text == "Pending section name", "Stop and ordinary reload preserve unapplied section settings");
        Check(!window.Title!.StartsWith('*'), "Undo tempo returns accepted project to clean state");
        window.Close(); Pump();
        var dialog = window.OwnedWindows.Single();
        Check(dialog.Title == "Unaccepted changes", "Clean accepted project protects unapplied outline on close");
        using (var frame = dialog.CaptureRenderedFrame())
            frame!.Save(Path.Combine(evidence, "unaccepted-outline-dialog.png"), PngBitmapEncoderOptions.Default);
        Check(dialog.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible) == 2,
            "Unaccepted-change decision offers discard and cancel");
        Click(dialog, "Cancel"); Pump();
        Check(window.IsVisible && name.Text == "Pending section name", "Cancel close preserves unapplied outline");
        name.Text = originalName;
        Check(ReferenceEquals(Field<SongProject>("savedProject"), session.Project), "Draft protection does not mutate accepted project");

        var firstSection = session.SelectedSectionId;
        session.AddSection(); session.SelectSection(firstSection); Invoke("Reload");
        name.Text = "Section switch draft";
        Field<ComboBox>("sections").SelectedIndex = 1; Pump();
        Check(window.OwnedWindows.Single().Title == "Unaccepted changes", "Changing section protects outline draft");
        Click(window.OwnedWindows.Single(), "Cancel"); Pump();
        Check(session.SelectedSectionId == firstSection && name.Text == "Section switch draft", "Cancel restores section selection and draft");
        name.Text = originalName; session.Undo(); Invoke("Reload");

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



