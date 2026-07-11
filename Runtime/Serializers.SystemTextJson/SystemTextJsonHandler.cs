// Enable by adding PERSISTENCEKIT_STJ to Project Settings → Player → Scripting Define
// Symbols. Requires System.Text.Json on the runtime — Unity 6's Mono ships it; trimmed
// IL2CPP builds may need a link.xml or the assembly preserved.
#if PERSISTENCEKIT_STJ
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PersistenceKit.Serializers
{
    /// <summary>
    /// JSON serializer handler backed by <see cref="System.Text.Json"/>. Demonstrates that
    /// the kit's pluggable serializer abstraction supports more than one implementation —
    /// drop in any additional handler by implementing <see cref="ISerializerHandler"/>.
    /// </summary>
    public sealed class SystemTextJsonHandler : ISerializerHandler
    {
        private readonly JsonSerializerOptions _options;
        private readonly bool _indent;

        public SystemTextJsonHandler(bool indent = false, JsonSerializerOptions options = null)
        {
            _indent = indent;
            _options = options ?? new JsonSerializerOptions { IncludeFields = true };
        }

        public ReadOnlyMemory<byte> Serialize(IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = _indent }))
            {
                w.WriteStartObject();
                state.WritePayload(target, new STJWriter(w, _options, encryptor));
                w.WriteEndObject();
            }
            return buffer.WrittenMemory;
        }

        public void Deserialize(ReadOnlySpan<byte> payload, IPersistentState state, PersistTarget target, IEncryptor encryptor)
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            state.ReadPayload(target, new STJReader(doc.RootElement, _options, encryptor));
        }

        private sealed class STJWriter : IPayloadWriter
        {
            private readonly Utf8JsonWriter _w;
            private readonly JsonSerializerOptions _opts;
            private readonly IEncryptor _enc;

            public STJWriter(Utf8JsonWriter w, JsonSerializerOptions opts, IEncryptor enc)
            { _w = w; _opts = opts; _enc = enc; }

            public void WriteString(string name, string value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteStringValue(_enc.Encrypt(Encoding.UTF8.GetBytes(value ?? string.Empty), "default"));
                else _w.WriteStringValue(value);
            }
            public void WriteBool   (string name, bool value, bool encrypted)   { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteBooleanValue(value); }
            public void WriteInt32  (string name, int value, bool encrypted)    { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteInt64  (string name, long value, bool encrypted)   { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteUInt32 (string name, uint value, bool encrypted)   { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteUInt64 (string name, ulong value, bool encrypted)  { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteSingle (string name, float value, bool encrypted)  { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteDouble (string name, double value, bool encrypted) { _w.WritePropertyName(name); if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(value), "default")); else _w.WriteNumberValue(value); }
            public void WriteBytes  (string name, byte[] value, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (value == null) { _w.WriteNullValue(); return; }
                _w.WriteStringValue(encrypted ? _enc.Encrypt(value, "default") : Convert.ToBase64String(value));
            }
            public void WriteEnum<TEnum>(string name, TEnum value, bool encrypted) where TEnum : struct, Enum
            {
                _w.WritePropertyName(name);
                if (encrypted) _w.WriteStringValue(_enc.Encrypt(BitConverter.GetBytes(Convert.ToInt32(value)), "default"));
                else _w.WriteStringValue(value.ToString());
            }
            public void WriteObject(string name, object value, Type declaredType, bool encrypted)
            {
                _w.WritePropertyName(name);
                if (value == null) { _w.WriteNullValue(); return; }
                if (encrypted)
                {
                    using var ms = new MemoryStream();
                    JsonSerializer.Serialize(ms, value, declaredType, _opts);
                    _w.WriteStringValue(_enc.Encrypt(ms.ToArray(), "default"));
                }
                else
                {
                    JsonSerializer.Serialize(_w, value, declaredType, _opts);
                }
            }
        }

        private sealed class STJReader : IPayloadReader
        {
            private readonly JsonElement _root;
            private readonly JsonSerializerOptions _opts;
            private readonly IEncryptor _enc;

            public STJReader(JsonElement root, JsonSerializerOptions opts, IEncryptor enc)
            { _root = root; _opts = opts; _enc = enc; }

            public bool ReadString(string name, bool encrypted, out string value)
            {
                if (!_root.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) { value = default; return false; }
                value = encrypted ? Encoding.UTF8.GetString(_enc.Decrypt(p.GetString(), "default")) : p.GetString();
                return true;
            }
            public bool ReadBool(string name, bool encrypted, out bool value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToBoolean(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetBoolean();
                return true;
            }
            public bool ReadInt32(string name, bool encrypted, out int value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToInt32(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetInt32();
                return true;
            }
            public bool ReadInt64(string name, bool encrypted, out long value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToInt64(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetInt64();
                return true;
            }
            public bool ReadUInt32(string name, bool encrypted, out uint value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? unchecked((uint)BitConverter.ToInt32(_enc.Decrypt(p.GetString(), "default"), 0)) : p.GetUInt32();
                return true;
            }
            public bool ReadUInt64(string name, bool encrypted, out ulong value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToUInt64(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetUInt64();
                return true;
            }
            public bool ReadSingle(string name, bool encrypted, out float value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToSingle(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetSingle();
                return true;
            }
            public bool ReadDouble(string name, bool encrypted, out double value)
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                value = encrypted ? BitConverter.ToDouble(_enc.Decrypt(p.GetString(), "default"), 0) : p.GetDouble();
                return true;
            }
            public bool ReadBytes(string name, bool encrypted, out byte[] value)
            {
                if (!_root.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) { value = default; return false; }
                value = encrypted ? _enc.Decrypt(p.GetString(), "default") : Convert.FromBase64String(p.GetString());
                return true;
            }
            public bool ReadEnum<TEnum>(string name, bool encrypted, out TEnum value) where TEnum : struct, Enum
            {
                if (!_root.TryGetProperty(name, out var p)) { value = default; return false; }
                if (encrypted)
                {
                    var i = BitConverter.ToInt32(_enc.Decrypt(p.GetString(), "default"), 0);
                    value = (TEnum)Enum.ToObject(typeof(TEnum), i); return true;
                }
                if (p.ValueKind == JsonValueKind.String) return Enum.TryParse(p.GetString(), out value);
                if (p.ValueKind == JsonValueKind.Number) { value = (TEnum)Enum.ToObject(typeof(TEnum), p.GetInt32()); return true; }
                value = default; return false;
            }
            public bool ReadObject(string name, Type declaredType, bool encrypted, out object value)
            {
                if (!_root.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) { value = null; return false; }
                if (encrypted)
                {
                    var bytes = _enc.Decrypt(p.GetString(), "default");
                    value = JsonSerializer.Deserialize(bytes, declaredType, _opts);
                    return true;
                }
                value = p.Deserialize(declaredType, _opts);
                return true;
            }
        }
    }
}
#endif
