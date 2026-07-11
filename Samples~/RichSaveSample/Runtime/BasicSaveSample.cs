using System.Threading.Tasks;
using PersistenceKit;
using PersistenceKit.Autosave;
using PersistenceKit.Targets;
using UnityEngine;

namespace PersistenceKit.Samples
{
    /// <summary>
    /// End-to-end smoke sample. Drop this on a GameObject in a scene, hit Play, and the
    /// state survives Stop → Play. Wires Json + PlayerPrefs targets with autosave.
    /// </summary>
    [PersistentState]
    public partial class SampleState
    {
        [Persist]                                    private string _playerName;
        [Persist(target: PersistTarget.PlayerPrefs)] private int    _highScore;
    }

    public sealed class BasicSaveSample : MonoBehaviour
    {
        [SerializeField] private string _newName = "kobi";
        [SerializeField] private int    _newScore = 1234;

        private PersistenceManager _kit;
        private SampleState _state;

        private async void Start()
        {
            _kit = BuildKit();
            AutoSaveLoop.Install(_kit, debounceSeconds: 0.5f);

            _state = await _kit.LoadOrCreateAsync<SampleState>();
            Debug.Log($"[PersistenceKit sample] Loaded — Name='{_state.PlayerName}', Score={_state.HighScore}");
        }

        // Call from a UI button to mutate.
        public void Mutate()
        {
            if (_state == null) return;
            _state.PlayerName = _newName;
            _state.HighScore  = _newScore;
            Debug.Log($"[PersistenceKit sample] Mutated — Name='{_state.PlayerName}', Score={_state.HighScore}. Autosave will flush in 0.5s.");
        }

        // Call to flush immediately without waiting on the debounce.
        public async void SaveNow()
        {
            await _kit.SaveAllAsync();
            Debug.Log("[PersistenceKit sample] SaveAll completed.");
        }

        private static PersistenceManager BuildKit()
        {
#if PERSISTENCEKIT_NEWTONSOFT
            var jsonHandler = (ISerializerHandler)new PersistenceKit.Serializers.NewtonsoftJsonHandler();
#else
            // Without Newtonsoft, the user must wire their own handler.
            ISerializerHandler jsonHandler = null;
            Debug.LogError("[PersistenceKit sample] No serializer is wired — install com.unity.nuget.newtonsoft-json or supply your own ISerializerHandler.");
#endif
            return PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json,        new JsonDiskTarget())
                .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
                .UseSerializer(PersistTarget.Json,        jsonHandler)
                .UseSerializer(PersistTarget.PlayerPrefs, jsonHandler)
                .Build();
        }
    }
}
