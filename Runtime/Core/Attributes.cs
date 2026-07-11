using System;

namespace PersistenceKit
{
    /// <summary>
    /// Where a persisted field is stored. Each value corresponds to one bit in
    /// <see cref="PersistTargetMask"/> (bit index = enum value).
    /// </summary>
    public enum PersistTarget : byte
    {
        Json        = 0,
        Binary      = 1,
        PlayerPrefs = 2,
        Remote      = 3,
    }

    /// <summary>
    /// Bitmask over <see cref="PersistTarget"/>. The bit index of a target equals its enum value.
    /// </summary>
    [Flags]
    public enum PersistTargetMask : byte
    {
        None        = 0,
        Json        = 1 << 0,
        Binary      = 1 << 1,
        PlayerPrefs = 1 << 2,
        Remote      = 1 << 3,
    }

    /// <summary>
    /// Marks a class as a persistent state. The source generator emits a partial
    /// implementation that wires the class up to <c>IPersistentState</c> and registers
    /// it via a module initializer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class PersistentStateAttribute : Attribute
    {
        /// <summary>Optional override for the type id used as the storage key. Defaults to the class name.</summary>
        public string TypeId { get; set; }
    }

    /// <summary>
    /// Marks a field for persistence. With no arguments the field is routed to the kit's
    /// configured default target (set via <c>PersistenceKitBuilder.UseDefaultTarget</c>;
    /// falls back to <see cref="PersistTarget.Json"/>). Use the <c>target</c> argument to
    /// route the field to a specific backend.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class PersistAttribute : Attribute
    {
        /// <summary>If true the field uses the kit's configured default target.</summary>
        public bool UsesDefaultTarget { get; }

        /// <summary>Resolved target when <see cref="UsesDefaultTarget"/> is false.</summary>
        public PersistTarget Target { get; }

        /// <summary>Optional override for the field's serialized name. Defaults to the field name without a leading underscore.</summary>
        public string Name { get; set; }

        public PersistAttribute()
        {
            UsesDefaultTarget = true;
            Target = default;
        }

        public PersistAttribute(PersistTarget target)
        {
            UsesDefaultTarget = false;
            Target = target;
        }
    }

    /// <summary>
    /// Marks a persisted field as encrypted. The value is encrypted at the leaf level
    /// (string / byte[] / scalar boxed as object) using AES-GCM with a key obtained from
    /// the kit's <c>IKeyProvider</c>. The on-disk shape stays a single string token of
    /// the form <c>"enc:v1:&lt;nonce&gt;:&lt;ct+tag&gt;"</c> so the surrounding payload
    /// remains valid in its host format (JSON, binary, etc).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class EncryptedAttribute : Attribute
    {
        /// <summary>Key purpose passed to <c>IKeyProvider.GetKey</c>. Defaults to "default".</summary>
        public string KeyPurpose { get; set; } = "default";
    }
}
