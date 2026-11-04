using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

internal sealed class BeatPulse : Control
{
    private Meter meter = new();
    private double tick;
    private bool running;
    public BeatPulse()
    {
        Height = 58; MinWidth = 220;
        AutomationProperties.SetName(this, "Beat pulse");
        ToolTip.SetTip(this, "One pulse per meter beat. Bar starts are accented; compound meters group eighth notes in threes.");
    }
    public void Update(Meter value, double position, bool playing)
    {
        meter = value; tick = Math.Max(0, position); running = playing; InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var unit = MusicalTime.Ppq * 4d / meter.Denominator;
        var active = (int)(tick / unit) % meter.Numerator;
        var phase = tick / unit % 1;
        var cell = Math.Min(54, Bounds.Width / meter.Numerator);
        var accents = new HashSet<int>(); var offset = 0;
        foreach (var group in MeterGrouping.Resolve(meter)) { accents.Add(offset); offset += group; }
        for (var i = 0; i < meter.Numerator; i++)
        {
            var strong = i == 0 || meter.Denominator >= 8 && accents.Contains(i);
            var brightness = running && i == active ? .45 + .55 * (1 - phase) : .15;
            var color = strong ? Color.FromRgb(239, 189, 101) : Color.FromRgb(75, 174, 188);
            var rect = new Rect(i * cell + 2, 4, Math.Max(2, cell - 5), strong ? 40 : 32);
            context.DrawRectangle(new SolidColorBrush(color, brightness), new Pen(new SolidColorBrush(color, .5)), rect, 4, 4);
            if (cell > 20) context.DrawText(new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI, sans-serif"), 13, Brushes.White), new Point(rect.X + 7, 12));
        }
    }
}
