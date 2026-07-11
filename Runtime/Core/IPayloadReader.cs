using System;

namespace PersistenceKit
{
    /// <summary>
    /// Serializer-agnostic payload reader paired with <see cref="IPayloadWriter"/>. Each
    /// method returns <c>true</c> when the named field is present in the payload — generated
    /// <c>ReadPayload</c> code uses this to support forward compatibility (extra/missing fields
    /// are tolerated).
    /// </summary>
    public interface IPayloadReader
    {
        bool ReadString(string name, bool encrypted, out string value);
        bool ReadBool   (string name, bool encrypted, out bool value);
        bool ReadInt32  (string name, bool encrypted, out int value);
        bool ReadInt64  (string name, bool encrypted, out long value);
        bool ReadUInt32 (string name, bool encrypted, out uint value);
        bool ReadUInt64 (string name, bool encrypted, out ulong value);
        bool ReadSingle (string name, bool encrypted, out float value);
        bool ReadDouble (string name, bool encrypted, out double value);
        bool ReadBytes  (string name, bool encrypted, out byte[] value);
        bool ReadEnum<TEnum>(string name, bool encrypted, out TEnum value) where TEnum : struct, Enum;
        bool ReadObject (string name, Type declaredType, bool encrypted, out object value);
    }
}
