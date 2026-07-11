using System;

namespace PersistenceKit
{
    /// <summary>
    /// Fluent configuration entry point. Wires targets, serializers, the default target,
    /// and the encryptor — then produces a <see cref="PersistenceManager"/>.
    /// </summary>
    public sealed class PersistenceKitBuilder
    {
        private readonly PersistenceKitOptions _options = new PersistenceKitOptions();

        /// <summary>
        /// Empty builder — no targets wired. Useful when the caller wants explicit control.
        /// Most users want <see cref="Default"/>.
        /// </summary>
        public static PersistenceKitBuilder Empty() => new PersistenceKitBuilder();

        /// <summary>
        /// Builder pre-wired with sensible defaults. Phase-2 baseline wires nothing; targets
        /// are added by their respective phases (<see cref="UseTarget"/>) and a serializer
        /// must be set explicitly for each target.
        /// </summary>
        public static PersistenceKitBuilder Default() => new PersistenceKitBuilder();

        /// <summary>Set the target used for fields with bare <c>[Persist]</c>.</summary>
        public PersistenceKitBuilder UseDefaultTarget(PersistTarget target)
        {
            _options.DefaultTarget = target;
            return this;
        }

        /// <summary>Wire a backing store for one target.</summary>
        public PersistenceKitBuilder UseTarget(PersistTarget target, IPersistenceTarget impl)
        {
            if (impl == null) throw new ArgumentNullException(nameof(impl));
            if (impl.Target != target)
                throw new ArgumentException($"Target impl reports {impl.Target} but was registered for {target}.", nameof(impl));
            _options.Targets[target] = impl;
            return this;
        }

        /// <summary>Wire a serializer handler for one target.</summary>
        public PersistenceKitBuilder UseSerializer(PersistTarget target, ISerializerHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _options.Serializers[target] = handler;
            return this;
        }

        /// <summary>Install an encryptor. When omitted, <see cref="EncryptedAttribute"/> usage throws at runtime.</summary>
        public PersistenceKitBuilder UseEncryptor(IEncryptor encryptor)
        {
            _options.Encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
            return this;
        }

        /// <summary>Validate configuration and produce the manager.</summary>
        public PersistenceManager Build()
        {
            _options.Validate();
            PersistentStateRegistry.ResolveDefaults(_options.DefaultTarget);
            return new PersistenceManager(_options);
        }
    }
}
