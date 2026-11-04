using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Desktop;

// Gestures publish one edit on release, so a drag is one undo step.
internal sealed class NoteEditor : Control
{
    internal const double Header = 26, Lane = 22, Labels = 106;
    private static readonly IBrush BackgroundBrush = Brush("#171B22"), Alternate = Brush("#202630"),
        NoteBrush = Brush("#4BAEBC"), SelectedBrush = Brush("#EFBD65"), Ink = Brush("#C9D1DC");
    private static readonly Pen GridPen = new(Brush("#303845")), BeatPen = new(Brush("#4E5969")), BarPen = new(Brush("#788698"));
    private readonly Typeface font = new("Inter, Segoe UI, sans-serif");
    private Part? part;
    private Outline? outline;
    private int[] lanes = [];
    private MidiNote? original, draft;
    private Point origin;
    private bool resizing, dragging;
    private long snap = MusicalTime.Ppq / 4;
    private double pixelsPerQuarter = 96;
    private double playhead = -1;
    public string? SelectedId { get; private set; }
    public bool DrumMode { get; private set; }
    public bool ReadOnly { get; set; }
    public int NewVelocity { get; set; } = 90;
    public long NewDuration { get; set; } = 240;
    public long SnapTicks { get => snap; set { snap = Math.Max(1, value); InvalidateVisual(); } }
    public double Zoom { get => pixelsPerQuarter; set { pixelsPerQuarter = Math.Clamp(value, 24, 240); Resize(); } }
    public double Playhead { get => playhead; set { if (Math.Abs(playhead - value) > .01) { playhead = value; InvalidateVisual(); } } }
    public event Action<MidiNote?>? SelectionChanged;
    public event Action<MidiNote>? NoteEdited;
    public event Action<string>? NoteDeleted;

    public NoteEditor()
    {
        Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "Note editor");
        ToolTip.SetTip(this, "Click empty space to add. Drag a note to move; drag its right edge to resize. Right-click or Delete removes. Arrows move; Shift+Left/Right resizes; L toggles lock; +/- changes velocity.");
    }
    public void Show(Part? value, Outline? section, bool drums)
    {
        var changed = part?.Id != value?.Id || DrumMode != drums;
        part = value; outline = section; DrumMode = drums;
        lanes = drums ? new[] { 49, 51, 46, 42, 44, 39, 38, 40, 50, 48, 47, 45, 43, 41, 36, 35 }
            .Concat(value?.Pattern.Notes.Select(n => n.Pitch).Distinct().Except(new[] { 49, 51, 46, 42, 44, 39, 38, 40, 50, 48, 47, 45, 43, 41, 36, 35 }) ?? []).ToArray()
            : Enumerable.Range(0, 128).Reverse().ToArray();
        if (changed) SelectedId = null;
        if (SelectedId is not null && value?.Pattern.Notes.Any(n => n.Id == SelectedId) != true) SelectedId = null;
        Resize();
    }
    public void Select(string? id)
    {
        SelectedId = id; SelectionChanged?.Invoke(part?.Pattern.Notes.FirstOrDefault(n => n.Id == id)); InvalidateVisual();
    }
    public double SuggestedOffset => DrumMode ? 0 : Math.Max(0, Header + (127 - (part?.Pattern.Notes.Select(n => n.Pitch).DefaultIfEmpty(60).Max() ?? 60) - 2) * Lane);
    private Rect Viewport => this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } s
        ? new Rect(s.Offset.X, s.Offset.Y, s.Viewport.Width, s.Viewport.Height) : new Rect(Bounds.Size);
    private void Resize()
    {
        Width = Math.Max(650, Labels + (outline?.LengthTicks ?? MusicalTime.Ppq * 16) * pixelsPerQuarter / MusicalTime.Ppq + 24);
        Height = Header + lanes.Length * Lane; InvalidateVisual();
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private void Text(DrawingContext context, string value, double x, double y, IBrush? brush = null) =>
        context.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, brush ?? Ink), new Point(x, y));
    internal Rect NoteRect(MidiNote n) => new(Labels + n.Tick * pixelsPerQuarter / MusicalTime.Ppq,
        Header + Array.IndexOf(lanes, n.Pitch) * Lane + 2, Math.Max(5, n.Duration * pixelsPerQuarter / MusicalTime.Ppq), Lane - 4);
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(BackgroundBrush, Bounds.WithX(0).WithY(0));
        if (part is null || outline is null) return;
        // Cull to the scroll viewport rather than walking the entire song at every pulse.
        var viewport = Viewport;
        var firstRow = Math.Max(0, (int)((viewport.Top - Header) / Lane));
        var lastRow = Math.Min(lanes.Length, (int)((viewport.Bottom - Header) / Lane) + 2);
        for (var i = firstRow; i < lastRow; i++)
        {
            var p = lanes[i]; var y = Header + i * Lane;
            if (DrumMode ? i % 2 == 1 : p % 12 is 1 or 3 or 6 or 8 or 10) context.FillRectangle(Alternate, new Rect(0, y, Width, Lane));
            context.DrawLine(GridPen, new Point(0, y + Lane), new Point(Width, y + Lane));
        }
        var unit = MusicalTime.Ppq * 4L / outline.Meter.Denominator;
        var lineStep = Math.Max(snap, (long)Math.Ceiling(6 * MusicalTime.Ppq / pixelsPerQuarter / snap) * snap);
        var start = Math.Max(0, (long)((viewport.Left - Labels) * MusicalTime.Ppq / pixelsPerQuarter) / lineStep * lineStep);
        var end = Math.Min(outline.LengthTicks, (long)((viewport.Right - Labels) * MusicalTime.Ppq / pixelsPerQuarter) + lineStep);
        for (var t = start; t <= end; t += lineStep)
        {
            var x = Labels + t * pixelsPerQuarter / MusicalTime.Ppq;
            context.DrawLine(t % outline.Meter.BarTicks == 0 ? BarPen : t % unit == 0 ? BeatPen : GridPen, new Point(x, Header), new Point(x, Height));
        }
        // Draw meter beats independently: a triplet snap must not hide bar lines.
        for (var t = Math.Max(0, start / unit * unit); t <= end; t += unit)
        {
            var x = Labels + t * pixelsPerQuarter / MusicalTime.Ppq;
            context.DrawLine(t % outline.Meter.BarTicks == 0 ? BarPen : BeatPen, new Point(x, Header), new Point(x, Height));
        }
        foreach (var stored in part.Pattern.Notes)
        {
            var n = draft?.Id == stored.Id ? draft : stored;
            var rect = NoteRect(n);
            if (!rect.Intersects(viewport)) continue;
            context.FillRectangle(n.Id == SelectedId ? SelectedBrush : NoteBrush, rect, 2);
            var velocityHeight = (Lane - 7) * n.Velocity / 127d;
            context.FillRectangle(Ink, new Rect(rect.X + 2, rect.Bottom - velocityHeight - 1, 2, velocityHeight));
            if (n.Locked) context.DrawRectangle(null, new Pen(Brushes.White, 1), rect.Deflate(2));
            if (n.Id == SelectedId && rect.Width > 9) context.DrawLine(new Pen(BackgroundBrush, 2), new Point(rect.Right - 3, rect.Y + 3), new Point(rect.Right - 3, rect.Bottom - 3));
        }
        if (playhead >= 0 && playhead <= outline.LengthTicks)
        {
            var x = Labels + playhead * pixelsPerQuarter / MusicalTime.Ppq;
            context.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, 0), new Point(x, Height));
        }
        // Keep the ruler and pitch labels readable while either axis scrolls.
        context.FillRectangle(BackgroundBrush, new Rect(viewport.Left, viewport.Top, Labels, viewport.Height));
        for (var i = firstRow; i < lastRow; i++)
            Text(context, DrumMode ? DrumLabel(lanes[i]) : PitchLabel(lanes[i]), viewport.Left + 6, Header + i * Lane + 3);
        context.FillRectangle(BackgroundBrush, new Rect(viewport.Left, viewport.Top, viewport.Width, Header));
        for (var t = Math.Max(0, start / outline.Meter.BarTicks * outline.Meter.BarTicks); t <= end; t += outline.Meter.BarTicks)
        {
            var x = Labels + t * pixelsPerQuarter / MusicalTime.Ppq;
            if (x >= viewport.Left + Labels - 1) Text(context, (t / outline.Meter.BarTicks + 1).ToString(CultureInfo.InvariantCulture), x + 4, viewport.Top + 5);
        }
    }
    private long Snap(double ticks) => Math.Max(0, (long)Math.Round(ticks / snap, MidpointRounding.AwayFromZero) * snap);
    private int PitchAt(double y) => lanes[Math.Clamp((int)((y - Header) / Lane), 0, lanes.Length - 1)];
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (part is null || outline is null || lanes.Length == 0) return;
        Focus(); var point = e.GetCurrentPoint(this); var pos = point.Position;
        var viewport = Viewport;
        if (pos.X < viewport.Left + Labels || pos.Y < viewport.Top + Header) return;
        var hit = part.Pattern.Notes.Reverse().FirstOrDefault(n => NoteRect(n).Contains(pos));
        if (point.Properties.IsRightButtonPressed)
        {
            if (!ReadOnly && hit is not null) { Select(hit.Id); NoteDeleted?.Invoke(hit.Id); }
            e.Handled = true; return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        Select(hit?.Id);
        if (ReadOnly) { e.Handled = true; return; }
        if (hit is null)
        {
            var at = Math.Min(Math.Max(0, outline.LengthTicks - 1), Snap((pos.X - Labels) * MusicalTime.Ppq / pixelsPerQuarter));
            var n = new MidiNote(Guid.NewGuid().ToString("N"), at, Math.Min(Math.Max(1, NewDuration), outline.LengthTicks - at), PitchAt(pos.Y), NewVelocity, true);
            NoteEdited?.Invoke(n); Select(n.Id);
        }
        else
        {
            original = draft = hit; origin = pos; dragging = false;
            resizing = pos.X >= NoteRect(hit).Right - Math.Min(7, NoteRect(hit).Width / 3);
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (original is null || outline is null || ReadOnly) return;
        var pos = e.GetPosition(this);
        if (!dragging && Math.Abs(pos.X - origin.X) < 3 && Math.Abs(pos.Y - origin.Y) < 3) return;
        dragging = true;
        var delta = (long)Math.Round((pos.X - origin.X) * MusicalTime.Ppq / pixelsPerQuarter / snap, MidpointRounding.AwayFromZero) * snap;
        draft = resizing ? original with { Duration = Math.Clamp(original.Duration + delta, Math.Min(snap, outline.LengthTicks - original.Tick), outline.LengthTicks - original.Tick) }
            : original with { Tick = Math.Clamp(original.Tick + delta, 0, outline.LengthTicks - original.Duration), Pitch = PitchAt(pos.Y) };
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var changed = dragging && draft != original ? draft : null;
        original = draft = null; dragging = false; e.Pointer.Capture(null);
        if (changed is not null && !ReadOnly) NoteEdited?.Invoke(changed);
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        original = draft = null; dragging = false; InvalidateVisual(); base.OnPointerCaptureLost(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ReadOnly || outline is null || part?.Pattern.Notes.FirstOrDefault(n => n.Id == SelectedId) is not { } n) return;
        MidiNote? edited = e.Key switch
        {
            Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => n with { Duration = Math.Max(1, n.Duration - snap) },
            Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => n with { Duration = Math.Min(outline.LengthTicks - n.Tick, n.Duration + snap) },
            Key.Left => n with { Tick = Math.Max(0, n.Tick - snap) },
            Key.Right => n with { Tick = Math.Min(outline.LengthTicks - n.Duration, n.Tick + snap) },
            Key.Up => n with { Pitch = Math.Min(127, n.Pitch + 1) },
            Key.Down => n with { Pitch = Math.Max(0, n.Pitch - 1) },
            Key.L => n with { Locked = !n.Locked },
            Key.Add or Key.OemPlus => n with { Velocity = Math.Min(127, n.Velocity + 5) },
            Key.Subtract or Key.OemMinus => n with { Velocity = Math.Max(1, n.Velocity - 5) },
            _ => null
        };
        if (e.Key is Key.Delete or Key.Back) { NoteDeleted?.Invoke(n.Id); e.Handled = true; }
        else if (edited is not null) { NoteEdited?.Invoke(edited); e.Handled = true; }
    }
    internal static string PitchLabel(int pitch) => $"{new[] { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" }[pitch % 12]}{pitch / 12 - 1}  {pitch}";
    private static string DrumLabel(int pitch) => pitch switch
    {
        35 => "Acoustic kick", 36 => "Kick", 38 => "Snare", 40 => "Electric snare", 39 => "Clap", 42 => "Closed hat",
        44 => "Pedal hat", 46 => "Open hat", 49 => "Crash", 51 => "Ride", 41 => "Low floor tom", 43 => "Floor tom",
        45 => "Low tom", 47 => "Mid tom", 48 => "High-mid tom", 50 => "High tom", _ => PitchLabel(pitch)
    };
}
