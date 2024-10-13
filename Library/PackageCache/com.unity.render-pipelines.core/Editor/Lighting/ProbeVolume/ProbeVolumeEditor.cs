using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditorInternal;

using Object = UnityEngine.Object;

namespace UnityEditor.Rendering
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ProbeVolume))]
    internal class ProbeVolumeEditor : Editor
    {
        SerializedProbeVolume m_SerializedProbeVolume;
        internal const EditMode.SceneViewEditMode k_EditShape = EditMode.SceneViewEditMode.ReflectionProbeBox;

        static HierarchicalBox _ShapeBox;
        static HierarchicalBox s_ShapeBox
        {
            get
            {
                if (_ShapeBox == null)
                    _ShapeBox = new HierarchicalBox(ProbeVolumeUI.Styles.k_GizmoColorBase, ProbeVolumeUI.Styles.k_BaseHandlesColor);
                return _ShapeBox;
            }
        }

        protected void OnEnable()
        {
            m_SerializedProbeVolume = new SerializedProbeVolume(serializedObject);
        }

        internal static void APVDisabledHelpBox()
        {
            var renderPipelineAssetType = GraphicsSettings.currentRenderPipelineAssetType;
            var messageType = MessageType.Warning;

            // HDRP
            if (renderPipelineAssetType != null && renderPipelineAssetType.Name == "HDRenderPipelineAsset")
            {
                static int IndexOf(string[] names, string name) { for (int i = 0; i < names.Length; i++) { if (name == names[i]) return i; } return -1; }

                var k_ExpandableGroup = Type.GetType("UnityEditor.Rendering.HighDefinition.HDRenderPipelineUI+ExpandableGroup,Unity.RenderPipelines.HighDefinition.Editor");
                var lightingGroup = k_ExpandableGroup.GetEnumValues().GetValue(IndexOf(k_ExpandableGroup.GetEnumNames(), "Lighting"));

                var k_LightingSection = Type.GetType("UnityEditor.Rendering.HighDefinition.HDRenderPipelineUI+ExpandableLighting,Unity.RenderPipelines.HighDefinition.Editor");
                var probeVolume = k_LightingSection.GetEnumValues().GetValue(IndexOf(k_LightingSection.GetEnumNames(), "ProbeVolume"));

                var k_QualitySettingsHelpBox = Type.GetType("UnityEditor.Rendering.HighDefinition.HDEditorUtils,Unity.RenderPipelines.HighDefinition.Editor")
                    .GetMethod("QualitySettingsHelpBoxForReflection", BindingFlags.Static | BindingFlags.NonPublic);

                string apvDisabledErrorMsg = "The current HDRP Asset does not support Adaptive Probe Volumes.";
                k_QualitySettingsHelpBox.Invoke(null, new object[] { apvDisabledErrorMsg, messageType, lightingGroup, probeVolume, "m_RenderPipelineSettings.lightProbeSystem" });
            }

            // URP
            else if (renderPipelineAssetType != null && renderPipelineAssetType.Name == "UniversalRenderPipelineAsset")
            {
                var k_QualitySettingsHelpBox = Type.GetType("UnityEditor.Rendering.Universal.EditorUtils,Unity.RenderPipelines.Universal.Editor")
                    .GetMethod("QualitySettingsHelpBox", BindingFlags.Static | BindingFlags.NonPublic);

                string apvDisabledErrorMsg = "The current URP Asset does not support Adaptive Probe Volumes.";
                k_QualitySettingsHelpBox.Invoke(null, new object[] { apvDisabledErrorMsg, messageType, "m_LightProbeSystem" });
            }

            // Custom pipelines
            else
            {
                string apvDisabledErrorMsg = "The current SRP does not support Adaptive Probe Volumes.";
                EditorGUILayout.HelpBox(apvDisabledErrorMsg, messageType);
            }
        }

        internal static void FrameSettingDisabledHelpBox()
        {
            var renderPipelineAssetType = GraphicsSettings.currentRenderPipelineAssetType;

            // HDRP only
            if (renderPipelineAssetType != null && renderPipelineAssetType.Name == "HDRenderPipelineAsset")
            {
                static int IndexOf(string[] names, string name) { for (int i = 0; i < names.Length; i++) { if (name == names[i]) return i; } return -1; }

                var k_FrameSettingsField = Type.GetType("UnityEngine.Rendering.HighDefinition.FrameSettingsField,Unity.RenderPipelines.HighDefinition.Runtime");
                var k_APVFrameSetting = k_FrameSettingsField.GetEnumValues().GetValue(IndexOf(k_FrameSettingsField.GetEnumNames(), "AdaptiveProbeVolume"));

                var k_EnsureFrameSetting = Type.GetType("UnityEditor.Rendering.HighDefinition.HDEditorUtils,Unity.RenderPipelines.HighDefinition.Editor")
                    .GetMethod("EnsureFrameSetting", BindingFlags.Static | BindingFlags.NonPublic);

                k_EnsureFrameSetting.Invoke(null, new object[] { k_APVFrameSetting, "Adaptive Probe Volumes" });
            }
        }

        public override void OnInspectorGUI()
        {
            ProbeVolume probeVolume = target as ProbeVolume;

            bool hasChanges = false;
            if (probeVolume.cachedTransform != probeVolume.gameObject.transform.worldToLocalMatrix)
            {
                hasChanges = true;
            }

            if (probeVolume.cachedHashCode != probeVolume.GetHashCode())
            {
                hasChanges = true;
            }

            probeVolume.mightNeedRebaking = hasChanges;

            bool drawInspector = true;

            if (ProbeVolumeLightingTab.GetLightingSettings().realtimeGI)
            {
                EditorGUILayout.HelpBox("Adaptive Probe Volumes are not supported when using Enlighten.", MessageType.Warning, wide: true);
                drawInspector = false;
            }

            if (!ProbeReferenceVolume.instance.isInitialized || !ProbeReferenceVolume.instance.enabledBySRP)
            {
                APVDisabledHelpBox();
                drawInspector = false;
            }

            if (drawInspector)
            {
                ProbeVolumeEditor.FrameSettingDisabledHelpBox();

                serializedObject.Update();
                ProbeVolumeUI.Inspector.Draw(m_SerializedProbeVolume, this);
                m_SerializedProbeVolume.Apply();
            }
        }

        [DrawGizmo(GizmoType.InSelectionHierarchy)]
        static void DrawGizmosSelected(ProbeVolume probeVolume, GizmoType gizmoType)
        {
            using (new Handles.DrawingScope(Matrix4x4.TRS(probeVolume.transform.position, probeVolume.transform.rotation, Vector3.one)))
            {
                // Bounding box.
                s_ShapeBox.center = Vector3.zero;
                s_ShapeBox.size = probeVolume.size;
                s_ShapeBox.DrawHull(EditMode.editMode == k_EditShape);
            }
        }

        protected void OnSceneGUI()
        {
            ProbeVolume probeVolume = target as ProbeVolume;
            if (probeVolume.mode != ProbeVolume.Mode.Local)
                return;

            //important: if the origin of the handle's space move along the handle,
            //handles displacement will appears as moving two time faster.
            using (new Handles.DrawingScope(Matrix4x4.TRS(Vector3.zero, probeVolume.transform.rotation, Vector3.one)))
            {
                //contained must be initialized in all case
                s_ShapeBox.center = Quaternion.Inverse(probeVolume.transform.rotation) * probeVolume.transform.position;
                s_ShapeBox.size = probeVolume.size;

                s_ShapeBox.monoHandle = false;
                EditorGUI.BeginChangeCheck();
                s_ShapeBox.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { probeVolume, probeVolume.transform }, "Change Adaptive Probe Volume Bounding Box");

                    probeVolume.size = s_ShapeBox.size;
                    Vector3 delta = probeVolume.transform.rotation * s_ShapeBox.center - probeVolume.transform.position;
                    probeVolume.transform.position += delta; ;
                }
            }
        }

        [MenuItem("CONTEXT/ProbeVolume/Rendering Debugger...")]
        internal static void AddProbeVolumeContextMenu()
        {
            ProbeVolumeLightingTab.OpenProbeVolumeDebugPanel(null, null, 0);
        }
    }
}
