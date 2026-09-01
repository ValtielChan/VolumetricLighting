# Volumetric Lighting

Raymarched volumetric scattering for the Universal Render Pipeline. Real god
rays through the main directional light's shadow cascades, plus opt-in
scattering from spot lights. One renderer feature, one Volume override, no
per-object setup — drop it in and your existing lights start casting light
through the air.

![Sun shafts through a slotted wall, with three coloured volumetric spot lights](Documentation/images/hero.png)

<p align="center">
  <img src="Documentation/images/shafts-angle.png" width="49%" alt="Shafts seen from inside the colonnade">
  <img src="Documentation/images/spot-lights.png" width="49%" alt="Volumetric spot lights at night, sun disabled">
</p>

<sub>The demo scene (`Samples/Demo.unity`) at 1600×900, Full resolution,
64 raymarch steps. Right: the same scene with the sun turned off — only the
three `VolumetricSpotLight` cones remain.</sub>

## Same frame, effect off / on

<p align="center">
  <img src="Documentation/images/compare-off.png" width="49%" alt="Volume override disabled">
  <img src="Documentation/images/hero.png" width="49%" alt="Volume override enabled">
</p>

<sub>Nothing changes but the `Enabled` checkbox on the Volumetric Lighting
override. Geometry, materials and light setup are identical.</sub>

## Requirements

- Unity **6000.0.61f1** (Unity 6.0 LTS) or later — the version this was
  developed and tested on.
- Universal Render Pipeline **17+**, using **Render Graph** (the default in
  Unity 6). The pass is written against the Render Graph API only.

## Install

Copy the `Volumetric Lighting` folder into your project's `Assets/`. That's
the whole asset — two assembly definitions, one shader, no dependencies
beyond URP itself.

## Setup

1. Open your **URP Renderer** asset (`Assets/Settings/*_Renderer.asset` in a
   default project) and `Add Renderer Feature → Volumetric Lighting Feature`.
2. Add a **Volume** to your scene (or reuse an existing one) and
   `Add Override → Lighting → Volumetric Lighting`.
3. Tick **Enabled** on the override, and raise **Density** until you see it.
4. Optional: add `Add Component → Rendering → Volumetric Spot Light` to each
   spot light that should scatter.

`Samples/Demo.unity` is steps 2–4 already done; you still need step 1, since
renderer features live on the render pipeline asset rather than in the scene.

## Volume parameters

| Parameter | What it does |
|---|---|
| `Enabled` | Master switch. Off = the pass is never enqueued, zero cost. |
| `Intensity` | Scattering multiplier for the **main directional light**. `0` skips the sun path (and its shadow sample) entirely — set it to 0 at night. |
| `Steps` | Raymarch samples per pixel. **16–24** mobile, **32–48** desktop. |
| `Max Distance` | How far the ray marches, in meters. Keep it near the size of what the camera actually sees: a large value spreads the same step count over more distance and coarsens the result. |
| `Scattering` | Henyey-Greenstein anisotropy. Positive = forward scattering, the classic bright halo around the sun. |
| `Density` | Scattering coefficient of the medium — "how much fog". |
| `Tint` | HDR tint applied to every contribution. |
| `Spots Enabled` / `Spot Intensity` | Enable and globally scale the spot light contribution. |
| `Spot Cull Distance` | Spot lights further than this from the camera are skipped on the CPU. `0` = no culling. |

The demo profile (`Samples/VolumetricLightingProfile.asset`) is a reasonable
desktop starting point: intensity 1.5, 64 steps, 45 m, scattering 0.72,
density 3.5, spot intensity 6.

## Renderer feature settings

| Setting | What it does |
|---|---|
| `Injection Point` | When the pass runs. Default `BeforeRenderingTransparents`, so transparents composite over the fog. |
| `Resolution` | Raymarch buffer size: `Full`, `Half`, `Quarter`. **Quarter** is the mobile default, `Full` for stills and desktop. |
| `Shader` | Auto-resolved to `Hidden/VolumetricLighting`; leave it empty. |

## How it works

Two passes, recorded on the Render Graph:

1. **Raymarch** into an off-screen `R16G16B16A16_SFloat` target at the chosen
   resolution. For each pixel the scene depth gives the ray's end point (the
   sky clamps to `Max Distance`), and the ray is marched with an
   interleaved-gradient-noise jitter to break up banding. At each step the
   directional light's **cascade shadow map is sampled directly** — not the
   screen-space shadow texture, which is meaningless for a point floating in
   mid-air — and every registered spot light is accumulated analytically.
   The loop early-outs once transmittance drops below 1%.
2. **Composite** the result into camera colour with hardware additive
   blending (`Blend One One`) — no extra fullscreen copy.

Spot lights are packed CPU-side into three `Vector4` arrays (position +
1/range, forward + cos(outer), colour·intensity + cos(inner)) and culled by
camera distance there; the shader culls again per sample by range and cone
angle. The `_VL_SPOTS_ON` keyword removes the whole spot loop when no spot is
active.

The pass sets `requiresIntermediateTexture`, which forces URP to allocate an
intermediate colour target. Without it URP renders straight to the back
buffer in simple frames and the composite — which reads a texture while
writing camera colour — silently does nothing.

## Mobile guidance

- Renderer feature resolution → **Quarter**.
- `Steps` 16–24, `Density` 0.1–0.3, `Max Distance` 30–40 m.
- Keep 2–4 `VolumetricSpotLight` visible at once.
- Set `Intensity` to 0 whenever the sun is not the story: it removes the
  shadow-map sample from the inner loop, which is the expensive part.

## Limits

- **No shadows for spot lights.** Headlights and candles scatter through
  walls; only the main directional light is occluded.
- **No light cookies** on the volumetrics.
- **8 active spot lights** maximum. Raise `VolumetricSpotLight.MaxActive` and
  `VL_MAX_SPOTS` in the shader together if you need more.
- **Point lights are not supported** — directional and spot only.
- The raymarch jitter is spatial only (no temporal reprojection, no bilateral
  upsample), so a bright narrow cone close to its own bulb shows visible
  dither. More `Steps` and a shorter `Max Distance` are the current fix.

## License

[MIT](LICENSE.md) — free to use, modify and redistribute, including in
commercial projects. Just keep the copyright notice and license text.

© 2026 Fabien Boco
