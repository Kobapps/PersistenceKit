using System;

namespace PersistenceKit
{
    /// <summary>
    /// Implemented by every <see cref="PersistentStateAttribute"/>-marked class. The
    /// implementation is generated as a partial class — user state code does not implement
    /// this directly.
    /// </summary>
    /// <remarks>
    /// All members are explicit interface implementations on the generated partial, so
    /// they do not bleed into the user-facing public surface of the state class.
    /// </remarks>
    public interface IPersistentState
    {
        /// <summary>
        /// Storage key. <c>TypeId</c> for the default slot, or <c>TypeId:Slot</c> for a named slot.
        /// </summary>
        string Key { get; }

        /// <summary>
        /// Set of targets this state writes to. Determined at registration time after
        /// <c>__ResolveDefaults</c> has run.
        /// </summary>
        PersistTargetMask TargetMask { get; }

        /// <summary>
        /// Walks the state's persisted fields routed to <paramref name="target"/>, calling
        /// the appropriate writer methods on <paramref name="writer"/>.
        /// </summary>
        void WritePayload(PersistTarget target, IPayloadWriter writer);

        /// <summary>
        /// Reads back the state's persisted fields routed to <paramref name="target"/>
        /// from <paramref name="reader"/>. Implementations write directly to backing fields
        /// (bypassing setter side-effects) so loading does not mark the state dirty.
        /// </summary>
        void ReadPayload(PersistTarget target, IPayloadReader reader);

        /// <summary>
        /// Called once by <c>PersistenceManager</c> after construction to wire the state's
        /// slot and dirty-marking callback.
        /// </summary>
        void Bind(string slot, Action<PersistTarget> markDirty);

        /// <summary>
        /// Mark every target in this state's <see cref="TargetMask"/> dirty. Useful when the
        /// user writes directly to the state's fields (instead of generated properties) and
        /// needs to flush pending changes in a single call.
        /// </summary>
        void MarkDirty();

        /// <summary>Mark a single target dirty.</summary>
        void MarkDirty(PersistTarget target);
    }
}
