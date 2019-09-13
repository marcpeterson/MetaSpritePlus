

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MetaSprite {

    public class MetaLayerPivot : MetaLayerProcessor {
        public override string actionName {
            get { return "pivot"; }
        }
        
        public override int executionOrder {
            get {
                return 1; // After atlas generation
            }
        }

        public struct PivotFrame {
            public int frame;
            public Vector2 pivot;
        }

        public struct OffsetFrame
        {
            public int frame;
            public Vector2 offset;
        }

        /**
         * Retusn a list of frames, and the pivot on each frame.  Note the pivot is in pixel coordinates
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

                            var col = cel.GetPixelRaw(x, y);
                            if ( col.a > 0.1f ) {
                                center += new Vector2(texX, texY);
                                ++pixelCount;
                            }
                        }
                    }

                    if ( pixelCount > 0 ) {
                        // pivot becomes the average of all pixels found
                        center /= pixelCount;
                        pivots.Add(new PivotFrame { frame = i, pivot = center });
                    } else {
                        Debug.LogWarning($"Pivot layer '{pivotLayer.layerName}' is missing a pivot pixel in frame {i}");
                    }
                }
            }

            return pivots;
        }

        public override void Process(ImportContext ctx, Layer layer)
        {
            var pivots = CalcPivotsForAllFrames(ctx, layer);

            if ( pivots.Count == 0 )
                return;

            var importer = AssetImporter.GetAtPath(ctx.atlasPath) as TextureImporter;
            var spriteSheet = importer.spritesheet;

            // each sprite in the sheet is a frame (no frame reuse, yet)
            for ( int i = 0; i < spriteSheet.Length; ++i) {
                
                // find the pivot for this frame
                int j = 1;
                while (j < pivots.Count && pivots[j].frame <= i) ++j; // j = index after found item

                // get the pivot for this frame
                Vector2 pivot = pivots[j - 1].pivot;

                // pivot is in pixel coordinates, convert to percent of bounding rectangle of width 1x1
                // - translate the pivot's texture location to its location in the sprite/image
                pivot -= ctx.spriteCropPositions[i];

                // default pixel origin is bottom left.  if centered, add half a pixel in x and y directions
                if ( ctx.settings.pixelOrigin == PixelOrigin.Center ) {
                    pivot += new Vector2(0.5f, 0.5f);
                }

                // - now translate pivot's sprite/frame location as a percentage of its dimensions (1x1)
                pivot =  Vector2.Scale(pivot, new Vector2(1.0f / spriteSheet[i].rect.width, 1.0f / spriteSheet[i].rect.height));
                
                spriteSheet[i].pivot = pivot;

                ctx.spritePivots[i] = pivot;
            }

            importer.spritesheet = spriteSheet;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }

}