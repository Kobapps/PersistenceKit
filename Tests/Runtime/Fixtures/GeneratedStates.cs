namespace PersistenceKit.Tests.Fixtures
{
    /// <summary>
    /// State driven entirely by the source generator. The generator emits the partial that
    /// implements <see cref="IPersistentState"/>, the property setters, the per-target
    /// dispatch, and the registration hook. If the generator is missing or broken, this file
    /// will fail to compile.
    /// </summary>
    [PersistentState]
    public partial class GeneratedFixtureState
    {
        [Persist]                                       private string _userId;
        [Persist(target: PersistTarget.PlayerPrefs)]    private int    _sessionCount;
        [Persist(target: PersistTarget.Remote)]         private string _cloudTag;
        [Persist, Encrypted]                            private string _authToken;

        // Plain runtime field — no attribute, ignored by the generator.
        private float _runtimeOnly;
    }

    /// <summary>Single-target generator fixture used in the DEF-* tests.</summary>
    [PersistentState]
    public partial class GeneratedAllDefaultState
    {
        [Persist] private string _name;
        [Persist] private int    _level;
    }
}
