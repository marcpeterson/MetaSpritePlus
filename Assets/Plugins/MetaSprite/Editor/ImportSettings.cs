using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using System;
using UnityEditor.Animations;

using GL = UnityEngine.GUILayout;
using EGL = UnityEditor.EditorGUILayout;

namespace MetaSpritePlus {

    public enum AnimControllerOutputPolicy {
        Skip, CreateOrOverride
    }

    // Hacky extension of Unity's SpriteAlignment so we can have "None" as a value
    public enum SpriteAlignmentEx
    {
        None = 10,
        Center = 0,
        TopLeft = 1,
        TopCenter = 2,
        TopRight = 3,
        LeftCenter = 4,
        RightCenter = 5,
        BottomLeft = 6,
        BottomCenter = 7,
        BottomRight = 8,
        Custom = 9
    }

    [CreateAssetMenu(menuName = "ASE Import Settings")]
    public class ImportSettings : ScriptableObject {

        public int ppu = 100;

        public SpriteAlignmentEx alignment = SpriteAlignmentEx.None;

        public Vector2 defaultPivot;

        public int border = 1;

        public string baseName = ""; // If left empty, use Aseprite source file name

        public string spriteRootPath = "";

        public string atlasOutputDirectory = "";

        public string clipOutputDirectory = "";

        public AnimControllerOutputPolicy controllerPolicy;

        public string animControllerOutputPath;

        public string dataOutputDirectory = "";

        public bool createEventForEachFrame = false;

        public string eventFunctionName = "OnFrame";

        public Vector2 PivotRelativePos {
            get {
                return alignment.GetRelativePos(defaultPivot);
            }
        }

    }

    [CustomEditor(typeof(ImportSettings))]
    public class ImportSettingsEditor : Editor {
    
        public override void OnInspectorGUI() {
            var settings = (ImportSettings) target;
            EditorGUI.BeginChangeCheck();
        
            using (new GL.HorizontalScope(EditorStyles.toolbar)) {
                GL.Label("Options");
            }

            settings.baseName = EGL.TextField(new GUIContent("Base Name",
                "Used to name the atlas, clips, and other assets generated"),
                settings.baseName);

            settings.spriteRootPath = EGL.TextField(new GUIContent("Sprite Root Path",
                "Optional path from the Animator Component to the root child object that sprites will render to. " +
                "Any split-sprite targets will use this as thier root.  Non-split sprites will simply render here."),
                settings.spriteRootPath);

            EGL.Space();
        
            settings.ppu = EGL.IntField(new GUIContent("Pixel Per Unit",
                "How many pixels span one Unity unit"),
                settings.ppu);

            EGL.Space();

            settings.alignment = (SpriteAlignmentEx) EGL.EnumPopup(new GUIContent("Default Pivot",
                "The default pivot location in relation to the sprite's texture. " +
                "Only used for files without a root '@pivot' layer. "),
                settings.alignment);

            if (settings.alignment == SpriteAlignmentEx.Custom) {
                settings.defaultPivot = EGL.Vector2Field(new GUIContent("Custom", "Use a value from 0 to 1, where (0, 0) " +
                    "is the bottom-left of the sprite, and (1, 1) is the top-right of the sprite"), settings.defaultPivot);
            }

            settings.border = EGL.IntField(new GUIContent("Border", 
                "How many empty pixels should surround each sprite?"),
                settings.border);

            EGL.Space();
            using (new GL.HorizontalScope(EditorStyles.toolbar)) {
                GL.Label("Output");
            }
        
            settings.atlasOutputDirectory = PathSelection("Atlas Directory", settings.atlasOutputDirectory);
            settings.clipOutputDirectory = PathSelection("Anim Clip Directory", settings.clipOutputDirectory);

            settings.controllerPolicy = (AnimControllerOutputPolicy) EGL.EnumPopup("Anim Controller Policy", settings.controllerPolicy);
            if (settings.controllerPolicy == AnimControllerOutputPolicy.CreateOrOverride) {
                settings.animControllerOutputPath = PathSelection("Anim Controller Directory", settings.animControllerOutputPath);
            }

            settings.dataOutputDirectory = PathSelection("Anim Data Directory", settings.dataOutputDirectory);

            settings.createEventForEachFrame = EGL.Toggle("Create Event For Each Frame", settings.createEventForEachFrame);
            if ( settings.createEventForEachFrame ) {
                settings.eventFunctionName = EGL.TextField(new GUIContent("Call This Function",
                    "Name of function on the GameObject where the Animator resides. This will be called with the frame number. Defaults to 'OnFrame'"),
                    settings.eventFunctionName);
            }


            if ( EditorGUI.EndChangeCheck()) {
                EditorUtility.SetDirty(settings);
            }
        }

        string PathSelection(string id, string path) {
            EGL.BeginHorizontal();
            EGL.PrefixLabel(id);
            path = EGL.TextField(path);
            if (GL.Button("...", GL.Width(30))) {
                path = GetAssetPath(EditorUtility.OpenFolderPanel("Select path", path, ""));
            }

            EGL.EndHorizontal();
            return path;
        }

        static string GetAssetPath(string path) {
            if (path == null) {
                return null;
            }

            var projectPath = Application.dataPath;
            projectPath = projectPath.Substring(0, projectPath.Length - "/Assets".Length);
            path = Remove(path, projectPath);

            if (path.StartsWith("\\") || path.StartsWith("/")) {
                path = path.Remove(0, 1);
            }

            if (!path.StartsWith("Assets") && !path.StartsWith("/Assets")) {
                path = Path.Combine("Assets", path);
            }

            path.Replace('\\', '/');

            return path;
        }

        static string Remove(string s, string exactExpression) {
            return s.Replace(exactExpression, "");
        }

    }

}