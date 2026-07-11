#if PERSISTENCEKIT_NEWTONSOFT
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PersistenceKit.Serializers
{
    /// <summary>
    /// JSON serializer handler backed by Newtonsoft.Json. Writes via
    /// <see cref="JsonTextWriter"/> and reads via <see cref="JObject"/> (so missing fields
    /// are tolerated for forward compatibility). Encrypted leaves go through
    /// <see cref="IEncryptor"/> as base64-bearing string tokens.
    /// </summary>
    public sealed class NewtonsoftJsonHandler : ISerializerHandler
    {
        private readonly JsonSerializer _serializer;
        private readonly Formatting _formatting;

        public NewtonsoftJsonHandler(bool indent = false, JsonSerializerSettings settings = null)
        {
            _formatting = indent ? Formatting.Indented : Formatting.None;
            _serializer = settings != null ? JsonSerializer.Create(settings) : JsonSerializer.CreateDefault();
        }

        public ReadOnlyMemory<byte> Serialize(IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            using var ms = new MemoryStream();
            using (var sw = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true))
            using (var jw = new JsonTextWriter(sw) { Formatting = _formatting, CloseOutput = false })
            {
                jw.WriteStartObject();
                state.WritePayload(target, new NewtonsoftPayloadWriter(jw, _serializer, encryptor));
                jw.WriteEndObject();
                jw.Flush();
                sw.Flush();
            }
            return ms.ToArray();
        }

        public void Deserialize(ReadOnlySpan<byte> payload, IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            // ReadOnlySpan can't go directly into a Newtonsoft JsonTextReader, but we can
            // parse from a string. Newtonsoft will only allocate the JToken graph once.
            var json = Encoding.UTF8.GetString(payload);
            var root = JObject.Parse(json);
            state.ReadPayload(target, new NewtonsoftPayloadReader(root, _serializer, encryptor));
        }

        private sealed class NewtonsoftPayloadWriter : IPayloadWriter
        {
            private readonly JsonWriter _w;
            private readonly JsonSerializer _ser;
            private readonly IEncryptor _enc;

            public NewtonsoftPayloadWriter(JsonWriter w, JsonSerializer ser, IEncryptor enc)
            { _w = w; _ser = ser; _enc = enc; }

            public void WriteString(string name, string value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted)
                {
                    var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                    _w.WriteValue(_enc.Encrypt(bytes, "default"));
                }
                else _w.WriteValue(value);
            }

            public void WriteBool(string name, bool value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteInt32(string name, int value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteInt64(string name, long value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteUInt32(string name, uint value, bool encrypted)
                => WriteInt64(name, value, encrypted);

            public void WriteUInt64(string name, ulong value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteSingle(string name, float value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteDouble(string name, double value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(value), "default"));
                else _w.WriteValue(value);
            }

            public void WriteBytes(string name, byte[] value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (value == null) { _w.WriteNull(); return; }
                if (encrypted) _w.WriteValue(_enc.Encrypt(value, "default"));
                else _w.WriteValue(Convert.ToBase64String(value));  // keep payload text-safe even unencrypted
            }

            public void WriteEnum<TEnum>(string name, TEnum value, bool encrypted) where TEnum : struct, Enum
            {
                _w.WritePropertyName(name);
                if (encrypted)
                {
                    _w.WriteValue(_enc.Encrypt(BitConverter.GetBytes(Convert.ToInt32(value)), "default"));
                }
                else
                {
                    // Stored as the enum's name to keep payloads diffable.
                    _w.WriteValue(value.ToString());
                }
            }

            public void WriteObject(string name, object value, Type declaredType, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (value == null) { _w.WriteNull(); return; }
                if (encrypted)
                {
                    using var ms = new MemoryStream();
                    using (var sw = new StreamWriter(ms, new UTF8Encoding(false), 256, leaveOpen: true))
                    using (var jw = new JsonTextWriter(sw) { CloseOutput = false })
                    {
                        _ser.Serialize(jw, value, declaredType);
                    }
                    _w.WriteValue(_enc.Encrypt(ms.ToArray(), "default"));
                }
                else
                {
                    _ser.Serialize(_w, value, declaredType);
                }
            }
        }

        private sealed class NewtonsoftPayloadReader : IPayloadReader
        {
            private readonly JObject _root;
            private readonly JsonSerializer _ser;
            private readonly IEncryptor _enc;

            public NewtonsoftPayloadReader(JObject root, JsonSerializer ser, IEncryptor enc)
            { _root = root; _ser = ser; _enc = enc; }

            public bool ReadString(string name, bool encrypted, out string value)
            {
                if (!TryGet(name, out var t) || t.Type == JTokenType.Null) { value = default; return false; }
                if (encrypted) { value = Encoding.UTF8.GetString(_enc.Decrypt((string)t, "default")); return true; }
                value = (string)t; return true;
            }

            public bool ReadBool(string name, bool encrypted, out bool value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToBoolean(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (bool)t; return true;
            }

            public bool ReadInt32(string name, bool encrypted, out int value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToInt32(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (int)t; return true;
            }

            public bool ReadInt64(string name, bool encrypted, out long value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToInt64(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (long)t; return true;
            }

            public bool ReadUInt32(string name, bool encrypted, out uint value)
            {
                if (ReadInt64(name, encrypted, out var v)) { value = unchecked((uint)v); return true; }
                value = default; return false;
            }

            public bool ReadUInt64(string name, bool encrypted, out ulong value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToUInt64(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (ulong)t; return true;
            }

            public bool ReadSingle(string name, bool encrypted, out float value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToSingle(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (float)t; return true;
            }

            public bool ReadDouble(string name, bool encrypted, out double value)
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted) { value = BitConverter.ToDouble(_enc.Decrypt((string)t, "default"), 0); return true; }
                value = (double)t; return true;
            }

            public bool ReadBytes(string name, bool encrypted, out byte[] value)
            {
                if (!TryGet(name, out var t) || t.Type == JTokenType.Null) { value = default; return false; }
                value = encrypted ? _enc.Decrypt((string)t, "default") : Convert.FromBase64String((string)t);
                return true;
            }

            public bool ReadEnum<TEnum>(string name, bool encrypted, out TEnum value) where TEnum : struct, Enum
            {
                if (!TryGet(name, out var t)) { value = default; return false; }
                if (encrypted)
                {
                    var i = BitConverter.ToInt32(_enc.Decrypt((string)t, "default"), 0);
                    value = (TEnum)Enum.ToObject(typeof(TEnum), i);
                    return true;
                }
                if (t.Type == JTokenType.String) return Enum.TryParse((string)t, out value);
                if (t.Type == JTokenType.Integer) { value = (TEnum)Enum.ToObject(typeof(TEnum), (int)t); return true; }
                value = default; return false;
            }

            public bool ReadObject(string name, Type declaredType, bool encrypted, out object value)
            {
                if (!TryGet(name, out var t) || t.Type == JTokenType.Null) { value = null; return false; }
                if (encrypted)
                {
                    var bytes = _enc.Decrypt((string)t, "default");
                    var s = Encoding.UTF8.GetString(bytes);
                    value = JsonConvert.DeserializeObject(s, declaredType);
                    return true;
                }
                value = t.ToObject(declaredType, _ser);
                return true;
            }

            private bool TryGet(string name, out JToken token) => _root.TryGetValue(name, StringComparison.Ordinal, out token);
        }
    }
}
#endif
