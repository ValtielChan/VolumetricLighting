using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VolumetricLighting
{
    /// <summary>
    /// URP ScriptableRendererFeature that adds raymarched volumetric scattering
    /// for the main directional light and registered spot lights.
    /// Mobile-friendly defaults: quarter resolution, low step count, additive composite.
    /// </summary>
    public class VolumetricLightingFeature : ScriptableRendererFeature
    {
        public enum Resolution { Full = 1, Half = 2, Quarter = 4 }

        [System.Serializable]
        public class Settings
        {
            [Tooltip("When in the URP frame the volumetric pass runs.")]
            public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;

            [Tooltip("Lower = sharper but more expensive. Quarter is the recommended mobile default.")]
            public Resolution resolution = Resolution.Half;

            [Tooltip("Separable gaussian passes over the volumetric buffer before compositing. " +
                     "This is what removes the raymarch dither: 1 is enough in most scenes, 2 for very " +
                     "bright spot cones. 0 skips the blur entirely - cheapest, and the dither comes back.")]
            [Range(0, 3)] public int blurIterations = 1;

            [Tooltip("Auto-resolved if left empty.")]
            public Shader shader;
        }

        public Settings settings = new Settings();

        private Material _material;
        private VolumetricLightingPass _pass;

        public override void Create()
        {
            if (settings.shader == null)
                settings.shader = Shader.Find("Hidden/VolumetricLighting");

            if (settings.shader == null)
            {
                Debug.LogWarning("[VolumetricLightingFeature] Shader 'Hidden/VolumetricLighting' not found.");
                return;
            }

            _material = CoreUtils.CreateEngineMaterial(settings.shader);
            _pass = new VolumetricLightingPass(_material, settings)
            {
                renderPassEvent = settings.injectionPoint
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || _material == null) return;

            var stack = VolumeManager.instance.stack;
            var vol = stack.GetComponent<VolumetricLightingVolume>();
            if (vol == null || !vol.IsActive()) return;

            var camType = renderingData.cameraData.cameraType;
            if (camType == CameraType.Reflection || camType == CameraType.Preview) return;

            _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }

        private class VolumetricLightingPass : ScriptableRenderPass
        {
            private readonly Material _material;
            private readonly Settings _settings;

            private static readonly int IntensityID    = Shader.PropertyToID("_VL_Intensity");
            private static readonly int StepsID        = Shader.PropertyToID("_VL_Steps");
            private static readonly int MaxDistanceID  = Shader.PropertyToID("_VL_MaxDistance");
            private static readonly int ScatteringID   = Shader.PropertyToID("_VL_Scattering");
            private static readonly int DensityID      = Shader.PropertyToID("_VL_Density");
            private static readonly int TintID         = Shader.PropertyToID("_VL_Tint");
            private static readonly int SpotCountID    = Shader.PropertyToID("_VL_SpotCount");
            private static readonly int SpotPosID      = Shader.PropertyToID("_VL_SpotPos");
            private static readonly int SpotDirID      = Shader.PropertyToID("_VL_SpotDir");
            private static readonly int SpotColorID    = Shader.PropertyToID("_VL_SpotColor");
            private static readonly int BlurTexelID    = Shader.PropertyToID("_VL_BlurTexel");

            private const int PassRaymarch  = 0;
            private const int PassComposite = 1;
            private const int PassBlurH     = 2;
            private const int PassBlurV     = 3;

            private const int MaxSpots = VolumetricSpotLight.MaxActive;
            private readonly Vector4[] _spotPos   = new Vector4[MaxSpots];
            private readonly Vector4[] _spotDir   = new Vector4[MaxSpots];
            private readonly Vector4[] _spotColor = new Vector4[MaxSpots];

            public VolumetricLightingPass(Material material, Settings settings)
            {
                _material = material;
                _settings = settings;
                profilingSampler = new ProfilingSampler("VolumetricLighting");
                // The composite reads the volumetric RT while writing camera color, so the
                // camera must not render straight to the back buffer (which URP does when
                // nothing else in the frame needs an intermediate texture).
                requiresIntermediateTexture = true;
            }

            private class PassData
            {
                public Material material;
                public int passIndex;
                public TextureHandle volumetric;
            }

            private void PackSpotLights(VolumetricLightingVolume vol, Camera cam)
            {
                int count = 0;
                bool spotsOn = vol.spotsEnabled.value && vol.spotIntensity.value > 0f;
                float globalMul = vol.spotIntensity.value;

                if (spotsOn)
                {
                    var list = VolumetricSpotLight.Active;
                    Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
                    float cullDistSqr = vol.spotCullDistance.value;
                    cullDistSqr = cullDistSqr * cullDistSqr;

                    for (int i = 0; i < list.Count && count < MaxSpots; i++)
                    {
                        var vsl = list[i];
                        if (vsl == null || !vsl.isActiveAndEnabled) continue;
                        var l = vsl.Light;
                        if (l == null || !l.enabled || l.type != LightType.Spot) continue;
                        if (vsl.volumetricIntensity <= 0f) continue;

                        float range = vsl.rangeOverride > 0f ? vsl.rangeOverride : l.range;
                        if (range <= 0f) continue;

                        var t = l.transform;
                        Vector3 pos = t.position;

                        // Cheap distance cull (skip spots that are far away).
                        if (cullDistSqr > 0f)
                        {
                            Vector3 d = pos - camPos;
                            if (d.sqrMagnitude > cullDistSqr + range * range) continue;
                        }

                        Vector3 fwd = t.forward;

                        float outerRad = l.spotAngle * 0.5f * Mathf.Deg2Rad;
                        float innerDeg = l.innerSpotAngle > 0f ? l.innerSpotAngle : l.spotAngle * 0.8f;
                        float innerRad = Mathf.Min(innerDeg, l.spotAngle) * 0.5f * Mathf.Deg2Rad;
                        float cosOuter = Mathf.Cos(outerRad);
                        float cosInner = Mathf.Cos(innerRad);

                        _spotPos[count]   = new Vector4(pos.x, pos.y, pos.z, 1f / range);
                        _spotDir[count]   = new Vector4(fwd.x, fwd.y, fwd.z, cosOuter);

                        Color c = l.color.linear * (l.intensity * vsl.volumetricIntensity * globalMul);
                        _spotColor[count] = new Vector4(c.r, c.g, c.b, cosInner);

                        count++;
                    }
                }

                for (int i = count; i < MaxSpots; i++)
                {
                    _spotPos[i] = Vector4.zero;
                    _spotDir[i] = Vector4.zero;
                    _spotColor[i] = Vector4.zero;
                }

                _material.SetInt(SpotCountID, count);
                _material.SetVectorArray(SpotPosID,   _spotPos);
                _material.SetVectorArray(SpotDirID,   _spotDir);
                _material.SetVectorArray(SpotColorID, _spotColor);

                const string kSpotsKeyword = "_VL_SPOTS_ON";
                if (count > 0) _material.EnableKeyword(kSpotsKeyword);
                else _material.DisableKeyword(kSpotsKeyword);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData   = frameData.Get<UniversalCameraData>();

                if (resourceData.isActiveTargetBackBuffer) return;

                var stack = VolumeManager.instance.stack;
                var vol = stack.GetComponent<VolumetricLightingVolume>();
                if (vol == null || !vol.IsActive()) return;

                _material.SetFloat(IntensityID,   vol.intensity.value);
                _material.SetFloat(StepsID,       vol.steps.value);
                _material.SetFloat(MaxDistanceID, vol.maxDistance.value);
                _material.SetFloat(ScatteringID,  vol.scattering.value);
                _material.SetFloat(DensityID,     vol.density.value);
                _material.SetColor(TintID,        vol.tint.value);

                PackSpotLights(vol, cameraData.camera);

                int divisor = (int)_settings.resolution;
                var camDesc = cameraData.cameraTargetDescriptor;
                int w = Mathf.Max(1, camDesc.width  / divisor);
                int h = Mathf.Max(1, camDesc.height / divisor);

                var volDesc = new TextureDesc(w, h)
                {
                    colorFormat  = GraphicsFormat.R16G16B16A16_SFloat,
                    name         = "_VL_Volumetric",
                    clearBuffer  = true,
                    clearColor   = Color.clear,
                    filterMode   = FilterMode.Bilinear,
                    wrapMode     = TextureWrapMode.Clamp,
                    msaaSamples  = MSAASamples.None
                };
                TextureHandle volTex = renderGraph.CreateTexture(volDesc);

                TextureHandle cameraColor = resourceData.cameraColor;

                // Raymarch into the off-screen RT.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Volumetric Raymarch", out var data))
                {
                    data.material  = _material;
                    data.passIndex = PassRaymarch;

                    builder.SetRenderAttachment(volTex, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, new Vector4(1f, 1f, 0f, 0f), d.material, d.passIndex);
                    });
                }

                // Separable gaussian: the only thing standing between the jittered raymarch
                // and visible dither, since there is no temporal filter. Widening happens by
                // repeating the fixed one-texel kernel - stretching its taps instead would
                // undersample the kernel itself and alias the dither into a coarser pattern.
                if (_settings.blurIterations > 0)
                {
                    _material.SetVector(BlurTexelID, new Vector4(1f / w, 1f / h, 0f, 0f));

                    var blurDesc = volDesc;
                    blurDesc.name = "_VL_VolumetricBlur";
                    blurDesc.clearBuffer = false;
                    TextureHandle blurTex = renderGraph.CreateTexture(blurDesc);

                    for (int i = 0; i < _settings.blurIterations; i++)
                    {
                        AddBlitPass(renderGraph, "Volumetric Blur H", volTex,  blurTex, PassBlurH);
                        AddBlitPass(renderGraph, "Volumetric Blur V", blurTex, volTex,  PassBlurV);
                    }
                }

                // Additive composite directly into camera color (no extra fullscreen copy).
                AddBlitPass(renderGraph, "Volumetric Composite", volTex, cameraColor, PassComposite);
            }

            private void AddBlitPass(RenderGraph renderGraph, string name, TextureHandle source, TextureHandle target, int passIndex)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data))
                {
                    data.material   = _material;
                    data.passIndex  = passIndex;
                    data.volumetric = source;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, d.volumetric, new Vector4(1f, 1f, 0f, 0f), d.material, d.passIndex);
                    });
                }
            }
        }
    }
}
