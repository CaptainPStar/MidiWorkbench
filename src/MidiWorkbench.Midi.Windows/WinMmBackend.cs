using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Collections.Concurrent;
using MidiWorkbench.Engine;

namespace MidiWorkbench.Midi.Windows;

public static class WindowsMidi
{
    public static IMidiBackend? CreateBackend() => OperatingSystem.IsWindows() ? new WinMmBackend() : null;
    public static IHostWait CreateWait() => OperatingSystem.IsWindows() ? HighResolutionWait.Create() : new SleepWait();
}

// WinMM short messages: immediate send, no driver scheduling. IDs are name plus occurrence, since WinMM has no stable endpoint identity.
[SupportedOSPlatform("windows")]
public sealed class WinMmBackend : IMidiBackend
{
    private readonly Timer watcher;
    private string signature;
    private readonly ConcurrentDictionary<string, WeakReference<OutputPort>> openedOutputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WeakReference<InputPort>> openedInputs = new(StringComparer.Ordinal);
    public event Action? EndpointsChanged;
    public WinMmBackend()
    {
        signature = Signature();
        // Polled away from the transport worker; WinMM has no arrival/removal notification.
        watcher = new Timer(state =>
        {
            var endpoints = Endpoints();
            var connected = endpoints.Where(e => e.Output).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, reference) in openedOutputs)
            {
                if (!reference.TryGetTarget(out var port)) openedOutputs.TryRemove(id, out _);
                else if (!connected.Contains(id)) port.MarkDisconnected();
            }
            var connectedInputs = endpoints.Where(e => e.Input).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, reference) in openedInputs)
            {
                if (!reference.TryGetTarget(out var port)) openedInputs.TryRemove(id, out _);
                else if (!connectedInputs.Contains(id)) port.MarkDisconnected();
            }
            var next = string.Join("\n", endpoints.Select(e => e.Id));
            if (Interlocked.Exchange(ref signature, next) != next) EndpointsChanged?.Invoke();
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }
    private string Signature() => string.Join("\n", Endpoints().Select(e => e.Id));
    public IReadOnlyList<MidiEndpoint> Endpoints()
    {
        var result = new List<MidiEndpoint>();
        result.AddRange(Ids(Native.OutputNames()).Select(x => new MidiEndpoint("winmm-out:" + x.Id, x.Name, false, true)));
        result.AddRange(Ids(Native.InputNames()).Select(x => new MidiEndpoint("winmm-in:" + x.Id, x.Name, true, false)));
        return result;
    }
    private static IEnumerable<(string Id, string Name, int Index)> Ids(IReadOnlyList<string> names)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            seen.TryGetValue(names[i], out var n); seen[names[i]] = ++n;
            yield return ($"{names[i]}#{n}", n == 1 ? names[i] : $"{names[i]} ({n})", i);
        }
    }
    private static int Index(string endpointId, string prefix, IReadOnlyList<string> names)
    {
        if (!endpointId.StartsWith(prefix, StringComparison.Ordinal)) throw new MidiPortException($"Unknown endpoint '{endpointId}'.");
        var id = endpointId[prefix.Length..];
        foreach (var x in Ids(names)) if (x.Id == id) return x.Index;
        throw new MidiPortException($"'{endpointId}' is not connected.");
    }
    public IMidiOutputPort OpenOutput(string endpointId)
    {
        var port = new OutputPort(endpointId, Index(endpointId, "winmm-out:", Native.OutputNames()));
        openedOutputs[endpointId] = new(port);
        return port;
    }
    public IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received)
    {
        var port = new InputPort(endpointId, Index(endpointId, "winmm-in:", Native.InputNames()), received);
        openedInputs[endpointId] = new(port);
        return port;
    }
    public void Dispose() => watcher.Dispose();

    private sealed class OutputPort : IMidiOutputPort
    {
        private IntPtr handle;
        private volatile bool faulted;
        public string EndpointId { get; }
        public bool ScheduledSend => false;
        public bool Faulted => faulted;
        public void MarkDisconnected() => faulted = true;
        public OutputPort(string id, int index)
        {
            EndpointId = id;
            Native.Check(Native.midiOutOpen(out handle, (uint)index, IntPtr.Zero, IntPtr.Zero, 0), "open output");
        }
        public void Submit(TimedMidi m)
        {
            if (faulted) throw new MidiPortException("endpoint disconnected");
            var result = Native.midiOutShortMsg(handle, (uint)(m.Status | m.Data1 << 8 | m.Data2 << 16));
            if (result == 0) return;
            faulted = true;
            throw new MidiPortException(Native.Error(result));
        }
        public void Cancel(double after) { }
        public void Dispose()
        {
            faulted = true;
            var closing = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (closing != IntPtr.Zero) _ = Native.midiOutClose(closing);
        }
    }

    private sealed class InputPort : IMidiInputPort
    {
        private IntPtr handle;
        private readonly Native.MidiInProc callback;
        private readonly Action<TimedMidi> received;
        private volatile bool faulted;
        public string EndpointId { get; }
        public bool Faulted => faulted;
        public void MarkDisconnected() => faulted = true;
        public InputPort(string id, int index, Action<TimedMidi> received)
        {
            EndpointId = id; this.received = received;
            callback = OnMessage; // field keeps the delegate alive for the driver
            Native.Check(Native.midiInOpen(out handle, (uint)index, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, Native.CallbackFunction), "open input");
            try { Native.Check(Native.midiInStart(handle), "start input"); }
            catch { _ = Native.midiInClose(handle); handle = IntPtr.Zero; throw; }
        }
        // Driver thread: timestamp on arrival and enqueue only. Driver timestamps are not yet calibrated against the host clock.
        private void OnMessage(IntPtr h, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (faulted || message != Native.MimData) return;
            var data = (uint)param1.ToInt64();
            received(new(HostTime.Now, (byte)data, (byte)(data >> 8), (byte)(data >> 16)));
        }
        public void Dispose()
        {
            faulted = true;
            var closing = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (closing == IntPtr.Zero) return;
            _ = Native.midiInStop(closing); _ = Native.midiInReset(closing); _ = Native.midiInClose(closing);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class Native
{
    public const uint CallbackFunction = 0x30000;
    public const uint MimData = 0x3C3;
    public delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort Mid, Pid; public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Technology, Voices, Notes, ChannelMask; public uint Support;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort Mid, Pid; public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }

    [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiOutGetDevCapsW")] private static extern uint midiOutGetDevCaps(UIntPtr id, out MidiOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiInGetDevCapsW")] private static extern uint midiInGetDevCaps(UIntPtr id, out MidiInCaps caps, uint size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiOutGetErrorTextW")] private static extern uint midiOutGetErrorText(uint error, [Out] char[] text, uint size);
    [DllImport("winmm.dll")] public static extern uint midiOutOpen(out IntPtr handle, uint id, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint midiOutShortMsg(IntPtr handle, uint message);
    [DllImport("winmm.dll")] public static extern uint midiOutClose(IntPtr handle);
    [DllImport("winmm.dll")] public static extern uint midiInOpen(out IntPtr handle, uint id, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] public static extern uint midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] public static extern uint midiInReset(IntPtr handle);
    [DllImport("winmm.dll")] public static extern uint midiInClose(IntPtr handle);

    public static IReadOnlyList<string> OutputNames()
    {
        var count = midiOutGetNumDevs(); var names = new List<string>();
        for (uint i = 0; i < count; i++)
            names.Add(midiOutGetDevCaps(i, out var caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0 ? caps.Name : $"Output {i}");
        return names;
    }
    public static IReadOnlyList<string> InputNames()
    {
        var count = midiInGetNumDevs(); var names = new List<string>();
        for (uint i = 0; i < count; i++)
            names.Add(midiInGetDevCaps(i, out var caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0 ? caps.Name : $"Input {i}");
        return names;
    }
    public static string Error(uint code)
    {
        var text = new char[256];
        return midiOutGetErrorText(code, text, (uint)text.Length) == 0 ? new string(text).TrimEnd('\0') : $"WinMM error {code}";
    }
    public static void Check(uint code, string action)
    {
        if (code != 0) throw new MidiPortException($"Cannot {action}: {Error(code)}");
    }
}

// Waitable timer with sub-millisecond resolution (Windows 10 1803+); falls back to a 1 ms system timer period.
[SupportedOSPlatform("windows")]
internal sealed class HighResolutionWait : IHostWait
{
    private const uint HighResolution = 0x2, AllAccess = 0x1F0003, Infinite = 0xFFFFFFFF;
    private readonly IntPtr timer;
    private readonly bool period;
    private HighResolutionWait(IntPtr timer, bool period) { this.timer = timer; this.period = period; }
    public static IHostWait Create()
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, AllAccess);
        if (timer != IntPtr.Zero) return new HighResolutionWait(timer, false);
        _ = timeBeginPeriod(1);
        return new HighResolutionWait(IntPtr.Zero, true);
    }
    public void Wait(TimeSpan duration)
    {
        if (timer == IntPtr.Zero) { Thread.Sleep(duration); return; }
        var due = -duration.Ticks;
        if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) _ = WaitForSingleObject(timer, Infinite);
        else Thread.Sleep(duration);
    }
    public void Dispose()
    {
        if (timer != IntPtr.Zero) CloseHandle(timer);
        if (period) _ = timeEndPeriod(1);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
