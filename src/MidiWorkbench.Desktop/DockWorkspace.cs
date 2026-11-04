using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Model.Controls;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;

namespace MidiWorkbench.Desktop;

internal sealed class DockWorkspace : ContentControl, IDisposable
{
    private readonly Factory factory = new();
    private readonly DockSerializer serializer = new();
    private readonly Dictionary<string, Control> views;
    private readonly Dictionary<string, ContentControl> presenters = new();
    private readonly IDataTemplate template;
    private readonly DockControl host;
    private IDockWindow[] floating = [];
    public DockWorkspace(Dictionary<string, Control> panes)
    {
        views = panes;
        template = new FuncDataTemplate<Document>((document, _) =>
        {
            var presenter = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            if (document?.Id is not {} id || !views.TryGetValue(id, out var view)) return presenter;
            presenter.AttachedToVisualTree += (_, _) =>
            {
                // Moving a pane must transfer its view, not create another session owner.
                if (presenters.TryGetValue(id, out var old) && old != presenter) old.Content = null;
                presenters[id] = presenter; presenter.Content = view;
            };
            presenter.DetachedFromVisualTree += (_, _) => presenter.Content = null;
            return presenter;
        });
        Avalonia.Application.Current!.DataTemplates.Add(template);
        host = new DockControl { Factory = factory, InitializeFactory = true, InitializeLayout = true };
        Content = host; host.Layout = DefaultLayout();
    }
    private RootDock DefaultLayout()
    {
        var left = Group("left", ["Outline", "Routing"]);
        var right = Group("right", ["Patterns", "Perform"]);
        left.Proportion = .3; right.Proportion = .7;
        var split = new ProportionalDock { Id = "body", Orientation = Dock.Model.Core.Orientation.Horizontal,
            VisibleDockables = factory.CreateList<IDockable>(left, new ProportionalDockSplitter(), right) };
        return new RootDock { Id = "root", VisibleDockables = factory.CreateList<IDockable>(split), ActiveDockable = split, DefaultDockable = split };
    }
    private DocumentDock Group(string id, string[] names)
    {
        var docs = names.Select(n => (IDockable)new Document { Id = n, Title = n, CanClose = false }).ToArray();
        return new DocumentDock { Id = id, CanCreateDocument = false, VisibleDockables = factory.CreateList(docs), ActiveDockable = docs[0] };
    }
    private static IEnumerable<IDockable> Walk(IDockable root)
    {
        var pending = new Stack<IDockable>(); var seen = new HashSet<IDockable>(); pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item) || seen.Count > 512) throw new InvalidDataException("Invalid dock layout; reset layout.");
            yield return item;
            if (item is IDock dock) foreach (var child in dock.VisibleDockables ?? []) pending.Push(child);
            if (item is IRootDock rd) foreach (var window in rd.Windows ?? []) if (window.Layout is {} layout) pending.Push(layout);
        }
    }
    public void Restore(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var layout = serializer.Load<IDock>(stream);
        if (layout is not IRootDock root) throw new InvalidDataException("Invalid dock layout; reset layout.");
        var ids = Walk(root).OfType<Document>().Select(d => d.Id).ToArray();
        if (ids.Length != views.Count || !ids.ToHashSet().SetEquals(views.Keys)) throw new InvalidDataException("Layout panes changed; reset layout.");
        floating = root.Windows?.ToArray() ?? []; root.Windows = null;
        host.Layout = root;
    }
    public void OpenFloatingWindows()
    {
        if (host.Layout is not IRootDock root) return;
        foreach (var window in floating)
        {
            // Restore within the main display rather than retaining an unavailable monitor position.
            window.X = 80; window.Y = 80;
            window.Width = 800; window.Height = 600;
            factory.AddWindow(root, window);
        }
        floating = []; root.ShowWindows.Execute(null);
    }
    public string Save()
    {
        using var stream = new MemoryStream(); serializer.Save(stream, host.Layout!);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    public void Show(string id)
    {
        if (host.Layout is not IRootDock root) return;
        var document = Walk(root).OfType<Document>().FirstOrDefault(d => d.Id == id);
        if (document?.Owner is IDock owner) { owner.ActiveDockable = document; factory.SetActiveDockable(document); }
    }
    public void AttachShortcuts(EventHandler<KeyEventArgs> handler)
    {
        // Pane roots travel with their contents when floated. On the main window the
        // tunnel handler runs first and marks the event handled, so it is not repeated.
        foreach (var view in views.Values) view.AddHandler(InputElement.KeyDownEvent, handler, RoutingStrategies.Tunnel);
    }
    private void CloseFloating()
    {
        if (host.Layout is not IRootDock root) return;
        foreach (var window in root.Windows?.ToArray() ?? [])
        {
            if (window.Layout is {} layout) foreach (var item in Walk(layout)) item.CanClose = true;
            factory.CloseWindow(window);
        }
    }
    public void Reset() { CloseFloating(); floating = []; host.Layout = DefaultLayout(); }
    public void Dispose() { CloseFloating(); Avalonia.Application.Current?.DataTemplates.Remove(template); }
}
