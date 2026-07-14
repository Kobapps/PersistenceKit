using System.IO;
using PersistenceKit.Editor.Settings;
using PersistenceKit.Samples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// Builds a comprehensive sample scene that exercises every state type, every target,
    /// encryption, and multi-slot rotation. Layout: a vertical column of mutation buttons
    /// on the left, a live-updating status pane on the right.
    /// </summary>
    internal static class SampleSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        // (label, methodName) pairs — each binds to a public method on RichSaveSample.
        private static readonly (string label, string method)[] Buttons =
        {
            ("Mutate Profile",     nameof(RichSaveSample.MutateProfile)),
            ("Rotate Auth Token",  nameof(RichSaveSample.RotateAuthToken)),
            ("Add Item",           nameof(RichSaveSample.AddItem)),
            ("Remove Last Item",   nameof(RichSaveSample.RemoveLastItem)),
            ("Add Coins",          nameof(RichSaveSample.AddCoins)),
            ("Add Gems",           nameof(RichSaveSample.AddGems)),
            ("Bump Stat",          nameof(RichSaveSample.BumpStat)),
            ("Unlock Achievement", nameof(RichSaveSample.UnlockAchievement)),
            ("Clear Stats",        nameof(RichSaveSample.ClearStats)),
            ("Switch Slot",        nameof(RichSaveSample.SwitchSlot)),
            ("Toggle Vibration",   nameof(RichSaveSample.ToggleVibration)),
            ("Adjust Volume",      nameof(RichSaveSample.AdjustVolume)),
            ("Cycle Language",     nameof(RichSaveSample.CycleLanguage)),
            ("Touch Cloud",        nameof(RichSaveSample.TouchCloud)),
            ("Save All Now",       nameof(RichSaveSample.SaveAllNow)),
            ("Wipe All",           nameof(RichSaveSample.WipeAll)),
        };

        [MenuItem("Tools/PersistenceKit/Configure Sample Scene", priority = 100)]
        public static void Build()
        {
            var scene = EnsureScene();
            ClearExistingSampleObjects();

            var driverGO = new GameObject("PersistenceKit Sample");
            var driver = driverGO.AddComponent<RichSaveSample>();
            // Carry the project-settings debounce into the scene's serialized component.
            driver.DebounceSeconds = PersistenceKitSettings.Instance.DefaultAutoSaveDebounceSeconds;

            EnsureEventSystem();
            var canvas = EnsureCanvas();

            var leftPanel  = BuildPanel(canvas, "ButtonPanel", anchorMin: new Vector2(0, 0), anchorMax: new Vector2(0, 1),
                                        offsetMin: new Vector2(20, 20), offsetMax: new Vector2(280, -20));
            var rightPanel = BuildPanel(canvas, "StatusPanel",  anchorMin: new Vector2(0, 0), anchorMax: new Vector2(1, 1),
                                        offsetMin: new Vector2(300, 20), offsetMax: new Vector2(-20, -20));

            BuildButtonColumn(leftPanel, driver);
            driver.StatusLabel = BuildStatusLabel(rightPanel);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.OpenScene(ScenePath);

            EditorUtility.DisplayDialog(
                "PersistenceKit",
                "Rich sample scene configured.\n\n" +
                "Press Play, then click buttons on the left to mutate state.\n\n" +
                "• Each mutation marks dirty bits for the relevant target only.\n" +
                "• The autosave loop flushes ~0.4s after the last change.\n" +
                "• Stop & Play again — values restore from disk / PlayerPrefs.\n" +
                "• Switch Slot rotates between three PlayerProfile save slots.\n" +
                "• Wipe All deletes from every wired target and reloads blanks.\n\n" +
                "Open Window → PersistenceKit → Inspector for live tree, dirty pills, and the on-disk preview.",
                "OK");

            Selection.activeObject = driverGO;
        }

        // ─── Layout helpers ──────────────────────────────────────

        private static Scene EnsureScene()
        {
            var dir = Path.GetDirectoryName(ScenePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
            if (File.Exists(ScenePath))
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            return scene;
        }

        private static void ClearExistingSampleObjects()
        {
            string[] names = { "PersistenceKit Sample", "PersistenceKit Canvas" };
            foreach (var name in names)
            {
                var existing = GameObject.Find(name);
                if (existing != null) Object.DestroyImmediate(existing);
            }
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(go, "Create EventSystem");
        }

        private static Canvas EnsureCanvas()
        {
            var go = new GameObject("PersistenceKit Canvas",
                                    typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            return canvas;
        }

        private static RectTransform BuildPanel(Canvas canvas, string name,
                                                Vector2 anchorMin, Vector2 anchorMax,
                                                Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(canvas.transform, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            go.GetComponent<Image>().color = new Color(0.13f, 0.13f, 0.16f, 0.9f);
            return rt;
        }

        private static void BuildButtonColumn(RectTransform parent, RichSaveSample driver)
        {
            const float btnHeight = 36f;
            const float gap       = 6f;
            const float padding   = 12f;

            var driverType = typeof(RichSaveSample);
            for (int i = 0; i < Buttons.Length; i++)
            {
                var btn = BuildButton(parent, "Btn_" + Buttons[i].method, Buttons[i].label,
                                      yFromTop: padding + i * (btnHeight + gap),
                                      height: btnHeight,
                                      sidePadding: padding);

                // Wire the click via reflection-free UnityEvent persistent listener — keeps
                // the binding visible/editable in the inspector and survives play-mode resets.
                var method = driverType.GetMethod(Buttons[i].method);
                if (method != null)
                {
                    var call = (UnityEngine.Events.UnityAction)System.Delegate.CreateDelegate(
                        typeof(UnityEngine.Events.UnityAction), driver, method);
                    UnityEditor.Events.UnityEventTools.AddPersistentListener(btn.onClick, call);
                }
            }
        }

        private static Button BuildButton(RectTransform parent, string name, string label,
                                          float yFromTop, float height, float sidePadding)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);   // top-stretch — y measured from top
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot     = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2( sidePadding, -(yFromTop + height));
            rt.offsetMax = new Vector2(-sidePadding, -yFromTop);

            var img = go.GetComponent<Image>();
            img.color = new Color(0.18f, 0.55f, 1f, 0.85f);

            var labelGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            labelGO.transform.SetParent(go.transform, worldPositionStays: false);
            var lrt = (RectTransform)labelGO.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var t = labelGO.GetComponent<Text>();
            t.text = label;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.color = Color.white;
            t.fontSize = 14;
            t.fontStyle = FontStyle.Bold;

            return go.GetComponent<Button>();
        }

        private static Text BuildStatusLabel(RectTransform parent)
        {
            var go = new GameObject("StatusLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(16, 16);
            rt.offsetMax = new Vector2(-16, -16);

            var t = go.GetComponent<Text>();
            t.alignment = TextAnchor.UpperLeft;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = 14;
            t.color     = new Color(0.92f, 0.92f, 0.95f);
            t.supportRichText = true;
            t.text = "Press a button to mutate state.";
            return t;
        }
    }
}
