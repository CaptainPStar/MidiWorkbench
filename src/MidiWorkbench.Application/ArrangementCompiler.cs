using System.Collections.Immutable;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;

namespace MidiWorkbench.Application;

public static class ArrangementCompiler
{
    public static PlaybackPlan Compile(SongProject project)
    {
        project.Validate();
        var entries = project.Arrangement.IsEmpty
            ? project.Sections.Select(s => new ArrangementEntry(s.Id)).ToImmutableArray()
            : project.Arrangement;
        var sections = project.Sections.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var events = new List<PlannedMessage>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        long offset = 0; var instance = 0;
        foreach (var entry in entries)
        for (var repeat = 0; repeat < entry.Repeats; repeat++)
        {
            var section = sections[entry.SectionId];
            foreach (var part in section.Parts) destinations.Add(part.Destination);
            var solo = section.Parts.Any(p => p.Solo && !p.Muted);
            var audible = section with { Parts = section.Parts.Where(p => !p.Muted && (!solo || p.Solo)).ToImmutableArray() };
            if (!audible.Parts.IsEmpty)
            {
                var plan = PlanCompiler.Compile(audible);
                foreach (var e in plan.Events) events.Add(e with { Tick = checked(offset + e.Tick), Owner = instance + "/" + e.Owner });
                foreach (var d in plan.Destinations) destinations.Add(d);
            }
            offset = checked(offset + section.Outline.LengthTicks); instance++;
        }
        return new(offset, events.OrderBy(e => e.Tick).ThenBy(e => e.Status & 0xF0).ToImmutableArray(), destinations.ToImmutableArray());
    }
}
