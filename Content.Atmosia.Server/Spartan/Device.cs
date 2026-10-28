namespace Spartan;

public enum CommandDisposition { Fault = -1, Unhandled = 0, Response = 1 }

public readonly record struct DeviceResponse(CommandDisposition Disposition, int Words = 0)
{
    public static DeviceResponse Unhandled => new(CommandDisposition.Unhandled);
    public static DeviceResponse Fault => new(CommandDisposition.Fault);
    public static DeviceResponse Reply(int words) => new(CommandDisposition.Response, words);
}

public interface ISpartanDevice
{
    uint Id { get; }
    uint RingWords => 0;
    uint ResponseWords => 1024;
    bool InterruptPending => false;
    bool DmaRead(uint offset, Span<byte> destination);
    bool ValidateDmaWrite(uint offset, uint length);
    bool DmaWrite(uint offset, ReadOnlySpan<byte> source);
    DeviceResponse MmioCommand(ReadOnlySpan<uint> request, Span<uint> response) => DeviceResponse.Unhandled;
    bool RingCommand(DeviceRing ring, ReadOnlySpan<uint> entry) => false;
    bool RingAdded(DeviceRing ring) => true;
    void RingRemoved(ulong id, RingRemoval reason) { }
    void RingFault(ulong id, RingFailure reason) { }
    void Reset() { }
}

public abstract class StorageDevice : ISpartanDevice
{
    public abstract uint Id { get; }
    public virtual uint RingWords => 0;
    public virtual uint ResponseWords => 1024;
    public virtual bool InterruptPending => false;
    public byte[] Storage { get; }
    public bool Writable { get; }

    protected StorageDevice(byte[] storage, bool writable = true)
    {
        Storage = storage;
        Writable = writable;
    }

    protected bool Contains(uint offset, uint length) => (ulong)offset + length <= (ulong)Storage.Length;

    public bool DmaRead(uint offset, Span<byte> destination)
    {
        if (!Contains(offset, (uint)destination.Length)) return false;
        Storage.AsSpan((int)offset, destination.Length).CopyTo(destination);
        return true;
    }

    public bool ValidateDmaWrite(uint offset, uint length) => Writable && Contains(offset, length);

    public bool DmaWrite(uint offset, ReadOnlySpan<byte> source)
    {
        if (!ValidateDmaWrite(offset, (uint)source.Length)) return false;
        source.CopyTo(Storage.AsSpan((int)offset, source.Length));
        return true;
    }

    public virtual DeviceResponse MmioCommand(ReadOnlySpan<uint> request, Span<uint> response) => DeviceResponse.Unhandled;
    public virtual bool RingCommand(DeviceRing ring, ReadOnlySpan<uint> entry) => false;
    public virtual bool RingAdded(DeviceRing ring) => true;
    public virtual void RingRemoved(ulong id, RingRemoval reason) { }
    public virtual void RingFault(ulong id, RingFailure reason) { }
    public virtual void Reset() { }
}

public sealed class DiskDevice(byte[] image, bool writable = true) : StorageDevice(image, writable)
{
    public override uint Id => 0x0170e001;
}
