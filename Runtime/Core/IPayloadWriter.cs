using System;

namespace PersistenceKit
{
    /// <summary>
    /// Serializer-agnostic payload writer used by generated <c>WritePayload</c> methods.
    /// Each <see cref="ISerializerHandler"/> provides its own concrete implementation.
    /// </summary>
    /// <remarks>
    /// Primitive overloads exist to avoid boxing on the hot path. Implementations route
    /// the <c>encrypted</c> flag through the kit's encryptor.
    /// </remarks>
    public interface IPayloadWriter
    {
        void WriteString(string name, string value, bool encrypted);
        void WriteBool   (string name, bool value, bool encrypted);
        void WriteInt32  (string name, int value, bool encrypted);
        void WriteInt64  (string name, long value, bool encrypted);
        void WriteUInt32 (string name, uint value, bool encrypted);
        void WriteUInt64 (string name, ulong value, bool encrypted);
        void WriteSingle (string name, float value, bool encrypted);
        void WriteDouble (string name, double value, bool encrypted);
        void WriteBytes  (string name, byte[] value, bool encrypted);
        void WriteEnum<TEnum>(string name, TEnum value, bool encrypted) where TEnum : struct, Enum;

        /// <summary>Generic object path — used for collections and user types. Boxes value types.</summary>
        void WriteObject(string name, object value, Type declaredType, bool encrypted);
    }
}
