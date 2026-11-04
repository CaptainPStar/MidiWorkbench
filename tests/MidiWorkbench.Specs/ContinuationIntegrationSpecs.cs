using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class ContinuationIntegrationSpecs
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RejectMissingCandidate(WorkbenchSession session)
    {
        try { session.AcceptGeneration(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("An invalidated candidate was accepted.");
    }

    public static void Run()
    {
        ReloadInvalidatesCandidate();
        DiscardDuringPlayback();
        LiveAcceptPreservesMaterialAndClock();
        SelectingSectionDoesNotLaunch();
        DisposeInvalidatesInFlightGeneration();
        SupersededRequestCannotPublish();
        ReloadInvalidatesInFlightGeneration();
        AuditionReturnPreservesClockAndNotes();
        SupersedeOrSelectCancelsQueuedAudition();
        DiscardCancelsQueuedAudition();
        DiscardPlayingAuditionReturnsToAccepted();
    }

    private static void ReloadInvalidatesCandidate()
    {
        using var session = new WorkbenchSession();
        var replacement = WorkbenchSession.CreateDemo() with { Name = "Replacement" };
        var path = Path.Combine(Path.GetTempPath(), $"midi-reload-{Guid.NewGuid():N}.json");
        try
        {
            ProjectStore.Save(path, replacement);
            session.PrepareGeneration(session.SelectedSection.Outline with { Name = "Stale candidate" });
            session.Load(path);
            RejectMissingCandidate(session);
            Check(session.Project.Id == replacement.Id && session.SelectedSection.Outline.Name != "Stale candidate",
                "Reload was overwritten by a candidate from the prior project.");
        }
        finally { File.Delete(path); }
    }

    private static void DiscardDuringPlayback()
    {
        using var session = new WorkbenchSession();
        var original = session.SelectedSection;
        session.PrepareGeneration(original.Outline with { Name = "Discarded candidate" });
        session.StartSection(repeat: true);
        session.DiscardGeneration();
        Check(session.Status().Running, "Discarding a candidate stopped playback.");
        session.Stop();
        RejectMissingCandidate(session);
        Check(session.SelectedSection == original, "Discarding a candidate changed the accepted section.");
    }

    private static void LiveAcceptPreservesMaterialAndClock()
    {
        using var backend = new MemoryBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        var parts = session.SelectedSection.Parts;
        var fixedId = parts[0].Id;
        session.SetPatternKind(fixedId, PatternKind.Fixed);
        var lockedPart = parts[1];
        var locked = new MidiNote("integration-protected", 10, 120, 75, 90, true);
        session.EditNote(lockedPart.Id, locked);
        var original = session.SelectedSection;
        session.StartSection(repeat: true);
        var candidate = session.PrepareGenerationAsync(original.Outline with { Seed = original.Outline.Seed + 1 }).GetAwaiter().GetResult();
        Check(candidate.Parts.Single(p => p.Id == fixedId).Pattern == original.Parts.Single(p => p.Id == fixedId).Pattern,
            "Live preparation changed a fixed pattern.");
        Check(candidate.Parts.Single(p => p.Id == lockedPart.Id).Pattern.Notes.Contains(locked),
            "Live preparation lost a locked note.");
        Check(session.SelectedSection == original && session.Status().Running, "Preparation changed accepted material or stopped playback.");
        session.AcceptGeneration(240);
        Thread.Sleep(300);
        session.Stop();
        var take = session.Takes[^1];
        Check(take.Actions.Any(a => a.Kind == "queue"), "Acceptance did not queue a boundary transition.");
        Check(take.Outputs.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 75 && e.Data2 > 0 && e.Seconds > .10),
            "Accepted candidate did not play its protected note after the boundary.");
        Check(take.Outputs.Count(e => e.Status == 0xFA) == 1 && take.Outputs.Count(e => e.Status == 0xFC) == 1,
            "Live acceptance restarted or failed to stop MIDI clock.");
        foreach (var group in take.Outputs.Where(e => (e.Status & 0xF0) is 0x80 or 0x90)
            .GroupBy(e => (e.Endpoint, Channel: e.Status & 15, e.Data1)))
            Check(group.Count(e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0) ==
                group.Count(e => (e.Status & 0xF0) == 0x80 || (e.Status & 0xF0) == 0x90 && e.Data2 == 0),
                $"Unbalanced note after live acceptance: {group.Key}.");
    }

    private static void SelectingSectionDoesNotLaunch()
    {
        using var session = new WorkbenchSession();
        var first = session.SelectedSectionId;
        var second = session.DuplicateSection(first);
        var snapshot = session.Project;
        session.StartSection(first, true);
        session.SelectSection(second);
        Thread.Sleep(160);
        Check(session.Project == snapshot && session.PlayingSectionId == first,
            "Selecting a section changed project material or launched it.");
        session.Stop();
        Check(!session.Takes[^1].Actions.Any(a => a.Kind == "queue"), "Selection queued a section launch.");
    }

    private static void DisposeInvalidatesInFlightGeneration()
    {
        var session = new WorkbenchSession();
        var task = session.PrepareGenerationAsync(session.SelectedSection.Outline with { Bars = 256, Seed = 37 });
        session.Dispose();
        try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Check(session.Candidate is null, "Generation published a candidate after disposal.");
        try { session.PrepareGenerationAsync(WorkbenchSession.CreateDemo().Sections[0].Outline).GetAwaiter().GetResult(); }
        catch (ObjectDisposedException) { return; }
        throw new Exception("Disposed session accepted a generation request.");
    }

    private static void SupersededRequestCannotPublish()
    {
        using var session = new WorkbenchSession();
        var outline = session.SelectedSection.Outline;
        var earlier = session.PrepareGenerationAsync(outline with { Bars = 256, Seed = 11 });
        var latest = session.PrepareGenerationAsync(outline with { Seed = 12 });
        var expected = latest.GetAwaiter().GetResult();
        try { earlier.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Check(session.Candidate == expected && session.Candidate.Outline.Seed == 12,
            "Superseded generation replaced the latest candidate.");
    }

    private static void ReloadInvalidatesInFlightGeneration()
    {
        using var session = new WorkbenchSession();
        var replacement = WorkbenchSession.CreateDemo() with { Name = "Reload during generation" };
        var path = Path.Combine(Path.GetTempPath(), $"midi-generation-{Guid.NewGuid():N}.json");
        try
        {
            ProjectStore.Save(path, replacement);
            var pending = session.PrepareGenerationAsync(session.SelectedSection.Outline with { Bars = 256, Seed = 19 });
            session.Load(path);
            try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Check(session.Project.Id == replacement.Id && session.Candidate is null,
                "Generation from the prior project survived reload.");
        }
        finally { File.Delete(path); }
    }

    private static void WaitForAudition(WorkbenchSession session, AuditionPhase phase)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (session.AuditionStatus.Phase != phase && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Check(session.AuditionStatus.Phase == phase, $"Audition did not reach {phase}.");
    }

    private static void AuditionReturnPreservesClockAndNotes()
    {
        using var session = new WorkbenchSession();
        session.SetTempo(240);
        var original = session.Project;
        session.StartSection(repeat: true);
        var candidate = session.PrepareGenerationAsync(session.SelectedSection.Outline with { Seed = 731 }).GetAwaiter().GetResult();
        session.AuditionCandidate(240);
        WaitForAudition(session, AuditionPhase.Playing);
        Check(session.Project == original && session.Candidate == candidate, "Audition committed or lost the candidate.");
        session.ReturnFromAudition(240);
        WaitForAudition(session, AuditionPhase.Idle);
        Check(session.Project == original && session.Candidate == candidate, "Return changed project or candidate.");
        session.AuditionCandidate(240);
        WaitForAudition(session, AuditionPhase.Playing);
        session.AcceptGeneration(240);
        WaitForAudition(session, AuditionPhase.Idle);
        Check(session.SelectedSection == candidate, "Accept did not commit the auditioned candidate.");
        session.Stop();
        var outputs = session.Takes[^1].Outputs;
        Check(outputs.Count(e => e.Status == 0xFA) == 1 && outputs.Count(e => e.Status == 0xFC) == 1,
            "Audition, return or acceptance restarted clock.");
        foreach (var group in outputs.Where(e => (e.Status & 0xF0) is 0x80 or 0x90)
            .GroupBy(e => (e.Endpoint, Channel: e.Status & 15, e.Data1)))
            Check(group.Count(e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0) ==
                group.Count(e => (e.Status & 0xF0) == 0x80 || (e.Status & 0xF0) == 0x90 && e.Data2 == 0),
                $"Unbalanced note after audition transitions: {group.Key}.");
    }

    private static void DiscardCancelsQueuedAudition()
    {
        using var session = new WorkbenchSession();
        session.SetTempo(240);
        var original = session.Project;
        session.StartSection(repeat: true);
        session.PrepareGenerationAsync(session.SelectedSection.Outline with { Seed = 991 }).GetAwaiter().GetResult();
        session.AuditionCandidate(960);
        Check(session.AuditionStatus.Phase == AuditionPhase.Queued, "Audition was not queued.");
        session.DiscardGeneration();
        Thread.Sleep(350);
        Check(session.Candidate is null && session.Project == original && session.AuditionStatus.Phase == AuditionPhase.Idle,
            "Discarded candidate remained queued or started playing.");
        session.Stop();
    }

    private static void SupersedeOrSelectCancelsQueuedAudition()
    {
        var failed = new List<string>();
        foreach (var select in new[] { false, true })
        {
            using var session = new WorkbenchSession();
            var first = session.SelectedSectionId;
            var second = session.DuplicateSection(first);
            session.SelectSection(first);
            session.SetTempo(240);
            var original = session.Project;
            session.StartSection(first, true);
            session.PrepareGenerationAsync(session.SelectedSection.Outline with { Seed = 500 }).GetAwaiter().GetResult();
            session.AuditionCandidate(960);
            if (select) session.SelectSection(second);
            else session.PrepareGenerationAsync(session.SelectedSection.Outline with { Seed = 501 }).GetAwaiter().GetResult();
            Thread.Sleep(350);
            if (session.AuditionStatus.Phase != AuditionPhase.Idle || session.Project != original)
                failed.Add(select ? "selection" : "supersession");
            session.Stop();
        }
        Check(failed.Count == 0, $"Orphan audition launched after {string.Join(" and ", failed)}.");
    }

    private static void DiscardPlayingAuditionReturnsToAccepted()
    {
        using var session = new WorkbenchSession();
        session.SetTempo(240);
        var original = session.Project;
        session.StartSection(repeat: true);
        session.PrepareGenerationAsync(session.SelectedSection.Outline with { Seed = 710 }).GetAwaiter().GetResult();
        session.AuditionCandidate(240);
        WaitForAudition(session, AuditionPhase.Playing);
        session.DiscardGeneration();
        WaitForAudition(session, AuditionPhase.Idle);
        Check(session.Candidate is null && session.Project == original && session.Status().Running,
            "Discarding an active audition did not return to the accepted section.");
        session.Stop();
        Check(session.Takes[^1].Outputs.Count(e => e.Status == 0xFA) == 1, "Discarding audition restarted clock.");
    }
}
