using MidiWorkbench.Engine;

// Probe-only, bounded, single-writer instrumentation. Analysis and file I/O happen after Stop.
internal readonly record struct DispatchSample(TimedMidi Message, double Entered, double Returned);
internal sealed class MeasuredOutput(IMidiOutputPort port, int capacity) : IMidiOutputPort
{
    private readonly DispatchSample[] samples = new DispatchSample[capacity];
    private int count;
    public string EndpointId => port.EndpointId;
    public bool ScheduledSend => port.ScheduledSend;
    public bool Faulted => port.Faulted;
    public DispatchSample[] Snapshot() => samples.AsSpan(0, count).ToArray();
    public void Submit(TimedMidi message)
    {
        var entered = HostTime.Now;
        port.Submit(message);
        var returned = HostTime.Now;
        if (count >= samples.Length) throw new MidiPortException("Probe dispatch trace overflow");
        samples[count++] = new(message, entered, returned);
    }
    public void Cancel(double after) => port.Cancel(after);
    public void Dispose() => port.Dispose();
}
