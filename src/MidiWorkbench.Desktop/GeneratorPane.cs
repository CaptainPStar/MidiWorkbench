using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed class GeneratorPane : StackPanel
{
    private readonly ComboBox kind = new() { ItemsSource = Enum.GetValues<GeneratorKind>(), SelectedItem = GeneratorKind.StepSequencer };
    private readonly NumericUpDown steps = N(1, 128, 16), stepTicks = N(8, 122880, 240), pulses = N(0, 128, 5), rotation = N(0, 127, 0),
        velocity = N(1, 127, 96), spread = N(0, 126, 8), ratchet = N(1, 8, 1), octaves = N(1, 5, 1),
        minPitch = N(0, 127, 36), maxPitch = N(0, 127, 96), seed = N(0, uint.MaxValue, 1), walk = N(1, 24, 2);
    private readonly Slider gate = A(.8, .01, 1), probability = A(1, 0, 1), swing = A(0, 0, .45);
    private readonly TextBox degrees = new() { Text = "0,2,4,2" }, chord = new() { Text = "0,2,4" }, progression = new() { Text = "0" },
        enabled = new() { Text = "" }, groups = new() { Text = "" };
    private readonly ComboBox direction = new() { ItemsSource = Enum.GetValues<ArpeggioDirection>(), SelectedItem = ArpeggioDirection.Up };
    private readonly CheckBox source = new() { Content = "Use part notes" };
    private readonly Button preview = new() { Content = "Preview part" };
    private readonly Dictionary<Control, Control> fields = new();
    private string? loadedDraft;
    public Func<GeneratorSettings, Task>? Prepare { get; set; }
    public bool IsPreparing { set => preview.IsEnabled = !value; }
    public event Action<Exception>? Error;
    public GeneratorPane()
    {
        Spacing = 8;
        Add("Generator", kind); Children.Add(preview);
        Add("Seed", seed); Add("Steps", steps); Add("Step ticks", stepTicks);
        Add("Degrees", degrees); Add("Enabled steps", enabled);
        ToolTip.SetTip(degrees, "Scale degrees separated by commas; repeated over the sequence");
        ToolTip.SetTip(enabled, "Optional 1,0 sequence. Empty enables all steps.");
        Add("Gate", gate); Add("Velocity", velocity); Add("Velocity spread", spread); Add("Probability", probability);
        Add("Ratchet", ratchet); Add("Swing", swing); Add("Min pitch", minPitch); Add("Max pitch", maxPitch);
        Add("Pulses", pulses); Add("Rotation", rotation); Add("Walk step", walk);
        Add("Direction", direction); Add("Octaves", octaves); Add("Chord degrees", chord); Add("Progression", progression); Add("Beat groups", groups);
        ToolTip.SetTip(groups, "Optional denominator-note groups, for example 2,2,3 in 7/8");
        ToolTip.SetTip(source, "Arpeggiate the selected part's notes; source notes are copied into the recipe");
        Children.Add(source);
        preview.Click += async (_, _) =>
        {
            preview.IsEnabled = false;
            try { if (Prepare is not null) await Prepare(Read()); } catch (Exception ex) { Error?.Invoke(ex); }
            finally { preview.IsEnabled = true; }
        };
        kind.SelectionChanged += (_, _) => UpdateFields(); UpdateFields();
        steps.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.ValueProperty) return;
            pulses.Maximum = steps.Value ?? 16; rotation.Maximum = (steps.Value ?? 16) - 1;
        };
    }
    public bool UseSource => kind.SelectedItem is GeneratorKind.Arpeggiator && source.IsChecked == true;
    private string Draft() => System.Text.Json.JsonSerializer.Serialize(new object?[] { kind.SelectedItem, seed.Value, steps.Value, stepTicks.Value,
        degrees.Text, enabled.Text, gate.Value, velocity.Value, spread.Value, probability.Value, ratchet.Value, swing.Value, minPitch.Value, maxPitch.Value,
        pulses.Value, rotation.Value, walk.Value, direction.SelectedItem, octaves.Value, chord.Text, progression.Text, groups.Text, source.IsChecked });
    public bool HasDraft => loadedDraft is not null && loadedDraft != Draft();
    private static NumericUpDown N(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, MinWidth = 100 };
    private static Slider A(double value, double min, double max) => new() { Minimum = min, Maximum = max, Value = value, MinWidth = 130 };
    private void Add(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        var field = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = label }, control } };
        fields[control] = field; Children.Add(field);
    }
    private void UpdateFields()
    {
        var k = (GeneratorKind)(kind.SelectedItem ?? GeneratorKind.StepSequencer);
        void Show(Control control, bool visible) => fields[control].IsVisible = visible;
        Show(degrees, k is GeneratorKind.StepSequencer or GeneratorKind.Euclidean);
        Show(pulses, k == GeneratorKind.Euclidean); Show(rotation, k == GeneratorKind.Euclidean);
        Show(walk, k == GeneratorKind.RandomWalk);
        Show(direction, k == GeneratorKind.Arpeggiator); Show(octaves, k == GeneratorKind.Arpeggiator);
        source.IsVisible = k == GeneratorKind.Arpeggiator;
        Show(chord, k is GeneratorKind.Arpeggiator or GeneratorKind.Chord or GeneratorKind.Pad);
        Show(progression, k is GeneratorKind.Chord or GeneratorKind.Pad);
        Show(stepTicks, k != GeneratorKind.Pad); Show(swing, k != GeneratorKind.Pad);
    }
    private static ImmutableArray<int> Parse(string? text) => (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToImmutableArray();
    private GeneratorSettings Read()
    {
        var defaults = new GeneratorSettings();
        var settings = new GeneratorSettings((GeneratorKind)(kind.SelectedItem ?? GeneratorKind.StepSequencer))
        {
            Seed = (uint)(seed.Value ?? 1), Steps = (int)(steps.Value ?? 16), StepTicks = (long)(stepTicks.Value ?? 240),
            StepDegrees = fields[degrees].IsVisible ? Parse(degrees.Text) : defaults.StepDegrees,
            EnabledSteps = Parse(enabled.Text).Select(v => v switch { 0 => false, 1 => true, _ => throw new ArgumentException("Enabled steps use 1 or 0.") }).ToImmutableArray(),
            Gate = gate.Value, Velocity = (int)(velocity.Value ?? 96), VelocitySpread = (int)(spread.Value ?? 8), Probability = probability.Value,
            Ratchet = (int)(ratchet.Value ?? 1), Swing = swing.Value, MinPitch = (int)(minPitch.Value ?? 36), MaxPitch = (int)(maxPitch.Value ?? 96),
            Pulses = (int)(pulses.Value ?? 5), Rotation = (int)(rotation.Value ?? 0), WalkStep = (int)(walk.Value ?? 2),
            Direction = (ArpeggioDirection)(direction.SelectedItem ?? ArpeggioDirection.Up), Octaves = (int)(octaves.Value ?? 1),
            ChordDegrees = fields[chord].IsVisible ? Parse(chord.Text) : defaults.ChordDegrees,
            Progression = fields[progression].IsVisible ? Parse(progression.Text) : defaults.Progression, Grouping = Parse(groups.Text)
        };
        settings.Validate(); return settings;
    }
    public void Load(GeneratorSettings? settings, uint sectionSeed)
    {
        var s = settings ?? new();
        kind.SelectedItem = s.Kind; seed.Value = s.Seed ?? sectionSeed; steps.Value = s.Steps; stepTicks.Value = s.StepTicks;
        degrees.Text = string.Join(',', s.StepDegrees); enabled.Text = string.Join(',', s.EnabledSteps.Select(v => v ? 1 : 0));
        gate.Value = s.Gate; velocity.Value = s.Velocity; spread.Value = s.VelocitySpread; probability.Value = s.Probability;
        ratchet.Value = s.Ratchet; swing.Value = s.Swing; minPitch.Value = s.MinPitch; maxPitch.Value = s.MaxPitch;
        pulses.Value = s.Pulses; rotation.Value = s.Rotation; walk.Value = s.WalkStep; direction.SelectedItem = s.Direction;
        octaves.Value = s.Octaves; chord.Text = string.Join(',', s.ChordDegrees); progression.Text = string.Join(',', s.Progression);
        groups.Text = string.Join(',', s.Grouping); source.IsChecked = !s.SourceNotes.IsEmpty;
        loadedDraft = Draft();
    }
}
