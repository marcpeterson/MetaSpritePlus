using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

using GenericToDataString;  // for object dumper

using MetaSpritePlus.Internal;
using System.Linq;

namespace MetaSpritePlus {

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

        // stored date about each sprite target
        public Dictionary<string, Target> targets = new Dictionary<string, Target>();

        public Dictionary<FrameTag, AnimationClip> generatedClips = new Dictionary<FrameTag, AnimationClip>();

        // this will be saved to an AnimData game object
        public AnimData animData;

    }

    /**
     * Pivots are used in atlas generation to determine (0,0) of the sprite.  It may be outside the bounds of the sprite, eg (-2,-5)
     * 
     * TODO: use a list of Vector2. no need for frame number since list index can be the frame number
     * 
     */
    public class PivotFrame
    {
        public int frame;
        public Vector2 coord;   // coordinate, in pixels, of the pivot in sprite-space
    }

    public class Dimensions
    {
        public int frame;
        public int width;
        public int height;
    }

    /**
     * Stores data for each target.  Each split sprite will render to a target.  This class is used to collect
     * data related to the target.
     * 
     * TODO: consider using animData as storage? Most things end up there. seems a bit weird to save data here
     *       only to transfer it again to the final animData serialized object. Although Targets contain
     *       housekeeping and non-serialized data too...
     */
    public class Target {
        public static int DEFAULT_PIVOT = -100; // default pivot index
        public string path;                     // path to gameobject we will render to
        public string spriteName;               // base name of sprite in atlas for this target
        public int pivotIndex = DEFAULT_PIVOT;  // the target's pivot layer
        public List<PivotFrame> pivots = new List<PivotFrame>();        // pivots in texture space. pixel coordinates.
        public Dictionary<int, Vector2> offsets       = new Dictionary<int, Vector2>();    // offset, in pixels, between this target's pivots and its parent's pivots. (0,0) if no parent pivots.
        public Dictionary<int, PivotFrame> pivotNorms = new Dictionary<int, PivotFrame>(); // pivots in sprite space. coordinate is 0-1 based on sprite dimensions.
        public Dictionary<int, Dimensions> dimensions = new Dictionary<int, Dimensions>(); // dimensions of each animation frame, in pixels
        public int numPivotLayers = 0;          // only used for internal housekeeping and warn user if target has more than one pivot layer
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
//                context.animData.data = new Dictionary<string, Dictionary<string, List<FrameData>>>();        // is this needed or legacy?

                ImportStage(context, Stage.LoadFile);
                LoadFile(context, settings, path);

                ImportStage(context, Stage.CalculateTargets);
                CalculateTargets(context);

                ImportStage(context, Stage.CalculatePivots);
                CalculatePivots(context);

                ImportStage(context, Stage.GenerateAtlas);

                context.generatedSprites = AtlasGenerator.GenerateSplitAtlas(context,
                    context.file.layers.Where(it => it.type == LayerType.Content).ToList(),
                    context.atlasPath);

                ImportStage(context, Stage.GenerateClips);
                GenerateAnimClips(context);

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
                    if ( context.animData.animations.ContainsKey(animName) ) {
                        context.animData.animations[animName].numFrames = numFrames;
                    } else {
                        context.animData.animations.Add(animName, new Animation { numFrames = numFrames });
                    }
                }

                // var importer = AssetImporter.GetAtPath(context.atlasPath) as TextureImporter;
                // var spriteSheet = importer.spritesheet;
                // Debug.Log("== SPRITESHEET ==");
                // Debug.Log($"{spriteSheet[0].rect}");
                // Debug.Log($"{spriteSheet[0].pivot}");

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
                    // merge any new animations with old (and overwrite any that match with old)
                    foreach ( KeyValuePair<string, Animation> item in context.animData.animations ) {
                        asset.animations[item.Key] = item.Value;
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


        /**
         * StAGE 1 - Load the file and create folders
         */
        private static void LoadFile(ImportContext ctx, ImportSettings settings, String path)
        {
            ctx.file = ASEParser.Parse(File.ReadAllBytes(path));

            ctx.atlasPath = Path.Combine(settings.atlasOutputDirectory, ctx.fileNameNoExt + ".png");

            if ( settings.controllerPolicy == AnimControllerOutputPolicy.CreateOrOverride )
                ctx.animControllerPath = settings.animControllerOutputPath + "/" + settings.baseName + ".controller";
            ctx.animClipDirectory = settings.clipOutputDirectory;
            ctx.animDataDirectory = settings.dataOutputDirectory;

            // Create paths in advance
            Directory.CreateDirectory(settings.atlasOutputDirectory);
            Directory.CreateDirectory(ctx.animClipDirectory);
            Directory.CreateDirectory(ctx.animDataDirectory);
            if ( ctx.animControllerPath != null )
                Directory.CreateDirectory(Path.GetDirectoryName(ctx.animControllerPath));
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

            // now set up targets for pivot and data layers.  this is so we can warn users if a pivot target doesn't have any content layers.
            ctx.file.layers
                .Where(layer => layer.type == LayerType.Meta && ( layer.actionName == "pivot" || layer.actionName == "data") )
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    var target = ExtractLayerTarget(ctx, layer);
                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40) + target + "\n";
                });

            //Debug.Log($"=== TARGETS ===\n{debugStr}");
        }


        private static string ExtractLayerTarget(ImportContext ctx, Layer layer)
        {
            string targetPath = "";

            if ( layer.actionName == "data" && layer.parameters.Count > 1 ) {
                // data layer requires a name. if 2 parameters, then 1st is a target
                targetPath = layer.GetParamString(0);
            } else if ( layer.actionName != "data" && layer.parameters.Count > 0 ) {
                // target path should be the first (and only) parameter
                targetPath = layer.GetParamString(0);
            }

            if ( targetPath == "" ) {
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
                targetPath.TrimEnd('/');                      // clean sloppy user data
                if ( ! targetPath.StartsWith("/") ) {         // if not an absolute path, then append to parent target path
                    var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                    if ( parentLayer != null ) {
                        targetPath = parentLayer.targetPath.TrimEnd('/') + "/" + targetPath;
                    } else {
                        Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                    }
                }
            }
            layer.targetPath = targetPath;

            // data layers do not need to continue. i think.
            if ( layer.actionName == "data" ) {
                return targetPath;
            }

            if ( ! ctx.targets.ContainsKey(targetPath) ) {
                var spriteName = targetPath.Replace('/', '.').Trim('.');
                ctx.targets.Add(targetPath, new Target { path = targetPath, spriteName = spriteName });
            }

            // keep track of how many layers and pivots each target has.  warn if more than one pivot.
            var curTarget = ctx.targets[targetPath];
            if ( layer.type == LayerType.Meta && layer.actionName == "pivot" ) {
                if ( curTarget.numPivotLayers > 0 ) {
                    Debug.LogWarning($"Pivot layer '{layer.layerName}' ignored because target '{targetPath}' already has a pivot");
                } else {
                    curTarget.numPivotLayers++;
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
         * STAGE 3
         * Calculates the pivots and offets for each layer/group target.
         */
        public static void CalculatePivots(ImportContext ctx)
        {
            string debugStr = "=== CALCULATING PIVOTS FOR TARGETS ===\n";

            // get each pivot layer, and set it as its parent group's pivotIndex
            var pivotLayers = ctx.file.layers.Where(layer => layer.type == LayerType.Meta && layer.actionName == "pivot")
                .OrderByDescending(layer => layer.index)    // this will walk down the heirachy
                .ToList();

            // associate each pivot layer with its matching target
            pivotLayers.ForEach(pivotLayer => {
                if ( pivotLayer.targetPath == "" ) {
                    // a blank target path indicates something is wrong
                    Debug.LogWarning($"Pivot layer '{pivotLayer.layerName}' has no target.  Something is wrong.");
                } else if ( ! ctx.targets.ContainsKey(pivotLayer.targetPath) ) {
                    Debug.LogWarning($"Pivot layer '{pivotLayer.layerName}' target '{pivotLayer.targetPath}' could not be found");
                } else {
                    ctx.targets[pivotLayer.targetPath].pivotIndex = pivotLayer.index;
                    ctx.targets[pivotLayer.targetPath].pivots = CalcPivotsForAllFrames(ctx, pivotLayer);
                }
            });

            // if the root doesn't have a pivotIndex, then use the default pivot
            var rootTarget = ctx.targets["/"];
            if ( rootTarget.pivotIndex == Target.DEFAULT_PIVOT ) {
                debugStr += $" ** root has no pivot layer. using default pivot {ctx.settings.alignment} = {ctx.settings.PivotRelativePos}\n";
                var pivots = new List<PivotFrame>();
                var file = ctx.file;

                for ( int i = 0; i < file.frames.Count; ++i ) {
                    var defaultPivotTex = Vector2.Scale(ctx.settings.PivotRelativePos, new Vector2(ctx.file.width, ctx.file.height));
                    pivots.Add(new PivotFrame { frame = i, coord = defaultPivotTex });
                }
                rootTarget.pivots = pivots;
            }

            // calculate pivot offets for all frames between parent and child pivot layers
            pivotLayers.ForEach(pivotLayer => {
                var target = ctx.targets[pivotLayer.targetPath];

                var targetPath = target.path;

                debugStr += $"pivot layer {targetPath}\n";

                if ( targetPath != "/" ) {  // skip the root layer
                    bool found = false;
                    while ( !found && targetPath != "/" ) {
                        var parentTarget = FindParentTarget(ctx, targetPath);
                        if ( parentTarget.pivotIndex != target.pivotIndex ) {
                            target.offsets = CalcPivotOffsets(ctx, parentTarget.pivots, target.pivots, pivotLayer.targetPath);
                            found = true;
                            debugStr += $" - found target {parentTarget.path}\n";
                        } else {
                            targetPath = parentTarget.path;
                        }
                    }

                    if ( !found ) {
                        debugStr += $" - Could not find parent pivot for '{pivotLayer.targetPath}'. last target path checked was {targetPath}\n";
                        Debug.LogWarning($"Could not find parent pivot for '{pivotLayer.targetPath}'");
                    }
                } else {
                    debugStr += $" - skipped because it's the root layer '/'\n";
                }
            });

            // for any target that doesn't have a pivot index, set it to its parent pivot index
            foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                var target = entry.Value;
                if ( target.pivotIndex == Target.DEFAULT_PIVOT && target.path != "/" ) {    // if not the root, then check the target's parents
                    var targetPath = target.path;
                    bool found = false;
                    while ( !found && targetPath != "/" ) {
                        var parentTarget = FindParentTarget(ctx, targetPath);
                        if ( parentTarget.pivotIndex != Target.DEFAULT_PIVOT ) {
                            target.pivotIndex = parentTarget.pivotIndex;
                            target.pivots = parentTarget.pivots;
                            debugStr += $"target '{target.path}' took pivot index from '{parentTarget.path}': {parentTarget.pivotIndex} \n";
                            found = true;
                            break;
                        } else {
                            targetPath = parentTarget.path;
                        }
                    }

                    if ( !found && target.path != "/" ) {
                        // if no parent found with a pivotIndex, then use the root pivot pivots
                        target.pivotIndex = ctx.targets["/"].pivotIndex;
                        target.pivots = ctx.targets["/"].pivots;
                        debugStr += $"setting '{target.path}' pivotIndex to root pivot index: {target.pivotIndex} \n";
                    }
                }

                debugStr += $"target {target.path} pivotIndex: ";
                debugStr += (target.pivotIndex == Target.DEFAULT_PIVOT) ? "DEFAULT\n" : $"{target.pivotIndex}\n";
            }

            //Debug.Log(debugStr);
        }


        /**
         * STAGE 3 Helper
         * Returns a list of frames, and the pivot on each frame.  Note the pivot is in pixel coordinates
         */
        public static List<PivotFrame> CalcPivotsForAllFrames(ImportContext ctx, Layer pivotLayer)
        {
            var pivots = new List<PivotFrame>();
            var file = ctx.file;

            for ( int i = 0; i < file.frames.Count; ++i ) {
                Cel cel;
                file.frames[i].cels.TryGetValue(pivotLayer.index, out cel);

                if ( cel != null ) {
                    Vector2 center = Vector2.zero;
                    int pixelCount = 0;

                    for ( int y = 0; y < cel.height; ++y ) {
                        for ( int x = 0; x < cel.width; ++x ) {
                            // tex coords relative to full texture boundaries
                            int texX = cel.x + x;
                            int texY = -(cel.y + y) + file.height - 1;

                            var pixel = cel.GetPixelRaw(x, y);
                            if ( pixel.a > 0.1f ) {
                                center += new Vector2(texX, texY);
                                pixelCount++;
                            }
                        }
                    }

                    if ( pixelCount > 0 ) {
                        // pivot becomes the average of all pixels found
                        center /= pixelCount;
                        pivots.Add(new PivotFrame { frame = i, coord = center });
                    } else {
                        Debug.LogWarning($"Pivot layer '{pivotLayer.layerName}' is missing a pivot pixel in frame {i}");
                    }
                }
            }

            return pivots;
        }

        
        /**
         * STAGE 3 Helper
         * Calculate the difference, in pixels, of the parent pivot to the child pivot.  This will be used to transform the child target to the pivot's location.
         * Because all pivots are stored in texture space, we don't need to calculate up the entire chain of pivots. Simply comparing the parent and child pivot
         * is all that's needed.
         */
        static Dictionary<int, Vector2> CalcPivotOffsets(ImportContext ctx, List<PivotFrame> parentPivots, List<PivotFrame> targetPivots, string targetPath)
        {
            string debugStr = "";
            debugStr += $"CalcPivotOffsets - {targetPath}\n";
            var offsets = new Dictionary<int, Vector2>();

            if ( targetPivots == null ) {
                debugStr += $" - exiting because no target pivots\n";
                Debug.LogWarning(debugStr);
                return offsets;
            }

            if ( parentPivots != null && parentPivots.Count > 0 ) {
                debugStr += $" - has {parentPivots.Count} parent pivots\n";
                // for each frame, calculate difference between parent pivot and child pivot
                targetPivots.ForEach(pivot => {
                    Vector2 offset;
                    int frameNum = pivot.frame;
                    int parentPivotIdx = parentPivots.FindIndex(item => item.frame == frameNum);
                    if ( parentPivotIdx != -1 ) {
                        PivotFrame parentPivot = parentPivots[parentPivotIdx];
                        debugStr += $" - parent pivot index = {parentPivotIdx} coord: ({parentPivot.coord.x}, {parentPivot.coord.y})\n";
                        offset = pivot.coord - parentPivot.coord;
                    } else {
                        debugStr += $" - no parent pivot index\n";
                        // If there is no parent pivot, then this pivot offset should be treated as (0,0).
                        // Why?  Because offsets represent the difference between two pivots.  If there is no parent pivot, then this
                        // pivot is essentially the root.  The sprite will be drawn in relation to this pivot.

                        // BUT - This means the pivot should not move.  Doing so will actually move the sprite in the opposite direction.  Check.
                        if ( frameNum > 0 ) {
                            var prevPivot = targetPivots.Where(it => it.frame == frameNum-1).FirstOrDefault();
                            if ( prevPivot != null && prevPivot.coord != pivot.coord ) {
                                Debug.LogWarning($"Target '{targetPath}' has no parent pivots, but its pivot moves in frame {frameNum}.  This may cause unintented sprite movement.");
                            }
                        }

                        offset = Vector2.zero;
                    }
                    debugStr += $"   frame: {frameNum} offset: ({offset.x}, {offset.y})\n";
                    offsets.Add(frameNum, offset);
                });
            } else {
                debugStr += $" - using default pivot\n";
                debugStr += $" - alignment {ctx.settings.alignment} = {ctx.settings.PivotRelativePos}\n";
                // no parent pivots so use the default pivot
                targetPivots.ForEach(pivot =>
                {
                    // calculate default pivot position relative to the entire source texture. converts coordinate to pixel-space.
                    int frameNum = pivot.frame;
                    var defaultPivotTex = Vector2.Scale(ctx.settings.PivotRelativePos, new Vector2(ctx.file.width, ctx.file.height));
                    var offset = pivot.coord - defaultPivotTex;
                    debugStr += $"   frame: {frameNum} default: ({defaultPivotTex.x}, {defaultPivotTex.y}) offset: ({offset.x}, {offset.y})\n";
                    offsets.Add(frameNum, offset);
                });
            }

            //Debug.Log(debugStr);
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

                if ( !ctx.animData.animations.ContainsKey(tag.name) ) {
                    ctx.animData.animations.Add(tag.name, new Animation());
                }

                // Create clip
                if ( !clip) {
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
                string animName = tag.name;
                AnimationClip clip = ctx.generatedClips[tag];

                foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                    var target = entry.Value;
                    var targetPath = target.path;
                    var spriteName = target.spriteName;
                    var finalTarget = (spriteRootPath + targetPath).TrimStart('/');

                    debugStr += $"target: '{targetPath}' - sprite: '{spriteName}'\n";
                    debugStr += $" - num pivots {target.pivots.Count} - num pivot norms {target.pivotNorms.Count}\n";
                    Sprite sprite = null;
                    Vector2 offset = new Vector2();
                    bool hasOffset = false;
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
                    PivotFrame pivot;

                    for ( int i = tag.from; i <= tag.to; i++ ) {
                        int frameNum = i - tag.from;
                        sprite = frameSprites.Where(it => it.name == spriteName + "_" + i).FirstOrDefault();
                        if ( sprite != null ) {

                            // only create animation data for this target if it's not empty
                            if ( ! ctx.animData.animations[animName].targets.ContainsKey(targetPath) ) {
                                ctx.animData.animations[animName].targets.Add(targetPath, new TargetData { path = targetPath, atlasId = spriteName });
                            }

                            // save animation data
                            var spriteData = new SpriteData { frame = frameNum, width = 0, height = 0 };
                            if ( target.pivotNorms.TryGetValue(i, out pivot) ) {
                                spriteData.pivot = pivot.coord;
                            } else {
                                // do anything? use (0,0)?
                                Debug.Log($"GenerateClipImageLayer() missing normalized target pivot coord. frame {i}, target: {target.path}");
                            }

                            if ( target.dimensions.TryGetValue(i, out var dimension) ) {
                                spriteData.width  = dimension.width;
                                spriteData.height = dimension.height;
                            } else {
                                Debug.Log($"GenerateClipImageLayer() missing sprite dimensions. frame {i}, target: {target.path}");
                            }

                            ctx.animData.animations[animName].targets[targetPath].sprites.Add(frameNum, spriteData);
                            debugStr += $"    {i} - sprite found with name '{sprite.name}'";
                        } else if ( didPrevFrameHaveSprite ) {
                            debugStr += $"    {i} - no sprite found for '{spriteName}_{i}'";
                        }
                        var aseFrame = ctx.file.frames[i];
                        time = duration * 0.001f;   // aesprite time is in ms, convert to seconds

                        if ( didPrevFrameHaveSprite || sprite != null ) {
                            spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
                            debugStr += $" - created sprite keyframe";
                        }

                        // move the target according to its offset
                        if ( target.offsets.TryGetValue(i, out offset) ) {
                            // infinity removes interpolation between frames, so change happens immediately when keyframe is reached
                            hasOffset = true;
                            tformXKeyFrames.Add(new Keyframe(time, offset.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                            tformYKeyFrames.Add(new Keyframe(time, offset.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                        }
                        debugStr += $"\n";

                        // add this frame's duration to calculate when the next frame starts
                        duration += aseFrame.duration;

                        didPrevFrameHaveSprite = (sprite != null);

                        // save any pivot data in data section
                        if ( target.pivotNorms.TryGetValue(i, out pivot) ) {

                            /** HERE **/

                            // Debug.Log($"got pivot for {targetPath}");


                            if ( !ctx.animData.animations[animName].targets.ContainsKey(targetPath) ) {
                                ctx.animData.animations[animName].targets.Add(targetPath, new TargetData { path = targetPath });
                            }
                            var frameData = new FrameData { frame = frameNum };
                            frameData.coords.Add(pivot.coord);
                            ctx.animData.animations[animName].targets[targetPath].data.Add($"pivot::{frameNum}", frameData);
                        }
                    }

                    time = duration * 0.001f - 1.0f / clip.frameRate;   // clip duration in seconds, minus one frame's time

                    // Give the last frame an extra keyframe at the end of the animation to give that frame its duration
                    if ( didPrevFrameHaveSprite ) {
                        spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
                    }

                    if ( hasOffset ) {
                        tformXKeyFrames.Add(new Keyframe(time, offset.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                        tformYKeyFrames.Add(new Keyframe(time, offset.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                    }

                    // only save sprite keyframes if they exist
                    if ( spriteKeyFrames.Count() > 0 ) {
                        var binding = new EditorCurveBinding
                        {
                            path = finalTarget,
                            type = typeof(SpriteRenderer),
                            propertyName = "m_Sprite"
                        };
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, spriteKeyFrames.ToArray());
                    } else {
                        debugStr += $" - no sprites\n";
                    }

                    // only save offsets if they exist.  offsets are transforms.
                    if ( tformXKeyFrames.Count > 0 ) {
                        debugStr += ($" - final target '{finalTarget}' offset count: {tformXKeyFrames.Count}\n");
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.x", new AnimationCurve(tformXKeyFrames.ToArray()));
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.y", new AnimationCurve(tformYKeyFrames.ToArray()));
                    } else if ( spriteKeyFrames.Count() > 0 ) {
                        debugStr += $" - final target '{finalTarget}' has no offsets\n";
                    }
                };
            }

            Debug.Log(debugStr);
        }


        /**
         * STAGE 6
         * Create the animation controller
         */
        static void GenerateAnimController(ImportContext ctx) {
            if (ctx.animControllerPath == null) {
                Debug.LogWarning("No animator controller specified. Controller generation will be ignored");
                return;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctx.animControllerPath);
            if ( ! controller ) {
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