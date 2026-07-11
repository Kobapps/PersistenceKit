using System;

namespace PersistenceKit
{
    /// <summary>
    /// Bridges <see cref="IPersistentState"/> to a concrete serialization format. The handler
    /// owns its own <see cref="IPayloadWriter"/>/<see cref="IPayloadReader"/> implementations.
    /// </summary>
    public interface ISerializerHandler
    {
        /// <summary>Produce the on-disk bytes for the fields routed to <paramref name="target"/>.</summary>
        ReadOnlyMemory<byte> Serialize(IPersistentState state, PersistTarget target, IEncryptor encryptor);

        /// <summary>Read fields routed to <paramref name="target"/> from <paramref name="payload"/> into <paramref name="state"/>.</summary>
        void Deserialize(ReadOnlySpan<byte> payload, IPersistentState state, PersistTarget target, IEncryptor encryptor);
    }
}
