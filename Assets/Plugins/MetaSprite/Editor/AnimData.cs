using UnityEngine;
using System.Collections;
using System.Collections.Generic;
//using System.Diagnostics;   // for stacktrace
using RotaryHeart.Lib.SerializableDictionary;

using GenericToDataString;  // for object dumper

/**
 * Animation data
 * Defined in layers named @data("subject"), any pixels with alpha > 0 will be saved as data points.
 * Since multiple tags can deliminate different animations in the same file, we save data for each animation.
 * Data will be saved in a ScriptingObject asset file with the same name as the base name in the Aesprite Import config.
 *   AnimData : ScriptableObject {
 *     ppu                                     : currently needed to compensate for sprite flipping (calculates world-coordinates per pixel)
 *     animation<"anim name", Animation>       : AnimDictionary key is name of animation, such as "run e"
 *       numFrames,                            : number of frames in this animation
 *       targets<"path", TargetData>           : TargetDictionary key is path to game object, such as "/body/top/l arm"
 *         path,                               : path to target's game object, such as "/body/top/l arm"
 *         atlasId,                            : base name of target in sprite atlas
 *         distance,                           : optional distance between all pivots. only filled if "prev pivot' found
 *         sprites<frame, SpriteData>          : one entry per frame of animation
 *           frame,                              : frame number
 *           width,                              : width of sprite in pixels
 *           height,                             : height of sprite in pixels
 *           pivot                               : sprite's pivot, from 0-1 in relation to pixel width/height
 *         data<"data name::frame", FrameData> : Data key is name of data point. eg "l foot pos"
 *           frame,                              : frame number
 *           coords<Vector2>                     : list of coordinates
 *         
 *   
 * HINTS
 *  - Aesprite defaults the frist frame number at 1.  But you can change this to 0.  Either way, frames in framedata will be numbered
 *    from 0, so it's best to make Aesprite match.
 *  - If you import a file and the animation data file already exists, it will not be updated until you save the Unity project.
 */

namespace MetaSpritePlus
{
    [System.Serializable]
    public class Animation                               // was AnimList
    {
        public int numFrames = 0;
        [SerializeField] public TargetDictionary targets = new TargetDictionary();
    }

    [System.Serializable]
    public class TargetData
    {
        public string path;                     // path to gameobject we will render to. use this for TargetDictionary key?
        public string atlasId;                  // base name of sprite in atlas for this target. must be appended by "_{frame num}" for an actual sprite.
        [SerializeField] public Vector3 distance         = Vector3.zero;            // will only be filled if data "prev pivot" found.  it's the sum of all pivot differences.
        [SerializeField] public SpriteDictionary sprites = new SpriteDictionary();  // indexed by frame number?
        [SerializeField] public DataDictionary data      = new DataDictionary();
        [SerializeField] public CoordDictionary offsets  = new CoordDictionary();   // offset location, in pixels, of target pivot in relation to parent pivot
    }

    
    [System.Serializable]
    public class FrameData
    {
        [SerializeField] public List<Vector2> coords = new List<Vector2>();       // each frame may have more than one coordinate of data
    }

    [System.Serializable]
    public class SpriteData
    {
        [SerializeField] public int width  = 0; // width of sprite in pixels
        [SerializeField] public int height = 0; // height of sprite in pixels
        [SerializeField] public Vector2 pivot;  // sprite pivot normalized (from 0 to 1)
    }


    [System.Serializable]
    public class AnimDictionary : SerializableDictionaryBase<string, Animation> { }      // indexed by name of animation. eg: 'run e'

    [System.Serializable]
    public class DataDictionary : SerializableDictionaryBase<string, FrameData> { }      // indexed by name of data point and frame. eg: 'l foot pos::1'. was FrameDictionary

    [System.Serializable]
    public class TargetDictionary : SerializableDictionaryBase<string, TargetData> { }   // indexed by path to object?
    
    [System.Serializable]
    public class SpriteDictionary : SerializableDictionaryBase<int, SpriteData> { }      // indexed by frame of animation

    [System.Serializable]
    public class CoordDictionary : SerializableDictionaryBase<int, Vector2> { }          // indexed by frame of animation


    public class AnimData : ScriptableObject
    {
        public float ppu;                   // needed to convert sprite dimensions/coordinates to world dimensions/coordinates
        public AnimDictionary animations;   // was animDict

        void OnEnable()
        {
            // if already exists or serialized avoid multiple instantiation to avoid memory leak
            if ( animations == null ) {
                animations = new AnimDictionary();
            }
        }


        /**
         * Gets data coordinates, but returns them in local-space by using PPU
         */
        public List<Vector3> GetLocalData(string clipName, string targetName, string dataName, int frameNum, bool flipX=false)
        {
            List<Vector2> texelCoords = GetData(clipName, targetName, dataName, frameNum);
            List<Vector3> localCoords = new List<Vector3>();
            if ( texelCoords != null ) {
                for ( int i=0; i<texelCoords.Count; i++ ) {
                    Vector3 coord = texelCoords[i] / ppu;
                    if ( flipX ) {
                        coord.x = -coord.x;
                    }
                    localCoords.Add(coord);
                }
            }
            return localCoords;
        }


        public Vector2 GetSpriteDimensions(string clipName, string targetName, int frameNum)
        {
            if ( animations.TryGetValue(clipName, out Animation animation) ) {
                if ( animation.targets.TryGetValue(targetName, out TargetData targetData) ) {
                    if ( targetData.sprites.TryGetValue(frameNum, out SpriteData spriteData) ) {
                        return new Vector2(spriteData.width, spriteData.height);
                    }
                }
            }
            return Vector2.zero;
        }


        /**
         * Returns the first coordinate for the animation and data name. Or null.
         */
        public Vector2? GetFirstData(string clipName, string targetName, string dataName, int frame)
        {
            List<Vector2> coords = GetData(clipName, targetName, dataName, frame);
            if ( coords != null ) {
                return coords[0];
            }

            return null;
        }


        /**
         * Gets all the data for the specified animation and target. Or null.
         */
        public List<Vector2> GetData(string clipName, string targetName, string dataName, int frameNum)
        {
            if ( animations.TryGetValue(clipName, out Animation animation) ) {
                if ( animation.targets.TryGetValue(targetName, out TargetData targetData) ) {
                    if ( targetData.data.TryGetValue($"{dataName}::{frameNum}", out FrameData frameData) ) {
                        if ( frameData != null ) {
                            return frameData.coords;
                        }
                    }
                }
            }

            return null;
        }


        /**
         * Calculates the difference in world-space between data points in two clips.
         * Typically, this is the position of something in the last frame of one animation, 
         * and the position of that in the first frame of the next animation.
         */
        public Vector3 DataDiff(string clipFrom, string targetFrom, string dataFrom, int frameFrom, bool flipFrom, string clipTo, string targetTo, string dataTo, int frameTo, bool flipTo, bool debug = false)
        {
            Vector2? temp = GetFirstData(clipFrom, targetFrom, dataFrom, frameFrom);
            if ( temp == null ) {
                Debug.LogWarning($"no coord for clip '{clipFrom}' data '{dataFrom}' at frame {frameFrom}");
                return Vector3.zero;
            }
            Vector2 from = (Vector2) temp / ppu;

            temp = GetFirstData(clipTo, targetTo, dataTo, frameTo);
            if ( temp == null ) {
                Debug.LogWarning($"no coord for clip '{clipTo}' data '{dataTo}' at frame {frameTo}");
                return Vector3.zero;
            }
            Vector2 to = (Vector2) temp / ppu;

            if ( flipFrom ) {
                from.x = -from.x;
            }

            if ( flipTo ) {
                to.x = -to.x;
            }

            Vector3 diff = new Vector3(to.x - from.x, to.y - from.y, 0);
            if ( debug ) {
                Debug.Log($"from '{dataFrom}':({from.x}, {from.y})  to '{dataTo}':({to.x}, {to.y}) = diff ({diff.x}, {diff.y})");
                Debug.Log($"pxl from '{dataFrom}':({from.x * ppu}, {from.y * ppu})  to '{dataTo}':({to.x * ppu}, {to.y * ppu}) = diff ({diff.x * ppu}, {diff.y * ppu})");
            }

            return diff;
        }


        /**
         * Converts a coordinate from sprite-space to world-space.
         * 
         * Given the coord within the sprite, the sprite's dimensions, and the position of the sprite in worlds-space, converts the coordinate
         * to world space.  Can also pass the "flipX=true" boolean if the sprite is flipped on the x-axis.
         * 
         * A pixel origin is in its center so it doesn't require compensation if the sprite is flipped.
         */
        public Vector3 SpriteCoordToWorld(Vector2 coord, string clipName, string targetName, int frame, Vector3? spriteWorldPos = null, bool flipX = false, bool popZ = false)
        {
            // compensate for defaulting to null
            if ( spriteWorldPos == null ) {
                spriteWorldPos = Vector3.zero;
            }

            // get the sprite's dimensions
            Vector2? temp = GetFirstData(clipName, targetName, "dims", frame);
            if ( temp == null ) {
                Debug.LogWarning($"no dimensions found for '{clipName}' in animation data");
                return Vector2.zero;
            }
            Vector2 dims = (Vector2) temp/ppu;

            // convert from sprite-space to local-space
            Vector3 worldPos;
            if ( flipX ) {
                worldPos = new Vector3(-coord.x * dims.x, coord.y * dims.y, 0);
            } else {
                worldPos = new Vector3(coord.x * dims.x, coord.y * dims.y, 0);
            }

            // convert from local-space to world-space by adding the position of the sprite
            // the sprite's position should be where the sprite's pivot is.  the coordinate is originally placed in relation to this pivot's position in the sprite.
            worldPos += (Vector3) spriteWorldPos;

            // NOTE: this is only helpful if sprites' depth is calculated based on z-position. Some may use y-position instead, making this less useful.
            if ( popZ ) {
                worldPos.z = ((Vector3) spriteWorldPos).z - 0.01f;    // place in front of sprite
            }

            return worldPos;
        }


        /**
         * The distance, in world coordinates, between last frame's pivot and this frame's pivot
         */
        public Vector3 GetPivotDiff(string clipName, int frame, bool flipX = false)
        {
            // previous pivot's coord is relative to the current pivot
            Vector2? temp = GetFirstData(clipName, "/", "prev pivot", frame);
            if ( temp == null ) {
                return Vector2.zero;
            }
            Vector2 prevPos = (Vector2) temp;

            Vector2 diff = - prevPos;
            diff = diff / ppu;

            if ( flipX ) {
                return new Vector3(-diff.x, diff.y, 0);
            } else {
                return new Vector3(diff.x, diff.y, 0);
            }
        }


        public int GetNumFrames(string clipName)
        {
            if ( animations.TryGetValue(clipName, out Animation animation) ) {
                return animation.numFrames;
            }

            Debug.LogWarning($"No frame count for '{clipName}' in animation data");
            return 0;
        }


        public Vector3 GetDistance(string clipName, string targetName)
        {
            Debug.LogWarning("GetDistance() needs updating");
            if ( animations.TryGetValue(clipName, out Animation animation) ) {
                if ( animation.targets.TryGetValue(targetName, out TargetData targetData ) ) {
                    return targetData.distance;
                }
            }

            Debug.LogWarning($"no distance for clip:'{clipName}' target:'{targetName}'");
            return Vector3.zero;
        }

    }
}
