using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

using GenericToDataString;  // for object dumper

using MetaSprite.Internal;
using System.Linq;

namespace MetaSprite {

    public class ImportContext {

        public ASEFile file;
        public ImportSettings settings;

        public string fileDirectory;
        public string fileName;
        public string fileNameNoExt;
    
        public string atlasPath;
        public string animControllerPath;
        public string animClipDirectory;
        public string animDataDirectory;

        public List<Sprite> generatedSprites = new List<Sprite>();

        // The local texture coordinate for bottom-left point of each frame's crop rect, in Unity texture space.
        public List<Vector2> spriteCropPositions = new List<Vector2>();

        // dimensions of each sprite, in pixels?
        public List<Vector2> spriteDimensions = new List<Vector2>();

        // where each sprite's pivot is, as a percentage of its dimentions (1x1)
        public List<Vector2> spritePivots = new List<Vector2>();

        public Dictionary<FrameTag, AnimationClip> generatedClips = new Dictionary<FrameTag, AnimationClip>();

        public Dictionary<string, List<Layer>> subImageLayers = new Dictionary<string, List<Layer>>();

        // this will be saved to an AnimData game object
//        public Dictionary<string, Dictionary<string, List<FrameData>>> frameData = new Dictionary<string, Dictionary<string, List<FrameData>>>();
        public AnimData animData;

    }

    public static class ASEImporter {

        static readonly Dictionary<string, MetaLayerProcessor> layerProcessors = new Dictionary<string, MetaLayerProcessor>();

        enum Stage {
            LoadFile,
            GenerateAtlas,
            GenerateClips,
            GenerateController,
            InvokeMetaLayerProcessor,
            SaveAnimData
        }

	    // returns what percent of the stages have been processed
        static float GetProgress(this Stage stage) {
            return (float) (int) stage / Enum.GetValues(typeof(Stage)).Length;
        }

        static string GetDisplayString(this Stage stage) {
            return stage.ToString();
        }

        public static void Refresh() {
            layerProcessors.Clear();
            var processorTypes = FindAllTypes(typeof(MetaLayerProcessor));
            // Debug.Log("Found " + processorTypes.Length + " layer processor(s).");
            foreach (var type in processorTypes) {
                if (type.IsAbstract) continue;
                try {
                    var instance = (MetaLayerProcessor) type.GetConstructor(new Type[0]).Invoke(new object[0]);
                    if (layerProcessors.ContainsKey(instance.actionName)) {
                        Debug.LogError(string.Format("Duplicate processor with name {0}: {1}", instance.actionName, instance));
                    } else {
                        layerProcessors.Add(instance.actionName, instance);
                    }
                } catch (Exception ex) {
                    Debug.LogError("Can't instantiate meta processor " + type);
                    Debug.LogException(ex);
                }
            }
        }

	    // get all the type names in the passed interface
	    // used to get all the child types derived from the base MetaLayerProcessor class
        static Type[] FindAllTypes(Type interfaceType) {
            var types = System.Reflection.Assembly.GetExecutingAssembly()
                .GetTypes();
            return types.Where(type => type.IsClass && interfaceType.IsAssignableFrom(type))
                        .ToArray();
        }

        struct LayerAndProcessor {
            public Layer layer;
            public MetaLayerProcessor processor;
        }


        public static void Import(DefaultAsset defaultAsset, ImportSettings settings) {

            var path = AssetDatabase.GetAssetPath(defaultAsset);

            var context = new ImportContext {
                // file = file,
                settings = settings,
                fileDirectory = Path.GetDirectoryName(path),
                fileName = Path.GetFileName(path),
                fileNameNoExt = Path.GetFileNameWithoutExtension(path),
                animData = ScriptableObject.CreateInstance<AnimData>(),
            };

            try {
//                context.animData.data = new Dictionary<string, Dictionary<string, List<FrameData>>>();

                ImportStage(context, Stage.LoadFile);
                context.file = ASEParser.Parse(File.ReadAllBytes(path));        

                context.atlasPath = Path.Combine(settings.atlasOutputDirectory, context.fileNameNoExt + ".png");

                if (settings.controllerPolicy == AnimControllerOutputPolicy.CreateOrOverride)
                    context.animControllerPath = settings.animControllerOutputPath + "/" + settings.baseName + ".controller";
                context.animClipDirectory = settings.clipOutputDirectory;
                context.animDataDirectory = settings.dataOutputDirectory;

                // Create paths in advance
                Directory.CreateDirectory(settings.atlasOutputDirectory);
                Directory.CreateDirectory(context.animClipDirectory);
                Directory.CreateDirectory(context.animDataDirectory);
                if ( context.animControllerPath != null)
                    Directory.CreateDirectory(Path.GetDirectoryName(context.animControllerPath));

                ImportStage(context, Stage.GenerateAtlas);
                context.generatedSprites = AtlasGenerator.GenerateAtlas(context, 
                    context.file.layers.Where(it => it.type == LayerType.Content).ToList(),
                    context.atlasPath);

                ImportStage(context, Stage.GenerateClips);
                GenerateAnimClips(context);

                ImportStage(context, Stage.GenerateController);
                GenerateAnimController(context);

                ImportStage(context, Stage.InvokeMetaLayerProcessor);
                context.file.layers
                    .Where(layer => layer.type == LayerType.Meta)
                    .Select(layer => {
                        MetaLayerProcessor processor;
                        layerProcessors.TryGetValue(layer.actionName, out processor);
                        return new LayerAndProcessor { layer = layer, processor = processor };                     
                    })
                    .OrderBy(it => it.processor != null ? it.processor.executionOrder : 0)
                    .ToList()
                    .ForEach(it => {
                        var layer = it.layer;
                        var processor = it.processor;
                        if (processor != null) {
                            processor.Process(context, layer);
                        } else {
                            Debug.LogWarning(string.Format("No processor for meta layer {0}", layer.layerName));                        
                        }
                    });

                // calc num frames for each animation, save to animData
                foreach ( var tag in context.file.frameTags ) {
                    string animName = tag.name;
                    int numFrames = tag.to - tag.from + 1;
                    if ( context.animData.animDict.ContainsKey(animName) ) {
                        context.animData.animDict[animName].numFrames = numFrames;
                    } else {
                        context.animData.animDict.Add(animName, new AnimList
                        {
                            numFrames = numFrames,
                            frameDict = new FrameDictionary(),
                        });
                    }
                }

                // save each frame's pivot and dimensions in animData
                foreach ( var tag in context.file.frameTags ) {
                    string animName = tag.name;

                    var pivotDataList = new FrameDataList { frames = new List<FrameData>() };
                    var dimsDataList = new FrameDataList { frames = new List<FrameData>() };

                    for ( int i=tag.from, j=0; i <= tag.to; i++, j++ ) {
                        pivotDataList.frames.Add(new FrameData { frame = j, coords = new List<Vector2> { context.spritePivots[i] } });
                        dimsDataList.frames.Add(new FrameData { frame = j, coords = new List<Vector2> { context.spriteDimensions[i] } });
                    }
                    context.animData.animDict[animName].frameDict.Add("pivot", pivotDataList);
                    context.animData.animDict[animName].frameDict.Add("dims", dimsDataList);
                }

                /*
                var importer = AssetImporter.GetAtPath(context.atlasPath) as TextureImporter;
                var spriteSheet = importer.spritesheet;
                Debug.Log("== SPRITESHEET ==");
                Debug.Log($"{spriteSheet[0].rect}");
                Debug.Log($"{spriteSheet[0].pivot}");
                Debug.Log(ObjectDumper.Dump(context.spriteDimensions));
                Debug.Log(ObjectDumper.Dump(context.spritePivots));
                */

                ImportStage(context, Stage.SaveAnimData);
                var filePath = context.animDataDirectory + "/" + context.settings.baseName + " data.asset";

                AnimData asset = (AnimData)AssetDatabase.LoadAssetAtPath(filePath, typeof(AnimData));
                if ( !asset ) {
                    asset = ScriptableObject.CreateInstance<AnimData>();
                    asset = context.animData;
                    asset.ppu = context.settings.ppu;
                    asset.pixelOrigin = context.settings.pixelOrigin.ToString();
                    AssetDatabase.CreateAsset(asset, filePath);
                } else {
                    asset.ppu = context.settings.ppu;
                    asset.pixelOrigin = context.settings.pixelOrigin.ToString();
                    // merge any new animations with old (and overwrite matching old)
                    foreach ( KeyValuePair<string, AnimList> item in context.animData.animDict ) {
                        asset.animDict[item.Key] = item.Value;
                    }
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                }

            } catch (Exception e) {
                Debug.LogException(e);
            }

            ImportEnd(context);
        }

	    // updates the UI progress bar
        static void ImportStage(ImportContext ctx, Stage stage) {
            EditorUtility.DisplayProgressBar("Importing " + ctx.fileName, stage.GetDisplayString(), stage.GetProgress());
        }

        static void ImportEnd(ImportContext ctx) {
            EditorUtility.ClearProgressBar();
        }

        public static void GenerateClipImageLayer(ImportContext ctx, string childPath, List<Sprite> frameSprites) {
            foreach (var tag in ctx.file.frameTags) {
                AnimationClip clip = ctx.generatedClips[tag];

                int duration = 0;   // store accumulated duration of frames.  on each loop iteration will have the start time of the cur frame, in ms
                var keyFrames = new ObjectReferenceKeyframe[tag.to - tag.from + 2];
                for (int i = tag.from; i <= tag.to; ++i) {
                    var aseFrame = ctx.file.frames[i];
                    keyFrames[i - tag.from] = new ObjectReferenceKeyframe {
                        time = duration * 0.001f,   // aesprite time is in ms, convert to seconds
                        value = frameSprites[aseFrame.frameID]
                    };

                    // add this frame's duration to calculate when the next frame starts
                    duration += aseFrame.duration;
                }

                // Give the last frame an extra keyframe at the end of the animation to give that frame its duration
                keyFrames[keyFrames.Length - 1] = new ObjectReferenceKeyframe {
                    time = duration * 0.001f - 1.0f / clip.frameRate,   // clip duration in seconds, minus one frame's time
                    value = frameSprites[tag.to]
                };

                var binding = new EditorCurveBinding {
                    path = childPath,
                    type = typeof(SpriteRenderer),
                    propertyName = "m_Sprite"
                };

                AnimationUtility.SetObjectReferenceCurve(clip, binding, keyFrames);
            }
        }

        static void GenerateAnimClips(ImportContext ctx) {
            Directory.CreateDirectory(ctx.animClipDirectory);       
            var fileNamePrefix = ctx.animClipDirectory + '/' + ctx.settings.baseName; 

            string childPath = ctx.settings.spriteTarget;

            // Generate one animation for each tag
            foreach (var tag in ctx.file.frameTags) {
                var clipPath = fileNamePrefix + '_' + tag.name + ".anim";
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                // Create clip
                if (!clip) {
                    clip = new AnimationClip();
                    AssetDatabase.CreateAsset(clip, clipPath);
                } else {
                    AnimationUtility.SetAnimationEvents(clip, new AnimationEvent[0]);
                }

                // Set loop property
                var loop = tag.properties.Contains("loop");
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                if (loop) {
                    clip.wrapMode = WrapMode.Loop;
                    settings.loopBlend = true;
                    settings.loopTime = true;
                } else {
                    clip.wrapMode = WrapMode.Clamp;
                    settings.loopBlend = false;
                    settings.loopTime = false;
                }
                AnimationUtility.SetAnimationClipSettings(clip, settings);

                EditorUtility.SetDirty(clip);
                ctx.generatedClips.Add(tag, clip);
            }

            // Generate main image
            GenerateClipImageLayer(ctx, childPath, ctx.generatedSprites);
        }

        static void GenerateAnimController(ImportContext ctx) {
            if (ctx.animControllerPath == null) {
                Debug.LogWarning("No animator controller specified. Controller generation will be ignored");
                return;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctx.animControllerPath);
            if (!controller) {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ctx.animControllerPath);
            }

            var layer = controller.layers[0];
            var stateMap = new Dictionary<string, AnimatorState>();
            PopulateStateTable(stateMap, layer.stateMachine);
        
            foreach (var pair in ctx.generatedClips) {
                var frameTag = pair.Key;
                var clip = pair.Value;

                AnimatorState st;
                stateMap.TryGetValue(frameTag.name, out st);
                if (!st) {
                    st = layer.stateMachine.AddState(frameTag.name);
                }

                st.motion = clip;
            }

            EditorUtility.SetDirty(controller);
        }

        static void PopulateStateTable(Dictionary<string, AnimatorState> table, AnimatorStateMachine machine) {
            foreach (var state in machine.states) {
                var name = state.state.name;
                if (table.ContainsKey(name)) {
                    Debug.LogWarning("Duplicate state with name " + name + " in animator controller. Behaviour is undefined.");
                } else {
                    table.Add(name, state.state);
                }
            }

            foreach (var subMachine in machine.stateMachines) {
                PopulateStateTable(table, subMachine.stateMachine);
            }
        }

    }

}