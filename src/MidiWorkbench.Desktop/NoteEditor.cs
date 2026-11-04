using System.Globalization;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiWorkbench.Domain;
using MidiWorkbench.Application;

namespace MidiWorkbench.Desktop;

internal sealed record NoteEdit(ImmutableArray<MidiNote> Upserts, ImmutableArray<string> DeleteIds, ImmutableArray<string> Selection);

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
    private MidiNote? anchor;
    private ImmutableArray<MidiNote> gesture = [], copied = [];
    private readonly HashSet<string> selection = new(StringComparer.Ordinal);
    private Dictionary<string, MidiNote> draft = new(StringComparer.Ordinal);
    private Pattern? indexedPattern;
    private MidiNote[] notesByTick = [];
    private long[] prefixMaxEnd = [];
    private Dictionary<int, int> pitchRows = [];
    private Dictionary<string, int> paintOrder = new(StringComparer.Ordinal);
    private Point origin;
    private Rect? marquee;
    private string[] marqueeBase = [];
    private string? sectionIdentity;
    private IPointer? captured;
    private long insertTick;
    private bool resizing, dragging, adding;
    private long snap = MusicalTime.Ppq / 4;
    private double pixelsPerQuarter = 96;
    private double playhead = -1;
    public string? SelectedId { get; private set; }
    public IReadOnlyCollection<string> SelectedIds => selection.ToArray();
    public ImmutableArray<MidiNote> SelectedNotes => part?.Pattern.Notes.Where(n => selection.Contains(n.Id)).ToImmutableArray() ?? [];
    public bool DrumMode { get; private set; }
    public bool ReadOnly { get; set; }
    public int NewVelocity { get; set; } = 90;
    public long NewDuration { get; set; } = 240;
    public bool NewLocked { get; set; } = true;
    public long SnapTicks { get => snap; set { snap = Math.Max(1, value); InvalidateVisual(); } }
    public double Zoom { get => pixelsPerQuarter; set { pixelsPerQuarter = Math.Clamp(value, 24, 240); Resize(); } }
    public double Playhead { get => playhead; set { if (Math.Abs(playhead - value) > .01) { playhead = value; InvalidateVisual(); } } }
    public event Action<MidiNote?>? SelectionChanged;
    public Func<NoteEdit, bool>? CommitEdit { get; set; }
    public event Action<Exception>? Error;

    public NoteEditor()
    {
        Focusable = true; ClipToBounds = true;
        // Culling depends on the parent's viewport even when this fixed-size control has
        // unchanged bounds. Resize and scroll must repaint stopped editors as well.
        EffectiveViewportChanged += (_, _) => InvalidateVisual();
        AutomationProperties.SetName(this, "Note editor");
        ToolTip.SetTip(this, "Click to add; drag empty space to select a rectangle. Shift-click toggles notes. Drag selected notes to move; right edges resize. Click the ruler to place pasted notes. Ctrl+A selects all; Ctrl+C/V copies/pastes; Ctrl+D duplicates; Delete removes. Arrows move; Shift+Left/Right resizes; L locks; +/- changes velocity.");
    }
    public void Show(Part? value, Outline? section, bool drums, string? sectionId = null)
    {
        var changed = part?.Id != value?.Id || sectionIdentity != sectionId || DrumMode != drums;
        var patternChanged = !ReferenceEquals(part?.Pattern, value?.Pattern);
        if (patternChanged) { captured?.Capture(null); CancelGesture(); }
        sectionIdentity = sectionId;
        part = value; outline = section; DrumMode = drums;
        lanes = drums ? new[] { 49, 51, 46, 42, 44, 39, 38, 40, 50, 48, 47, 45, 43, 41, 36, 35 }
            .Concat(value?.Pattern.Notes.Select(n => n.Pitch).Distinct().Except(new[] { 49, 51, 46, 42, 44, 39, 38, 40, 50, 48, 47, 45, 43, 41, 36, 35 }) ?? []).ToArray()
            : Enumerable.Range(0, 128).Reverse().ToArray();
        if (!ReferenceEquals(indexedPattern, value?.Pattern)) RebuildNoteIndex(value?.Pattern);
        pitchRows = lanes.Select((pitch, row) => (pitch, row)).ToDictionary(x => x.pitch, x => x.row);
        if (changed) { selection.Clear(); SelectedId = null; insertTick = 0; CancelGesture(); }
        var valid = value?.Pattern.Notes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        selection.RemoveWhere(id => !valid.Contains(id));
        if (SelectedId is not null && !selection.Contains(SelectedId)) SelectedId = selection.FirstOrDefault();
        Resize();
        if (changed || patternChanged) SelectionChanged?.Invoke(part?.Pattern.Notes.FirstOrDefault(n => n.Id == SelectedId));
    }
    public void Select(string? id) => SelectMany(id is null ? [] : [id], id);
    public void SelectMany(IEnumerable<string> ids, string? primary = null)
    {
        var valid = part?.Pattern.Notes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        selection.Clear(); selection.UnionWith(ids.Where(valid.Contains));
        SelectedId = primary is not null && selection.Contains(primary) ? primary : part?.Pattern.Notes.FirstOrDefault(n => selection.Contains(n.Id))?.Id;
        SelectionChanged?.Invoke(part?.Pattern.Notes.FirstOrDefault(n => n.Id == SelectedId));
        AutomationProperties.SetName(this, $"Note editor, {selection.Count} selected");
        InvalidateVisual();
    }
    public void SelectAll() => SelectMany(part?.Pattern.Notes.Select(n => n.Id) ?? []);
    public void Copy()
    {
        if (part is null || outline is null) return;
        if (!SelectedNotes.IsEmpty) insertTick = SelectedNotes.Max(n => n.EndTick);
        copied = NoteSelectionEdits.Copy(part.Pattern, selection, outline.LengthTicks);
    }
    public void Paste() => Command(() =>
    {
        if (outline is null || ReadOnly || copied.IsEmpty) return;
        if (part is null) return;
        var pattern = NoteSelectionEdits.Paste(part.Pattern, copied, outline.LengthTicks, insertTick);
        PublishCopies(pattern);
    });
    public void Duplicate() => Command(() =>
    {
        var notes = SelectedNotes;
        if (outline is null || ReadOnly || notes.IsEmpty) return;
        var end = notes.Max(n => n.EndTick);
        var at = end + (snap - end % snap) % snap;
        if (part is null) return;
        var pattern = NoteSelectionEdits.Duplicate(part.Pattern, selection, outline.LengthTicks, at - notes.Min(n => n.Tick));
        PublishCopies(pattern);
    });
    public void DeleteSelection()
    {
        if (!ReadOnly && selection.Count > 0 && part is not null && outline is not null) PublishPattern(NoteSelectionEdits.Delete(part.Pattern, selection, outline.LengthTicks), []);
    }
    public void ApplyFields(MidiNote edited) => Command(() =>
    {
        if (ReadOnly || outline is null || SelectedNotes.IsEmpty || part?.Pattern.Notes.FirstOrDefault(n => n.Id == SelectedId) is not { } primary) return;
        var moved = NoteSelectionEdits.Move(part.Pattern, selection, outline.LengthTicks, edited.Tick - primary.Tick, edited.Pitch - primary.Pitch);
        var resized = NoteSelectionEdits.Resize(moved, selection, outline.LengthTicks, edited.Duration - primary.Duration);
        var velocity = NoteSelectionEdits.SetVelocity(resized, selection, outline.LengthTicks, edited.Velocity);
        PublishPattern(NoteSelectionEdits.SetLocked(velocity, selection, outline.LengthTicks, edited.Locked), selection.ToArray());
    });
    public void Add(MidiNote note) => Add(note, reveal: true);
    private void Add(MidiNote note, bool reveal) { if (!ReadOnly) Publish([note], [], [note.Id], reveal); }
    private void PublishCopies(Pattern pattern)
    {
        var existing = part!.Pattern.Notes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var added = pattern.Notes.Where(n => !existing.Contains(n.Id)).ToImmutableArray();
        if (PublishPattern(pattern, added.Select(n => n.Id)) && !added.IsEmpty) insertTick = added.Max(n => n.EndTick);
    }
    private bool PublishPattern(Pattern pattern, IEnumerable<string> selected) =>
        Publish(pattern.Notes, part!.Pattern.Notes.Select(n => n.Id).Except(pattern.Notes.Select(n => n.Id)), selected);
    private bool Publish(IEnumerable<MidiNote> notes, IEnumerable<string> deleted, IEnumerable<string> selected, bool reveal = true)
    {
        var existing = part?.Pattern.Notes.ToDictionary(n => n.Id, StringComparer.Ordinal) ?? [];
        var changed = notes.Where(n => existing.GetValueOrDefault(n.Id) != n).ToImmutableArray();
        var removed = deleted.ToImmutableArray(); var ids = selected.ToImmutableArray();
        if (changed.IsEmpty && removed.IsEmpty) return false;
        if (CommitEdit?.Invoke(new(changed, removed, ids)) != true) return false;
        SelectMany(ids, SelectedId);
        if (reveal) RevealSelection();
        return true;
    }
    public void RevealSelection() => QueueReveal(material: false);
    public void RevealMaterial() => QueueReveal(material: true);
    private void QueueReveal(bool material)
    {
        var id = SelectedId; var pattern = part?.Pattern;
        if ((!material && id is null) || pattern is null || pattern.Notes.IsEmpty) return;
        // A commit can add a drum lane or resize the editor. Reveal after layout,
        // and ignore requests superseded by another selection or edit.
        Dispatcher.UIThread.Post(() =>
        {
            if (SelectedId != id || !ReferenceEquals(part?.Pattern, pattern)) return;
            var scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null || scroll.Viewport.Width <= Labels || scroll.Viewport.Height <= Header) return;
            var visible = new Rect(scroll.Offset.X + Labels, scroll.Offset.Y + Header,
                scroll.Viewport.Width - Labels, scroll.Viewport.Height - Header);
            // New proposals need a visible musical reference, but must retain a
            // useful manually chosen viewport when no note is selected.
            if (material && id is null && pattern.Notes.Any(n => NoteRect(n).Intersects(visible))) return;
            var note = pattern.Notes.FirstOrDefault(n => n.Id == id) ?? (material ? pattern.Notes[0] : null);
            if (note is null) return;
            var bounds = NoteRect(note);
            // Long notes reveal their start; the sticky ruler and pitch labels
            // must not cover the selected note after scrolling.
            var focus = new Rect(bounds.Position, new Size(Math.Min(bounds.Width, Math.Max(1, visible.Width - 8)),
                Math.Min(bounds.Height, Math.Max(1, visible.Height - 8))));
            if (focus.Left >= visible.Left && focus.Top >= visible.Top && focus.Right <= visible.Right && focus.Bottom <= visible.Bottom) return;
            this.BringIntoView(new Rect(Math.Max(0, focus.Left - Labels - 4), Math.Max(0, focus.Top - Header - 4),
                Math.Min(scroll.Viewport.Width, focus.Width + Labels + 8), Math.Min(scroll.Viewport.Height, focus.Height + Header + 8)));
        }, DispatcherPriority.Loaded);
    }
    private void Command(Action command) { try { command(); } catch (Exception ex) { Error?.Invoke(ex); } }
    public double SuggestedOffset => DrumMode ? 0 : Math.Max(0, Header + (127 - (part?.Pattern.Notes.Select(n => n.Pitch).DefaultIfEmpty(60).Max() ?? 60) - 2) * Lane);
    private Rect Viewport => this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } s
        ? new Rect(s.Offset.X, s.Offset.Y, s.Viewport.Width, s.Viewport.Height) : new Rect(Bounds.Size);
    private void Resize()
    {
        Width = Math.Max(650, Labels + (outline?.LengthTicks ?? MusicalTime.Ppq * 16) * pixelsPerQuarter / MusicalTime.Ppq + 24);
        Height = Header + lanes.Length * Lane; InvalidateVisual();
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private void RebuildNoteIndex(Pattern? pattern)
    {
        indexedPattern = pattern;
        notesByTick = pattern?.Notes.OrderBy(n => n.Tick).ToArray() ?? [];
        paintOrder = pattern?.Notes.Select((note, index) => (note.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        prefixMaxEnd = new long[notesByTick.Length];
        var maxEnd = long.MinValue;
        for (var i = 0; i < notesByTick.Length; i++)
        {
            maxEnd = Math.Max(maxEnd, notesByTick[i].EndTick);
            prefixMaxEnd[i] = maxEnd;
        }
    }
    private int FirstVisibleNote(double leftTick)
    {
        // Find the first note whose end could reach the viewport. Prefix maxima keep
        // this logarithmic even when long notes precede short notes in tick order.
        var low = 0; var high = prefixMaxEnd.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (prefixMaxEnd[middle] < leftTick) low = middle + 1; else high = middle;
        }
        return low;
    }
    private void Text(DrawingContext context, string value, double x, double y, IBrush? brush = null) =>
        context.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, brush ?? Ink), new Point(x, y));
    internal Rect NoteRect(MidiNote n) => new(Labels + n.Tick * pixelsPerQuarter / MusicalTime.Ppq,
        Header + (pitchRows.TryGetValue(n.Pitch, out var row) ? row : 0) * Lane + 2,
        Math.Max(5, n.Duration * pixelsPerQuarter / MusicalTime.Ppq), Lane - 4);
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
        var visibleNotes = new Dictionary<string, MidiNote>(StringComparer.Ordinal);
        var firstNote = FirstVisibleNote(Math.Max(0, (viewport.Left - Labels - 5) * MusicalTime.Ppq / pixelsPerQuarter));
        var lastTick = (viewport.Right - Labels) * MusicalTime.Ppq / pixelsPerQuarter;
        for (var i = firstNote; i < notesByTick.Length && notesByTick[i].Tick <= lastTick; i++)
            visibleNotes[notesByTick[i].Id] = notesByTick[i];
        // Drafts can be moved into the viewport from an offscreen starting position.
        foreach (var (id, note) in draft) visibleNotes[id] = note;
        // Preserve authored stacking order, matching the reverse-order pointer hit test.
        foreach (var stored in visibleNotes.Values.OrderBy(n => paintOrder.GetValueOrDefault(n.Id, int.MaxValue)))
        {
            var n = draft.GetValueOrDefault(stored.Id, stored);
            var rect = NoteRect(n);
            if (!rect.Intersects(viewport)) continue;
            context.FillRectangle(selection.Contains(n.Id) ? SelectedBrush : NoteBrush, rect, 2);
            var velocityHeight = (Lane - 7) * n.Velocity / 127d;
            context.FillRectangle(Ink, new Rect(rect.X + 2, rect.Bottom - velocityHeight - 1, 2, velocityHeight));
            if (n.Locked) context.DrawRectangle(null, new Pen(Brushes.White, 1), rect.Deflate(2));
            if (selection.Contains(n.Id) && rect.Width > 9) context.DrawLine(new Pen(BackgroundBrush, 2), new Point(rect.Right - 3, rect.Y + 3), new Point(rect.Right - 3, rect.Bottom - 3));
        }
        if (playhead >= 0 && playhead <= outline.LengthTicks)
        {
            var x = Labels + playhead * pixelsPerQuarter / MusicalTime.Ppq;
            context.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, 0), new Point(x, Height));
        }
        if (marquee is { } area) context.DrawRectangle(new SolidColorBrush(Color.Parse("#4BAEBC"), .15), new Pen(SelectedBrush), area);
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
        if (pos.X < viewport.Left + Labels) return;
        if (pos.Y < viewport.Top + Header)
        {
            insertTick = Math.Min(outline.LengthTicks, Snap((pos.X - Labels) * MusicalTime.Ppq / pixelsPerQuarter));
            e.Handled = true; InvalidateVisual(); return;
        }
        var hit = part.Pattern.Notes.Reverse().FirstOrDefault(n => NoteRect(n).Contains(pos));
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (point.Properties.IsRightButtonPressed)
        {
            if (hit is not null) { if (!selection.Contains(hit.Id)) Select(hit.Id); DeleteSelection(); }
            e.Handled = true; return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        origin = pos; dragging = false; adding = hit is null && !additive;
        insertTick = Math.Min(outline.LengthTicks - 1, Snap((pos.X - Labels) * MusicalTime.Ppq / pixelsPerQuarter));
        if (hit is null)
        {
            marqueeBase = additive ? selection.ToArray() : [];
            if (!additive) Select(null);
            marquee = new Rect(pos, pos);
        }
        else
        {
            if (additive)
            {
                var ids = selection.ToHashSet(StringComparer.Ordinal);
                if (!ids.Add(hit.Id)) { ids.Remove(hit.Id); SelectMany(ids); e.Handled = true; return; }
                SelectMany(ids, hit.Id);
            }
            else if (!selection.Contains(hit.Id)) Select(hit.Id);
            if (ReadOnly) { e.Handled = true; return; }
            anchor = hit; gesture = SelectedNotes;
            resizing = pos.X >= NoteRect(hit).Right - Math.Min(7, NoteRect(hit).Width / 3);
        }
        captured = e.Pointer; captured.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (captured is null || outline is null) return;
        var pos = e.GetPosition(this);
        if (!dragging && Math.Abs(pos.X - origin.X) < 3 && Math.Abs(pos.Y - origin.Y) < 3) return;
        dragging = true;
        if (marquee is not null)
        {
            marquee = new Rect(origin, pos).Normalize();
            SelectMany(marqueeBase.Concat(part?.Pattern.Notes.Where(n => NoteRect(n).Intersects(marquee.Value)).Select(n => n.Id) ?? []));
        }
        else if (anchor is not null && !ReadOnly)
        {
            var delta = (long)Math.Round((pos.X - origin.X) * MusicalTime.Ppq / pixelsPerQuarter / snap, MidpointRounding.AwayFromZero) * snap;
            var source = part!.Pattern with { Notes = gesture };
            var edited = resizing ? NoteSelectionEdits.Resize(source, selection, outline.LengthTicks, delta)
                : NoteSelectionEdits.Move(source, selection, outline.LengthTicks, delta, PitchAt(pos.Y) - anchor.Pitch);
            draft = edited.Notes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        }
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (captured is null) return;
        var add = adding && !dragging && !ReadOnly;
        var changed = dragging && marquee is null && !ReadOnly ? draft.Values.ToArray() : [];
        var selected = selection.ToArray(); var at = insertTick; var pos = origin;
        CancelGesture(); e.Pointer.Capture(null);
        if (add && outline is not null)
        {
            var note = new MidiNote(Guid.NewGuid().ToString("N"), at, Math.Min(Math.Max(1, NewDuration), outline.LengthTicks - at), PitchAt(pos.Y), NewVelocity, NewLocked);
            Add(note, reveal: false); insertTick = note.EndTick;
        }
        else if (changed.Length > 0) Publish(changed, [], selected, reveal: false);
        InvalidateVisual(); e.Handled = true;
    }
    private void CancelGesture()
    {
        anchor = null; gesture = []; draft.Clear(); marquee = null; marqueeBase = [];
        captured = null; dragging = adding = false; InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        CancelGesture(); base.OnPointerCaptureLost(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (captured is not null) { captured.Capture(null); CancelGesture(); }
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command)
        {
            switch (e.Key)
            {
                case Key.A: SelectAll(); break;
                case Key.C: Copy(); break;
                case Key.X: if (!ReadOnly) { Copy(); DeleteSelection(); } break;
                case Key.V: Paste(); break;
                case Key.D: Duplicate(); break;
                default: return;
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape) { captured?.Capture(null); CancelGesture(); Select(null); e.Handled = true; return; }
        if (e.Key is Key.Home or Key.End || selection.Count == 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var n = e.Key == Key.End ? part?.Pattern.Notes.LastOrDefault() : part?.Pattern.Notes.FirstOrDefault();
            if (n is not null) { Select(n.Id); RevealSelection(); }
            e.Handled = true; return;
        }
        if (ReadOnly || outline is null || SelectedNotes.IsEmpty) return;
        var notes = SelectedNotes; var pattern = part!.Pattern;
        Pattern? edited = e.Key switch
        {
            Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => NoteSelectionEdits.Resize(pattern, selection, outline.LengthTicks, -snap),
            Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => NoteSelectionEdits.Resize(pattern, selection, outline.LengthTicks, snap),
            Key.Left => NoteSelectionEdits.Move(pattern, selection, outline.LengthTicks, -snap),
            Key.Right => NoteSelectionEdits.Move(pattern, selection, outline.LengthTicks, snap),
            Key.Up => NoteSelectionEdits.Move(pattern, selection, outline.LengthTicks, 0, 1),
            Key.Down => NoteSelectionEdits.Move(pattern, selection, outline.LengthTicks, 0, -1),
            Key.L => NoteSelectionEdits.SetLocked(pattern, selection, outline.LengthTicks, !notes.All(n => n.Locked)),
            Key.Add or Key.OemPlus => NoteSelectionEdits.AdjustVelocity(pattern, selection, outline.LengthTicks, 5),
            Key.Subtract or Key.OemMinus => NoteSelectionEdits.AdjustVelocity(pattern, selection, outline.LengthTicks, -5),
            _ => null
        };
        if (e.Key is Key.Delete or Key.Back) { DeleteSelection(); e.Handled = true; }
        else if (edited is not null) { PublishPattern(edited, selection.ToArray()); e.Handled = true; }
    }
    internal static string PitchLabel(int pitch) => $"{new[] { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" }[pitch % 12]}{pitch / 12 - 1}  {pitch}";
    private static string DrumLabel(int pitch) => pitch switch
    {
        35 => "Acoustic kick", 36 => "Kick", 38 => "Snare", 40 => "Electric snare", 39 => "Clap", 42 => "Closed hat",
        44 => "Pedal hat", 46 => "Open hat", 49 => "Crash", 51 => "Ride", 41 => "Low floor tom", 43 => "Floor tom",
        45 => "Low tom", 47 => "Mid tom", 48 => "High-mid tom", 50 => "High tom", _ => PitchLabel(pitch)
    };
}
