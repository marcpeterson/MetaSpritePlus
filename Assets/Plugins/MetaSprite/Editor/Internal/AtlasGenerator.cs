using System;   // for throwing exception
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MetaSpritePlus.Internal {

    public static class AtlasGenerator {

        struct PackData {
            public int width,  height;
        }

        struct PackPos {
            public int x, y;
        }

        class PackResult {
            public int imageSize;
            public List<PackPos> positions;
        }


        public static List<Sprite> GenerateSplitAtlas(ImportContext ctx, List<Layer> layers, string atlasPath) {
            var file     = ctx.file;
            var settings = ctx.settings;
            var path     = atlasPath;

            var images     = CreateTargetImages(ctx, layers);
            var packList   = images.Select(image => new PackData { width = image.finalWidth, height = image.finalHeight }).ToList();
            var packResult = PackAtlas(packList, settings.border);

            if ( packResult.imageSize > 2048 ) {
                Debug.LogWarning("Generate atlas size is larger than 2048, this might force Unity to compress the image.");
            }

            var texture = new Texture2D(packResult.imageSize, packResult.imageSize);
            var transparent = new Color(0,0,0,0);
            for ( int y = 0; y < texture.height; ++y ) {
                for ( int x = 0; x < texture.width; ++x ) {
                    texture.SetPixel(x, y, transparent);
                }
            }

            var metaList = new List<SpriteMetaData>();

            for ( int i = 0; i < images.Count; ++i ) {
                var pos = packResult.positions[i];
                var image = images[i];

                for ( int y = image.miny; y <= image.maxy; ++y ) {
                    for ( int x = image.minx; x <= image.maxx; ++x ) {
                        int texX = (x - image.minx) + pos.x;
                        int texY = -(y - image.miny) + pos.y + image.finalHeight - 1;
                        texture.SetPixel(texX, texY, image.GetPixel(x, y));
                    }
                }

                if ( ! ctx.targets.TryGetValue(image.target, out Target target) ) {
                    Debug.LogError($"Could not find sprite name for target '{image.target}'");
                    continue;
                }

                // The metadata describes the sprite in Unity's terms
                var metadata        = new SpriteMetaData();
                metadata.name       = target.spriteName + "_" + image.frame;
                metadata.alignment  = (int) SpriteAlignment.Custom;
                metadata.rect       = new Rect(pos.x, pos.y, image.finalWidth, image.finalHeight);

                Vector2 newPivotNorm;
                Vector2 cropPos = new Vector2(image.minx, file.height - image.maxy - 1);

                if ( target == null ) {

                    /* TODO: UNTESTED */

                    Vector2 oldPivotNorm = settings.PivotRelativePos;

                    // calculate sprite pivot position relative to the entire source texture
                    var oldPivotTex = Vector2.Scale(oldPivotNorm, new Vector2(file.width, file.height));

                    // re-calculate sprite pivot position relative to the sprite image's position within the texture.
                    // texture coords are from top-left.  but unity coords are from bot-left.  so y-value must be recalulated from bottom.
                    var newPivotTex = oldPivotTex - new Vector2(image.minx, file.height - image.maxy - 1);

                    // Note: no need to offset by half a pixel because we're not using sprite pixels to represent the pivot

                    // normalize the pivot position based on the dimensions of the sprite's image.
                    newPivotNorm = Vector2.Scale(newPivotTex, new Vector2(1.0f / image.finalWidth, 1.0f / image.finalHeight));
                } else {
//                    Debug.Log($"  - target {target.targetPath} - pivot count: {target.pivots.Count}");

                    // TODO: if no pivots, can we skip for this frame?


                    Vector2 pivotTex = target.pivots.Where(it => it.frame == image.frame)
                        .Select(it => it.coord)
                        .FirstOrDefault();        // default is (0,0)

                    pivotTex -= cropPos;

                    // default pixel origin is bottom left.  if centered, add half a pixel in x and y directions
                    if ( ctx.settings.pixelOrigin == PixelOrigin.Center ) {
                        pivotTex += new Vector2(0.5f, 0.5f);
                    }

                    // now translate pivot's sprite/frame location as a percentage of its dimensions (1x1)
                    newPivotNorm =  Vector2.Scale(pivotTex, new Vector2(1.0f / image.finalWidth, 1.0f / image.finalHeight));
                }

                metadata.pivot = newPivotNorm;
                ctx.spriteCropPositions.Add(cropPos);
                ctx.spriteDimensions.Add(new Vector2(image.finalWidth, image.finalHeight));
                ctx.spritePivots.Add(newPivotNorm);

                metaList.Add(metadata);
            }

            var bytes = texture.EncodeToPNG();

            File.WriteAllBytes(path, bytes);

            // Import texture
            AssetDatabase.Refresh();
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = settings.ppu;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            importer.spritesheet = metaList.ToArray();
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.maxTextureSize = 4096;

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            return GetAtlasSprites(path);
        }


        /**
         * Generates all the images for all frames of animation.
         * 
         * Iterates through all the targets. Then for each frame of animation, grab all the cels for each layer, merge
         * them into a single image, and store. Returns this list of images, indexed by the name of the target.
         */
        private static List<FrameImage> CreateTargetImages(ImportContext ctx, List<Layer> layers)
        {
            var file   = ctx.file;
            var images = new List<FrameImage>();

            // for each target, generate a sprite for each frame of animation
            foreach ( KeyValuePair<string, Target> entry in ctx.targets ) {
                var target     = entry.Value;
                var targetPath = target.path;
                var spriteName = target.spriteName;

                // gets all the layers that make up this target
                var targetLayersIds = layers.Where(layer => layer.targetPath == targetPath)
                    .Select(layer => layer.index)
                    .OrderBy(index => index)
                    .ToArray();

                // for each frame, create the image for all cels (layers) associated with this target
                file.frames.ForEach(frame => {
                    var cels = frame.cels.Values
                        .Where(it => targetLayersIds.Contains(it.layerIndex))
                        .OrderBy(it => it.layerIndex)
                        .ToList();

                    var image = ExtractImage(ctx, cels);
                    if ( image.hasContent ) {
                        image.target = targetPath;
                        image.frame = frame.frameID;
                        images.Add(image);
                    }
                });
            };

            return images;
        }


        /**
         * Generates a single image from all the cels (layers) passed to it.
         */
        private static FrameImage ExtractImage(ImportContext ctx, List<Cel> cels)
        {
            var file     = ctx.file;
            var settings = ctx.settings;
            var image    = new FrameImage(file.width, file.height);

            foreach ( var cel in cels ) {
                for ( int cy = 0; cy < cel.height; ++cy ) {
                    for ( int cx = 0; cx < cel.width; ++cx ) {
                        var c = cel.GetPixelRaw(cx, cy);
                        if ( c.a != 0f ) {
                            var x = cx + cel.x;
                            var y = cy + cel.y;
                            if ( 0 <= x && x < file.width &&
                                0 <= y && y < file.height ) { // Aseprite allows some pixels out of bounds to be kept, ignore them
                                var lastColor = image.GetPixel(x, y);

                                // blending
                                var color = Color.Lerp(lastColor, c, c.a);
                                color.a = lastColor.a + c.a * (1 - lastColor.a);
                                color.r /= color.a;
                                color.g /= color.a;
                                color.b /= color.a;

                                image.SetPixel(x, y, color);

                                // expand image area
                                image.minx = Mathf.Min(image.minx, x);
                                image.miny = Mathf.Min(image.miny, y);

                                image.maxx = Mathf.Max(image.maxx, x);
                                image.maxy = Mathf.Max(image.maxy, y);
                            }
                        }
                    }
                }
            }

            if ( image.minx == int.MaxValue ) {
                image.minx = image.maxx = image.miny = image.maxy = 0;
            }

            if ( !settings.densePacked ) {       // override image border for sparsely packed atlas
                image.minx = image.miny = 0;
                image.maxx = file.width - 1;
                image.maxy = file.height - 1;
            }

            return image;
        }


        /// Pack the atlas
        static PackResult PackAtlas(List<PackData> list, int border) {
            int size = 128;
            while (true) {
                var result = DoPackAtlas(list, size, border);
                if (result != null)
                    return result;
                size *= 2;
            }
        }

        static PackResult DoPackAtlas(List<PackData> list, int size, int border) {
            // Pack using the most simple shelf algorithm
        
            List<PackPos> posList = new List<PackPos>();

            // x: the position after last rect; y: the baseline height of current shelf
            // axis: x left -> right, y bottom -> top
            int x = 0, y = 0; 
            int shelfHeight = 0;

            foreach (var data in list) {
                if (data.width > size)
                    return null;

                // if image has no content, give it 0,0 coordinates
                if ( data.width == 0 ) {
                    posList.Add(new PackPos { x = 0, y = 0 });
                    continue;
                }

                if (x + data.width + border > size) { // create a new shelf
                    y += shelfHeight;
                    x = 0;
                    shelfHeight = data.height + border;
                } else if (data.height + border > shelfHeight) { // increase shelf height
                    shelfHeight = data.height + border;
                }

                if (y + shelfHeight > size) { // can't place this anymore
                    return null;
                }

                posList.Add(new PackPos { x = x, y = y });

                x += data.width + border;
            }

            return new PackResult {
                imageSize = size,
                positions = posList
            };
        }

        static List<Sprite> GetAtlasSprites(string path) {
            // Get frames of the atlas
            var frameSprites = new List<Sprite>(); 
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (var asset in assets) {
                if (asset is Sprite) {
                    frameSprites.Add((Sprite) asset);
                }
            }
            
            return frameSprites
                .OrderBy(sprite => sprite.name)
//                .OrderBy(sprite => int.Parse(sprite.name.Substring(sprite.name.LastIndexOf('_') + 1)))
                .ToList();
        }

        class FrameImage {

            public bool hasContent = false; // used to skip empty frames
            public int frame;
            public string target;           // destination object to put sprite on

            public int minx = int.MaxValue, miny = int.MaxValue, 
                       maxx = int.MinValue, maxy = int.MinValue;

            public int finalWidth {
                get {
                    return ( maxx == minx ) ? 0 : maxx - minx + 1;
                }
            }

            public int finalHeight {
                get {
                    return (maxy == miny) ? 0 : maxy - miny + 1;
                }
            }

            readonly int width, height;

            readonly Color[] data;

            public FrameImage(int width, int height) {
                this.width = width;
                this.height = height;
                data = new Color[this.width * this.height];
                for (int i = 0; i < data.Length; ++i) {
                    data[i].a = 0;
                }
            }

            public Color GetPixel(int x, int y) {
                int idx = y * width + x;
                if (idx < 0 || idx >= data.Length) {
                    throw new Exception(string.Format("Pixel read of range! x: {0}, y: {1} where w: {2}, h: {3}", x, y, width, height));
                }
                return data[idx];
            }

            public void SetPixel(int x, int y, Color color) {
                data[y * width + x] = color;
                hasContent = true;
            }

        }

    }


}

