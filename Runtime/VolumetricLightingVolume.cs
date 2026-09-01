using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricLighting
{
    [Serializable, VolumeComponentMenu("Lighting/Volumetric Lighting")]
    public sealed class VolumetricLightingVolume : VolumeComponent, IPostProcessComponent
    {
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Directional (Sun / Moon)")]
        [Tooltip("Intensity multiplier for scattering from the main directional light. Set 0 to disable the sun path entirely.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0f, 10f);

        [Tooltip("Raymarch step count. Lower = cheaper. 16-24 is good for mobile, 32-48 for desktop.")]
        public ClampedIntParameter steps = new ClampedIntParameter(24, 8, 128);

        [Tooltip("Maximum world-space distance the ray travels, in meters.")]
        public ClampedFloatParameter maxDistance = new ClampedFloatParameter(60f, 5f, 500f);

        [Tooltip("Henyey-Greenstein anisotropy. Positive = forward-scattering (sun god rays).")]
        public ClampedFloatParameter scattering = new ClampedFloatParameter(0.6f, -0.95f, 0.95f);

        [Tooltip("Fog/medium density. Controls how much light scatters per unit length.")]
        public ClampedFloatParameter density = new ClampedFloatParameter(0.5f, 0f, 5f);

        [Tooltip("Tints the scattered light (applies to both directional and spot lights).")]
        public ColorParameter tint = new ColorParameter(Color.white, hdr: true, showAlpha: false, showEyeDropper: true);

        [Header("Spot Lights")]
        [Tooltip("Enable volumetric scattering from registered spot lights (VolumetricSpotLight component).")]
        public BoolParameter spotsEnabled = new BoolParameter(true);

        [Tooltip("Global intensity multiplier applied to all volumetric spot lights.")]
        public ClampedFloatParameter spotIntensity = new ClampedFloatParameter(1.0f, 0f, 20f);

        [Tooltip("Camera distance beyond which spot lights are skipped (cheap CPU cull). 0 = no culling.")]
        public ClampedFloatParameter spotCullDistance = new ClampedFloatParameter(80f, 0f, 500f);

        public bool IsActive() => enabled.value && (intensity.value > 0f || (spotsEnabled.value && spotIntensity.value > 0f));
        public bool IsTileCompatible() => false;
    }
}
