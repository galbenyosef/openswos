using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenSwos.Tools.AdfExtract;

public enum FileSystem : byte
{
    OfsStandard = 0,
    FfsStandard = 1,
    OfsIntl = 2,
    FfsIntl = 3,
    OfsDirCache = 4,
    FfsDirCache = 5,
}

public sealed class AdfDisk
{
    public const int BlockSize = 512;
    public const int DdBlockCount = 1760;
    public const int DdRootBlock = 880;

    private readonly byte[] _data;
    public string VolumeName { get; }
    public FileSystem FileSystem { get; }

    public bool IsFfs => ((byte)FileSystem & 1) == 1;

    private AdfDisk(byte[] data, string volume, FileSystem fs)
    {
        _data = data;
        VolumeName = volume;
        FileSystem = fs;
    }

    public static AdfDisk Open(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int minSize = DdBlockCount * BlockSize;
        if (data.Length < minSize)
            throw new InvalidDataException($"expected at least {minSize} bytes (DD floppy = 901120), got {data.Length}");

        if (data[0] != (byte)'D' || data[1] != (byte)'O' || data[2] != (byte)'S')
            throw new InvalidDataException("not an AmigaDOS disk (missing 'DOS' signature at offset 0)");

        var fs = (FileSystem)data[3];

        // Root block: name at offset (BlockSize-80) length-prefixed, max 30 bytes
        var root = data.AsSpan(DdRootBlock * BlockSize, BlockSize);
        int nameLen = Math.Min((int)root[BlockSize - 80], 30);
        string volume = Encoding.Latin1.GetString(root.Slice(BlockSize - 79, nameLen));

        return new AdfDisk(data, volume, fs);
    }

    private uint ReadU32(int block, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(block * BlockSize + offset, 4));

    private int ReadI32(int block, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(block * BlockSize + offset, 4));

    public IEnumerable<AdfEntry> Walk() => WalkDir(DdRootBlock, parentPath: "");

    // Walk a directory header (root or subdir). Both use a 72-entry hash table at offset 24,
    // with each non-zero slot pointing to a header block, hash-chained via offset BlockSize-16.
    private IEnumerable<AdfEntry> WalkDir(int dirBlock, string parentPath)
    {
        const int HashTableOffset = 24;
        const int HashTableSize = 72;
        var visited = new HashSet<int>();

        for (int i = 0; i < HashTableSize; i++)
        {
            int chain = ReadI32(dirBlock, HashTableOffset + i * 4);
            while (chain > 0 && visited.Add(chain))
            {
                var entry = ReadHeader(chain, parentPath);
                yield return entry;
                if (entry.IsDirectory)
                {
                    foreach (var sub in WalkDir(chain, entry.FullPath))
                        yield return sub;
                }
                chain = entry.NextChain;
            }
        }
    }

    private AdfEntry ReadHeader(int block, string parentPath)
    {
        int secType = ReadI32(block, BlockSize - 4);
        int nameLen = Math.Min((int)_data[block * BlockSize + BlockSize - 80], 30);
        string name = Encoding.Latin1.GetString(_data.AsSpan(block * BlockSize + BlockSize - 79, nameLen));
        int nextChain = ReadI32(block, BlockSize - 16);
        uint byteSize = secType == -3 ? ReadU32(block, BlockSize - 188) : 0;
        string fullPath = parentPath.Length == 0 ? name : $"{parentPath}/{name}";

        return new AdfEntry
        {
            HeaderBlock = block,
            FullPath = fullPath,
            Name = name,
            IsDirectory = secType == 2,
            IsFile = secType == -3,
            Size = byteSize,
            NextChain = nextChain,
        };
    }

    public byte[] ReadFile(AdfEntry entry)
    {
        if (!entry.IsFile)
            throw new InvalidOperationException($"not a file: {entry.FullPath}");

        var result = new byte[entry.Size];
        int pos = 0;

        // Collect data block numbers across header + extension blocks.
        // In each header/extension: high_seq at offset 8 = count of valid entries in data_blocks[72].
        // data_blocks[72] at offset 24, stored in REVERSE order (index 71 = first data block).
        // extension pointer at offset BlockSize-8.
        var dataBlocks = new List<int>(capacity: 64);
        int currentHeader = entry.HeaderBlock;
        var visitedHeaders = new HashSet<int>();
        while (currentHeader > 0 && visitedHeaders.Add(currentHeader))
        {
            int highSeq = (int)ReadU32(currentHeader, 8);
            if (highSeq > 72) highSeq = 72;
            for (int i = 71; i >= 72 - highSeq; i--)
            {
                int blk = ReadI32(currentHeader, 24 + i * 4);
                if (blk > 0) dataBlocks.Add(blk);
            }
            currentHeader = ReadI32(currentHeader, BlockSize - 8);
        }

        bool ffs = IsFfs;
        foreach (int blk in dataBlocks)
        {
            int remaining = (int)(entry.Size - pos);
            if (remaining <= 0) break;

            if (ffs)
            {
                int copy = Math.Min(BlockSize, remaining);
                Array.Copy(_data, blk * BlockSize, result, pos, copy);
                pos += copy;
            }
            else
            {
                // OFS data block: type(4) header_key(4) seq_num(4) data_size(4) next_data(4) checksum(4) data(488)
                int dataSize = (int)ReadU32(blk, 12);
                if (dataSize > 488) dataSize = 488;
                int copy = Math.Min(dataSize, remaining);
                Array.Copy(_data, blk * BlockSize + 24, result, pos, copy);
                pos += copy;
            }
        }

        if (pos != entry.Size)
            Console.Error.WriteLine($"warning: {entry.FullPath} read {pos} of {entry.Size} expected bytes");

        return result;
    }
}

public sealed record AdfEntry
{
    public int HeaderBlock { get; init; }
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsFile { get; init; }
    public uint Size { get; init; }
    public int NextChain { get; init; }
}
