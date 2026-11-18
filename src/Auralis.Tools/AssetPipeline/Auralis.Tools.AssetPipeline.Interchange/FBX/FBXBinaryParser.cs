using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

internal static class FBXBinaryParser
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes(FBXHeader.BinarySignature);
    private delegate T[] SpanDecoder<T>(ReadOnlySpan<byte> bytes, int count);

    public static FBXDocument Parse(ReadOnlyMemory<byte> data, FBXParserOptions options)
    {
        var reader = new Reader(data.Span, options);
        return reader.ReadDocument();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly FBXParserOptions _options;
        private int _offset;
        private int _nodeCount;
        private uint _version;
        private int _recordWidth;

        public Reader(ReadOnlySpan<byte> data, FBXParserOptions options)
        {
            _data = data;
            _options = options;
            _offset = 0;
            _nodeCount = 0;
            _version = 0;
            _recordWidth = 13;
        }

        public FBXDocument ReadDocument()
        {
            if (_data.Length < Magic.Length + sizeof(uint) || !_data.StartsWith(Magic))
                throw Error("Invalid binary FBX signature");

            _offset = Magic.Length;
            _version = ReadUInt32(_data.Length);
            _recordWidth = _version >= 7500 ? 25 : 13;

            var roots = new List<FBXNode>();
            while (_offset < _data.Length)
            {
                if (IsNullRecord(_offset, _data.Length))
                {
                    _offset += _recordWidth;
                    break;
                }

                if (_data.Length - _offset < _recordWidth)
                    break;

                FBXNode? node = ReadNode(0, _data.Length);
                if (node is null) break;
                roots.Add(node);
            }

            return FBXDocument.Create(new FBXHeader(true, _version, Magic), roots);
        }

        private FBXNode? ReadNode(int depth, int parentEnd)
        {
            if (depth > _options.MaxDepth)
                throw Error("Maximum FBX node nesting depth exceeded");
            if (++_nodeCount > _options.MaxNodes)
                throw Error("Maximum FBX node count exceeded");
            if (_offset >= parentEnd || parentEnd > _data.Length)
                throw Error("Node starts outside its parent boundary");

            long endOffset;
            long propertyCount;
            long propertyLength;
            byte nameLength;

            if (_version >= 7500)
            {
                endOffset = checked((long)ReadUInt64(parentEnd));
                propertyCount = checked((long)ReadUInt64(parentEnd));
                propertyLength = checked((long)ReadUInt64(parentEnd));
                nameLength = ReadByte(parentEnd);
            }
            else
            {
                endOffset = ReadUInt32(parentEnd);
                propertyCount = ReadUInt32(parentEnd);
                propertyLength = ReadUInt32(parentEnd);
                nameLength = ReadByte(parentEnd);
            }

            if (endOffset == 0 && propertyCount == 0 && propertyLength == 0 && nameLength == 0)
                return null;
            if (endOffset <= _offset || endOffset > parentEnd)
                throw Error("Invalid FBX node end offset");
            if (propertyCount < 0 || propertyCount > _options.MaxPropertiesPerNode)
                throw Error("FBX node property count exceeds the configured limit");
            if (propertyLength < 0 || propertyLength > endOffset - _offset)
                throw Error("Invalid FBX property list length");
            if (nameLength == 0 || nameLength > endOffset - _offset)
                throw Error("Invalid FBX node name length");

            string name = ReadString(nameLength, checked((int)Math.Min(endOffset, parentEnd)));
            var node = new FBXNode(name);
            int propertyEnd = checked(_offset + (int)propertyLength);

            for (long i = 0; i < propertyCount; i++)
                node.AddProperty(ReadProperty(propertyEnd));

            if (_offset > propertyEnd)
                throw Error("FBX properties overran the declared property list");
            if (_offset < propertyEnd)
                _offset = propertyEnd;

            int nodeEnd = checked((int)endOffset);
            while (_offset < nodeEnd)
            {
                if (IsNullRecord(_offset, nodeEnd))
                {
                    _offset += _recordWidth;
                    break;
                }

                int before = _offset;
                FBXNode? child = ReadNode(depth + 1, nodeEnd);
                if (child is null) break;
                node.AddChild(child);
                if (_offset <= before)
                    throw Error("FBX parser did not advance while reading a child node");
            }

            if (_offset != nodeEnd)
                throw Error("FBX child nodes do not end at the declared node boundary");
            return node;
        }

        private object ReadProperty(int boundary)
        {
            byte type = ReadByte(boundary);
            switch (type)
            {
                case (byte)'Y': return ReadInt16(boundary);
                case (byte)'C': return ReadByte(boundary) != 0;
                case (byte)'I': return ReadInt32(boundary);
                case (byte)'F': return ReadSingle(boundary);
                case (byte)'D': return ReadDouble(boundary);
                case (byte)'L': return ReadInt64(boundary);
                case (byte)'S':
                {
                    uint length = ReadUInt32(boundary);
                    if (length > _options.MaxStringBytes || length > boundary - _offset)
                        throw Error("FBX string length exceeds the allowed boundary or limit");
                    return ReadString(checked((int)length), boundary);
                }
                case (byte)'R':
                {
                    uint length = ReadUInt32(boundary);
                    if (length > boundary - _offset || length > int.MaxValue)
                        throw Error("Invalid FBX raw-data length");
                    byte[] bytes = _data.Slice(_offset, (int)length).ToArray();
                    _offset += (int)length;
                    return bytes;
                }
                case (byte)'f': return ReadArray(boundary, 4, DecodeSingleArray);
                case (byte)'d': return ReadArray(boundary, 8, DecodeDoubleArray);
                case (byte)'i': return ReadArray(boundary, 4, DecodeInt32Array);
                case (byte)'l': return ReadArray(boundary, 8, DecodeInt64Array);
                case (byte)'b': return ReadArray(boundary, 1, DecodeBooleanArray);
                case (byte)'c': return ReadArray(boundary, 1, DecodeSByteArray);
                default: throw Error($"Unsupported FBX property type '{(char)type}'");
            }
        }

        private T[] ReadArray<T>(int boundary, int elementSize, SpanDecoder<T> decoder)
        {
            uint rawCount = ReadUInt32(boundary);
            uint encoding = ReadUInt32(boundary);
            uint encodedLength = ReadUInt32(boundary);
            if (rawCount > _options.MaxArrayElements)
                throw Error("FBX array element count exceeds the configured limit");

            long expectedLength = (long)rawCount * elementSize;
            if (expectedLength > int.MaxValue || encodedLength > boundary - _offset)
                throw Error("Invalid FBX array byte length");

            ReadOnlySpan<byte> encoded = _data.Slice(_offset, (int)encodedLength);
            _offset += (int)encodedLength;

            if (encoding == 0)
            {
                if (encodedLength != expectedLength)
                    throw Error("Uncompressed FBX array length does not match its element count");
                return decoder(encoded, (int)rawCount);
            }
            if (encoding != 1)
                throw Error($"Unsupported FBX array compression mode {encoding}");

            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)expectedLength));
            try
            {
                using var input = new MemoryStream(encoded.ToArray(), writable: false);
                using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
                int total = 0;
                while (total < expectedLength)
                {
                    int read = inflater.Read(rented, total, (int)expectedLength - total);
                    if (read == 0) break;
                    total += read;
                }
                if (total != expectedLength)
                    throw Error("Decompressed FBX array has an unexpected length");
                if (inflater.ReadByte() != -1)
                    throw Error("Decompressed FBX array contains more data than declared");
                return decoder(rented.AsSpan(0, (int)expectedLength), (int)rawCount);
            }
            catch (InvalidDataException ex)
            {
                throw new FBXFormatException("Invalid compressed FBX array", _offset, ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static float[] DecodeSingleArray(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4)));
            return result;
        }

        private static double[] DecodeDoubleArray(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new double[count];
            for (int i = 0; i < count; i++)
                result[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(i * 8, 8)));
            return result;
        }

        private static int[] DecodeInt32Array(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
            return result;
        }

        private static long[] DecodeInt64Array(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new long[count];
            for (int i = 0; i < count; i++)
                result[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(i * 8, 8));
            return result;
        }

        private static bool[] DecodeBooleanArray(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new bool[count];
            for (int i = 0; i < count; i++) result[i] = bytes[i] != 0;
            return result;
        }

        private static sbyte[] DecodeSByteArray(ReadOnlySpan<byte> bytes, int count)
        {
            var result = new sbyte[count];
            for (int i = 0; i < count; i++) result[i] = unchecked((sbyte)bytes[i]);
            return result;
        }

        private byte ReadByte(int boundary)
        {
            EnsureAvailable(1, boundary);
            return _data[_offset++];
        }

        private short ReadInt16(int boundary)
        {
            EnsureAvailable(2, boundary);
            short value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        private int ReadInt32(int boundary)
        {
            EnsureAvailable(4, boundary);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        private uint ReadUInt32(int boundary)
        {
            EnsureAvailable(4, boundary);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        private long ReadInt64(int boundary)
        {
            EnsureAvailable(8, boundary);
            long value = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_offset, 8));
            _offset += 8;
            return value;
        }

        private ulong ReadUInt64(int boundary)
        {
            EnsureAvailable(8, boundary);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_offset, 8));
            _offset += 8;
            return value;
        }

        private float ReadSingle(int boundary) => BitConverter.Int32BitsToSingle(ReadInt32(boundary));
        private double ReadDouble(int boundary) => BitConverter.Int64BitsToDouble(ReadInt64(boundary));

        private string ReadString(int length, int boundary)
        {
            if (length < 0 || length > _options.MaxStringBytes)
                throw Error("FBX string exceeds the configured limit");
            EnsureAvailable(length, boundary);
            try
            {
                string value = new UTF8Encoding(false, true).GetString(_data.Slice(_offset, length));
                _offset += length;
                return value;
            }
            catch (DecoderFallbackException ex)
            {
                throw new FBXFormatException("FBX string is not valid UTF-8", _offset, ex);
            }
        }

        private void EnsureAvailable(int count, int boundary)
        {
            if (count < 0 || _offset > boundary - count || _offset > _data.Length - count)
                throw Error("Unexpected end of FBX data");
        }

        private bool IsNullRecord(int offset, int boundary)
        {
            if (offset < 0 || boundary - offset < _recordWidth)
                return false;
            for (int i = 0; i < _recordWidth; i++)
                if (_data[offset + i] != 0) return false;
            return true;
        }

        private FBXFormatException Error(string message) => new(message, _offset);
    }
}
