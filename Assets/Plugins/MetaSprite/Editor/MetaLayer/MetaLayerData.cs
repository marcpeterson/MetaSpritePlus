

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
            // the name of the data variable should be the first parameter. eg @data("leg l")
            string dataName = layer.GetParamString(0);

            var file = ctx.file;

            var importer = AssetImporter.GetAtPath(ctx.atlasPath) as TextureImporter;
            var spriteSheet = importer.spritesheet;

            // each tag represents a different animation.  look at each frame of each tag.  store coordinates of any visible pixels.
            // these represent the data points.
            foreach ( var tag in ctx.file.frameTags ) {
                string animName = tag.name;
                Vector3 distance = Vector3.zero;
                var frameDataList = new FrameDataList {
                    frames = new List<FrameData>()
                };
                int numFrames = tag.to - tag.from + 1;

                for ( int i = tag.from, j = 0; i <= tag.to; ++i, j++ ) {
                    var frameData = new FrameData { frame = j, coords = new List<Vector2>() };
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
                                var pxl = cel.GetPixelRaw(x, y);
                                if ( pxl.a > 0.1f ) {
                                    // start the coordinate of the pixel on the layer (from bottom left corner)
                                    Vector2 coord = new Vector2(texX, texY);

                                    // default pixel origin is bottom left.  if centered, add half a pixel in x and y directions
                                    if ( ctx.settings.pixelOrigin == PixelOrigin.Center ) {
                                        coord += new Vector2(0.5f, 0.5f);
                                    }

                                    // calculate position in relation to pivot
                                    Vector2 pivot = spriteSheet[i].pivot;
                                    Vector2 pivotPxl = new Vector2(pivot.x * spriteSheet[i].rect.width, pivot.y * spriteSheet[i].rect.height);

                                    // get coordinate relative to pivot
                                    coord -= ctx.spriteCropPositions[i];
                                    coord -= pivotPxl;

                                    // if calculating "prev pivot" data, and this is first pixel (should only be one), then store its distance
                                    if ( dataName == "prev pivot" && pixelCount == 0 ) {
                                        // coord is distance from pivot.  negate to make positive, and round to get rid of float errors
                                        distance += new Vector3(-Mathf.Round(coord.x), -Mathf.Round(coord.y), 0);
                                    }

                                    // points are all relative to the sprite's bounding rectangle, which is 1 by 1 in both dimensions 
                                    // regardless of sprite size.  So (0.5, 0.5) would be the center of the sprite.
                                    // it's ok for points to be outside the bounding rectangle.  they'll just be less than 0, or greater than 1.
                                    // WHY? so if the sprite is transformed, everything stays relative. You can multiply points by the transforms
                                    // to get their position relative to the transform.
                                    // NOTE: spriteSheet[i].rect.width/height are in pixels
                                    coord = new Vector2(coord.x/spriteSheet[i].rect.width, coord.y/spriteSheet[i].rect.height);

                                    frameData.coords.Add(coord);
                                    ++pixelCount;
                                }
                            }
                        }

                        if ( pixelCount > 0 ) {
                            frameDataList.frames.Add(frameData);
                        }
                    }
                }

                // if we've collected all the data for this animation, save it in appropriate dictionary spot
                if ( frameDataList.frames.Count > 0 ) {
                    if ( ctx.animData.animDict.ContainsKey(animName) ) {
                        ctx.animData.animDict[animName].frameDict.Add(dataName, frameDataList);
                        if ( dataName == "prev pivot" ) {
                            ctx.animData.animDict[animName].distance = distance;
                        }
                    } else {
                        ctx.animData.animDict.Add(animName, new AnimList {
                            numFrames = numFrames,
                            distance = distance,
                            frameDict = new FrameDictionary() { {
                                dataName,
                                frameDataList
                            } }
                        });
                    }
//                    Debug.Log(ctx.animData.data["run e"]);
                }
            }

//            Debug.Log(data);
        }
    }

}