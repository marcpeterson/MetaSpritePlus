using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using TangentMode = UnityEditor.AnimationUtility.TangentMode;

namespace MetaSpritePlus {

    public class MetaLayerEvent : MetaLayerProcessor {

        public override string actionName {
            get { return "event"; }
        }

        public override void Process(ImportContext ctx, Layer layer) {
            var eventFrames = new HashSet<int>();
            var file = ctx.file;

            for (int i = 0; i < file.frames.Count; ++i) {
                bool isEvent = file.frames[i].cels.ContainsKey(layer.index);
                if (isEvent) {
                    eventFrames.Add(i);
                }
            }

            LayerParamType paramType = layer.GetParamType(1);

            /*
            foreach (var frametag in file.frameTags) {
                var clip = ctx.generatedClips[frametag];
            */

            foreach ( var clipInfo in ctx.generatedClips ) {
                var clip = clipInfo.clip;
                var events = new List<AnimationEvent>(clip.events);

                var time = 0.0f;
                for (int f = clipInfo.tag.from; f <= clipInfo.tag.to; ++f) {
                    if (eventFrames.Contains(f)) {
                        var evt = new AnimationEvent {
                            time = time,
                            functionName = layer.GetParamString(0),
                            messageOptions = SendMessageOptions.DontRequireReceiver
                        };

                        dynamic value = layer.GetParam(1);
                        if ( value.GetType().Equals(typeof(string)) ) {
                            evt.stringParameter = (string) value;
                        } else if ( value.GetType().Equals(typeof(int)) ) {
                            evt.intParameter = (int) value;
                            evt.floatParameter = (float) value;
                        } else if ( value.GetType().Equals(typeof(float)) ) {
                            evt.floatParameter = (float) value;
                        }

                        events.Add(evt);
                    }

                    time += file.frames[f].duration * 0.001f;   // aesprite time is in ms, convert to seconds
                }

                events.Sort((lhs, rhs) => lhs.time.CompareTo(rhs.time));
                AnimationUtility.SetAnimationEvents(clip, events.ToArray());
                EditorUtility.SetDirty(clip);
            }

        }

    }

}