using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;

public static class LiveGenerationSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Cancelled(Task task)
    {
        try { task.GetAwaiter().GetResult(); throw new Exception("Generation should have been cancelled"); }
        catch (OperationCanceledException) { }
    }
    public static void Run()
    {
        using var backend = new MemoryBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        var original = session.Project;
        var id = session.SelectedSectionId;
        session.StartSection(id, true);
        var outline = session.SelectedSection.Outline with { Seed = 987, Name = "Candidate" };
        var prepared = Wait(session.PrepareGenerationAsync(outline));
        Check(session.Status().Running && session.Project == original, "Preparation changed playing project");
        Check(session.GenerationStatus.Phase == GenerationPhase.Ready && session.Candidate == prepared, "Candidate status");
        session.AcceptGeneration(MusicalTime.Ppq * 128L);
        Check(session.Project.Sections[0].Outline.Name == "Candidate", "Live acceptance failed");
        session.CancelQueuedSection();
        Check(session.Status().Running && session.Project.Sections[0].Outline.Name == "Candidate", "Cancel launch reverted accepted edit");
        session.Stop();
        Check(session.Takes[^1].Outputs.Count(e => e.Status == 0xFA) == 1, "Generation restarted MIDI clock");
        session.Undo();
        Check(session.Project.Sections[0].Outline.Name == original.Sections[0].Outline.Name, "Acceptance missing undo");

        session.SetTempo(240);
        session.StartSection(id, true);
        var accepted = session.Project;
        var variation = Wait(session.PrepareVariationAsync(2468));
        Check(variation.Outline.Seed == 2468 && session.Project == accepted, "Variation was accepted during preparation");
        session.AuditionCandidate(MusicalTime.Ppq * 128L);
        Check(session.AuditionStatus.Phase == AuditionPhase.Queued && session.Project == accepted, "Audition changed project before boundary");
        session.ReturnFromAudition(MusicalTime.Ppq);
        Check(session.AuditionStatus.Phase == AuditionPhase.Idle, "Pending audition was not cancelled");
        session.AuditionCandidate(MusicalTime.Ppq / 4);
        var auditionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != AuditionPhase.Playing && DateTime.UtcNow < auditionDeadline) Thread.Sleep(10);
        Check(session.AuditionStatus.Phase == AuditionPhase.Playing && session.PlayingSectionId == id && session.Project == accepted, "Audition did not activate separately");
        session.ReturnFromAudition(MusicalTime.Ppq / 4);
        auditionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != AuditionPhase.Idle && DateTime.UtcNow < auditionDeadline) Thread.Sleep(10);
        Check(session.AuditionStatus.Phase == AuditionPhase.Idle && session.PlayingSectionId == id && session.Project == accepted, "Audition did not return");
        session.AuditionCandidate(MusicalTime.Ppq * 128L);
        Wait(session.PrepareVariationAsync(2468));
        Check(session.AuditionStatus.Phase == AuditionPhase.Idle, "Superseded candidate stayed queued");
        session.AuditionCandidate(MusicalTime.Ppq / 4);
        auditionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != AuditionPhase.Playing && DateTime.UtcNow < auditionDeadline) Thread.Sleep(10);
        Check(session.AuditionStatus.Phase == AuditionPhase.Playing, "Candidate did not begin audition");
        session.DiscardGeneration();
        auditionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != AuditionPhase.Idle && DateTime.UtcNow < auditionDeadline) Thread.Sleep(10);
        Check(session.AuditionStatus.Phase == AuditionPhase.Idle && session.Candidate is null && session.Project == accepted, "Discard did not return to accepted material");
        Wait(session.PrepareVariationAsync(2468));
        session.AuditionCandidate(MusicalTime.Ppq / 4);
        auditionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != AuditionPhase.Playing && DateTime.UtcNow < auditionDeadline) Thread.Sleep(10);
        session.AcceptGeneration(MusicalTime.Ppq / 4);
        Check(session.Project.Sections[0].Outline.Seed == 2468 && session.Status().Running, "Commit while auditioning failed");
        Thread.Sleep(200); session.Stop();
        Check(session.Takes[^1].Outputs.Count(e => e.Status == 0xFA) == 1, "Audition restarted clock");

        using (var meterSession = new WorkbenchSession())
        {
            meterSession.SetTempo(240);
            var originalSection = meterSession.SelectedSection;
            meterSession.StartSection(true);
            var next = Wait(meterSession.PrepareGenerationAsync(originalSection.Outline with { Meter = new Meter(3, 4) }));
            meterSession.AcceptGeneration(MusicalTime.Ppq * 8L);
            var before = meterSession.Status();
            Check(before.PlayingSection == originalSection && before.PlayingSection!.Outline.Meter == new Meter(4, 4), "Accepted meter appeared before boundary");
            Check(meterSession.SelectedSection == next, "Accepted project not updated");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (meterSession.Status().PlayingSection != next && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Check(meterSession.Status().PlayingSection == next && meterSession.PlayingSection == next, "Playing snapshot did not switch at boundary");
            var alternate = Wait(meterSession.PrepareGenerationAsync(next.Outline with { Meter = new Meter(7, 8) }));
            meterSession.AuditionCandidate(MusicalTime.Ppq * 2L);
            Check(meterSession.Status().PlayingSection == next, "Audition meter appeared before boundary");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (meterSession.Status().PlayingSection != alternate && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Check(meterSession.Status().PlayingSection == alternate, "Audition meter did not activate");
            meterSession.ReturnFromAudition(MusicalTime.Ppq * 2L);
            Check(meterSession.Status().PlayingSection == alternate, "Return meter appeared before boundary");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (meterSession.Status().PlayingSection != next && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Check(meterSession.Status().PlayingSection == next, "Return meter did not activate");
            meterSession.Stop();
            Check(meterSession.Status().PlayingSection is null, "Stopped snapshot remained active");
        }

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Cancelled(session.PrepareGenerationAsync(outline, cancellationToken: cancelled.Token));
        Check(session.Candidate is null && session.GenerationStatus.Phase == GenerationPhase.Cancelled, "Cancelled candidate published");
        var pending = new List<Task<Section>>();
        for (var i = 0; i < 12; i++)
            pending.Add(session.PrepareGenerationAsync(outline with { Seed = (uint)(1000 + i), Bars = 64 }));
        var latest = Wait(pending[^1]);
        foreach (var task in pending.Take(pending.Count - 1))
            try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Check(session.Candidate == latest && latest.Outline.Seed == 1011, "Earlier request replaced latest candidate");
        session.DiscardGeneration();
        Check(session.Candidate is null, "Discard left candidate");

        var originalPart = session.SelectedSection.Parts[0];
        session.SetPatternKind(originalPart.Id, PatternKind.Fixed);
        var fixedCandidate = Wait(session.PreparePartGenerationAsync(originalPart.Id, new GeneratorSettings()));
        Check(fixedCandidate.Parts[0].Pattern == session.SelectedSection.Parts[0].Pattern, "Fixed material changed");
        session.DiscardGeneration();
        var other = session.DuplicateSection(id);
        var stale = session.PrepareGenerationAsync(outline with { Bars = 64 });
        session.SelectSection(id);
        try { stale.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Check(session.Candidate is null && session.SelectedSectionId != other, "Selection published stale candidate");

        var editable = session.SelectedSection.Parts.First(p => p.Pattern.Notes.Length > 0 && p.Pattern.Kind != PatternKind.Fixed);
        var locked = editable.Pattern.Notes[0] with { Locked = true };
        session.EditNote(editable.Id, locked);
        var protectedVariation = Wait(session.PrepareVariationAsync(752));
        Check(protectedVariation.Parts.Single(p => p.Id == editable.Id).Pattern.Notes.Contains(locked), "Variation changed locked note");
        session.DiscardGeneration();

        using (var seeded = new WorkbenchSession())
        {
            var variedOutline = seeded.SelectedSection.Outline with { Variation = 1, Density = 1 };
            seeded.Generate(variedOutline);
            var stochastic = new GeneratorSettings(GeneratorKind.Stochastic) { Seed = 12, PartSeed = 42, Probability = .8 };
            var step = new GeneratorSettings(GeneratorKind.StepSequencer) { Seed = 34, PartSeed = 9, Probability = .8 };
            Wait(seeded.PreparePartGenerationAsync("bass", stochastic)); seeded.AcceptGeneration();
            Wait(seeded.PreparePartGenerationAsync("pulse", step)); seeded.AcceptGeneration();
            var bass = seeded.SelectedSection.Parts.Single(p => p.Id == "bass");
            var protectedNote = bass.Pattern.Notes[0] with { Locked = true };
            seeded.EditNote("bass", protectedNote);
            seeded.SetPatternKind("pad", PatternKind.Fixed);
            var fixedPad = seeded.SelectedSection.Parts.Single(p => p.Id == "pad").Pattern;
            var variationOne = Wait(seeded.PrepareVariationAsync(101));
            seeded.DiscardGeneration();
            var repeat = Wait(seeded.PrepareVariationAsync(101));
            var bassOne = variationOne.Parts.Single(p => p.Id == "bass").Pattern;
            var bassRepeat = repeat.Parts.Single(p => p.Id == "bass").Pattern;
            Check(bassOne.Notes.SequenceEqual(bassRepeat.Notes), "Variation seed not deterministic");
            var pulseOne = variationOne.Parts.Single(p => p.Id == "pulse").Pattern;
            Check(bassOne.Recipe!.Settings!.Seed != stochastic.Seed && pulseOne.Recipe!.Settings!.Seed != step.Seed &&
                bassOne.Recipe.Settings.Seed != pulseOne.Recipe.Settings.Seed, "Part seeds not varied independently");
            Check(bassOne.Recipe.Settings with { Seed = stochastic.Seed } == stochastic, "Variation changed generator settings");
            Check(bassOne.Notes.Contains(protectedNote) && variationOne.Parts.Single(p => p.Id == "pad").Pattern == fixedPad, "Variation changed protected material");
            seeded.DiscardGeneration();
            var variationTwo = Wait(seeded.PrepareVariationAsync(102));
            Check(!bassOne.Notes.SequenceEqual(variationTwo.Parts.Single(p => p.Id == "bass").Pattern.Notes), "New variation seed did not change stochastic notes");
            Check(!pulseOne.Notes.SequenceEqual(variationTwo.Parts.Single(p => p.Id == "pulse").Pattern.Notes), "New variation seed did not change step notes");
        }
        var closed = new WorkbenchSession();
        var closingWork = closed.PrepareGenerationAsync(closed.SelectedSection.Outline with { Bars = 64 });
        closed.Dispose();
        try { closingWork.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Check(closed.Candidate is null, "Disposed session published a candidate");

        session.StartArrangement(true);
        Wait(session.PrepareGenerationAsync(outline));
        try { session.AcceptGeneration(MusicalTime.Ppq); throw new Exception("Arrangement acceptance should fail"); }
        catch (InvalidOperationException) { }
        Check(session.Candidate is not null && session.Status().Running, "Arrangement rejection lost candidate or playback");
        session.Stop();
    }
}
