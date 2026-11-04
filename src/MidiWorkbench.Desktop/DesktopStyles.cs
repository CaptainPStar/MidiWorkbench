using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MidiWorkbench.Desktop;

internal static class DesktopStyles
{
    public static void Apply(Avalonia.Application app)
    {
        var background = new SolidColorBrush(Color.Parse("#171B22"));
        var border = new SolidColorBrush(Color.Parse("#46505E"));
        app.Styles.Add(new Style(x => x.OfType<Window>())
        {
            Setters = { new Setter(Window.BackgroundProperty, background) }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(Button.BorderBrushProperty, border) }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("primary"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.Parse("#176D79"))),
                new Setter(Button.ForegroundProperty, Brushes.White),
                new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.Parse("#3E98A3")))
            }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("danger"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.Parse("#703B3E"))),
                new Setter(Button.ForegroundProperty, Brushes.White),
                new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.Parse("#A75F63")))
            }
        });
    }
}
