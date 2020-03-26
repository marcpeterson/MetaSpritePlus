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

    public enum PixelOrigin {
        Center, BottomLeft
    }

    [CreateAssetMenu(menuName = "ASE Import Settings")]
    public class ImportSettings : ScriptableObject {

        public int ppu = 32;

        public PixelOrigin pixelOrigin = PixelOrigin.Center;

        public SpriteAlignment alignment;

        public Vector2 defaultPivot;

        public bool densePacked = true;

        public int border = 3;

        public string baseName = ""; // If left empty, use .ase file name

        public string spriteRootPath = "";

        public string atlasOutputDirectory = "";

        public string clipOutputDirectory = "";

        public AnimControllerOutputPolicy controllerPolicy;

        public string animControllerOutputPath;

        public string dataOutputDirectory = "";

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

            settings.pixelOrigin = (PixelOrigin) EGL.EnumPopup(new GUIContent("Pixel Origin",
                "Where on the sprite's pixel data aligns to." +
                "\nCenter: center of the pixel (recommended)" +
                "\nBottom Left: bottom left of the pixel (original)"),
                settings.pixelOrigin);

            EGL.Space();

            settings.alignment = (SpriteAlignment) EGL.EnumPopup(new GUIContent("Default Pivot",
                "The default position of the pivot in relation to the sprite\n" +
                "Only used for files without a base @pivot layer"),
                settings.alignment);

            if (settings.alignment == SpriteAlignment.Custom) {
                settings.defaultPivot = EGL.Vector2Field(new GUIContent("Custom", "Use a value from 0 to 1, where (0, 0) " +
                    "is the bottom-left of the sprite, and (1, 1) is the top-right of the sprite"), settings.defaultPivot);
            }

            settings.densePacked = EGL.Toggle("Dense Pack", settings.densePacked);
            settings.border = EGL.IntField("Border", settings.border);

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

            if (EditorGUI.EndChangeCheck()) {
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