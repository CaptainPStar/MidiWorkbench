using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed class GeneratorPane : StackPanel
{
    private sealed record StepLengthOption(string Label, long? Ticks)
    {
        public override string ToString() => Label;
    }
    private static readonly StepLengthOption[] StepLengths =
    [
        new("1/1", 3840), new("1/2", 1920), new("1/4", 960), new("1/8", 480), new("1/16", 240), new("1/32", 120), new("1/64", 60),
        new("1/4 triplet", 640), new("1/8 triplet", 320), new("1/16 triplet", 160),
        new("1/4 dotted", 1440), new("1/8 dotted", 720), new("1/16 dotted", 360), new("Custom", null)
    ];
    private readonly ComboBox kind = new() { ItemsSource = Enum.GetValues<GeneratorKind>(), SelectedItem = GeneratorKind.StepSequencer };
    private readonly ComboBox stepLength = new() { ItemsSource = StepLengths, SelectedItem = StepLengths[4], MinWidth = 140 };
    private readonly NumericUpDown stepBeats = new()
        { Minimum = 8m / MusicalTime.Ppq, Maximum = 128, Value = .25m, Increment = .0625m, FormatString = "0.########", MinWidth = 140 };
    private readonly NumericUpDown steps = N(1, 128, 16), pulses = N(0, 128, 5), rotation = N(0, 127, 0),
        velocity = N(1, 127, 96), spread = N(0, 126, 8), ratchet = N(1, 8, 1), octaves = N(1, 5, 1),
        minPitch = N(0, 127, 36), maxPitch = N(0, 127, 96), seed = N(0, uint.MaxValue, 1), walk = N(1, 24, 2);
    private readonly Slider gate = A(.8, .01, 1), probability = A(1, 0, 1), swing = A(0, 0, .45);
    private readonly TextBox degrees = new() { Text = "0,2,4,2" }, chord = new() { Text = "0,2,4" }, progression = new() { Text = "0" },
        enabled = new() { Text = "" }, groups = new() { Text = "" };
    private readonly ComboBox direction = new() { ItemsSource = Enum.GetValues<ArpeggioDirection>(), SelectedItem = ArpeggioDirection.Up };
    private readonly CheckBox source = new() { Content = "Use part notes" };
    private readonly Button preview = new() { Content = "Preview part" };
    private readonly TextBlock densityContext = new() { MaxWidth = 260, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Dictionary<Control, Control> fields = new();
    private string? loadedDraft;
    private GeneratorSettings loadedSettings = new();
    private decimal loadedSeed = 1;
    private bool loading;
    public Func<GeneratorSettings, Task>? Prepare { get; set; }
    public bool IsPreparing { set => preview.IsEnabled = !value; }
    public event Action<Exception>? Error;
    public GeneratorPane()
    {
        Spacing = 8;
        Add("Generator", kind); Children.Add(preview); Children.Add(densityContext);
        ToolTip.SetTip(densityContext, "Uses the accepted section density. Preview section and Accept apply outline edits. Step masks, local probability and per-step probabilities also affect results.");
        AutomationProperties.SetName(densityContext, "Accepted section density");
        Add("Seed", seed); Add("Steps", steps); Add("Step length", stepLength); Add("Quarter-note beats", stepBeats);
        ToolTip.SetTip(stepLength, "Note value per step. Custom uses quarter-note beats.");
        ToolTip.SetTip(stepBeats, "1 beat = quarter note; 0.5 = eighth note. Rounded to the nearest MIDI tick.");
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
        stepLength.SelectionChanged += (_, _) =>
        {
            if (!loading && stepLength.SelectedItem is StepLengthOption { Ticks: { } ticks }) stepBeats.Value = ticks / (decimal)MusicalTime.Ppq;
            UpdateFields();
        };
        steps.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.ValueProperty) return;
            var count = Math.Clamp(steps.Value ?? 16, 1, 128);
            pulses.Maximum = count; rotation.Maximum = count - 1;
            UpdateFields();
        };
    }
    public bool UseSource => kind.SelectedItem is GeneratorKind.Arpeggiator && source.IsChecked == true;
    public void SetSectionDensity(double density) => densityContext.Text = $"Section density: {density:P0} · affects step chance";
    private long ReadStepTicks() => stepLength.SelectedItem is StepLengthOption { Ticks: { } ticks } ? ticks
        : (long)decimal.Round((stepBeats.Value ?? .25m) * MusicalTime.Ppq, 0, MidpointRounding.AwayFromZero);
    private string Draft() => System.Text.Json.JsonSerializer.Serialize(new object?[] { kind.SelectedItem, seed.Value, steps.Value, ReadStepTicks(),
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
        if (k == GeneratorKind.Euclidean)
        {
            var count = Math.Clamp(steps.Value ?? 16, 1, 128);
            pulses.Value = Math.Clamp(pulses.Value ?? 5, 0, count);
            rotation.Value = Math.Clamp(rotation.Value ?? 0, 0, count - 1);
        }
        void Show(Control control, bool visible) => fields[control].IsVisible = visible;
        Show(degrees, k is GeneratorKind.StepSequencer or GeneratorKind.Euclidean);
        Show(pulses, k == GeneratorKind.Euclidean); Show(rotation, k == GeneratorKind.Euclidean);
        Show(walk, k == GeneratorKind.RandomWalk);
        Show(direction, k == GeneratorKind.Arpeggiator); Show(octaves, k == GeneratorKind.Arpeggiator);
        source.IsVisible = k == GeneratorKind.Arpeggiator;
        Show(chord, k is GeneratorKind.Arpeggiator or GeneratorKind.Chord or GeneratorKind.Pad);
        Show(progression, k is GeneratorKind.Chord or GeneratorKind.Pad);
        Show(stepLength, k != GeneratorKind.Pad);
        Show(stepBeats, k != GeneratorKind.Pad && stepLength.SelectedItem is StepLengthOption { Ticks: null });
        Show(swing, k != GeneratorKind.Pad);
    }
    private static ImmutableArray<int> Parse(string? text) => (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToImmutableArray();
    private GeneratorSettings Read()
    {
        var defaults = new GeneratorSettings();
        var selectedKind = (GeneratorKind)(kind.SelectedItem ?? GeneratorKind.StepSequencer);
        var stepCount = (int)(steps.Value ?? 16);
        var pulseCount = (int)(pulses.Value ?? 5); var rotationCount = (int)(rotation.Value ?? 0);
        if (selectedKind == GeneratorKind.Euclidean)
        {
            pulseCount = Math.Clamp(pulseCount, 0, stepCount);
            rotationCount = Math.Clamp(rotationCount, 0, stepCount - 1);
        }
        var enabledSteps = Parse(enabled.Text).Select(v => v switch { 0 => false, 1 => true, _ => throw new ArgumentException("Enabled steps use 1 or 0.") }).ToImmutableArray();
        var settings = loadedSettings with
        {
            Kind = selectedKind, Seed = seed.Value == loadedSeed ? loadedSettings.Seed : (uint)(seed.Value ?? 1),
            Steps = stepCount, StepTicks = ReadStepTicks(),
            StepVelocities = loadedSettings.StepVelocities.Length > stepCount ? loadedSettings.StepVelocities.Slice(0, stepCount) : loadedSettings.StepVelocities,
            StepProbabilities = loadedSettings.StepProbabilities.Length > stepCount ? loadedSettings.StepProbabilities.Slice(0, stepCount) : loadedSettings.StepProbabilities,
            StepRatchets = loadedSettings.StepRatchets.Length > stepCount ? loadedSettings.StepRatchets.Slice(0, stepCount) : loadedSettings.StepRatchets,
            StepDegrees = fields[degrees].IsVisible ? Parse(degrees.Text)
                : selectedKind == loadedSettings.Kind ? loadedSettings.StepDegrees : defaults.StepDegrees,
            EnabledSteps = enabledSteps.Length > stepCount ? enabledSteps.Slice(0, stepCount) : enabledSteps,
            Gate = gate.Value, Velocity = (int)(velocity.Value ?? 96), VelocitySpread = (int)(spread.Value ?? 8), Probability = probability.Value,
            Ratchet = (int)(ratchet.Value ?? 1), Swing = swing.Value, MinPitch = (int)(minPitch.Value ?? 36), MaxPitch = (int)(maxPitch.Value ?? 96),
            Pulses = !fields[pulses].IsVisible && selectedKind == loadedSettings.Kind ? loadedSettings.Pulses : pulseCount,
            Rotation = !fields[rotation].IsVisible && selectedKind == loadedSettings.Kind ? loadedSettings.Rotation : rotationCount,
            WalkStep = (int)(walk.Value ?? 2),
            Direction = (ArpeggioDirection)(direction.SelectedItem ?? ArpeggioDirection.Up), Octaves = (int)(octaves.Value ?? 1),
            ChordDegrees = fields[chord].IsVisible ? Parse(chord.Text)
                : selectedKind == loadedSettings.Kind ? loadedSettings.ChordDegrees : defaults.ChordDegrees,
            Progression = fields[progression].IsVisible ? Parse(progression.Text)
                : selectedKind == loadedSettings.Kind ? loadedSettings.Progression : defaults.Progression,
            Grouping = Parse(groups.Text),
            SourceNotes = UseSource ? loadedSettings.SourceNotes : [], SourceLengthTicks = UseSource ? loadedSettings.SourceLengthTicks : null
        };
        settings.Validate(); return settings;
    }
    public void Load(GeneratorSettings? settings, uint sectionSeed)
    {
        var s = settings ?? new();
        loading = true;
        loadedSettings = s;
        kind.SelectedItem = s.Kind; seed.Value = s.Seed ?? sectionSeed; loadedSeed = seed.Value ?? sectionSeed; steps.Value = s.Steps;
        stepBeats.Value = s.StepTicks / (decimal)MusicalTime.Ppq;
        stepLength.SelectedItem = StepLengths.FirstOrDefault(option => option.Ticks == s.StepTicks) ?? StepLengths[^1];
        degrees.Text = string.Join(',', s.StepDegrees); enabled.Text = string.Join(',', s.EnabledSteps.Select(v => v ? 1 : 0));
        gate.Value = s.Gate; velocity.Value = s.Velocity; spread.Value = s.VelocitySpread; probability.Value = s.Probability;
        ratchet.Value = s.Ratchet; swing.Value = s.Swing; minPitch.Value = s.MinPitch; maxPitch.Value = s.MaxPitch;
        pulses.Value = s.Pulses; rotation.Value = s.Rotation; walk.Value = s.WalkStep; direction.SelectedItem = s.Direction;
        octaves.Value = s.Octaves; chord.Text = string.Join(',', s.ChordDegrees); progression.Text = string.Join(',', s.Progression);
        groups.Text = string.Join(',', s.Grouping); source.IsChecked = !s.SourceNotes.IsEmpty;
        loading = false; UpdateFields();
        loadedDraft = Draft();
    }
}
