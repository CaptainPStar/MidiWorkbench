using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed class ArrangementPane : StackPanel
{
    private readonly WorkbenchSession session;
    private readonly Action<Action> perform;
    private readonly ListBox order = new() { MinHeight = 120, MaxHeight = 260 };
    private readonly NumericUpDown repeats = new() { Minimum = 1, Maximum = 128, Value = 1, Width = 95 };
    public ArrangementPane(WorkbenchSession session, Action<Action> perform)
    {
        this.session = session; this.perform = perform; Spacing = 8;
        Children.Add(new TextBlock { Text = "Song order" }); Children.Add(order);
        AutomationProperties.SetName(order, "Song order"); AutomationProperties.SetName(repeats, "Repeats");
        order.SelectionChanged += (_, _) => { if (order.SelectedIndex >= 0 && order.SelectedIndex < Entries().Length) repeats.Value = Entries()[order.SelectedIndex].Repeats; };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        Add(buttons, "Append section", () => Change(Entries().Append(new ArrangementEntry(session.SelectedSectionId)).ToArray()));
        Add(buttons, "Remove", () => { var entries = Entries().ToList(); if (order.SelectedIndex < 0) return; entries.RemoveAt(order.SelectedIndex); Change(entries.ToArray()); });
        Add(buttons, "Up", () => Move(-1)); Add(buttons, "Down", () => Move(1));
        Add(buttons, "Section order", () => session.SetArrangement([])); Children.Add(buttons);
        var count = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        count.Children.Add(new TextBlock { Text = "Repeats", VerticalAlignment = VerticalAlignment.Center }); count.Children.Add(repeats);
        Add(count, "Set", () => { var entries = Entries(); if (order.SelectedIndex < 0) return; entries[order.SelectedIndex] = entries[order.SelectedIndex] with { Repeats = (int)(repeats.Value ?? 1) }; Change(entries); });
        Children.Add(count); Refresh();
    }
    private ArrangementEntry[] Entries() => session.Project.Arrangement.IsEmpty ? session.Project.Sections.Select(s => new ArrangementEntry(s.Id)).ToArray() : session.Project.Arrangement.ToArray();
    private void Add(Panel panel, string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) }; b.Click += (_, _) => perform(action); panel.Children.Add(b);
    }
    private void Change(ArrangementEntry[] entries) => session.SetArrangement(entries);
    private void Move(int delta)
    {
        var entries = Entries(); var i = order.SelectedIndex; var next = i + delta;
        if (i < 0 || next < 0 || next >= entries.Length) return;
        (entries[i], entries[next]) = (entries[next], entries[i]); Change(entries); order.SelectedIndex = next;
    }
    public void Refresh()
    {
        var index = order.SelectedIndex; var sections = session.Project.Sections.ToDictionary(s => s.Id);
        order.ItemsSource = Entries().Select((e, i) => $"{i + 1}. {sections[e.SectionId].Outline.Name} ×{e.Repeats}").ToArray();
        order.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, Entries().Length - 1));
    }
}
