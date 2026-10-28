using System.Collections.ObjectModel;
using Spartan.Native;

namespace Spartan;

[Flags]
public enum DmaAccess : uint { Read = 1, Write = 2 }
public enum RingRemoval : uint { Replaced = 1, Removed = 2, Reset = 3, Detached = 4, Destroyed = 5, Broken = 6 }
public enum RingFailure : uint { Overrun = 1, BadCommand = 2, DmaError = 3 }

public sealed class DeviceRing
{
    public ulong Id { get; }
    public uint Slot { get; }
    public uint Entries { get; }
    public uint EntryWords { get; }
    public ReadOnlyCollection<DeviceMemoryRange> Ranges { get; }
    public bool IsValid { get; private set; } = true;

    internal DeviceRing(Machine machine, RingInfo info, SliceRangeInfo ranges)
    {
        Id = info.id;
        Slot = info.slot;
        Entries = info.entries;
        EntryWords = info.words;
        var result = new DeviceMemoryRange[ranges.Count];
        for (var i = 0; i < result.Length; ++i)
            result[i] = new DeviceMemoryRange(machine, this, (uint)i, ranges[i]);
        Ranges = Array.AsReadOnly(result);
    }

    internal void Revoke() => IsValid = false;
}

public sealed class DeviceMemoryRange
{
    private readonly Machine _machine;
    private readonly DeviceRing _ring;
    private readonly uint _index;
    public uint Address { get; }
    public uint Length { get; }
    public DmaAccess Access { get; }

    internal DeviceMemoryRange(Machine machine, DeviceRing ring, uint index, RangeInfo info)
    {
        _machine = machine;
        _ring = ring;
        _index = index;
        Address = info.address;
        Length = info.length;
        Access = (DmaAccess)info.access;
    }

    public void Read(uint offset, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(!_ring.IsValid, _ring);
        _machine.ReadRange(_ring.Id, _index, offset, output);
    }

    public void Write(uint offset, ReadOnlySpan<byte> input)
    {
        ObjectDisposedException.ThrowIf(!_ring.IsValid, _ring);
        _machine.WriteRange(_ring.Id, _index, offset, input);
    }
}
