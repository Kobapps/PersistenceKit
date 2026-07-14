using UnityEditor;
using UnityEngine;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// A one-line text prompt. Unity ships a yes/no dialog but nothing that takes input, and
    /// slots that live only in PlayerPrefs can't be enumerated — the user has to name them.
    /// </summary>
    internal sealed class SlotPromptWindow : EditorWindow
    {
        // Static rather than instance state: ShowModalUtility returns once the window has been
        // closed, by which point Unity may have torn the instance down.
        private static string _result;
        private static bool   _accepted;

        private string _typeName = "";
        private string _slot = "";
        private bool   _focused;

        /// <summary>
        /// Blocks until the user confirms or cancels. Returns the slot name — possibly empty,
        /// meaning the default slot — or null when cancelled.
        /// </summary>
        public static string Prompt(string typeName)
        {
            _result   = null;
            _accepted = false;

            var w = CreateInstance<SlotPromptWindow>();
            w.titleContent = new GUIContent("Load Slot");
            w._typeName = typeName;
            var size = new Vector2(360, 116);
            w.minSize = size;
            w.maxSize = size;
            w.ShowModalUtility();

            return _accepted ? (_result ?? string.Empty) : null;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"Slot name for {_typeName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Leave empty for the default slot.", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            // Enter/Escape must be read before the TextField consumes the event.
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Accept(); e.Use(); return; }
                if (e.keyCode == KeyCode.Escape) { Cancel(); e.Use(); return; }
            }

            GUI.SetNextControlName("slotField");
            _slot = EditorGUILayout.TextField(_slot);
            if (!_focused)
            {
                EditorGUI.FocusTextInControl("slotField");
                _focused = true;
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) Cancel();
                if (GUILayout.Button("Load",   GUILayout.Width(80))) Accept();
            }
        }

        private void Accept()
        {
            _result   = _slot?.Trim() ?? string.Empty;
            _accepted = true;
            Close();
        }

        private void Cancel()
        {
            _accepted = false;
            Close();
        }
    }
}
