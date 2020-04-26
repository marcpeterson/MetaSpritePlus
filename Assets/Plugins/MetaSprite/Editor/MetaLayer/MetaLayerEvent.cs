﻿using System;
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

            foreach (var frametag in file.frameTags) {
                var clip = ctx.generatedClips[frametag];
                var events = new List<AnimationEvent>(clip.events);

                var time = 0.0f;
                for (int f = frametag.from; f <= frametag.to; ++f) {
                    if (eventFrames.Contains(f)) {
                        var evt = new AnimationEvent {
                            time = time,
                            functionName = layer.GetParamString(0),
                            messageOptions = SendMessageOptions.DontRequireReceiver
                        };

                        // Debug.Log(paramType + ", " + layer.metaInfo.ParamCount);

                        dynamic value = layer.GetParam(1);
                        if ( value.GetType().Equals(typeof(string)) ) {
                            evt.stringParameter = (string) value;
                        } else if ( value.GetType().Equals(typeof(int)) ) {
                            evt.intParameter = (int) value;
                            evt.floatParameter = (float) value;
                        } else if ( value.GetType().Equals(typeof(float)) ) {
                            evt.floatParameter = (float) value;
                        }

                        /*
                        if (paramType == LayerParamType.String) {
                            evt.stringParameter = layer.GetParamString(1);
                        } else if (paramType == LayerParamType.Number) {
                            var fval = layer.GetParamFloat(1);
                            evt.floatParameter = fval;
                            if (fval == Math.Floor(fval)) {
                                evt.intParameter = (int) fval;
                            } 
                        }
                        */

                        events.Add(evt);
                    }

                    time += file.frames[f].duration * 1e-3f;
                }

                events.Sort((lhs, rhs) => lhs.time.CompareTo(rhs.time));
                AnimationUtility.SetAnimationEvents(clip, events.ToArray());
                EditorUtility.SetDirty(clip);
            }

        }

    }

}