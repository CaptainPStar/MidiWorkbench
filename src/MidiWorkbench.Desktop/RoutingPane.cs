using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed record Choice(string? Id, string Label)
{
    public override string ToString() => Label;
}

// Edits the machine-local rig. Endpoints missing from the system stay selectable and are marked offline.
internal sealed class RoutingPane : ScrollViewer
{
    private readonly WorkbenchSession session;
    private readonly Action<Exception?> report;
    private readonly StackPanel body = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly List<(string Destination, ComboBox Endpoint, ComboBox Channel)> routes = new();
    private readonly List<(string Id, CheckBox Clock, NumericUpDown Offset)> endpoints = new();
    private ComboBox input = new();

    public RoutingPane(WorkbenchSession session, Action<Exception?> report)
    {
        this.session = session; this.report = report;
        Content = body;
        Rebuild();
    }

    public void Rebuild()
    {
        body.Children.Clear(); routes.Clear(); endpoints.Clear();
        var rig = session.Rig;
        var available = session.Endpoints();
        string Label(string id) => available.FirstOrDefault(e => e.Id == id)?.Name ?? $"{id} (offline)";
        var outputs = available.Where(e => e.Output).Select(e => e.Id).Union(rig.Endpoints.Select(e => e.Id)).Select(id => new Choice(id, Label(id))).ToArray();
        var channelChoices = new[] { new Choice(null, "Part") }.Concat(Enumerable.Range(1, 16).Select(c => new Choice(c.ToString(), c.ToString()))).ToArray();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        AddRow(grid, 0, new TextBlock { Text = "Destination" }, new TextBlock { Text = "Output" }, new TextBlock { Text = "Channel" });
        var row = 1;
        foreach (var destination in WorkbenchSession.Destinations(session.Project))
        {
            var route = rig.Routes.FirstOrDefault(r => r.Destination == destination);
            var endpoint = new ComboBox { ItemsSource = outputs, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = outputs.FirstOrDefault(o => o.Id == route?.EndpointId) };
            var channel = new ComboBox { ItemsSource = channelChoices, SelectedItem = channelChoices.First(c => c.Id == route?.Channel?.ToString()) };
            ToolTip.SetTip(channel, "Part uses the channel stored in the song");
            AddRow(grid, row++, new TextBlock { Text = destination, VerticalAlignment = VerticalAlignment.Center }, endpoint, channel);
            routes.Add((destination, endpoint, channel));
        }
        body.Children.Add(grid);

        var used = rig.Endpoints.ToArray();
        if (used.Length > 0)
        {
            var settings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(0, 12, 0, 0) };
            AddRow(settings, 0, new TextBlock { Text = "Output" }, new TextBlock { Text = "Clock" }, new TextBlock { Text = "Delay ms" });
            row = 1;
            foreach (var e in used)
            {
                var clock = new CheckBox { IsChecked = e.SendClock };
                var offset = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, Value = (decimal)e.OffsetMilliseconds, Width = 110 };
                ToolTip.SetTip(offset, "Delays everything sent to this output");
                AddRow(settings, row++, new TextBlock { Text = Label(e.Id), VerticalAlignment = VerticalAlignment.Center }, clock, offset);
                endpoints.Add((e.Id, clock, offset));
            }
            body.Children.Add(settings);
        }

        var inputs = new[] { new Choice(null, "None") }.Concat(available.Where(e => e.Input).Select(e => e.Id)
            .Union(rig.Input is null ? [] : [rig.Input]).Select(id => new Choice(id, Label(id)))).ToArray();
        input = new ComboBox { ItemsSource = inputs, MinWidth = 220, SelectedItem = inputs.First(i => i.Id == rig.Input) };
        ToolTip.SetTip(input, "Keyboard for recording and thru");
        body.Children.Add(new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0), Children = { new TextBlock { Text = "Input" }, input } });

        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => Apply();
        body.Children.Add(apply);
    }

    private void Apply()
    {
        try
        {
            var routeList = routes.Where(r => r.Endpoint.SelectedItem is Choice { Id: not null })
                .Select(r => new DeviceRoute(r.Destination, ((Choice)r.Endpoint.SelectedItem!).Id!,
                    r.Channel.SelectedItem is Choice { Id: { } c } ? int.Parse(c) : null)).ToImmutableArray();
            var endpointList = routeList.Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).Select(id =>
            {
                var existing = endpoints.FirstOrDefault(e => e.Id == id);
                return existing.Id is null ? new RigEndpoint(id)
                    : new RigEndpoint(id, existing.Clock.IsChecked == true, (double)(existing.Offset.Value ?? 0));
            }).ToImmutableArray();
            session.SetRig(new Rig(Rig.CurrentSchemaVersion, endpointList, routeList) { Input = (input.SelectedItem as Choice)?.Id });
            Rebuild();
            report(null);
        }
        catch (Exception ex) { report(ex); }
    }

    private static void AddRow(Grid grid, int row, params Control[] cells)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var column = 0; column < cells.Length; column++)
        {
            Grid.SetRow(cells[column], row); Grid.SetColumn(cells[column], column); grid.Children.Add(cells[column]);
        }
    }
}
