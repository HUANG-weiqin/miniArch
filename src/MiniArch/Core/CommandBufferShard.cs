namespace MiniArch.Core;

internal sealed class CommandBufferShard
{
    private const int InitialDataCapacity = 256;

    public CommandBufferShard(int order)
    {
        Order = order;
        _data = new byte[InitialDataCapacity];
    }

    public int Order { get; }

    public List<Entity> Creates { get; } = [];

    public List<RecordedHierarchyCommand> HierarchyCommands { get; } = [];

    public List<RecordedRawCommand> Adds { get; } = [];

    public List<RecordedRawCommand> Sets { get; } = [];

    public List<RecordedRemoveCommand> Removes { get; } = [];

    public List<Entity> Destroys { get; } = [];

    private byte[] _data;
    private int _dataLength;

    public byte[] Data => _data;

    public int DataLength => _dataLength;

    public int AllocateData(int size)
    {
        if (_dataLength + size > _data.Length)
        {
            var newCapacity = _data.Length;
            while (newCapacity < _dataLength + size)
            {
                newCapacity *= 2;
            }

            var newData = new byte[newCapacity];
            Array.Copy(_data, newData, _dataLength);
            _data = newData;
        }

        var offset = _dataLength;
        _dataLength += size;
        return offset;
    }

    public void Clear()
    {
        Creates.Clear();
        HierarchyCommands.Clear();
        Adds.Clear();
        Sets.Clear();
        Removes.Clear();
        Destroys.Clear();
        _dataLength = 0;
    }
}
