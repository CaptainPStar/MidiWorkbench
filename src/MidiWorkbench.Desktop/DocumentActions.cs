using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MidiWorkbench.Domain;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Desktop;

public sealed partial class MainWindow
{
    private SongProject? savedProject;
    private SongBank? savedBank;
    private string? projectPath;
    private bool closePending, documentDialog;
    private readonly TextBlock outputStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private bool ProjectChanged => !ReferenceEquals(savedProject, session.Project);
    private bool BankChanged => session.Bank is not null && !ReferenceEquals(savedBank, session.Bank);

    private void StartSong()
    {
        playback = session.Armed is null ? "song" : "section";
        auditionSection = session.SelectedSection;
        if (session.Armed is null) session.StartArrangement(loop.IsChecked == true);
        else session.StartSection(session.SelectedSectionId, loop.IsChecked == true);
    }

    private void RefreshDocumentTitle() => Title = $"{(ProjectChanged ? "* " : "")}{session.Project.Name}{(projectPath is null ? "" : $" ({Path.GetFileName(projectPath)})")} — MidiWorkbench{(BankChanged ? " · Bank unsaved" : "")}";

    private async Task RenameSong()
    {
        var name = await AskName("Rename song", session.Project.Name, "Rename");
        if (name is null || name == session.Project.Name) return;
        if ((session.Candidate is not null || session.GenerationStatus.Phase == MidiWorkbench.Application.GenerationPhase.Preparing)
            && await ConfirmSave("Unaccepted preview", "Renaming will discard the current preview. Continue?", null, "Discard preview") != 2) return;
        session.RenameProject(name); candidate = null; Reload();
    }

    private async Task RenameSection()
    {
        if (session.IsRunning) throw new InvalidOperationException("Stop playback before renaming a section.");
        var section = session.SelectedSection;
        var name = await AskName("Rename section", section.Outline.Name, "Rename");
        if (name is null || name == section.Outline.Name) return;
        var selectedId = SelectedPart()?.Id;
        var acceptedSettings = section.Parts.FirstOrDefault(p => p.Id == selectedId)?.Pattern.Recipe?.Settings;
        var discardGeneratorDraft = generators.HasDraft && !ReferenceEquals(loadedGeneratorSettings, acceptedSettings);
        var message = discardGeneratorDraft ? "Renaming will discard the preview and its unapplied generator settings. Continue?"
            : "Renaming will discard the current preview. Continue?";
        if ((session.Candidate is not null || session.GenerationStatus.Phase == MidiWorkbench.Application.GenerationPhase.Preparing)
            && await ConfirmSave("Unaccepted preview", message, null, "Discard preview") != 2) return;
        session.RenameSection(section.Id, name); candidate = null;
        // Only the name changed: retain unfinished outline controls against the
        // newly accepted outline. Unchanged generator settings retain their drafts.
        loadedOutline = session.SelectedSection.Outline;
        Reload();
    }

    private async Task NewBank()
    {
        var name = await AskName("New bank", "Bank", "Create");
        if (name is null || !await ConfirmBankReplacement()) return;
        session.NewBank(name); savedBank = null; RefreshBank(); RefreshDocumentTitle();
    }

    private async Task RenameBank()
    {
        if (session.Bank is not { } bank) throw new InvalidOperationException("Create or load a bank first.");
        var name = await AskName("Rename bank", bank.Name, "Rename");
        if (name is null) return;
        session.RenameBank(name); RefreshBank(); RefreshDocumentTitle();
    }

    private async Task<string?> AskName(string title, string initial, string confirmLabel)
    {
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.AddHandler(KeyDownEvent, HandleShortcuts, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var name = new TextBox { Text = initial };
        Avalonia.Automation.AutomationProperties.SetName(name, "Name");
        var error = new TextBlock { Text = "Enter a name.", Foreground = Brushes.Orange, IsVisible = false };
        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        void Accept()
        {
            var value = name.Text?.Trim();
            if (string.IsNullOrEmpty(value)) { error.IsVisible = true; name.Focus(); return; }
            dialog.Close(value);
        }
        confirm.Click += (_, _) => Accept(); cancel.Click += (_, _) => dialog.Close(null);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Accept(); } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm } };
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12,
            Children = { new TextBlock { Text = "Name" }, name, error, buttons } };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private void RefreshOutputStatus()
    {
        var rig = session.Rig;
        var available = session.Endpoints();
        var destinations = session.Project.Sections.SelectMany(s => s.Parts).Select(p => p.Destination).Distinct().ToArray();
        var routes = destinations.Select(d => rig.Routes.FirstOrDefault(r => r.Destination == d)).ToArray();
        var missing = destinations.Where((_, i) => routes[i] is null).ToArray();
        var offline = routes.Where(r => r is not null && !available.Any(e => e.Id == r.EndpointId && e.Output)).Select(r => r!.Destination).ToArray();
        var real = routes.Where(r => r is not null && r.EndpointId is not ("memory" or "loopback")).Select(r => r!.EndpointId).Distinct().ToArray();
        var simulated = routes.Count(r => r?.EndpointId is "memory" or "loopback");
        var clocks = rig.Endpoints.Count(e => e.SendClock && e.Id is not ("memory" or "loopback"));
        outputStatus.Text = missing.Length > 0 ? $"Not routed: {string.Join(", ", missing)}. Choose outputs in Routing."
            : offline.Length > 0 ? $"Output offline: {string.Join(", ", offline)}. Check Routing."
            : real.Length == 0 && clocks > 0 ? $"Notes: simulation · Clock enabled: {clocks} output(s). Choose note outputs in Routing."
            : real.Length == 0 ? "Simulation — no MIDI sent to instruments. Choose outputs in Routing."
            : $"MIDI: {string.Join(", ", real.Select(id => available.FirstOrDefault(e => e.Id == id)?.Name ?? id))} · Clock {(clocks == 0 ? "off" : $"enabled: {clocks} output(s)")}{(simulated > 0 ? $" · {simulated} simulated route(s)" : "")}";
        outputStatus.Foreground = missing.Length > 0 || offline.Length > 0 ? Brushes.Orange : Brushes.LightGray;
        Avalonia.Automation.AutomationProperties.SetName(outputStatus, "MIDI output status");
    }

    private async Task<bool> SaveProject(bool saveAs)
    {
        var path = projectPath;
        if (saveAs || path is null)
        {
            RequireStoppedForPicker();
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Save project", SuggestedFileName = path is null ? "song.midiwork.json" : Path.GetFileName(path),
                FileTypeChoices = [new("MidiWorkbench project") { Patterns = ["*.midiwork.json", "*.json"] }]
            });
            path = file?.TryGetLocalPath();
            if (path is null) return false;
        }
        session.Save(path); savedProject = session.Project; projectPath = path;
        RefreshDocumentTitle();
        status.Text = $"Saved {Path.GetFileName(path)}" + (session.Armed is not null && session.IsRunning ? ". Recording continues; stop and save again to include this phrase."
            : session.Candidate is not null ? ". Preview is not included; accept it to keep it." : "");
        return true;
    }

    private async Task<bool> ConfirmProjectReplacement()
    {
        if (ProjectChanged)
        {
            var choice = await ConfirmSave("Unsaved project", "Save your project changes?", "Save", "Discard changes");
            if (choice != 2 && !(choice == 1 && await SaveProject(false))) return false;
        }
        if (!HasUnacceptedChanges()) return true;
        return await ConfirmSave("Unaccepted changes", "Discard the preview and unapplied section settings?", null, "Discard preview") == 2;
    }

    private bool HasUnacceptedChanges()
    {
        if (session.Candidate is not null || session.GenerationStatus.Phase == MidiWorkbench.Application.GenerationPhase.Preparing) return true;
        if (generators.HasDraft || loadedDrummer is { } drums && drums != CurrentDrummer()) return true;
        if (loadedOutline is null) return false;
        try
        {
            var edited = ReadOutline();
            return edited with { Scale = loadedOutline.Scale } != loadedOutline || !edited.Scale.SequenceEqual(loadedOutline.Scale);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { return true; }
    }

    private async Task GuardDraftChange(Action action, bool generatorOnly = false)
    {
        if (documentDialog || closePending) return;
        documentDialog = true;
        try
        {
            var changed = generatorOnly ? generators.HasDraft : HasUnacceptedChanges();
            var message = generatorOnly ? "Discard unapplied generator settings?" : "Discard the preview and unapplied settings?";
            if (changed && await ConfirmSave("Unaccepted changes", message, null, "Discard changes") != 2) return;
            action();
            if (changed) RestoreDiscardedDrafts(generatorOnly);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { documentDialog = false; }
    }

    private void RestoreDiscardedDrafts(bool generatorOnly)
    {
        if (generatorOnly)
        {
            var settings = SelectedPart()?.Pattern.Recipe?.Settings;
            generators.Load(settings, DisplaySection.Outline.Seed);
            loadedGeneratorSettings = settings;
            return;
        }
        if (candidate is not null || session.Candidate is not null || session.GenerationStatus.Phase == MidiWorkbench.Application.GenerationPhase.Preparing) ClearCandidate();
        if (loadedDrummer is { } baseline)
        {
            var wasLoading = loading; loading = true;
            try
            {
                drummer.SelectedIndex = baseline.Persona; complexity.Value = baseline.Complexity; fills.Value = baseline.Fills;
                swing.Value = baseline.Swing; halfTime.IsChecked = baseline.HalfTime;
            }
            finally { loading = wasLoading; }
        }
        // Reload must repopulate even when an arrangement edit retains the same
        // immutable outline and recipe instances as before the edit.
        loadedOutline = null;
        Reload();
        var acceptedSettings = SelectedPart()?.Pattern.Recipe?.Settings;
        generators.Load(acceptedSettings, DisplaySection.Outline.Seed);
        loadedGeneratorSettings = acceptedSettings;
    }

    private async Task<bool> ConfirmBankReplacement()
    {
        if (!BankChanged) return true;
        var choice = await ConfirmSave("Unsaved bank", "Save your bank and set order?", "Save bank", "Discard changes");
        if (choice == 2) return true;
        if (choice != 1) return false;
        await SaveBank();
        return !BankChanged;
    }

    private async Task<int> ConfirmSave(string title, string message, string? saveLabel, string discardLabel)
    {
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.AddHandler(KeyDownEvent, HandleShortcuts, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = saveLabel };
        var discard = new Button { Content = discardLabel };
        cancel.Click += (_, _) => dialog.Close(0); save.Click += (_, _) => dialog.Close(1); discard.Click += (_, _) => dialog.Close(2);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(discard); actions.Children.Add(cancel); if (saveLabel is not null) actions.Children.Add(save);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, actions } };
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<int>(this);
    }

    private void RequireStoppedForPicker()
    {
        if (session.IsRunning) throw new InvalidOperationException("Stop playback before opening a file dialog.");
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (closePending || documentDialog) return;
        closePending = true;
        try
        {
            session.Stop(); Reload();
            if (!await ConfirmProjectReplacement() || !await ConfirmBankReplacement()) return;
            try { ProjectStore.WriteAtomic(layout, workspace.Save()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine($"Layout not saved: {ex.Message}"); }
            closing = true; Close();
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { closePending = false; }
    }

    private async void HandleShortcuts(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F8 || ctrl && shift && e.Key == Key.Space)
        {
            e.Handled = true; Try(() => { if (e.Key == Key.F8) session.Stop(); else session.Panic(); Reload(); RefreshTakes(); }); return;
        }
        if (documentDialog || closePending) return;
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; Try(() => { if (session.IsRunning) session.Stop(); else StartSong(); Reload(); }); return; }
        if (!ctrl) return;
        if (e.Key == Key.S || e.Key == Key.O && !session.IsRunning)
        {
            e.Handled = true; documentDialog = true;
            try { if (e.Key == Key.S) await SaveProject(shift); else await Open(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { documentDialog = false; }
            return;
        }
        if (e.Key == Key.Z && e.Source is not TextBox && e.Source is not NumericUpDown)
        { e.Handled = true; await GuardDraftChange(() => { if (shift) session.Redo(); else session.Undo(); ClearCandidate(); Reload(); }); }
    }
}
