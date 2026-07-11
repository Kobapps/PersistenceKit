using System;
using System.Collections.Generic;

namespace PersistenceKit
{
    /// <summary>
    /// Resolved configuration for a <c>PersistenceManager</c>. Built by
    /// <see cref="PersistenceKitBuilder"/>; consumed by the manager.
    /// </summary>
    public sealed class PersistenceKitOptions
    {
        public PersistTarget DefaultTarget { get; internal set; } = PersistTarget.Json;

        public Dictionary<PersistTarget, IPersistenceTarget>  Targets     { get; } = new Dictionary<PersistTarget, IPersistenceTarget>();
        public Dictionary<PersistTarget, ISerializerHandler> Serializers { get; } = new Dictionary<PersistTarget, ISerializerHandler>();

        public IEncryptor Encryptor { get; internal set; } = Internals.NoOpEncryptor.Instance;

        internal void Validate()
        {
            // Require a target + serializer for every target the user has wired in. The set
            // of "interesting" targets is the union of keys across both dictionaries.
            foreach (var t in Targets.Keys)
                if (!Serializers.ContainsKey(t))
                    throw new InvalidOperationException($"PersistenceKit: target '{t}' has no serializer handler. Call UseSerializer({t}, ...).");
            foreach (var t in Serializers.Keys)
                if (!Targets.ContainsKey(t))
                    throw new InvalidOperationException($"PersistenceKit: serializer for '{t}' has no backing target. Call UseTarget({t}, ...).");
        }
    }
}
