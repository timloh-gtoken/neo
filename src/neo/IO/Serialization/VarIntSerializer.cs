using System;
using System.Buffers.Binary;
using System.IO;

namespace Neo.IO.Serialization
{
    public class VarIntSerializer<T> : Serializer<T> where T : unmanaged
    {
        public unsafe override T Deserialize(BinaryReader reader, SerializedAttribute attribute)
        {
            long max = attribute?.Max >= 0 ? attribute.Max : long.MaxValue;
            ulong value = reader.ReadVarInt((ulong)max);
            return *(T*)&value;
        }

        public unsafe override void Serialize(BinaryWriter writer, T value)
        {
            ReadOnlySpan<byte> buffer = new ReadOnlySpan<byte>(&value, sizeof(T));
            long number = buffer.Length switch
            {
                1 => buffer[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(buffer),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
                8 => BinaryPrimitives.ReadInt64LittleEndian(buffer),
                _ => throw new NotSupportedException()
            };
            writer.WriteVarInt(number);
        }
    }
}
