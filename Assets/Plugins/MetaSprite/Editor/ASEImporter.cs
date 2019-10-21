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

        // stored date about each sprite target
        public Dictionary<string, Target> targets = new Dictionary<string, Target>();

        public Dictionary<FrameTag, AnimationClip> generatedClips = new Dictionary<FrameTag, AnimationClip>();

        public Dictionary<string, List<Layer>> subImageLayers = new Dictionary<string, List<Layer>>();

        // this will be saved to an AnimData game object
//        public Dictionary<string, Dictionary<string, List<FrameData>>> frameData = new Dictionary<string, Dictionary<string, List<FrameData>>>();
        public AnimData animData;


    }

    /**
     * Stores data for each target.  Each split sprite will render to a target.  This class is used to collect
     * data related to the target.
     */
    public class Target {
        public string path;                     // path to gameobject we will render to
        public string spriteName;               // base name of sprite in atlas for this target
        public int pivotIndex = -100;           // the target's pivot layer
        public List<MetaLayerPivot.PivotFrame> pivots = new List<MetaLayerPivot.PivotFrame>();
        public List<MetaLayerPivot.OffsetFrame> offsets = new List<MetaLayerPivot.OffsetFrame>();
        public int numPivots = 0;
        public int numLayers = 0;
    }


    public static class ASEImporter {

        static readonly Dictionary<string, MetaLayerProcessor> layerProcessors = new Dictionary<string, MetaLayerProcessor>();

        enum Stage {
            LoadFile,
            CalculateTargets,
            CalculatePivots,
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


        /**
         * Given a target path, returns the parent target
         */
        static Target FindParentTarget(ImportContext ctx, string targetPath)
        {
            if ( targetPath.Length == 0 || targetPath == "/" ) {
                return ctx.targets["/"];
            }

            var parentTargetPath = targetPath.Substring(0, targetPath.LastIndexOf('/'));
            if ( ctx.targets.TryGetValue(parentTargetPath, out var parentTarget) ) {
                return parentTarget;
            } else {
                return FindParentTarget(ctx, parentTargetPath);
            }
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

                ImportStage(context, Stage.CalculateTargets);
                CalculateTargets(context);

                ImportStage(context, Stage.CalculatePivots);
                CalculatePivots(context);

                ImportStage(context, Stage.GenerateAtlas);

                // test new way...
                context.generatedSprites = AtlasGenerator.GenerateSplitAtlas(context,
                    context.file.layers.Where(it => it.type == LayerType.Content).ToList(),
                    context.atlasPath);

                /*
                context.generatedSprites = AtlasGenerator.GenerateAtlas(context, 
                    context.file.layers.Where(it => it.type == LayerType.Content).ToList(),
                    context.atlasPath);
                */


                ImportStage(context, Stage.GenerateClips);
                GenerateAnimClips(context);

                /*

                ImportStage(context, Stage.GenerateController);
                GenerateAnimController(context);

                ImportStage(context, Stage.InvokeMetaLayerProcessor);

                context.file.layers
                    .Where(layer => layer.type == LayerType.Meta && layer.actionName != "pivot" )   // pivots are handled earlier
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

                
                // var importer = AssetImporter.GetAtPath(context.atlasPath) as TextureImporter;
                // var spriteSheet = importer.spritesheet;
                // Debug.Log("== SPRITESHEET ==");
                // Debug.Log($"{spriteSheet[0].rect}");
                // Debug.Log($"{spriteSheet[0].pivot}");
                // Debug.Log(ObjectDumper.Dump(context.spriteDimensions));
                // Debug.Log(ObjectDumper.Dump(context.spritePivots));

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

                */

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


        /**
         * STAGE 2 - Calculate the target for each layer.
         * If a layer or group has parameters:
         *   - if it starts with "/", then it's an absolute path, just use it
         *   - otherwise it's a relative path, append to its parent path
         * NOTE:
         *   The settings.spriteRootPath will be used in atlas generation.  no need to clutter target paths with it here.
         */
        public static void CalculateTargets(ImportContext ctx)
        {
            string debugStr = "";

            // first, set up targets for all content layers
            ctx.file.layers
                .Where(layer => layer.type == LayerType.Content || layer.type == LayerType.Group)
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    var target = ExtractLayerTarget(ctx, layer);
                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40) + target + "\n";
                });

            // now set up targets for pivot layers.  this is so we can warn users if a pivot target doesn't have any content layers.
            ctx.file.layers
                .Where(layer => layer.type == LayerType.Meta && layer.actionName == "pivot")
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    var target = ExtractLayerTarget(ctx, layer);
                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40) + target + "\n";
                });

            Debug.Log($"=== TARGETS ===\n{debugStr}");
        }


        public static string ExtractLayerTarget(ImportContext ctx, Layer layer)
        {
            string targetPath  = "";
            if ( layer.parameters.Count == 0 ) {
                // if layer/group has no target parameter, then it's not split.  target is its parent target.
                var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                if ( layer.index == -1 ) {
                    targetPath = layer.targetPath;
                } else if ( parentLayer != null ) {
                    targetPath = parentLayer.targetPath;
                } else {
                    Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                }
            } else {
                string path = layer.GetParamString(0);  // target path should be the first (and only) parameter
                path.TrimEnd('/');                      // clean sloppy user data
                if ( path.StartsWith("/") ) {           // absolute path
                    targetPath = path;
                } else {                                // relative path, so combine it with its parent
                    var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                    if ( parentLayer != null ) {
                        targetPath = parentLayer.targetPath.TrimEnd('/') + "/" + path;
                    } else {
                        Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                    }
                }
            }
            layer.targetPath = targetPath;

            if ( !ctx.targets.ContainsKey(targetPath) ) {
                var spriteName = targetPath.Replace('/', '.').Trim('.');
                ctx.targets.Add(targetPath, new Target { path = targetPath, spriteName = spriteName });
            }

            // keep track of how many layers and pivots each target has.  warn if more than one pivot.
            var curTarget = ctx.targets[targetPath];
            if ( layer.type == LayerType.Meta && layer.actionName == "pivot" ) {
                if ( curTarget.numPivots > 0 ) {
                    Debug.LogWarning($"Pivot layer '{layer.layerName}' ignored because target '{targetPath}' already has a pivot");
                } else {
                    curTarget.numPivots++;
                }
                if ( curTarget.numLayers == 0 ) {
                    Debug.LogWarning($"Pivot layer '{layer.layerName}' with target '{targetPath}' has no content layers");
                }
            } else {
                curTarget.numLayers++;
            }

            return targetPath;
        }


        /**
         * STAGE 3A
         * Calculates the pivots and offets for each layer/group target.
         */
        public static void CalculatePivots(ImportContext ctx)
        {
            string debugStr = "=== CALCULATING PIVOTS FOR TARGETS ===\n";

            // get each pivot metalayer, and set it as its parent group's pivotIndex
            var pivotLayers = ctx.file.layers.Where(layer => layer.type == LayerType.Meta && layer.actionName == "pivot")
                .OrderByDescending(layer => layer.index)    // this will walk down the heirachy
                .ToList();

            // associate pivot layer with their matching target
            pivotLayers.ForEach(pivotLayer => {
                if ( pivotLayer.targetPath == "" ) {
                    // if no target (set by parameter), then use its parent's target
                    //pivotLayer.target = ctx.file.FindLayer(pivotLayer.parentIndex).target;
                    //ctx.targets[ctx.file.FindLayer(pivotLayer.parentIndex).target].pivotIndex = pivotLayer.index;
                } else {
                    if ( ! ctx.targets.ContainsKey(pivotLayer.targetPath) ) {
                        Debug.LogWarning($"pivot target '{pivotLayer.targetPath}' could not be found");
                    } else {
                        ctx.targets[pivotLayer.targetPath].pivotIndex = pivotLayer.index;
                        ctx.targets[pivotLayer.targetPath].pivots = MetaLayerPivot.CalcPivotsForAllFrames(ctx, pivotLayer);
                    }
                }
            });

            // calculate pivot positions for all frames in every pivot layer.
            // calculate offets for all frames between parent and child pivot layers.
            pivotLayers.ForEach(pivotLayer => {
                var target = ctx.targets[pivotLayer.targetPath];

                var targetPath = target.path;
                if ( targetPath != "/" ) {
                    bool found = false;
                    while ( !found && targetPath != "/" ) {
                        var parentTarget = FindParentTarget(ctx, targetPath);
                        if ( parentTarget.pivotIndex != target.pivotIndex ) {
                            target.offsets = CalcPivotOffsets(parentTarget.pivots, target.pivots);
                            found = true;
                        } else {
                            targetPath = parentTarget.path;
                        }
                    }

                    if ( !found ) {
                        Debug.LogWarning($"Could not find parent pivot for '{pivotLayer.targetPath}'");
                    }
                }

            });

            // for any target that doesn't have a pivot index, set it to its parent pivot index
            foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                var target = entry.Value;
                if ( target.pivotIndex == -100 ) {
                    var targetPath = target.path;
                    bool found = false;
                    while ( !found && targetPath != "/" ) {
                        var parentTarget = FindParentTarget(ctx, targetPath);
                        if ( parentTarget.pivotIndex != -100 ) {
                            target.pivotIndex = parentTarget.pivotIndex;
                            target.pivots = parentTarget.pivots;
                            debugStr += $"target '{target.path}' took pivot index from '{parentTarget.path}': {parentTarget.pivotIndex} \n";
                            found = true;
                            break;
                        } else {
                            targetPath = parentTarget.path;
                        }
                    }

                    if ( !found ) {
                        // if no parent found with a pivotIndex, then use the root pivot index
                        target.pivotIndex = ctx.targets["/"].pivotIndex;
                        debugStr += $"setting '{target.path}' pivotIndex to root pivot index: {target.pivotIndex}";
                    }
                    if ( target.pivotIndex == -100 ) {
                        Debug.LogWarning($"  - couldn't find parent pivot for {target.path}");
                    }

                }
                debugStr += $"target {target.path} pivotIndex: {target.pivotIndex}\n";
            }

            Debug.Log(debugStr);
        }


        /**
         * STAGE 3 Helper
         * Calculate the difference, in pixels, of the parent pivot to the child pivot.  This will be used to transform the child target to the pivot's location.
         */
        static List<MetaLayerPivot.OffsetFrame> CalcPivotOffsets(List<MetaLayerPivot.PivotFrame> parentPivots, List<MetaLayerPivot.PivotFrame> childPivots)
        {
            var offsets = new List<MetaLayerPivot.OffsetFrame>();

            if ( childPivots != null ) {
                if ( parentPivots != null ) {
                    // for each frame, calculate difference between parent pivot and child pivot
                    childPivots.ForEach(pivot => {
                        MetaLayerPivot.OffsetFrame offset;
                        int parentPivotIdx = parentPivots.FindIndex(item => item.frame == pivot.frame);
                        if ( parentPivotIdx != -1 ) {
                            MetaLayerPivot.PivotFrame parentPivot = parentPivots[parentPivotIdx];
                            offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord - parentPivot.coord };
                        } else {
                            Debug.LogWarning($" -- parent pivot has no entry for frame {pivot.frame}");
                            offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord };
                        }
                        offsets.Add(offset);
                    });
                } else {
                    // no parent pivots... so just use pivot value for offset?
                    Debug.LogWarning($" -- no parent pivot layer...");
                    childPivots.ForEach(pivot =>
                    {
                        var offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord };
                        offsets.Add(offset);
                    });
                }
            }

            return offsets;
        }


        /**
         * STAGE 4 - Generate atlas happens in parent method
         */
        

        /**
         * STAGE 5 - Generate animation clips
         * Each tag will get it's own animation clip.
         */
        static void GenerateAnimClips(ImportContext ctx) {
            Directory.CreateDirectory(ctx.animClipDirectory);       
            var fileNamePrefix = ctx.animClipDirectory + '/' + ctx.settings.baseName; 

            // Generate one animation for each tag
            foreach (var tag in ctx.file.frameTags) {
                var clipPath = fileNamePrefix + '_' + tag.name + ".anim";
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                // Create clip
                if (!clip) {
                    clip = new AnimationClip();
                    AssetDatabase.CreateAsset(clip, clipPath);
                } else {
                    clip.ClearCurves(); // clear existing clip curves and keyframes
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
            GenerateClipImageLayer(ctx, ctx.settings.spriteRootPath, ctx.generatedSprites);
        }


        /**
         * STAGE 5B
         * For each target, set the sprite for each frame in the animation clip.
         * spriteRootPath is passed it for legacy MetaLayerSubTarget usage.
         */
        public static void GenerateClipImageLayer(ImportContext ctx, string spriteRootPath, List<Sprite> frameSprites)
        {
            string debugStr = "=== CLIPS ===\n";

            // get the user's path to the root sprite.  ensure it ends in a slash
            if ( spriteRootPath != "" ) {
                spriteRootPath.TrimEnd('/');
            }

            foreach ( var tag in ctx.file.frameTags ) {
                debugStr += $"tag:'{tag.name}'\n";
                AnimationClip clip = ctx.generatedClips[tag];

                foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                    var target = entry.Value;
                    var targetPath = target.path;
                    var spriteName = target.spriteName;
                    var finalTarget = (spriteRootPath + targetPath).TrimStart('/');

                    debugStr += $"target: '{targetPath}' - sprite: '{spriteName}'\n";
                    Sprite sprite = null;
                    MetaLayerPivot.OffsetFrame offset = null;
                    float time;
                    int duration = 0;   // store accumulated duration of frames.  on each loop iteration will have the start time of the cur frame, in ms
                    var spriteKeyFrames = new List<ObjectReferenceKeyframe>();
                    var tformXKeyFrames = new List<Keyframe>();
                    var tformYKeyFrames = new List<Keyframe>();
                    
                    //  set a variable to indicate if previous frame had a sprite.
                    //    - if previous frame has sprite, and this frame has a sprite, then add a keyframe
                    //    - if previous frame has sprite, and this frame doesn't, enter empty keyframe
                    //    - if previuos frame has no sprite, and this frame has no sprite, do nothing
                    //    - if previous frame has no sprite, and this frame has a sprite, then add a keyframe
                    bool didPrevFrameHaveSprite = false;

                    if ( target.path == "/top/head" ) {
                        Debug.Log("OFFSETS FOR /top/head");
                    }

                    for ( int i = tag.from; i <= tag.to; ++i ) {
                        sprite = frameSprites.Where(it => it.name == spriteName + "_" + i).FirstOrDefault();
                        if ( sprite != null ) {
                            debugStr += $"    {i} - sprite found with name '{sprite.name}'";
                        } else {
                            debugStr += $"    {i} - no sprite found";
                        }
                        var aseFrame = ctx.file.frames[i];
                        time = duration * 0.001f;   // aesprite time is in ms, convert to seconds

                        if ( didPrevFrameHaveSprite || sprite != null ) {
                            spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
                            debugStr += $" - created sprite keyframe";
                        }

                        offset = target.offsets.Where(it => it.frame == i).FirstOrDefault();
                        if ( offset != null ) {
                            debugStr += $" - using offset";
                            // infinity removes interpolation between frames, so change happens immediately when keyframe is reached
                            tformXKeyFrames.Add(new Keyframe(time, offset.coord.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                            tformYKeyFrames.Add(new Keyframe(time, offset.coord.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                            if ( target.path == "/top/head" ) {
                                Debug.Log($" - frame {i} original: ({offset.coord.x}, {offset.coord.y}) => ({offset.coord.x / ctx.settings.ppu}, {offset.coord.y / ctx.settings.ppu})");
                            }
                        }
                        debugStr += $"\n";

                        // add this frame's duration to calculate when the next frame starts
                        duration += aseFrame.duration;

                        didPrevFrameHaveSprite = (sprite != null);
                    }

                    time = duration * 0.001f - 1.0f / clip.frameRate;   // clip duration in seconds, minus one frame's time

                    if ( didPrevFrameHaveSprite ) {
                        // Give the last frame an extra keyframe at the end of the animation to give that frame its duration
                        spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
                    }

                    if ( offset != null ) {
                        tformXKeyFrames.Add(new Keyframe(time, offset.coord.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                        tformYKeyFrames.Add(new Keyframe(time, offset.coord.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                    }

                    // only save things if the sprite had keyframes
                    if ( spriteKeyFrames.Count() > 0 ) {
                        var binding = new EditorCurveBinding
                        {
                            path = finalTarget,
                            type = typeof(SpriteRenderer),
                            propertyName = "m_Sprite"
                        };
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, spriteKeyFrames.ToArray());
                    }

                    if ( tformXKeyFrames.Count > 0 ) {
                        Debug.Log($" - final target '{finalTarget}' offset count: {tformXKeyFrames.Count}");
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.x", new AnimationCurve(tformXKeyFrames.ToArray()));
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.y", new AnimationCurve(tformYKeyFrames.ToArray()));
                    } else {
                        Debug.Log($" - final target '{finalTarget}' has no offsets!!");
                    }

                };
            }

            Debug.Log(debugStr);
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