using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MetaSpritePlus {

    public static class ExtSpriteAlignment {

        public static Vector2 GetRelativePos(this SpriteAlignmentEx alignment, Vector2 defaultPivot) {
            switch (alignment) {
                case SpriteAlignmentEx.BottomCenter: return new Vector2(0.5f, 0);   
                case SpriteAlignmentEx.BottomLeft:   return new Vector2(0f, 0);     
                case SpriteAlignmentEx.BottomRight:  return new Vector2(1f, 0);     
                case SpriteAlignmentEx.Center:       return new Vector2(0.5f, 0.5f);
                case SpriteAlignmentEx.Custom:       return defaultPivot;   
                case SpriteAlignmentEx.LeftCenter:   return new Vector2(0, 0.5f);   
                case SpriteAlignmentEx.RightCenter:  return new Vector2(1, 0.5f);   
                case SpriteAlignmentEx.TopCenter:    return new Vector2(0.5f, 1f);  
                case SpriteAlignmentEx.TopLeft:      return new Vector2(0.0f, 1f);  
                case SpriteAlignmentEx.TopRight:     return new Vector2(1.0f, 1f);
                default: return Vector2.zero;  
            }
        }

    }

}