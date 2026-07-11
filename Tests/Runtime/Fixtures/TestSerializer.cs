using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PersistenceKit.Tests.Fixtures
{
    /// <summary>
    /// Tiny serializer used by phase-2 tests in lieu of the real Newtonsoft handler. Writes a
    /// length-prefixed list of <c>(name, type-tag, value)</c> records. Forward compatible: an
    /// unknown name in the payload is silently skipped on read.
    /// </summary>
    public sealed class TestSerializer : ISerializerHandler
    {
        public ReadOnlyMemory<byte> Serialize(IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                var w = new Writer(bw, encryptor);
                state.WritePayload(target, w);
                bw.Write((byte)0);  // sentinel — end of records
            }
            return ms.ToArray();
        }

        public void Deserialize(ReadOnlySpan<byte> payload, IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            // ReadOnlySpan can't be used inside MemoryStream; copy.
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var dict = new Dictionary<string, (byte tag, object value)>(StringComparer.Ordinal);
            while (true)
            {
                var tag = br.ReadByte();
                if (tag == 0) break;
                var name = br.ReadString();
                object v = tag switch
                {
                    1 => (object)br.ReadString(),
                    2 => br.ReadInt32(),
                    3 => br.ReadInt64(),
                    4 => br.ReadSingle(),
                    5 => br.ReadDouble(),
                    6 => br.ReadBoolean(),
                    7 => br.ReadBytes(br.ReadInt32()),
                    _ => throw new InvalidDataException($"Unknown tag {tag}"),
                };
                dict[name] = (tag, v);
            }
            state.ReadPayload(target, new Reader(dict, encryptor));
        }

        private sealed class Writer : IPayloadWriter
        {
            private readonly BinaryWriter _bw;
            private readonly IEncryptor _enc;

            public Writer(BinaryWriter bw, IEncryptor enc) { _bw = bw; _enc = enc; }

            public void WriteString(string name, string value, bool encrypted)
            {
                _bw.Write((byte)1);
                _bw.Write(name);
                _bw.Write(encrypted ? _enc.Encrypt(Encoding.UTF8.GetBytes(value ?? string.Empty), "default") : (value ?? string.Empty));
            }
            public void WriteBool   (string name, bool value, bool encrypted) { Tag(name, 6); _bw.Write(value); }
            public void WriteInt32  (string name, int value, bool encrypted)  { Tag(name, 2); _bw.Write(value); }
            public void WriteInt64  (string name, long value, bool encrypted) { Tag(name, 3); _bw.Write(value); }
            public void WriteUInt32 (string name, uint value, bool encrypted) { Tag(name, 2); _bw.Write(unchecked((int)value)); }
            public void WriteUInt64 (string name, ulong value, bool encrypted){ Tag(name, 3); _bw.Write(unchecked((long)value)); }
            public void WriteSingle (string name, float value, bool encrypted){ Tag(name, 4); _bw.Write(value); }
            public void WriteDouble (string name, double value, bool encrypted){ Tag(name, 5); _bw.Write(value); }
            public void WriteBytes  (string name, byte[] value, bool encrypted)
            {
                Tag(name, 7);
                _bw.Write(value?.Length ?? 0);
                if (value != null) _bw.Write(value);
            }
            public void WriteEnum<TEnum>(string name, TEnum value, bool encrypted) where TEnum : struct, Enum
                => WriteInt32(name, Convert.ToInt32(value), encrypted);
            public void WriteObject(string name, object value, Type declaredType, bool encrypted)
                => WriteString(name, value?.ToString() ?? string.Empty, encrypted);

            private void Tag(string name, byte tag) { _bw.Write(tag); _bw.Write(name); }
        }

        private sealed class Reader : IPayloadReader
        {
            private readonly Dictionary<string, (byte tag, object value)> _dict;
            private readonly IEncryptor _enc;

            public Reader(Dictionary<string, (byte tag, object value)> dict, IEncryptor enc) { _dict = dict; _enc = enc; }

            public bool ReadString(string name, bool encrypted, out string value)
            {
                if (_dict.TryGetValue(name, out var e) && e.tag == 1)
                {
                    var raw = (string)e.value;
                    value = encrypted ? Encoding.UTF8.GetString(_enc.Decrypt(raw, "default")) : raw;
                    return true;
                }
                value = default;
                return false;
            }
            public bool ReadBool   (string name, bool encrypted, out bool value)   => Get(name, 6, out value);
            public bool ReadInt32  (string name, bool encrypted, out int value)    => Get(name, 2, out value);
            public bool ReadInt64  (string name, bool encrypted, out long value)   => Get(name, 3, out value);
            public bool ReadUInt32 (string name, bool encrypted, out uint value)
            {
                if (Get<int>(name, 2, out var v)) { value = unchecked((uint)v); return true; }
                value = default; return false;
            }
            public bool ReadUInt64 (string name, bool encrypted, out ulong value)
            {
                if (Get<long>(name, 3, out var v)) { value = unchecked((ulong)v); return true; }
                value = default; return false;
            }
            public bool ReadSingle (string name, bool encrypted, out float value)  => Get(name, 4, out value);
            public bool ReadDouble (string name, bool encrypted, out double value) => Get(name, 5, out value);
            public bool ReadBytes  (string name, bool encrypted, out byte[] value) => Get(name, 7, out value);
            public bool ReadEnum<TEnum>(string name, bool encrypted, out TEnum value) where TEnum : struct, Enum
            {
                if (Get<int>(name, 2, out var v)) { value = (TEnum)Enum.ToObject(typeof(TEnum), v); return true; }
                value = default; return false;
            }
            public bool ReadObject (string name, Type declaredType, bool encrypted, out object value)
            {
                if (ReadString(name, encrypted, out var s)) { value = s; return true; }
                value = null; return false;
            }

            private bool Get<T>(string name, byte expectedTag, out T value)
            {
                if (_dict.TryGetValue(name, out var e) && e.tag == expectedTag)
                {
                    value = (T)e.value;
                    return true;
                }
                value = default;
                return false;
            }
        }
    }
}
