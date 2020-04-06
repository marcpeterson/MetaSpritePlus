using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using RotaryHeart.Lib.SerializableDictionary;

using GenericToDataString;  // for object dumper

/**
 * Animation data
 * Defined in layers named @data("subject"), any pixels with alpha > 0 will be saved as data points.
 * Since multiple tags can deliminate different animations in the same file, we save data for each animation.
 * Data will be saved in a ScriptingObject asset file with the same name as the base name in the Aesprite Import config.
 *   AnimData : ScriptableObject {
 *     ppu                                  : currently needed to compensate for sprite flipping (calculates world-coordinates per pixel)
 *     animation<"anim name", Animation>    : AnimDictionary key is name of animation, such as "run e"
 *       numFrames,                         : number of frames in this animation
 *       targets<"path", TargetData>        : TargetDictionary key is path to game object, such as "/body/top/l arm"
 *         path,                            : path to target's game object, such as "/body/top/l arm"
 *         atlasId,                         : base name of target in sprite atlas
 *         distance,                        : optional distance between all pivots. only filled if "prev pivot' found
 *         sprites<frame, SpriteData></frame>   : one entry per frame of animation
 *           frame,                             : frame number
 *           width,                             : width of sprite in pixels
 *           height,                            : height of sprite in pixels
 *           pivot                              : sprite's pivot, from 0-1 in relation to pixel width/height
 *         data<"data name::frame", FrameData   : Data key is name of data point. eg "l foot pos"
 *           frame,                             : frame number
 *           coords<Vector2>                    : list of coordinates
 *         
 *   
 * HINTS
 *  - Aesprite defaults the frist frame number at 1.  But you can change this to 0.  Either way, frames in framedata will be numbered
 *    from 0, so it's best to make Aesprite match.
 *  - If you import a file and the animation data file already exists, it will not be updated until you save the Unity project.
 * 
 *   
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
        [SerializeField] public Vector3 distance    = Vector3.zero;                 // will only be filled if data "prev pivot" found.  it's the sum of all pivot differences.
        [SerializeField] public SpriteDictionary sprites = new SpriteDictionary();  // indexed by frame number?
        [SerializeField] public DataDictionary data = new DataDictionary();
    }

    [System.Serializable]
    public class FrameData
    {
        [SerializeField] public int frame;
        [SerializeField] public List<Vector2> coords = new List<Vector2>();       // each frame may have more than one coordinate of data
    }

    [System.Serializable]
    public class SpriteData
    {
        [SerializeField] public int frame;      // also key to SpriteDictionary?
        [SerializeField] public int width  = 0; // width of sprite in pixels
        [SerializeField] public int height = 0; // height of sprite in pixels
        [SerializeField] public Vector2 pivot;  // sprite pivot (from 0 to 1)
    }


    [System.Serializable]
    public class AnimDictionary : SerializableDictionaryBase<string, Animation> { }       // indexed by name of animation. eg: 'run e'

    [System.Serializable]
    public class DataDictionary : SerializableDictionaryBase<string, FrameData> { }       // indexed by name of target and data point. eg: '/bot/l foot/base::l foot pos'. was FrameDictionary

    [System.Serializable]
    public class TargetDictionary : SerializableDictionaryBase<string, TargetData> { }   // indexed by path to object?
    
    [System.Serializable]
    public class SpriteDictionary : SerializableDictionaryBase<int, SpriteData> { }      // indexed by frame of animation


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
         * finds the first point for the animation and data name.  Or null.
         */
        public Vector2? FindFirstDataCoord(string clipName, string dataName, int frame)
        {
            List<Vector2> coords = FindAllData(clipName, dataName, frame);
            if ( coords != null ) {
                return coords[0];
            }

            return null;
        }


        /**
         * Gets all the data for the specified animation
         */
        public List<Vector2> FindAllData(string clipName, string dataName, int frame)
        {
            if ( animations.TryGetValue(clipName, out Animation animList) ) {

                Debug.Log("FindAllData() migration incomplete");

                /*
                if ( animList.frameDict.TryGetValue(dataName, out FrameDataList frameDataList) ) {
                    FrameData frameData = frameDataList.frames.Find(x => (x.frame == frame));
                    if ( frameData != null ) {
                        return frameData.coords;
                    }
                }
                */
            }

            return null;
        }


        /**
         * Calculates the difference in world-space between data points in two clips.
         * Typically, this is the position of something in the last frame of one animation, 
         * and the position of that in the first frame of the next animation.
         */
        public Vector3 dataCoordDiff(string clipFrom, string dataFrom, int frameFrom, bool flipFrom, string clipTo, string dataTo, int frameTo, bool flipTo, bool debug = false)
        {
            Vector2? temp = FindFirstDataCoord(clipFrom, dataFrom, frameFrom);
            if ( temp == null ) {
                Debug.LogWarning($"no coord for clip '{clipFrom}' data '{dataFrom}' at frame {frameFrom}");
                return Vector3.zero;
            }
            Vector2 from = (Vector2) temp;

            temp = FindFirstDataCoord(clipFrom, "dims", frameFrom);
            if ( temp == null ) {
                Debug.LogWarning($"no dimension found for clip '{clipFrom}' in animation data");
                return Vector3.zero;
            }
            Vector2 fromDims = (Vector2) temp/ppu;

            temp = FindFirstDataCoord(clipTo, dataTo, frameTo);
            if ( temp == null ) {
                Debug.LogWarning($"no coord for clip '{clipTo}' data '{dataTo}' at frame {frameTo}");
                return Vector3.zero;
            }
            Vector2 to = (Vector2) temp;

            temp = FindFirstDataCoord(clipTo, "dims", frameTo);
            if ( temp == null ) {
                Debug.LogWarning($"no dimension found for clip '{clipTo}' in animation data");
                return Vector3.zero;
            }
            Vector2 toDims = (Vector2) temp/ppu;

            // convert from sprite-space to local-space
            from = new Vector2(from.x * fromDims.x, from.y * fromDims.y);
            if ( flipFrom ) {
                from.x = -from.x;
            }

            to = new Vector2(to.x * toDims.x, to.y * toDims.y);
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
         * NOTE FOR DISPLAYING PIXELS
         * A pixel's origin can either be its bottom-left, or center.  Center is preferred as it doesn't require compensation if the sprite is flipped.
         * If bottom-left, then move the coordinate 1 pixel (1/ppu) in world space.  But only do so if the Pixel GameObject's pivot is in the bottom left, as
         * it is *not* being flipped, and is thus 1 pixel away from where it should be visibly placed.
         */
        public Vector3 SpriteCoordToWorld(Vector2 coord, string clipName, int frame, Vector3? spriteWorldPos = null, bool flipX = false, bool popZ = false)
        {
            // compensate for defaulting to null
            if ( spriteWorldPos == null ) {
                spriteWorldPos = Vector3.zero;
            }

            // get the sprite's dimensions
            Vector2? temp = FindFirstDataCoord(clipName, "dims", frame);
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

            if ( popZ ) {
                worldPos.z = ((Vector3) spriteWorldPos).z - 0.01f;    // place in front of sprite
            }

            return worldPos;
        }


        // The distance, in world coordinates, between last frame's pivot and this frame's pivot
        public Vector3 GetPivotDiff(string clipName, int frame, bool flipX = false)
        {
            // get the sprite's dimensions
            Vector2? temp = FindFirstDataCoord(clipName, "dims", frame);
            if ( temp == null ) {
                Debug.LogWarning($"no dimensions found for '{clipName}' in animation data at frame {frame}");
                return Vector2.zero;
            }
            Vector2 dims = (Vector2) temp/ppu;

            // previous pivot's coord is relative to the current pivot
            temp = FindFirstDataCoord(clipName, "prev pivot", frame);
            if ( temp == null ) {
                return Vector2.zero;
            }
            Vector2 prevPos = (Vector2) temp;

            Vector2 diff = - prevPos * dims;

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

            Debug.LogWarning($"no frame count for '{clipName}' in animation data");
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
