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
        public string targetPath;               // path to gameobject we will render to
        public string spriteName;               // base name of sprite in atlas for this target
        public int pivotIndex = -100;           // the target's pivot layer
        public int parentPivotIndex = -100;     // the target's parent pivot layer
        public List<MetaLayerPivot.PivotFrame> pivots = new List<MetaLayerPivot.PivotFrame>();
        public List<MetaLayerPivot.OffsetFrame> offsets = new List<MetaLayerPivot.OffsetFrame>();
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
         */
        public static void CalculateTargets(ImportContext ctx)
        {
            string debugStr = "";
            ctx.file.layers
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    string target  = "";
                    if ( layer.type == LayerType.Meta || layer.parameters.Count == 0 ) {
                        // meta layers have no target/path parameter, so use parent
                        // if layer/group has no target parameter, then it's not split.  target is its parent target.
                        var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                        if ( layer.index == -1 ) {
                            target = layer.target;
                        } else if ( parentLayer != null ) {
                            target = parentLayer.target;
                        } else {
                            Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                        }
                    } else {
                        string path = layer.GetParamString(0);  // target path should be the first (and only) parameter
                        path.TrimEnd('/');                      // clean sloppy user data
                        if ( path.StartsWith("/") ) {           // absolute path
                            target = path;
                        } else {                                // relative path, so combine it with its parent
                            var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                            if ( parentLayer != null ) {
                                target = parentLayer.target + "/" + path;
                            } else {
                                Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                            }
                        }
                    }
                    layer.target = target;

                    if ( ! ctx.targets.ContainsKey(target) ) {
                        var spriteName = target.Replace('/', '.').Trim('.');
                        ctx.targets.Add(target, new Target { targetPath = target, spriteName = spriteName });
                    }

                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40) + target + "\n";
                });

            Debug.Log($"=== TARGETS ===\n{debugStr}");
        }


        /**
         * STAGE 3
         * Calculates the pivots and transforms for each layer/group split into its own sprite.
         *  - Read the layers by index (orders layers bottom-to-top, with groups coming before child layers).
         *  - For each group that has a pivot:
         *    - Get that pivot layer
         *    - For each frame:
         *      - Read pixels in cel to determine its pivot
         *      - 
         */
        public static void CalculatePivots(ImportContext ctx)
        {
            Debug.Log("CalculatePivots()");
            string debugStr = "=== CALCULATING PIVOTS FOR TARGETS ===\n";

            // get each pivot metalayer, and set it as its parent group's pivotIndex
            ctx.file.layers.Where(layer => layer.type == LayerType.Meta && layer.actionName == "pivot")
                .ToList()
                .ForEach(pivotLayer => {

                    // by default, a pivot layer is assigned to the group it's in (which is it's parent)


                    ctx.file.FindLayer(pivotLayer.parentIndex).pivotIndex = pivotLayer.index;   // TOOD: deprecate


                    var parentGroup = ctx.file.FindLayer(pivotLayer.parentIndex);
                    if ( ! ctx.targets.ContainsKey(parentGroup.target) ) {
                        Debug.LogWarning($" - target $`{parentGroup.target}` was not initialized");
                        debugStr += $"Invalid pivot on layer '{pivotLayer.layerName}' - target $`{parentGroup.target}` was does not exist\n";
                    } else {
                        var target = ctx.targets[parentGroup.target];

                        if ( target.pivotIndex != -100 ) {
                            Debug.LogWarning($" - target '{target.targetPath}' already has a pivotIndex of {target.pivotIndex}");
                        } else {
                            target.pivotIndex = pivotLayer.index;
                            debugStr += $" - target '{target.targetPath}' getting pivotIndex {target.pivotIndex}\n";

                            // calculate pivots for all frames, find the parent pivot and calculate offsets between the two
                            bool found = false;
                            Layer curGroup = parentGroup;
                            target.pivots = MetaLayerPivot.CalcPivotsForAllFrames(ctx, pivotLayer);
                            while ( !found ) {
                                if ( curGroup.layerName != "__root" ) {
                                    curGroup = ctx.file.FindLayer(curGroup.parentIndex);
                                    if ( curGroup.pivotIndex != target.pivotIndex ) {
                                        target.parentPivotIndex = curGroup.pivotIndex;
                                        var parentPivots = MetaLayerPivot.CalcPivotsForAllFrames(ctx, ctx.file.FindLayer(curGroup.pivotIndex));
                                        Debug.Log($" - calculating offsets between '{curGroup.layerName}' and '{parentGroup.layerName}'");
                                        target.offsets = CalcPivotOffsets(parentPivots, target.pivots);
                                        found = true;
                                    }
                                } else {
                                    Debug.LogWarning($"Could not find parent pivot for '{parentGroup.layerName}'");
                                    break;
                                }
                            }
                        }
                    }


                    // OPTIONAL FEATURE: we could let pivot layers have a target, in which parent isn't looked for, rather the layer matching the target


                });

            Debug.Log(debugStr);

            // recursively set each layer's pivot layer, starting from the phantom root.
            SetChildPivots(ctx, ctx.file.FindLayer(-1));
            
            CalcPivotAndOffsetForAllFrames(ctx, ctx.file.FindLayer(-1));
        }


        /**
         * STAGE 3A
         * recursive method to set child pivot indexes to be same as parent, unless the child already has a pivot index set (a split sprite)
         */
        static void SetChildPivots(ImportContext ctx, Layer parent)
        {
            ctx.file.layers.Where(child => child.parentIndex == parent.index)
                .OrderBy(child => child.index)
                .ToList()
                .ForEach(child => {
                    if ( child.pivotIndex != -1 ) {
                        // layer is split, set children pivots to its pivot
                        SetChildPivots(ctx, child);
                    } else {
                        child.pivotIndex = parent.pivotIndex;
                        SetChildPivots(ctx, child);
                    }
                });
        }


        /**
         * STAGE 3B
         * Calculates each frame's pivots and offsets (from parent pivot) for a layer.
         * Called recursively.
         */
        static void CalcPivotAndOffsetForAllFrames(ImportContext ctx, Layer parent)
        {
            // if phantom root, then calculate its pivots
            if ( parent.layerName == "__root" ) {
                Layer pivotLayer = ctx.file.FindLayer(parent.pivotIndex);
                var pivots = MetaLayerPivot.CalcPivotsForAllFrames(ctx, pivotLayer);
                parent.pivots = pivots;

                // TOOD: set offsets to zero?
                // parent.offsets = CalcPivotOffsets(parent.pivots, parent.pivots);
            }
            
            // look at children.  if child has a different pivot, then calculate its pivot frames and offsets from this layer
            ctx.file.layers.Where(child => child.parentIndex == parent.index /* && child.type == LayerType.Group */)
                .OrderBy(child => child.index)
                .ToList()
                .ForEach(child => {
                    // calculate child's pivots for all frames
                    Layer pivotLayer = ctx.file.FindLayer(child.pivotIndex);
                    if ( pivotLayer.pivots != null ) {
                        // if pivot layer has its pivots calculated, the just use them
                        child.pivots = pivotLayer.pivots;
                    } else {
                        child.pivots = MetaLayerPivot.CalcPivotsForAllFrames(ctx, pivotLayer);
                    }

                    if ( child.pivotIndex != parent.pivotIndex ) {
                        // child has its own pivot layer, find offsets between it an parent
                        Debug.Log($"Calculating pivot offsets between parent `{parent.layerName}` and child `{child.layerName}`");
                        child.offsets = CalcPivotOffsets(parent.pivots, child.pivots);
                    } else {
                        child.offsets = parent.offsets;
                    }

                    if ( child.type == LayerType.Group ) {
                        CalcPivotAndOffsetForAllFrames(ctx, child);
                    }
                });

        }


        /**
         * STAGE 3C
         * Calculate the difference, in pixels, of the parent pivot to the child pivot.  This will be used to transform the child target to the pivot's location.
         */
        static List<MetaLayerPivot.OffsetFrame> CalcPivotOffsets(List<MetaLayerPivot.PivotFrame> parentPivots, List<MetaLayerPivot.PivotFrame> childPivots)
        {
            var offsets = new List<MetaLayerPivot.OffsetFrame>();
            string debugBuf = " -- OFFSETS\n";
            debugBuf       += " -- frame\tx\ty\n";

            if ( childPivots != null ) {
                if ( parentPivots != null ) {
                    // for each frame, calculate difference between parent pivot and child pivot
                    childPivots.ForEach(pivot => {
//                        Debug.Log($" ---- Looking at pivot on frame {pivot.frame}");
//                        Debug.Log($" ---- parent pivot count: {parentPivots.Count}");
                        MetaLayerPivot.OffsetFrame offset;
                        int parentPivotIdx = parentPivots.FindIndex(item => item.frame == pivot.frame);
                        if ( parentPivotIdx != -1 ) {
                            MetaLayerPivot.PivotFrame parentPivot = parentPivots[parentPivotIdx];
//                            Debug.Log($" ---- Found parent pivot at ({parentPivot.pivot.x},{parentPivot.pivot.y})");
                            offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord - parentPivot.coord };
                        } else {
                            Debug.LogWarning($" -- parent pivot has no entry for frame {pivot.frame}");
                            offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord };
                        }
//                        Debug.Log($" ---- Calculated offset ({offset.offset.x},{offset.offset.y})");
                        offsets.Add(offset);
                        debugBuf += $" -- {offset.frame}\t{offset.coord.x}\t{offset.coord.y}\n";
                    });
                } else {
                    // no parent pivots... so just use pivot value for offset?
                    Debug.LogWarning($" -- no parent pivot layer...");
                    childPivots.ForEach(pivot =>
                    {
                        var offset = new MetaLayerPivot.OffsetFrame() { frame = pivot.frame, coord = pivot.coord };
                        offsets.Add(offset);
                        debugBuf += $" -- {offset.frame}\t{offset.coord.x}\t{offset.coord.y}\n";
                    });
                }
            }

            Debug.Log(debugBuf);

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


        /**
         * STAGE 5B
         * For each target, set the sprite for each frame in the animation clip.
         */
        public static void GenerateClipImageLayer(ImportContext ctx, string childPath, List<Sprite> frameSprites)
        {
            string debugStr = "=== CLIPS ===\n";

            foreach ( var tag in ctx.file.frameTags ) {
                debugStr += $"tag:'{tag}'\n";
                AnimationClip clip = ctx.generatedClips[tag];

                foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                    var target = entry.Value;
                    var targetPath = target.targetPath;
                    var spriteName = target.spriteName;
                    var group = ctx.file.layers.Where(layer => layer.target == targetPath ).FirstOrDefault();
                    if  ( group != null ) {
                        Debug.Log($"   found group for target {targetPath}");
                    } else {
                        Debug.LogWarning($"   NO GROUP for target {targetPath}");
                    }

                    debugStr += $"  - :'group:  - target: '{targetPath}' - sprite: '{spriteName}'\n";
//                    debugStr += $"  - :'group: {group.baseName} - target: '{targetPath}' - sprite: '{spriteName}'\n";
                    Sprite sprite = null;
                    MetaLayerPivot.OffsetFrame offset = null;
                    float time;
                    int duration = 0;   // store accumulated duration of frames.  on each loop iteration will have the start time of the cur frame, in ms
                    var spriteKeyFrames = new ObjectReferenceKeyframe[tag.to - tag.from + 2];
                    var tformXKeyFrames = new Keyframe[tag.to - tag.from + 2];
                    var tformYKeyFrames = new Keyframe[tag.to - tag.from + 2];
                    for ( int i = tag.from; i <= tag.to; ++i ) {
                        sprite = frameSprites.Where(it => it.name == spriteName + "_" + i).FirstOrDefault();   // TODO: default or null???
                        if ( sprite != null ) {
                            debugStr += $"    {i} - sprite found with name '{sprite.name}'\n";
                        } else {
                            debugStr += $"    {i} - no sprite found\n";
                        }
                        var aseFrame = ctx.file.frames[i];
                        time = duration * 0.001f;   // aesprite time is in ms, convert to seconds

                        spriteKeyFrames[i - tag.from] = new ObjectReferenceKeyframe
                        {
                            time = time,   // aesprite time is in ms, convert to seconds
                            value = sprite
                        };

                        offset = target.offsets.Where(it => it.frame == i).FirstOrDefault();
                        if ( offset != null ) {
                            tformXKeyFrames[i - tag.from] = new Keyframe(time, offset.coord.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity); // infinity removes interpolation
                            tformYKeyFrames[i - tag.from] = new Keyframe(time, offset.coord.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity);
                        }

                        // add this frame's duration to calculate when the next frame starts
                        duration += aseFrame.duration;
                    }

                    // Give the last frame an extra keyframe at the end of the animation to give that frame its duration
                    time = duration * 0.001f - 1.0f / clip.frameRate;   // clip duration in seconds, minus one frame's time
                    spriteKeyFrames[spriteKeyFrames.Length - 1] = new ObjectReferenceKeyframe
                    {
                        time = time,
                        value = sprite
                    };

                    if ( offset != null ) {
                        tformXKeyFrames[tformXKeyFrames.Length - 1] = new Keyframe(time, offset.coord.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity);
                        tformYKeyFrames[tformYKeyFrames.Length - 1] = new Keyframe(time, offset.coord.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity);
                    }

                    var binding = new EditorCurveBinding
                    {
                        path = targetPath,
                        type = typeof(SpriteRenderer),
                        propertyName = "m_Sprite"
                    };

                    if ( tformXKeyFrames.Length > 0 ) {
                        clip.SetCurve(targetPath, typeof(Transform), "localPosition.x", new AnimationCurve(tformXKeyFrames));
                        clip.SetCurve(targetPath, typeof(Transform), "localPosition.y", new AnimationCurve(tformYKeyFrames));
                    }
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, spriteKeyFrames);
                };

                /*
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
                */
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