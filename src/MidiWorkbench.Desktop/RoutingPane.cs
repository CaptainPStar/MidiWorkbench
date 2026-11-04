using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed record Choice(string? Id, string Label)
{
    public override string ToString() => Label;
}

// Edits the machine-local rig. Endpoints missing from the system stay selectable and are marked offline.
internal sealed class RoutingPane : Grid
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
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(new ScrollViewer { Content = body });
        var apply = new Button { Content = "Apply routing", Margin = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        apply.Classes.Add("primary"); apply.Click += (_, _) => Apply();
        Grid.SetRow(apply, 1); Children.Add(apply);
        Rebuild();
    }

    public void Rebuild(bool preserveDraft = false)
    {
        var draftRoutes = preserveDraft ? routes.ToDictionary(r => r.Destination,
            r => ((r.Endpoint.SelectedItem as Choice)?.Id, (r.Channel.SelectedItem as Choice)?.Id)) : null;
        var draftEndpoints = preserveDraft ? endpoints.ToDictionary(e => e.Id, e => (e.Clock.IsChecked == true, e.Offset.Value ?? 0)) : null;
        var draftInput = (input.SelectedItem as Choice)?.Id;
        body.Children.Clear(); routes.Clear(); endpoints.Clear();
        var rig = session.Rig;
        var available = session.Endpoints();
        string Label(string id) => available.FirstOrDefault(e => e.Id == id)?.Name ?? $"{id} (offline)";
        var outputs = available.Where(e => e.Output).Select(e => e.Id).Union(rig.Endpoints.Select(e => e.Id))
            .Union(draftRoutes?.Values.Select(v => v.Item1).OfType<string>() ?? []).Union(draftEndpoints?.Keys.AsEnumerable() ?? [])
            .Select(id => new Choice(id, Label(id))).ToArray();
        var channelChoices = new[] { new Choice(null, "Part") }.Concat(Enumerable.Range(1, 16).Select(c => new Choice(c.ToString(), c.ToString()))).ToArray();

        foreach (var destination in WorkbenchSession.Destinations(session.Project))
        {
            var route = rig.Routes.FirstOrDefault(r => r.Destination == destination);
            var selected = draftRoutes is not null && draftRoutes.TryGetValue(destination, out var draftRoute)
                ? draftRoute : (route?.EndpointId, route?.Channel?.ToString());
            var endpoint = new ComboBox { ItemsSource = outputs, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = outputs.FirstOrDefault(o => o.Id == selected.Item1) };
            var channel = new ComboBox { ItemsSource = channelChoices, SelectedItem = channelChoices.First(c => c.Id == selected.Item2) };
            AutomationProperties.SetName(endpoint, $"Output for {destination}");
            AutomationProperties.SetName(channel, $"Channel for {destination}");
            ToolTip.SetTip(channel, "Part uses the channel stored in the song");
            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            fields.Children.Add(endpoint); Grid.SetColumn(channel, 1); fields.Children.Add(channel);
            body.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = $"{destination} · output / channel" }, fields } });
            routes.Add((destination, endpoint, channel));
        }

        if (outputs.Length > 0)
        {
            var settings = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var output in outputs)
            {
                var e = rig.Endpoints.FirstOrDefault(e => e.Id == output.Id);
                var settingsDraft = draftEndpoints is not null && draftEndpoints.TryGetValue(output.Id!, out var draftEndpoint)
                    ? draftEndpoint : (e?.SendClock == true, (decimal)(e?.OffsetMilliseconds ?? 0));
                var clock = new CheckBox { Content = "Clock", IsChecked = settingsDraft.Item1 };
                ToolTip.SetTip(clock, "Send MIDI clock to this output, including outputs with no note routes");
                AutomationProperties.SetName(clock, $"Clock to {output.Label}");
                var offset = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, Value = settingsDraft.Item2, Width = 110 };
                AutomationProperties.SetName(offset, $"Delay for {output.Label}");
                ToolTip.SetTip(offset, "Delays everything sent to this output");
                var fields = new WrapPanel { Children = { clock, new TextBlock { Text = "Delay ms", Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center }, offset } };
                settings.Children.Add(new StackPanel { Spacing = 3, Children = { new TextBlock { Text = output.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, fields } });
                endpoints.Add((output.Id!, clock, offset));
            }
            body.Children.Add(settings);
        }

        var selectedInput = preserveDraft ? draftInput : rig.Input;
        var inputs = new[] { new Choice(null, "None") }.Concat(available.Where(e => e.Input).Select(e => e.Id)
            .Union(selectedInput is null ? [] : [selectedInput]).Select(id => new Choice(id, Label(id)))).ToArray();
        input = new ComboBox { ItemsSource = inputs, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = inputs.First(i => i.Id == selectedInput) };
        AutomationProperties.SetName(input, "Recording input");
        ToolTip.SetTip(input, "Keyboard for recording and thru");
        body.Children.Add(new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0), Children = { new TextBlock { Text = "Input" }, input } });

    }

    private void Apply()
    {
        try
        {
            var routeList = routes.Where(r => r.Endpoint.SelectedItem is Choice { Id: not null })
                .Select(r => new DeviceRoute(r.Destination, ((Choice)r.Endpoint.SelectedItem!).Id!,
                    r.Channel.SelectedItem is Choice { Id: { } c } ? int.Parse(c) : null)).ToImmutableArray();
            var endpointList = routeList.Select(r => r.EndpointId).Concat(endpoints.Where(e => e.Clock.IsChecked == true).Select(e => e.Id)).Distinct(StringComparer.Ordinal).Select(id =>
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

}
