// Enabled automatically when the VContainer package is present in the project.
#if PERSISTENCEKIT_VCONTAINER
using System;
using VContainer;

namespace PersistenceKit.DI
{
    /// <summary>
    /// VContainer registration helpers. Builds the kit during container resolution and
    /// exposes <see cref="PersistenceManager"/> + <see cref="DirtyTracker"/> as singletons.
    /// </summary>
    public static class VContainerExtensions
    {
        public static void RegisterPersistenceKit(this IContainerBuilder builder, Action<PersistenceKitBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            builder.Register<PersistenceManager>(c =>
            {
                var kitBuilder = PersistenceKitBuilder.Default();
                configure(kitBuilder);
                return kitBuilder.Build();
            }, Lifetime.Singleton);

            // DirtyTracker is exposed off the manager for autosave wiring.
            builder.Register(c => c.Resolve<PersistenceManager>().Dirty, Lifetime.Singleton);
        }
    }
}
#endif
