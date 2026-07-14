using System;
using System.Collections.Generic;

namespace PersistenceKit
{
    /// <summary>
    /// Process-wide registry of <see cref="PersistentStateAttribute"/>-marked types. Populated
    /// by source-generator-emitted <c>[ModuleInitializer]</c> hooks; queried by
    /// <c>PersistenceManager</c> to instantiate states without reflection.
    /// </summary>
    public static class PersistentStateRegistry
    {
        private static readonly Dictionary<Type, Entry>   _byType   = new Dictionary<Type, Entry>();
        private static readonly Dictionary<string, Entry> _byTypeId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object _lock = new object();
        private static bool   _defaultsResolved;
        private static PersistTarget _resolvedDefault;

        internal sealed class Entry
        {
            public Type   Type;
            public string TypeId;
            public Func<IPersistentState>     Factory;
            public Action<PersistTarget>      ResolveDefaults;
            public bool   DefaultsResolved;
        }

        /// <summary>A registered state type paired with the type id used in its storage key.</summary>
        public readonly struct RegisteredState
        {
            public readonly Type   Type;
            public readonly string TypeId;
            public RegisteredState(Type type, string typeId) { Type = type; TypeId = typeId; }
        }

        /// <summary>Registers a state type. Called from generated <c>[ModuleInitializer]</c> code.</summary>
        public static void Register<T>(
            Func<T> factory,
            Action<PersistTarget> resolveDefaults,
            string typeId = null)
            where T : IPersistentState
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var t = typeof(T);
            var id = string.IsNullOrEmpty(typeId) ? t.Name : typeId;

            lock (_lock)
            {
                // Idempotent: same Type registered again is a silent no-op. The source
                // generator emits both [InitializeOnLoadMethod] (editor reload) and
                // [RuntimeInitializeOnLoadMethod] (Play-mode init), so __Register fires
                // twice when entering Play mode. We only want the first one to take effect.
                if (_byType.TryGetValue(t, out var existing))
                {
                    if (existing.TypeId != id)
                        throw new InvalidOperationException(
                            $"PersistentStateRegistry: {t.FullName} re-registered with a different TypeId ('{existing.TypeId}' vs '{id}').");
                    return;
                }
                if (_byTypeId.ContainsKey(id))
                    throw new InvalidOperationException($"PersistentStateRegistry: TypeId '{id}' already registered for {_byTypeId[id].Type.FullName}.");

                var e = new Entry
                {
                    Type            = t,
                    TypeId          = id,
                    Factory         = () => factory(),
                    ResolveDefaults = resolveDefaults,
                };
                _byType[t]    = e;
                _byTypeId[id] = e;

                // If a manager has already finalized defaults, apply to the new entry now.
                if (_defaultsResolved && e.ResolveDefaults != null && !e.DefaultsResolved)
                {
                    e.ResolveDefaults(_resolvedDefault);
                    e.DefaultsResolved = true;
                }
            }
        }

        /// <summary>Apply <paramref name="defaultTarget"/> to all registered types' default-tagged fields.</summary>
        /// <exception cref="InvalidOperationException">Defaults already resolved with a different target.</exception>
        public static void ResolveDefaults(PersistTarget defaultTarget)
        {
            lock (_lock)
            {
                if (_defaultsResolved)
                {
                    if (_resolvedDefault != defaultTarget)
                        throw new InvalidOperationException(
                            $"PersistentStateRegistry: defaults already resolved to {_resolvedDefault}; cannot re-resolve to {defaultTarget}.");
                    return;
                }

                _resolvedDefault  = defaultTarget;
                _defaultsResolved = true;

                foreach (var e in _byType.Values)
                {
                    if (e.ResolveDefaults == null || e.DefaultsResolved) continue;
                    e.ResolveDefaults(defaultTarget);
                    e.DefaultsResolved = true;
                }
            }
        }

        /// <summary>Instantiate a fresh state of type <typeparamref name="T"/>.</summary>
        public static T Create<T>() where T : IPersistentState
        {
            return (T)Create(typeof(T));
        }

        /// <summary>Instantiate a fresh state by reflected type.</summary>
        public static IPersistentState Create(Type t)
        {
            Entry e;
            lock (_lock)
            {
                if (!_byType.TryGetValue(t, out e))
                    throw new InvalidOperationException($"PersistentStateRegistry: {t.FullName} is not registered (forgot [PersistentState]?).");
            }
            return e.Factory();
        }

        /// <summary>Look up the storage TypeId associated with a registered type.</summary>
        public static string GetTypeId(Type t)
        {
            lock (_lock)
            {
                if (!_byType.TryGetValue(t, out var e))
                    throw new InvalidOperationException($"PersistentStateRegistry: {t.FullName} is not registered.");
                return e.TypeId;
            }
        }

        /// <summary>True once a manager has called <see cref="ResolveDefaults"/>.</summary>
        public static bool DefaultsAreResolved
        {
            get { lock (_lock) return _defaultsResolved; }
        }

        /// <summary>
        /// Snapshot of every registered state type. The registration hooks the generator emits
        /// run on editor domain reload as well as at runtime, so this is populated outside Play
        /// mode too — which is what lets editor tooling enumerate states with no manager built.
        /// </summary>
        public static List<RegisteredState> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<RegisteredState>(_byType.Count);
                foreach (var e in _byType.Values) list.Add(new RegisteredState(e.Type, e.TypeId));
                return list;
            }
        }

        /// <summary>
        /// The default target a manager resolved to, if one has. Lets a caller adopt the
        /// existing resolution instead of calling <see cref="ResolveDefaults"/> with a
        /// conflicting value (which throws).
        /// </summary>
        public static bool TryGetResolvedDefault(out PersistTarget target)
        {
            lock (_lock)
            {
                target = _resolvedDefault;
                return _defaultsResolved;
            }
        }

        /// <summary>
        /// Releases the resolved-default latch while keeping every registration, so a later
        /// <see cref="ResolveDefaults"/> may pick a different target.
        /// </summary>
        /// <remarks>
        /// Framework-only escape hatch (hence the underscores) for editor tooling that resolves
        /// defaults provisionally — the edit-mode inspector must resolve to read a state's
        /// <c>TargetMask</c>, but must not lock the game's own <c>Build()</c> out of resolving
        /// differently when Enter Play Mode is configured without a domain reload.
        /// <para>
        /// This does NOT undo the target bits <c>__ResolveDefaults</c> OR'd into each type's
        /// mask — the generated code only ever sets bits. A caller that resolved provisionally
        /// is responsible for restoring the masks it perturbed.
        /// </para>
        /// </remarks>
        public static void __UnresolveDefaults()
        {
            lock (_lock)
            {
                _defaultsResolved = false;
                _resolvedDefault  = default;
                foreach (var e in _byType.Values) e.DefaultsResolved = false;
            }
        }

        /// <summary>
        /// Clears all registrations and the resolved-default flag. Intended for tests and
        /// host frameworks that recreate the kit between sessions; the leading underscores
        /// signal "framework only."
        /// </summary>
        public static void __ResetForTests()
        {
            lock (_lock)
            {
                _byType.Clear();
                _byTypeId.Clear();
                _defaultsResolved = false;
                _resolvedDefault  = default;
            }
        }
    }
}
