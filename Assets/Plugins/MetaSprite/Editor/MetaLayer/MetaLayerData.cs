using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MetaSpritePlus {

    public class MetaLayerData : MetaLayerProcessor {
        public override string actionName {
            get { return "data"; }
        }

        public override int executionOrder {
            get {
                return 2; // Ensure pivots have been calculated
            }
        }

        public override void Process(ImportContext ctx, Layer layer)
        {
            var file = ctx.file;

            var importer = AssetImporter.GetAtPath(ctx.atlasPath) as TextureImporter;
            var spriteSheet = importer.spritesheet;

            string dataName = "";
            if ( layer.parameters.Count == 1 ) {
                // if one parameter, then it's the name of the data point
                dataName = layer.GetParamString(0);
            } else if ( layer.parameters.Count > 1 ) {
                // if two parameters, then 2nd is the name
                dataName = layer.GetParamString(1);
            }

            var data = new FrameData();

            if ( ! ctx.targets.ContainsKey(layer.targetPath) ) {
                Debug.LogWarning($"@data layer '{layer.layerName}' has invalid target '{layer.targetPath}' and has been skipped");
                return;
            }

            var target = ctx.targets[layer.targetPath];

            // each tag represents a different animation.  look at each frame of each tag.  store coordinates of any visible pixels.
            // these represent the data points.
            foreach ( var tag in ctx.file.frameTags ) {
                string animName = tag.name;
                Vector3 distance = Vector3.zero;
                int numFrames = tag.to - tag.from + 1;

                for ( int i = tag.from, j = 0; i <= tag.to; ++i, j++ ) {
                    int frameNum = i - tag.from;
                    var frameData = new FrameData { coords = new List<Vector2>() };
                    Cel cel;
                    file.frames[i].cels.TryGetValue(layer.index, out cel);

                    if ( cel != null ) {
                        int pixelCount = 0;

                        for ( int y = 0; y < cel.height; ++y ) {
                            for ( int x = 0; x < cel.width; ++x ) {
                                // tex coords relative to full texture boundaries
                                int texX = cel.x + x;
                                int texY = -(cel.y + y) + file.height - 1;

                                // store position of any visible pixels
                                var pixel = cel.GetPixelRaw(x, y);
                                if ( pixel.a > 0.1f ) {
                                    // coordinate of the pixel on the original texture image (from bottom left corner)
                                    Vector2 coord = new Vector2(texX, texY);

                                    // get the target's pivot location on the original texture
                                    Vector2 pivotTex;
                                    if ( target.pivots.ContainsKey(frameNum) ) {
                                        pivotTex = target.pivots[frameNum];
                                    } else {
                                        pivotTex = Vector2.zero;
                                        Debug.Log($"@data layer '{layer.layerName}' has no pivot for frame {frameNum}");
                                    }

                                    // subtract pivot location to make coordinate relative to target's pivot
                                    coord -= pivotTex;

                                    /* this scales the position based on the layer's sprite's dimensions, if it has one.
                                     * not sure it's useful anymore...
                                    // convert coordinate relative to target sprite's dimension
                                    // could also use ctx.animData.animations[animName].targets[layer.targetPath].sprites[frameNum]
                                    Dimensions dimensions;
                                    if ( target.dimensions.TryGetValue(frameNum, out dimensions) ) {
                                        // default pixel origin is bottom left. center to the actual pixel by adding 0.5 in x and y directions
                                        coord += new Vector2(0.5f, 0.5f); // NO!! saving pixel coordinates, not local/world coordinates
                                        coord = new Vector2(coord.x/dimensions.width, coord.y/dimensions.height);
                                    } else {
                                        // if target doesn't have dimensions, then it has no sprite. coordinate cannot be normalized based
                                        // on sprite's size. just store the pixel location.
                                        // Debug.Log($"@data layer '{layer.layerName}' has no sprite for frame {frameNum}");
                                    }
                                    */

                                    /* TODO - calculate previous pivot cumulative distance
                                    // if calculating "prev pivot" data, and this is first pixel (should only be one), then store its distance
                                    if ( dataName == "prev pivot" && pixelCount == 0 ) {
                                        // coord is distance from pivot.  negate to make positive, and round to get rid of float errors
                                        distance += new Vector3(-Mathf.Round(coord.x), -Mathf.Round(coord.y), 0);
                                    }
                                    */

                                    frameData.coords.Add(coord);
                                    ++pixelCount;
                                }
                            }
                        }

                        if ( pixelCount > 0 ) {
                            if ( ! ctx.animData.animations[animName].targets.ContainsKey(layer.targetPath) ) {
                                // Debug.LogWarning($"@data layer '{layer.layerName}' has a target with no sprites. Saving anyways.");
                                ctx.animData.animations[animName].targets.Add(layer.targetPath, new TargetData());
                            }

                            ctx.animData.animations[animName].targets[layer.targetPath].data.Add($"{dataName}::{frameNum}", frameData);
                        }
                    }
                }

                /*
                // if we've collected all the data for this animation, save it
                if ( data.frames.Count > 0 ) {s

                    if ( !ctx.targets.ContainsKey(layer.targetPath) ) {
                        Debug.LogWarning($"@data layer '{layer.layerName}' has invalid target '{layer.targetPath}' and has been skipped");
                        continue;
                    }

                    if ( ! ctx.animData.animations.ContainsKey(animName) ) {
                        Debug.LogWarning($"@data layer '{layer.layerName}' has invalid animation '{animName}' and has been skipped");
                    }

                    ctx.animData.animations[animName].targets[layer.targetPath].data.Add(dataName, data);
                    
                    if ( dataName == "prev pivot" ) {
                        // ctx.animData.animations[animName].targets[layer.targetPath].distance = distance;
                    }
                }
                */
            }

//            Debug.Log(data);
        }
    }

}