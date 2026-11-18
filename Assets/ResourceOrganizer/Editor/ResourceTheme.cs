using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    // Window-local styling. Never mutate EditorStyles, the editor skin, or Unity preferences.
    internal static class ResourceTheme
    {
        private static GUISkin skin;
        private static readonly List<Texture2D> textures = new List<Texture2D>();
        public static readonly Color Ink = new Color(.13f, .16f, .23f);
        public static readonly Color Accent = new Color(.29f, .24f, .76f);
        private static readonly Color Canvas = new Color(.955f, .960f, .976f);
        public static GUIStyle Title, Section, Label, Muted, Wrap, MiniWrap, Panel, Row, Field, PathText;
        public static GUIStyle Primary, Button, Tab, ActiveTab, TableHead, Notice, Warning, Error, Arrow;

        private static Texture2D Fill(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            textures.Add(texture);
            return texture;
        }

        private static GUIStyle Text(GUIStyle basis, int size, Color color)
        {
            var style = new GUIStyle(basis) { fontSize = size, fontStyle = FontStyle.Normal };
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
            return style;
        }

        private static GUIStyle Surface(Color color, int padding)
        {
            return new GUIStyle { normal = { background = Fill(color) },
                padding = new RectOffset(padding, padding, padding, padding),
                margin = new RectOffset(0, 0, 0, 0) };
        }

        private static GUIStyle Control(GUIStyle basis, Color background, Color foreground)
        {
            var style = Text(basis, 13, foreground);
            style.normal.background = Fill(background);
            style.hover.background = Fill(Color.Lerp(background, Accent, .08f));
            style.active.background = Fill(Color.Lerp(background, Accent, .16f));
            style.focused.background = Fill(Color.Lerp(background, Accent, .08f));
            style.onNormal.background = style.normal.background;
            style.onHover.background = style.hover.background;
            style.onActive.background = style.active.background;
            style.onFocused.background = style.focused.background;
            style.border = new RectOffset(0, 0, 0, 0);
            style.padding = new RectOffset(10, 10, 6, 6);
            style.margin = new RectOffset(3, 3, 3, 3);
            style.fixedHeight = 30;
            return style;
        }

        private static void Ensure()
        {
            if (skin != null) return;
            skin = Object.Instantiate(GUI.skin);
            skin.hideFlags = HideFlags.HideAndDontSave;
            Label = Text(skin.label, 13, Ink);
            Label.fixedHeight = 0;
            Label.wordWrap = true;
            Title = Text(Label, 19, Ink);
            Section = Text(Label, 15, Ink);
            Muted = Text(Label, 12, new Color(.39f, .43f, .52f));
            Wrap = new GUIStyle(Label) { wordWrap = true };
            MiniWrap = new GUIStyle(Muted) { wordWrap = true };
            PathText = new GUIStyle(Muted) { clipping = TextClipping.Clip, wordWrap = false };
            Panel = Surface(Color.white, 18);
            Row = Surface(new Color(.978f, .981f, .990f), 9);
            Notice = Surface(new Color(.935f, .940f, .995f), 12);
            Warning = Surface(new Color(1f, .960f, .875f), 12);
            Error = Surface(new Color(1f, .925f, .925f), 12);
            Button = Control(skin.button, new Color(.91f, .925f, .955f), Ink);
            Primary = Control(skin.button, Accent, Color.white);
            Primary.fixedHeight = 36;
            Field = Control(skin.textField, new Color(.935f, .944f, .967f), Ink);
            Field.alignment = TextAnchor.MiddleLeft;
            Tab = Control(skin.button, Color.white, new Color(.37f, .40f, .49f));
            Tab.fixedHeight = 40;
            ActiveTab = Control(skin.button, new Color(.93f, .925f, .995f), Accent);
            ActiveTab.fixedHeight = 40;
            TableHead = new GUIStyle(Section) { fontSize = 12, wordWrap = false, padding = new RectOffset(8, 8, 8, 8) };
            Arrow = new GUIStyle(Section) { fontSize = 24, alignment = TextAnchor.MiddleCenter, fixedHeight = 30 };
            Arrow.normal.textColor = Accent;
            skin.label = Label;
            skin.button = Button;
            skin.textField = Field;
            skin.textArea = new GUIStyle(Field) { wordWrap = true, fixedHeight = 0 };
            skin.box = Row;
        }

        public static IDisposable Begin(Rect window)
        {
            Ensure();
            return new WindowScope(window);
        }

        private sealed class WindowScope : IDisposable
        {
            private readonly GUISkin previous = GUI.skin;
            private readonly Color content = GUI.contentColor, background = GUI.backgroundColor;
            public WindowScope(Rect window)
            {
                GUI.skin = skin;
                GUI.contentColor = Color.white;
                GUI.backgroundColor = Color.white;
                EditorGUI.DrawRect(new Rect(0, 0, window.width, window.height), Canvas);
            }
            public void Dispose()
            {
                GUI.skin = previous;
                GUI.contentColor = content;
                GUI.backgroundColor = background;
            }
        }

        public static void Help(string text, MessageType type)
        {
            using (new EditorGUILayout.VerticalScope(type == MessageType.Error ? Error : type == MessageType.Warning ? Warning : Notice))
                GUILayout.Label(text, Wrap);
            GUILayout.Space(8);
        }

        public static string TextField(string label, string value, params GUILayoutOption[] options)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, Muted);
                return EditorGUILayout.TextField(value, Field, FieldOptions(options));
            }
        }

        public static int Popup(string label, int selected, string[] choices, params GUILayoutOption[] options)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, Muted);
                return EditorGUILayout.Popup(selected, choices, Field, FieldOptions(options));
            }
        }

        public static int Popup(int selected, string[] choices, params GUILayoutOption[] options)
        { return EditorGUILayout.Popup(selected, choices, Field, FieldOptions(options)); }

        private static GUILayoutOption[] FieldOptions(GUILayoutOption[] options)
        { return new[] { GUILayout.Height(32) }.Concat(options).ToArray(); }

        public static bool ToggleLeft(string text, bool value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool next = GUILayout.Toggle(value, GUIContent.none, GUILayout.Width(20), GUILayout.Height(24));
                Rect label = GUILayoutUtility.GetRect(new GUIContent(text), Wrap, GUILayout.ExpandWidth(true));
                GUI.Label(label, text, Wrap);
                if (GUI.enabled && Event.current.type == EventType.MouseDown && Event.current.button == 0 && label.Contains(Event.current.mousePosition))
                { next = !next; GUI.changed = true; Event.current.Use(); }
                return next;
            }
        }

        public static bool Foldout(bool open, string text, bool toggleOnLabelClick)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect arrow = GUILayoutUtility.GetRect(16, 26, GUILayout.Width(16));
                bool next = EditorGUI.Foldout(arrow, open, GUIContent.none, false);
                Rect label = GUILayoutUtility.GetRect(new GUIContent(text), Section, GUILayout.MinHeight(26), GUILayout.ExpandWidth(true));
                GUI.Label(label, text, Section);
                if (toggleOnLabelClick && GUI.enabled && Event.current.type == EventType.MouseDown &&
                    Event.current.button == 0 && label.Contains(Event.current.mousePosition))
                { next = !next; GUI.changed = true; Event.current.Use(); }
                return next;
            }
        }

        public static void LabelField(string text, params GUILayoutOption[] options)
        { GUILayout.Label(text, Label, options); }

        public static void LabelField(string text, GUIStyle style, params GUILayoutOption[] options)
        { GUILayout.Label(text, style, options); }

        public static void LabelField(string label, string value, params GUILayoutOption[] options)
        { GUILayout.Label(label + "：" + value, Wrap, options); }

        public static void SelectableLabel(string text, params GUILayoutOption[] options)
        { EditorGUILayout.SelectableLabel(text, PathText, options); }

        private static bool Accept(Object obj, Type type)
        {
            return obj == null || (type.IsInstanceOfType(obj) && EditorUtility.IsPersistent(obj));
        }

        public static Object ObjectField(string label, Object value, Type type, bool allowSceneObjects)
        {
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, Section);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Rect rect = GUILayoutUtility.GetRect(40, 34, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, Field);
            string path = value == null ? "拖入资源或点击选择" : AssetDatabase.GetAssetPath(value);
            Texture icon = value == null ? EditorGUIUtility.IconContent("Folder Icon").image : AssetPreview.GetMiniThumbnail(value);
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, 18, 18), new GUIContent(icon));
            GUI.Label(new Rect(rect.x + 32, rect.y + 6, Math.Max(10, rect.width - 120), 24),
                new GUIContent(value == null ? path : value.name, path), Label);
            if (value != null && GUI.Button(new Rect(rect.xMax - 80, rect.y + 3, 25, 28), "×", Button))
            { value = null; GUI.changed = true; }
            if (GUI.Button(new Rect(rect.xMax - 55, rect.y + 3, 52, 28), "选择", Button))
            {
                if (type == typeof(SceneAsset)) EditorGUIUtility.ShowObjectPicker<SceneAsset>(value, false, "", id);
                else if (type == typeof(TextAsset)) EditorGUIUtility.ShowObjectPicker<TextAsset>(value, false, "", id);
                else if (type == typeof(DefaultAsset)) EditorGUIUtility.ShowObjectPicker<DefaultAsset>(value, false, "", id);
                else EditorGUIUtility.ShowObjectPicker<Object>(value, false, "", id);
            }
            Event evt = Event.current;
            if (GUI.enabled && rect.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                Object candidate = DragAndDrop.objectReferences.FirstOrDefault(o => o != null && Accept(o, type));
                DragAndDrop.visualMode = candidate != null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (evt.type == EventType.DragPerform && candidate != null)
                { DragAndDrop.AcceptDrag(); value = candidate; GUI.changed = true; }
                evt.Use();
            }
            if (GUI.enabled && (evt.commandName == "ObjectSelectorUpdated" || evt.commandName == "ObjectSelectorClosed") &&
                EditorGUIUtility.GetObjectPickerControlID() == id)
            {
                Object candidate = EditorGUIUtility.GetObjectPickerObject();
                if (Accept(candidate, type)) { value = candidate; GUI.changed = true; }
                evt.Use();
            }
            return value;
        }

        public static Object AssetSlot(string label, Object value, Type type)
        {
            GUILayout.Space(12);
            using (new EditorGUILayout.VerticalScope(Row))
            {
                value = ObjectField(label, value, type, false);
                GUILayout.Space(8);
                GUILayout.Label(value == null ? "支持从 Project 窗口拖入" : AssetDatabase.GetAssetPath(value), MiniWrap);
            }
            return value;
        }

        public static Rect[] TableRow(params float[] widths)
        {
            Rect row = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
            float fixedWidth = widths.Where(w => w >= 0).Sum();
            float weight = -widths.Where(w => w < 0).Sum();
            float available = Mathf.Max(0, row.width - fixedWidth - 4 * (widths.Length - 1));
            var cells = new Rect[widths.Length];
            float x = row.x;
            for (int i = 0; i < widths.Length; i++)
            {
                float width = widths[i] < 0 ? available * -widths[i] / Mathf.Max(1, weight) : widths[i];
                cells[i] = new Rect(x, row.y + 2, width, row.height - 4);
                x += width + 4;
            }
            return cells;
        }

        public static void TableHeader(string[] titles, params float[] widths)
        {
            Rect[] cells = TableRow(widths);
            for (int i = 0; i < titles.Length; i++) GUI.Label(cells[i], titles[i], TableHead);
        }

        public static void Cell(Rect rect, string text, string tooltip = null)
        { GUI.Label(rect, new GUIContent(text, tooltip ?? text), Field); }

        public static void AssetCell(Rect rect, string path, string title = null, Texture icon = null, Color? textColor = null, string tooltip = null)
        {
            GUI.Box(rect, GUIContent.none, Field);
            Texture image = icon != null ? icon : AssetDatabase.GetCachedIcon(path);
            if (image != null) GUI.DrawTexture(new Rect(rect.x + 5, rect.y + 4, 20, 20), image, ScaleMode.ScaleToFit);
            Rect label = new Rect(rect.x + 29, rect.y, Mathf.Max(0, rect.width - 33), rect.height);
            Color previous = GUI.contentColor;
            try
            {
                if (textColor.HasValue) GUI.contentColor = textColor.Value;
                GUI.Label(label, new GUIContent(title ?? System.IO.Path.GetFileName(path), tooltip ?? path), PathText);
            }
            finally { GUI.contentColor = previous; }
        }

        public static void Dispose()
        {
            if (skin != null) Object.DestroyImmediate(skin);
            skin = null;
            foreach (var texture in textures) if (texture != null) Object.DestroyImmediate(texture);
            textures.Clear();
        }
    }
}
