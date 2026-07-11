// Enabled when Zenject/Extenject is present.
#if PERSISTENCEKIT_ZENJECT
using System;
using Zenject;

namespace PersistenceKit.DI
{
    /// <summary>
    /// Zenject binding helpers. Builds the kit at install time and binds
    /// <see cref="PersistenceManager"/> + <see cref="DirtyTracker"/> as non-lazy singletons
    /// (matches the lifecycle other singleton services typically use).
    /// </summary>
    public static class ZenjectExtensions
    {
        public static void BindPersistenceKit(this DiContainer container, Action<PersistenceKitBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var builder = PersistenceKitBuilder.Default();
            configure(builder);
            var manager = builder.Build();

            container.BindInstance(manager).AsSingle();
            container.BindInstance(manager.Dirty).AsSingle();
        }
    }
}
#endif
