using UnityEditor;
using UnityEngine;
using System.IO;

namespace Suyao.ResourceOrganizer
{
    internal static class ProjectReferenceMarker
    {
        public static void Draw(string guid, Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color red = new Color(1f, .22f, .25f);
            bool grid = rect.height > 32;
            EditorGUI.DrawRect(rect, new Color(1f, .08f, .12f, .10f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2, rect.height), red);
            if (grid)
            {
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2), red);
                EditorGUI.DrawRect(new Rect(rect.xMax - 2, rect.y, 2, rect.height), red);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2, rect.width, 2), red);
                var badge = new Rect(rect.x + 2, rect.y + 2, Mathf.Min(48, rect.width - 4), 17);
                EditorGUI.DrawRect(badge, new Color(.78f, .08f, .12f, .95f));
                GUI.Label(badge, "已引用", new GUIStyle(EditorStyles.miniBoldLabel)
                { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
            }
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Rect name = grid ? new Rect(rect.x + 2, rect.yMax - 18, rect.width - 4, 17)
                : new Rect(rect.x + 18, rect.y, Mathf.Max(0, rect.width - 18), rect.height);
            Color background = EditorGUIUtility.isProSkin ? new Color(.22f, .14f, .15f, .97f) : new Color(1f, .88f, .89f, .97f);
            EditorGUI.DrawRect(name, background);
            var label = new GUIStyle(EditorStyles.label)
            { alignment = grid ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(1f, .4f, .42f) : new Color(.76f, .05f, .1f) } };
            GUI.Label(name, new GUIContent(Path.GetFileNameWithoutExtension(path), path + "\n当前查询范围内已引用"), label);
        }
    }
}
