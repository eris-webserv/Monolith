using System.Runtime.InteropServices;
using Robust.Shared.Analyzers;
using Spartan.Native;

namespace Spartan;

[Virtual]
public class SpartanException(int code, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Code { get; } = code;
}

public sealed class SpartanPanicException(string message) : SpartanException(-5, message);
public sealed class SpartanDeviceException(string message, Exception? inner) : SpartanException(-7, message, inner);

public sealed class Machine : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<uint, Attachment> _devices = [];
    private ulong _handle;
    private Exception? _callbackError;
    private bool _inCall;

    public Machine(byte[] bootrom, uint hartId = 0, uint pmpEntries = 16)
    {
        using var rom = bootrom.Slice();
        _handle = Check(Interop.spartan_create(rom,
            new MachineConfig { hart_id = hartId, pmp_entries = pmpEntries }));
    }

    private void RangeReady()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Use this machine on its creating thread.");
        ObjectDisposedException.ThrowIf(_handle == 0, this);
    }

    internal unsafe void ReadRange(ulong id, uint index, uint offset, Span<byte> output)
    {
        RangeReady();
        fixed (byte* pointer = output)
            Check(Interop.spartan_range_read(id, index, offset, SliceMutByte.From((IntPtr)pointer, (ulong)output.Length)));
    }

    internal unsafe void WriteRange(ulong id, uint index, uint offset, ReadOnlySpan<byte> input)
    {
        RangeReady();
        fixed (byte* pointer = input)
            Check(Interop.spartan_range_write(id, index, offset, SliceByte.From((IntPtr)pointer, (ulong)input.Length)));
    }

    private void Ready()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Use this machine on its creating thread.");
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (_inCall) throw new InvalidOperationException("Device callbacks cannot re-enter the machine.");
    }

    private ulong Check(CallResult result)
    {
        using (result)
        {
            if (result.code == 0) return result.data;
            var error = result.error.String;
            if (result.code == -5) throw new SpartanPanicException(error);
            if (result.code == -7)
            {
                var inner = _callbackError;
                _callbackError = null;
                throw new SpartanDeviceException(error, inner);
            }
            throw new SpartanException(result.code, error);
        }
    }

    private ulong Invoke(Func<CallResult> operation)
    {
        Ready();
        _inCall = true;
        try { return Check(operation()); }
        finally { _inCall = false; }
    }

    public MachineState State
    {
        get { MachineState state = default; Invoke(() => Interop.spartan_state(_handle, ref state)); return state; }
    }

    public RunState Run(uint stepBudget)
    {
        RunState run = default;
        Invoke(() => Interop.spartan_run(_handle, stepBudget, ref run));
        return run;
    }

    public RunState Step() => Run(1);
    public void Reset() => Invoke(() => Interop.spartan_reset(_handle));
    public void SetTime(ulong ticks) => Invoke(() => Interop.spartan_time(_handle, ticks));
    public void SetInterrupts(InterruptLines lines) => Invoke(() => Interop.spartan_interrupts(_handle, lines));
    public uint ReadCsr(uint address) => (uint)Invoke(() => Interop.spartan_csr_read(_handle, address));
    public void WriteCsr(uint address, uint data) => Invoke(() => Interop.spartan_csr_write(_handle, address, data));
    public uint GetPmpAccess(uint address, uint length = 1, uint mode = 1) =>
        (uint)Invoke(() => Interop.spartan_pmp_access(_handle, address, length, mode));

    public void ReadMemory(uint address, byte[] output)
    {
        using var buffer = output.SliceMut();
        Invoke(() => Interop.spartan_memory_read(_handle, address, buffer));
    }

    public void WriteMemory(uint address, byte[] input)
    {
        using var buffer = input.Slice();
        Invoke(() => Interop.spartan_memory_write(_handle, address, buffer));
    }

    public uint[] ReadRegisters()
    {
        var output = new uint[33];
        using var buffer = output.SliceMut();
        Invoke(() => Interop.spartan_registers_read(_handle, buffer));
        return output;
    }

    public void WriteRegisters(uint[] registers)
    {
        using var buffer = registers.Slice();
        Invoke(() => Interop.spartan_registers_write(_handle, buffer));
    }

    public void Attach(uint port, ISpartanDevice device)
    {
        Ready();
        ArgumentNullException.ThrowIfNull(device);
        if (_devices.ContainsKey(port)) throw new InvalidOperationException("Port already occupied.");
        var attachment = new Attachment(this, device);
        Invoke(() => Interop.spartan_device_attach(_handle, port,
            new DeviceDescriptor { id = device.Id, ring_words = device.RingWords, response_words = device.ResponseWords },
            attachment.Callbacks));
        _devices.Add(port, attachment);
        SetDeviceInterrupt(port, device.InterruptPending);
    }

    public void Detach(uint port)
    {
        Invoke(() => Interop.spartan_device_detach(_handle, port));
        _devices.Remove(port);
    }

    public DeviceInfo GetDeviceInfo(uint port)
    {
        DeviceInfo info = default;
        Invoke(() => Interop.spartan_device_info(_handle, port, ref info));
        return info;
    }

    public void ResetDevice(uint port) => Invoke(() => Interop.spartan_device_reset(_handle, port));
    public void SetDeviceInterrupt(uint port, bool asserted) =>
        Invoke(() => Interop.spartan_device_interrupt(_handle, port, asserted ? 1u : 0u));

    public void Dispose()
    {
        if (_handle == 0) return;
        Ready();
        _inCall = true;
        try
        {
            var result = Interop.spartan_destroy(_handle);
            if (result.code is 0 or -2 or -5 or -7)
            {
                _handle = 0;
                _devices.Clear();
            }
            Check(result);
        }
        finally { _inCall = false; }
    }

    private sealed class Attachment
    {
        private readonly Machine _machine;
        private readonly ISpartanDevice _device;
        private readonly Dictionary<ulong, DeviceRing> _rings = [];
        public DeviceCallbacks Callbacks { get; }

        public Attachment(Machine machine, ISpartanDevice device)
        {
            _machine = machine;
            _device = device;
            Callbacks = new DeviceCallbacks {
                dma = new DmaCallback(Dma), command = new CommandCallback(Command),
                rings = new RingCallback(Rings), reset = new ResetCallback(Reset)
            };
        }

        private CommandReply Reply(int code, uint words = 0) => new() {
            code = code, words = words, interrupt = _device.InterruptPending ? 1u : 0u
        };

        private CommandReply Failed(Exception error)
        {
            _machine._callbackError ??= error;
            return new CommandReply { code = -2, words = 0, interrupt = 0 };
        }

        private CommandReply Dma(uint operation, uint offset, uint length, SliceByte input, SliceMutByte output)
        {
            try
            {
                var destination = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(output.ReadOnlySpan), output.Count);
                bool success;
                switch (operation)
                {
                    case 0: success = _device.DmaRead(offset, destination); break;
                    case 1: success = _device.ValidateDmaWrite(offset, length); break;
                    case 2: success = _device.DmaWrite(offset, input.ReadOnlySpan); break;
                    default: return Reply(-1);
                }
                return Reply(success ? 0 : -1);
            }
            catch (Exception error) { return Failed(error); }
        }

        private CommandReply Reset()
        {
            try { _device.Reset(); return Reply(0); }
            catch (Exception error) { return Failed(error); }
        }

        private CommandReply Rings(uint operation, RingInfo info, SliceRangeInfo ranges, uint reason)
        {
            try
            {
                switch (operation)
                {
                    case 0:
                        var ring = new DeviceRing(_machine, info, ranges);
                        _rings.Add(ring.Id, ring);
                        if (!_device.RingAdded(ring)) return Reply(-1);
                        break;
                    case 1:
                        if (_rings.Remove(info.id, out var removed)) removed.Revoke();
                        _device.RingRemoved(info.id, (RingRemoval)reason);
                        break;
                    case 2:
                        _device.RingFault(info.id, (RingFailure)reason);
                        break;
                    default: return Reply(-1);
                }
                return Reply(0);
            }
            catch (Exception error) { return Failed(error); }
        }

        private CommandReply Command(ulong ring, SliceUint input, SliceMutUint output)
        {
            try
            {
                if (ring != 0) return Reply(_device.RingCommand(_rings[ring], input.ReadOnlySpan) ? 0 : -1);
                var destination = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(output.ReadOnlySpan), output.Count);
                var response = _device.MmioCommand(input.ReadOnlySpan, destination);
                if (response.Words < 0 || response.Words > output.Count)
                    throw new InvalidOperationException("Device response exceeds its buffer.");
                return Reply((int)response.Disposition, (uint)response.Words);
            }
            catch (Exception error) { return Failed(error); }
        }
    }
}
