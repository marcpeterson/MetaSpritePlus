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
        
        /**
         * @config() meta layer key/value data stored here.
         * Since ImportSettings can be shared between multiple Aseprite files, this allows us to store config values specific to this file
         * Currently used keys:
         *   "animator-layer"   Name of the Animator Layer to save the clips to. By default all clips and states are saved on the default unity "Base Layer"
         */
        public Dictionary<string, dynamic> config = new Dictionary<string, dynamic>();
        
        // info about each sprite target
        public Dictionary<string, Target> targets = new Dictionary<string, Target>();

        // a list of all the animator layers we'll render to
        public List<String> animatorLayers = new List<String>();

        public List<ClipInfo> generatedClips = new List<ClipInfo>();

        // this will be saved to an AnimData game object
        public AnimData animData;

    }
    
    public class ClipInfo
    {
        public string clipName;
        public string animatorLayer;
        public FrameTag tag;
        public AnimationClip clip;
    }

    public class Dimensions
    {
        public int frame;
        public int width;
        public int height;
    }

    /**
     * Each sprite will associate to a sprite renderer in the character's game object tree. The target defines the path to that object in the tree,
     * and all the data needed to render an animation clip to it.
     * 
     * TODO: consider using animData as storage? Most things end up there. seems a bit weird to save data here
     *       only to transfer it again to the final animData serialized object. Although Targets contain
     *       housekeeping and non-serialized data too...
     */
    public class Target {
        public static int DEFAULT_PIVOT = -100; // default pivot index
        public string path;                     // path to gameobject we will render to
        public string spriteName;               // base name of sprite in atlas for this target
        public string animatorLayer;            // name of the layer in the animator state machine to render to
        public int pivotIndex = DEFAULT_PIVOT;  // the target's pivot layer
        public Dictionary<int, Vector2> pivots        = new Dictionary<int, Vector2>();    // pivots in texture space. in pixels.
        public Dictionary<int, Vector2> offsets       = new Dictionary<int, Vector2>();    // offset, in pixels, between this target's pivots and its parent's pivots. (0,0) if no parent pivots.
        public Dictionary<int, Vector2> pivotNorms    = new Dictionary<int, Vector2>();    // pivots in sprite space. coordinate is 0-1 based on sprite dimensions.
        public Dictionary<int, Dimensions> dimensions = new Dictionary<int, Dimensions>(); // dimensions of each animation frame, in pixels
        public Dictionary<int, int?> sortOrder        = new Dictionary<int, int?>();       // the target's sprite sorting order
        public int numPivotLayers = 0;          // only used for internal housekeeping and warn user if target has more than one pivot layer
        public int numLayers = 0;
    }


    public static class ASEImporter {

        static readonly Dictionary<string, MetaLayerProcessor> layerProcessors = new Dictionary<string, MetaLayerProcessor>();

        enum Stage {
            LoadFile,
            GetConfigValues,
            CalculateTargets,
            CalculatePivots,
            GenerateAtlas,
            GenerateClips,
            GenerateController,
            SaveOffsetsInAnimData,
            InvokeMetaLayerProcessor,
            SaveAnimData,
            GenerateFrameEvents,
        }

        // returns what percent of the stages have been processed
        static float GetProgress(this Stage stage)
        {
            return (float) (int) stage / Enum.GetValues(typeof(Stage)).Length;
        }

        static string GetDisplayString(this Stage stage)
        {
            return stage.ToString();
        }

        public static void Refresh()
        {
            layerProcessors.Clear();
            var processorTypes = FindAllTypes(typeof(MetaLayerProcessor));
            // Debug.Log("Found " + processorTypes.Length + " layer processor(s).");
            foreach ( var type in processorTypes ) {
                if ( type.IsAbstract ) continue;
                try {
                    var instance = (MetaLayerProcessor) type.GetConstructor(new Type[0]).Invoke(new object[0]);
                    if ( layerProcessors.ContainsKey(instance.actionName) ) {
                        Debug.LogError($"Duplicate processor with name {instance.actionName}: {instance}");
                    } else {
                        layerProcessors.Add(instance.actionName, instance);
                    }
                } catch ( Exception ex ) {
                    Debug.LogError("Can't instantiate meta processor " + type);
                    Debug.LogException(ex);
                }
            }
        }

        // get all the type names in the passed interface
        // used to get all the child types derived from the base MetaLayerProcessor class
        static Type[] FindAllTypes(Type interfaceType)
        {
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


        public static void Import(DefaultAsset defaultAsset, ImportSettings settings)
        {
            var path = AssetDatabase.GetAssetPath(defaultAsset);

            var context = new ImportContext {
                // file = file,
                settings = settings,
                fileDirectory = Path.GetDirectoryName(path),
                fileName = Path.GetFileName(path),
                fileNameNoExt = Path.GetFileNameWithoutExtension(path),
                animData = ScriptableObject.CreateInstance<AnimData>(),
            };

            context.animatorLayers.Add(""); // add a default animator layer

            try {

                ImportStage(context, Stage.LoadFile);
                LoadFile(context, settings, path);

                ImportStage(context, Stage.GetConfigValues);
                GetConfigValues(context);

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

                ImportStage(context, Stage.SaveOffsetsInAnimData);
                SaveOffsetsInAnimData(context);

                ImportStage(context, Stage.InvokeMetaLayerProcessor);

                context.file.layers
                    .Where(layer => layer.type == LayerType.Meta && layer.actionName != "config" && layer.actionName != "pivot")   // config and pivots are handled earlier
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
                        if ( processor != null ) {
                            processor.Process(context, layer);
                        } else {
                            Debug.LogWarning($"No processor for meta layer {layer.layerName}");
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

                ImportStage(context, Stage.SaveAnimData);
                var filePath = context.animDataDirectory + "/" + context.settings.baseName + " data.asset";

                AnimData asset = (AnimData)AssetDatabase.LoadAssetAtPath(filePath, typeof(AnimData));
                if ( !asset ) {
                    asset = ScriptableObject.CreateInstance<AnimData>();
                    asset = context.animData;
                    asset.ppu = context.settings.ppu;
                    AssetDatabase.CreateAsset(asset, filePath);
                } else {
                    asset.ppu = context.settings.ppu;
                    // merge any new animations with old (and overwrite any that match with old)
                    foreach ( KeyValuePair<string, Animation> item in context.animData.animations ) {
                        asset.animations[item.Key] = item.Value;
                    }
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                }

                // add an animation event for every frame
                if ( settings.createEventForEachFrame ) {
                    ImportStage(context, Stage.GenerateFrameEvents);
                    GenerateFrameEvents(context);
                }

            } catch ( Exception e ) {
                Debug.LogException(e);
            }

            AssetDatabase.SaveAssets();
            ImportEnd(context);
        }

        // updates the UI progress bar
        static void ImportStage(ImportContext ctx, Stage stage)
        {
            EditorUtility.DisplayProgressBar("Importing " + ctx.fileName, stage.GetDisplayString(), stage.GetProgress());
        }

        static void ImportEnd(ImportContext ctx)
        {
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
         * STAGE 2 - Look for any @config() layers and save their key/value pairs
         */
        public static void GetConfigValues(ImportContext ctx)
        {
            ctx.file.layers
                .Where(layer => layer.type == LayerType.Meta && layer.actionName == "config")
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    string key = layer.GetParam(0);
                    dynamic value = layer.GetParam(1);
                    ctx.config.Add(key, value);
                });
        }


        /**
         * STAGE 3 - Calculate the target for each layer.
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
                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40)
                        + Util.IndentColTab(str: target, indent: 0, numCharInCol: 30);
                    if ( layer.animatorLayer != null ) debugStr += " -> " + layer.animatorLayer;
                    debugStr += "\n";
                });

            // now set up targets for pivot and data layers.  this is so we can warn users if a pivot target doesn't have any content layers.
            ctx.file.layers
                .Where(layer => layer.type == LayerType.Meta && (layer.actionName == "pivot" || layer.actionName == "data"))
                .OrderBy(layer => layer.index)
                .ToList()
                .ForEach(layer =>
                {
                    var target = ExtractLayerTarget(ctx, layer);
                    debugStr += Util.IndentColTab(str: $"{layer.index} - {layer.layerName}", indent: layer.childLevel, numCharInCol: 40)
                        + Util.IndentColTab(str: target, indent: 0, numCharInCol: 30);
                    if ( layer.animatorLayer != null ) debugStr += " -> " + layer.animatorLayer;
                    debugStr += "\n";
                });

            Debug.Log($"=== TARGETS ===\n{debugStr}");
        }


        private static string ExtractLayerTarget(ImportContext ctx, Layer layer)
        {
            string targetPath = "";

            if ( layer.actionName == "data" && layer.parameters.Count > 1 ) {
                // data layer requires a name. if 2 parameters, then 1st is a target and 2nd is the data's name
                targetPath = layer.GetParam(0);
            } else if ( layer.actionName != "data" && layer.parameters.Count > 0 ) {
                // target path should be the 1st parameter
                targetPath = layer.GetParam(0);
            }

            if ( targetPath == "" ) {
                // if layer/group has no target parameter, then it's not split.  target is its parent target.
                var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                if ( layer.index == -1 ) {                       // if the root layer
                    targetPath = layer.targetPath;               //   just use the layer targetPath. probably ""
                } else if ( parentLayer != null ) {              // if there's a parent layer
                    targetPath = parentLayer.targetPath;         //   use the parent's target path
                    if ( layer.actionName != "data" ) {
                        layer.sortOrder = parentLayer.sortOrder; //   use the parent's sort order
                    }
                } else {
                    Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                }
            } else {
                // else if layer/group has a target parameter, then see if it's absolute or relative
                targetPath.TrimEnd('/');                        // clean sloppy user data
                if ( ! targetPath.StartsWith("/") ) {           // if not an absolute path, then append to parent target path
                    var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                    if ( parentLayer != null ) {                // relative path, combine with parent
                        targetPath = parentLayer.targetPath.TrimEnd('/') + "/" + targetPath;
                    } else {
                        Debug.LogWarning($"parent layer index {layer.parentIndex} not found");
                    }
                }

                // digest optional parameters
                //  - if a number, then it's the sort order
                //  - if a string on a group layer, then it's the controller's animator layer (optional) and state machine (optional)
                //    format is "animatorLayerName/stateMachineName/childMachineName/etc". start with slash for just a state machine path.
                if ( layer.actionName != "data" ) {
                    int paramIdx = 1;
                    while ( layer.parameters.Count > paramIdx ) {
                        dynamic param = layer.GetParam(paramIdx);
                        if ( param.GetType() == typeof(System.Double) ) {
                            layer.sortOrder = Convert.ToInt32(param);
                        } else if ( layer.type == LayerType.Group && param.GetType() == typeof(System.String) ) {
                            layer.animatorLayer = Convert.ToString(param);
                            if ( ! ctx.animatorLayers.Contains(layer.animatorLayer) ) {
                                ctx.animatorLayers.Add(layer.animatorLayer);
                            }
                            Debug.Log($"Group '{targetPath}' has animator layer '{layer.animatorLayer}'");
                        } else {
                            Debug.LogWarning($"Target '{targetPath}' has unprocessed parameter '{param}' of type {param.GetType()}");
                        }
                        paramIdx++;
                    }

                    if ( layer.sortOrder == null ) {
                        // no sort order, so use parent's

                        // TODO: test if this is working...

                        var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                        if ( parentLayer != null ) {
                            layer.sortOrder = parentLayer.sortOrder;
                        }
                    }

                    // if no animator layer, then use the parent's
                    if ( layer.animatorLayer == null ) {
                        var parentLayer = ctx.file.FindLayer(layer.parentIndex);
                        if ( parentLayer != null ) {
                            layer.animatorLayer = parentLayer.animatorLayer;
                        }
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
                string animatorLayer = ( layer.animatorLayer == null ) ? "" : layer.animatorLayer;  // convert null animator layer to default blank "" animator layer
                ctx.targets.Add(targetPath, new Target { path = targetPath, spriteName = spriteName, animatorLayer = animatorLayer });
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
         * STAGE 4
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
                    debugStr += $"target '{pivotLayer.targetPath}' has {ctx.targets[pivotLayer.targetPath].pivots.Count} pivots\n";
                }
            });

            // if the root doesn't have a pivotIndex, then use the default pivot
            var rootTarget = ctx.targets["/"];
            if ( rootTarget.pivotIndex == Target.DEFAULT_PIVOT ) {
                if ( ctx.settings.alignment == SpriteAlignmentEx.None ) {
                    debugStr += $" ** root has no pivot layer. not creating default because alignment set to 'None'\n";
                } else {
                    debugStr += $" ** root has no pivot layer. using default pivot {ctx.settings.alignment} = {ctx.settings.PivotRelativePos}\n";
                    var pivots = new Dictionary<int, Vector2>();
                    var file = ctx.file;

                    for ( int i = 0; i < file.frames.Count; ++i ) {
                        var defaultPivotTex = Vector2.Scale(ctx.settings.PivotRelativePos, new Vector2(ctx.file.width, ctx.file.height));
                        pivots.Add(i, defaultPivotTex);
                    }
                    rootTarget.pivots = pivots;
                }
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
                            debugStr += $" - found parent {parentTarget.path}\n";
                            target.offsets = CalcPivotOffsets(ctx, parentTarget, target);
                            found = true;
                        } else {
                            targetPath = parentTarget.path;
                        }
                    }

                    if ( !found ) {
                        debugStr += $" - Could not find parent pivot for '{pivotLayer.targetPath}'. last target path checked was {targetPath}\n";
                        Debug.LogWarning($"Could not find parent pivot for '{pivotLayer.targetPath}'");
                    }
                } else {
                    debugStr += $" - skipped offset calculation because it's the root layer '/'\n";
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

            Debug.Log(debugStr);
        }


        /**
         * STAGE 4 Helper
         * Returns a list of frames, and the pivot on each frame.  The pivot coordinates are in a bot-left origin (0,0) unity coordinate space,
         * in an texture the same size as the original image (no clipping).
         */
        public static Dictionary<int, Vector2> CalcPivotsForAllFrames(ImportContext ctx, Layer pivotLayer)
        {
            var pivots = new Dictionary<int, Vector2>();
            var file = ctx.file;

            string debugStr = $"=== PIVOTS FOR {pivotLayer.layerName} ===\n";
            
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
                            int texY = -(cel.y + y) + file.height - 1;  // convert aseprite top-left (0,0) coord to unity texture bot-left (0,0) coord

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
                        pivots.Add(i, center);
                        debugStr += $" - frame {i} - ({center.x}, {center.y})\n";
                    } else {
                        Debug.LogWarning($"Pivot layer '{pivotLayer.layerName}' is missing a pivot pixel in frame {i}");
                    }
                }
            }

            Debug.Log(debugStr);
            return pivots;
        }


        /**
         * STAGE 4 Helper
         * Calculate the difference, in pixels, of the parent pivot to the child pivot.  This will be used to transform the child target to the pivot's location.
         * Because all pivots are stored in texture space, we don't need to calculate up the entire chain of pivots. Simply comparing the parent and child pivot
         * is all that's needed.
         */
        static Dictionary<int, Vector2> CalcPivotOffsets(ImportContext ctx, Target parent, Target target)
        {
            string debugStr = "";
            debugStr += $"=== PIVOT OFFSETS BETWEEN {parent.path} AND {target.path} ===\n";
            var offsets = new Dictionary<int, Vector2>();

            if ( target.pivots == null ) {
                debugStr += $" - exiting because no target pivots\n";
                Debug.LogWarning(debugStr);
                return offsets;
            }

            if ( parent.pivots != null && parent.pivots.Count > 0 ) {
                debugStr += $" - has {parent.pivots.Count} parent pivots\n";
                // for each frame, calculate difference between parent pivot and child pivot
                foreach ( var item in target.pivots ) {
                    Vector2 offset;
                    int frameNum  = item.Key;
                    Vector2 pivot = item.Value;

                    if ( parent.pivots.TryGetValue(frameNum, out Vector2 parentPivot) ) {
                        debugStr += $" - frame {frameNum} parent pivot: ({parentPivot.x}, {parentPivot.y})\n";
                        offset = pivot - parentPivot;
                    } else {
                        debugStr += $" - no parent pivot index\n";
                        // If there is no parent pivot, then this pivot offset should be treated as (0,0).
                        //   Why?  Because offsets represent the difference between two pivots.  If there is no parent pivot, then this
                        //   pivot is essentially the root.  The sprite will be drawn in relation to this pivot.
                        // BUT - This means the pivot should not move.  Doing so will actually move the sprite in the opposite direction.  Check.
                        if ( frameNum == 0 ) {
                            var lastItem = target.pivots.Last();
                            Vector2 prevPivot = lastItem.Value;
                            if ( prevPivot != pivot ) {
                                Debug.LogWarning($"Target '{target.path}' has no parent pivot, but pivot changes from last to first frame. This may cause unintented sprite movement.");
                            }
                        } else {
                            if ( target.pivots.TryGetValue(frameNum-1, out Vector2 prevPivot) ) {
                                if ( prevPivot != null && prevPivot != pivot ) {
                                    Debug.LogWarning($"Target '{target.path}' has no parent pivots, but its pivot changes in frame {frameNum}.  This may cause unintented sprite movement.");
                                }
                            } else {
                                Debug.LogWarning($"Target '{target.path}' has no parent pivots, but its missing a pivot location frame {frameNum-1}.  This may cause unintented sprite movement.");
                            }
                        }

                        offset = Vector2.zero;
                    }
                    debugStr += $"   frame: {frameNum} offset: ({offset.x}, {offset.y})\n";
                    offsets.Add(frameNum, offset);
                }
            } else {
                if ( ctx.settings.alignment == SpriteAlignmentEx.None ) {
                    debugStr += $" - no parent pivots, and default set to None. no offset needed.\n";
                } else {
                    var defaultPivotTex = Vector2.Scale(ctx.settings.PivotRelativePos, new Vector2(ctx.file.width, ctx.file.height));
                    debugStr += $" - no parent pivots, so using default pivot location\n";
                    debugStr += $" - alignment {ctx.settings.alignment} = {ctx.settings.PivotRelativePos} = pixel ({defaultPivotTex.x}, {defaultPivotTex.y})\n";
                    // no parent pivots so use the default pivot
                    foreach ( var item in target.pivots ) {
                        // calculate default pivot position relative to the entire source texture. converts coordinate to pixel-space.
                        int frameNum  = item.Key;
                        Vector2 pivot = item.Value;
                        var offset = pivot - defaultPivotTex;
                        debugStr += $"   frame: {frameNum} default: ({defaultPivotTex.x}, {defaultPivotTex.y}) offset: ({offset.x}, {offset.y})\n";
                        offsets.Add(frameNum, offset);
                    }
                }
            }

            Debug.Log(debugStr);
            return offsets;
        }


        /**
         * STAGE 5 - Generate atlas happens in parent method
         */


        /**
         * STAGE 6 - Generate animation clips
         * Each tag will get it's own animation clip. Each animator layer will get each tag/clip.
         */
        static void GenerateAnimClips(ImportContext ctx)
        {
            string debugStr = $"=== CLIPS ===\n";

            var fileNamePrefix = ctx.animClipDirectory + '/' + ctx.settings.baseName;

            // Generate one animation clip for each tag, for each animator layer
            foreach ( var tag in ctx.file.frameTags ) {
                foreach ( var animLayer in ctx.animatorLayers ) {
                    var clipName = tag.name;
                    if ( animLayer != "" && animLayer != null ) {
                        var animLayerName = GetLayerName(animLayer);
                        if ( animLayerName != "" ) {
                            clipName += "_" + animLayerName;
                        }
                    }

                    var clipPath = fileNamePrefix + "_" + clipName + ".anim";

                    debugStr += $"{clipPath} - clip '{clipName}' - layer '{animLayer}'\n";

                    AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                    if ( ! ctx.animData.animations.ContainsKey(tag.name) ) {
                        ctx.animData.animations.Add(tag.name, new Animation());
                    }

                    // Create clip
                    if ( ! clip ) {
                        debugStr += $" - creating new clip\n";
                        clip = new AnimationClip();
                        AssetDatabase.CreateAsset(clip, clipPath);
                    } else {
                        debugStr += $" - existing clip\n";
                        clip.ClearCurves(); // clear existing clip curves and keyframes
                        AnimationUtility.SetAnimationEvents(clip, new AnimationEvent[0]);   // clear existing animation events
                    }

                    // Set loop property
                    var loop = tag.properties.Contains("loop");
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    if ( loop ) {
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

                    var clipInfo = new ClipInfo { clipName = clipName, animatorLayer = animLayer, tag = tag, clip = clip };
                    ctx.generatedClips.Add(clipInfo);
                }
            }

            Debug.Log(debugStr);

            GenerateClipSprites(ctx, ctx.settings.spriteRootPath, ctx.generatedSprites);
        }


        /**
         * STAGE 6B
         * For each target, set the sprite for each frame in the animation clip.
         */
        public static void GenerateClipSprites(ImportContext ctx, string spriteRootPath, List<Sprite> frameSprites)
        {
            string debugStr = "=== SPRITES IN EACH CLIP ===\n";

            // get the user's path to the root sprite.  ensure it ends in a slash
            if ( spriteRootPath != "" ) {
                spriteRootPath.TrimEnd('/');
            }

            /*
            foreach ( var tag in ctx.file.frameTags ) {
                debugStr += $"tag:'{tag.name}'\n";
                string animName = tag.name;
                AnimationClip clip = ctx.generatedClips[tag.name];
            */

            foreach ( var clipInfo in ctx.generatedClips ) {
                debugStr += $"clip:'{clipInfo.clipName}'\n";
                var tag      = clipInfo.tag;
                var clip     = clipInfo.clip;
                var animName = tag.name;

                foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                    var target      = entry.Value;
                    var targetPath  = target.path;
                    var spriteName  = target.spriteName;
                    var finalTarget = (spriteRootPath + targetPath).Trim('/');

                    // only render targets that belong in this animator layer
                    if ( target.animatorLayer != clipInfo.animatorLayer ) {
                        continue;
                    }
                    
                    debugStr += $" - target: '{targetPath}' - sprite: '{spriteName}'\n";
                    debugStr += $"   - num pivots {target.pivots.Count} - num pivot norms {target.pivotNorms.Count}\n";
                    Sprite sprite = null;
                    Vector2 offset = new Vector2();
                    float time;
                    int duration = 0;   // store accumulated duration of frames.  on each loop iteration will have the start time of the cur frame, in ms
                    var spriteKeyFrames = new List<ObjectReferenceKeyframe>();
                    var tformXKeyFrames = new List<Keyframe>();
                    var tformYKeyFrames = new List<Keyframe>();
                    var sortOrderFrames = new List<Keyframe>();

                    //  set a variable to indicate if previous frame had a sprite.
                    //    - if previous frame has sprite, and this frame has a sprite, then add a keyframe
                    //    - if previous frame has sprite, and this frame doesn't, enter empty keyframe
                    //    - if previuos frame has no sprite, and this frame has no sprite, do nothing
                    //    - if previous frame has no sprite, and this frame has a sprite, then add a keyframe
                    bool didPrevFrameHaveSprite = false;
                    Vector2 pivot;

                    int? prevSortOrder = null;
                    for ( int i = tag.from; i <= tag.to; i++ ) {
                        int frameNum = i - tag.from;
                        time = duration * 0.001f;   // aesprite time is in ms, convert to seconds
                        sprite = frameSprites.Where(it => it.name == spriteName + "_" + i).FirstOrDefault();
                        if ( sprite != null ) {

                            // only create animation data for this target if it's not empty
                            if ( ! ctx.animData.animations[animName].targets.ContainsKey(targetPath) ) {
                                ctx.animData.animations[animName].targets.Add(targetPath, new TargetData { path = targetPath, atlasId = spriteName });
                            }

                            // save animation data
                            var spriteData = new SpriteData { width = 0, height = 0 };
                            if ( target.pivotNorms.TryGetValue(i, out pivot) ) {
                                spriteData.pivot = pivot;
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

                            // make keyframe for any change in the sort order
                            if ( target.sortOrder.ContainsKey(frameNum) ) {
                                int? sortOrder = target.sortOrder[frameNum];
                                if ( sortOrder != null ) {
                                    //Debug.Log($"'{target.path}' sortOrder {sortOrder} on frame {frameNum}");
                                    if ( prevSortOrder != sortOrder ) {
                                        sortOrderFrames.Add(new Keyframe(time, (float) sortOrder, float.PositiveInfinity, float.PositiveInfinity));
                                        prevSortOrder = sortOrder;
                                    }
                                }
                            }
                        } else if ( didPrevFrameHaveSprite ) {
                            debugStr += $"    {i} - no sprite found for '{spriteName}_{i}'";
                        }

                        if ( didPrevFrameHaveSprite || sprite != null ) {
                            spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
                            debugStr += $" - created sprite keyframe";
                        }

                        // move the target according to its offset
                        if ( target.offsets.TryGetValue(i, out offset) ) {
                            // infinity removes interpolation between frames, so change happens immediately when keyframe is reached.
                            // can't set TangentMode to Constant. this does the same thing.
                            tformXKeyFrames.Add(new Keyframe(time, offset.x / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                            tformYKeyFrames.Add(new Keyframe(time, offset.y / ctx.settings.ppu, float.PositiveInfinity, float.PositiveInfinity));
                        }
                        debugStr += $"\n";

                        // add this frame's duration to calculate when the next frame starts
                        duration += ctx.file.frames[i].duration;

                        didPrevFrameHaveSprite = (sprite != null);
                    }

                    /**
                     * Create any final keyframes
                     */

                    time = (duration * 0.001f) - (1.0f / clip.frameRate);   // clip duration in seconds, minus one frame's time

                    // Give the last frame an extra keyframe at the end of the animation to give that frame its duration
                    if ( didPrevFrameHaveSprite ) {
                        spriteKeyFrames.Add(new ObjectReferenceKeyframe { time = time, value = sprite });
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
                        // copy the first transform keyframe to the end. this prevents a unity bug that causes the entire sequence
                        // to drift out of position during playback. time must be after final sprite keyframe to prevent popping.
                        time = duration * 0.001f;   // very end of animation
                        tformXKeyFrames.Add(new Keyframe(time, tformXKeyFrames[0].value, float.PositiveInfinity, float.PositiveInfinity));
                        tformYKeyFrames.Add(new Keyframe(time, tformYKeyFrames[0].value, float.PositiveInfinity, float.PositiveInfinity));

                        debugStr += ($" - final target '{finalTarget}' offset count: {tformXKeyFrames.Count}\n");
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.x", new AnimationCurve(tformXKeyFrames.ToArray()));
                        clip.SetCurve(finalTarget, typeof(Transform), "localPosition.y", new AnimationCurve(tformYKeyFrames.ToArray()));
                    } else if ( spriteKeyFrames.Count() > 0 ) {
                        debugStr += $" - final target '{finalTarget}' has no offsets\n";
                    }

                    // only save sort orders keyframes if they exist
                    if ( sortOrderFrames.Count > 0 ) {
                        debugStr += ($" - final target '{finalTarget}' sort order keyframes: {sortOrderFrames.Count}\n");
                        clip.SetCurve(finalTarget, typeof(SpriteRenderer), "m_SortingOrder", new AnimationCurve(sortOrderFrames.ToArray()));

                        // NOTE: tried to set the Sorting Group -> Order in Layer property, but it isn't exposed (not even in the editor)
                        //clip.SetCurve("parts", typeof(SortingGroup), "sortingOrder", new AnimationCurve(sortOrderFrames.ToArray()));
                    }
                }
            }

            Debug.Log(debugStr);
        }
        
        
        /**
         * STAGE 7
         * Create the animation controller
         */
        static void GenerateAnimController(ImportContext ctx)
        {
            if ( ctx.animControllerPath == null ) {
                Debug.LogWarning("No animator controller specified. Controller generation will be ignored");
                return;
            }

            string debug = "=== ANIM CONTROLLER ===\n";

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctx.animControllerPath);
            if ( ! controller ) {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ctx.animControllerPath);
            }

            AnimatorControllerLayer defaultAcLayer = null;
            AnimatorControllerLayer[] acLayers = controller.layers;
            string defaultSubMachinePath = "";

            if ( ctx.config.ContainsKey("animator-layer") ) {
                string animLayerName = ctx.config["animator-layer"];
                defaultAcLayer = GetOrCreateAnimatorLayer(controller, animLayerName, ref debug);
                defaultSubMachinePath = GetSubMachinePath(animLayerName);
                debug += $"Config 'animator-layer' defined with '{animLayerName}', has submachine path '{defaultSubMachinePath}'\n";
            } else {
                defaultAcLayer = controller.layers[0];
            }

            debug += $"\nCreating animator layers from clip info:\n";
            foreach ( var clipInfo in ctx.generatedClips ) {
                AnimatorControllerLayer acLayer = defaultAcLayer;
                string animLayerName = clipInfo.animatorLayer;
                string subMachinePath;
                debug += $" - clip '{clipInfo.clipName}' has animator layer '{animLayerName}'\n";
                if ( animLayerName != "" ) {
                    subMachinePath = GetSubMachinePath(animLayerName);
                    acLayer = GetOrCreateAnimatorLayer(controller, clipInfo.animatorLayer, ref debug);
                } else {
                    subMachinePath = defaultSubMachinePath;
                }

                var state = GetOrCreateAnimatorState(controller, acLayer, subMachinePath, clipInfo.tag.name, ref debug);
                state.motion = clipInfo.clip;
            }

            debug += "\nROOT STATE\n";
            foreach ( var state in controller.layers[0].stateMachine.states ) {
                debug += $"{state.state.name}\n";
            }

            debug += "\nSUB MACHINES & STATES\n";
            foreach ( var subMachine in controller.layers[0].stateMachine.stateMachines ) {
                debug += $"machine: {subMachine.stateMachine.name}\n";
                foreach ( var state in subMachine.stateMachine.states ) {
                    debug += $" - state: {state.state.name}\n";
                }
            }

            Debug.Log(debug);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }


        /**
         * Returns the submachine path embedded in a layer name.
         * Given a layer name in the form "name/child/submachine/path", return "child/submachine/path", or ""
         */
        static string GetSubMachinePath(string animLayerName)
        {
            if ( animLayerName.Contains("/") ) {
                return animLayerName.Substring(animLayerName.IndexOf("/") + 1);
            }
            return "";
        }


        /**
         * Returns the name embedded in a layer name.
         * Given a layer name in the form "name/child/submachine/path", return "name", or ""
         */
        static string GetLayerName(string animLayerName)
        {
            if ( animLayerName.Contains("/") ) {
                return animLayerName.Substring(0, animLayerName.IndexOf("/"));
            }
            return "";
        }


        static AnimatorControllerLayer GetOrCreateAnimatorLayer(AnimatorController controller, string animLayerName, ref string debug)
        {
            if ( animLayerName.StartsWith("/") ) { // if layer name starts with "/", then use the default root layer
                debug += $" - Slash as first character tell us to use root layer'\n";
                return controller.layers[0];
            } else {
                animLayerName = GetLayerName(animLayerName);
            }

            for ( int i = 0; i<controller.layers.Length; i++ ) {
                if ( controller.layers[i].name == animLayerName ) {
                    debug += $" - Found animator layer '{animLayerName}'\n";
                    return controller.layers[i];
                }
            }

            debug += $" - Creating animator layer '{animLayerName}'\n";
            controller.AddLayer(animLayerName);
            for ( int i=0; i < controller.layers.Length; i++ ) {
                if ( controller.layers[i].name == animLayerName ) {
                    controller.layers[i].stateMachine.hideFlags = HideFlags.None;   // AddLayer() hides in heircharcy. show it.
                    return controller.layers[i];
                }
            }

            Debug.LogWarning($"Couldn't find newly created layer '{animLayerName}");
            return null;
        }


        /**
         * Gets or creates the state in the given animation layer. If a subMachinePath exists, then follow/create that path of subMachines before making the state.
         * subMachinePath should be in the format "/path/to/machine/" or "path/to/machine".
         */
        static AnimatorState GetOrCreateAnimatorState(AnimatorController controller, AnimatorControllerLayer acLayer, string subMachinePath, string stateName, ref string debug)
        {
            // i don't think this works...
            // this often results in phantom layers that appear in the controller, but not the list of layers. you cannot delete them via Unity's UI.
            // the only way i've fixed it is by manually editing the controller file and removing the orphaned layers.
            if ( ! acLayer.stateMachine ) {
                debug += $" - WARNING: layer '{acLayer.name}' has no state machine. Controller may be corrupted. Attempting to add...\n";
                acLayer.stateMachine = new AnimatorStateMachine();
                acLayer.stateMachine.name = acLayer.name;
                AssetDatabase.AddObjectToAsset(acLayer.stateMachine, AssetDatabase.GetAssetPath(controller));
            }

            var curMachine = acLayer.stateMachine;

            debug += $" - Looking for layer '{acLayer.name}' with state machine '{subMachinePath}'\n";

            // follow down the heiarchy of state machines until we reach the final one
            subMachinePath.Trim('/');
            if ( subMachinePath != "" ) {
                string[] subMachineNames = subMachinePath.Split('/');
                foreach ( string machineName in subMachineNames ) {
                    bool found = false;
                    foreach ( var subMachine in curMachine.stateMachines ) {
                        if ( subMachine.stateMachine.name == machineName ) {
                            curMachine = subMachine.stateMachine;
                            found = true;
                            debug += $"   - found state machine '{machineName}'.\n";
                            break;
                        }
                    }

                    if ( ! found ) {
                        debug += $"   - could not find state machine '{machineName}'. Creating.\n";
                        var position = curMachine.stateMachines.Length > 0 ? curMachine.stateMachines[curMachine.stateMachines.Length - 1].position + new Vector3(0, 65) : new Vector3(450, 0, 0);
                        curMachine = curMachine.AddStateMachine(machineName, position);
                    }
                }
            }

            // find or create the state
            foreach ( var state in curMachine.states ) {
                if ( state.state.name == stateName ) {
                    debug += $"   - state '{stateName}' found.\n";
                    return state.state;
                }
            }

            debug += $"   - state '{stateName}' not found. Creating.\n";
            return curMachine.AddState(stateName);
        }


        /**
         * STAGE 8
         * Move offset data into the local animation data object so it will be saved to file.
         * 
         * TODO: evaluate if this is actually useful
         */
        static void SaveOffsetsInAnimData(ImportContext ctx)
        {
            foreach ( var tag in ctx.file.frameTags ) {
                string animName = tag.name;

                foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                    var target = entry.Value;
                    var targetPath = target.path;

                    for ( int i = tag.from; i <= tag.to; i++ ) {
                        int frameNum = i - tag.from;

                        // save any offset data in data section
                        Vector2 coord;

                        // if the target has texture (pixel) coordinates for pivot data, save it
                        // note, a target can have texture pivots and not normalized pivots if it doesn't have a sprite.
                        // normalized pivots are already saved in the target's sprite data.
                        if ( target.offsets.TryGetValue(i, out coord) ) {
                            if ( ! ctx.animData.animations[animName].targets.ContainsKey(targetPath) ) {
                                ctx.animData.animations[animName].targets.Add(targetPath, new TargetData { path = targetPath });
                            }
                            ctx.animData.animations[animName].targets[targetPath].offsets.Add(frameNum, coord);
                        }
                    }
                }
            }
        }


        /**
         * STAGE 9
         * Create an animation event on each keyframe that sets the frame number.
         * Unity doesn't make it easy to extract, so it's better to say what frame we're on manually. If function is not named in settings, 
         * then OnFrame("clip_name::frameNum") will be called.
         */
        public static void GenerateFrameEvents(ImportContext ctx)
        {
            var time = 0.0f;

            foreach ( var clipInfo in ctx.generatedClips ) {
                var clip = clipInfo.clip;
                var events = new List<AnimationEvent>(clip.events);
                var functionName = ctx.settings.eventFunctionName;

                if ( functionName == "" ) {
                    functionName = "OnFrame";
                }

                for ( int i = clipInfo.tag.from, frameNum = 0; i <= clipInfo.tag.to; i++, frameNum++ ) {
                    var evt = new AnimationEvent
                    {
                        time = time,
                        functionName = functionName,
                        stringParameter = $"{clipInfo.clipName}::{frameNum}",
                        messageOptions = SendMessageOptions.DontRequireReceiver // tell unity not to complain if functionName doesn't yet exist
                    };
                    time += ctx.file.frames[i].duration * 0.001f;   // aesprite time is in ms, convert to seconds
                    events.Add(evt);
                }

                events.Sort((lhs, rhs) => lhs.time.CompareTo(rhs.time));
                AnimationUtility.SetAnimationEvents(clip, events.ToArray());
                EditorUtility.SetDirty(clip);
            }
        }

    }
}