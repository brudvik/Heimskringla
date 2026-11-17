shader_type canvas_item;

// The logo: soft, dark shadow behind it (from a blurry mipmap of the alpha channel), and a light that sweeps
// across the metal on reveal and at regular intervals afterwards.

uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float reveal = 1.0;        // 0..1 fade-in
uniform float sweep = -1.0;        // position of the light streak (-0.3..1.3), < -0.5 = off
uniform vec3 glow_color : source_color = vec3(1.0, 0.62, 0.3);

void fragment() {
	vec4 c = texture(tex, UV);
	// The image file is cropped at the very bottom (the knotwork runs off the edge): fade it out softly
	c.a *= smoothstep(1.0, 0.9, UV.y);
	// Soft, dark shadow slightly below the logo (from a blurry mipmap of the alpha channel)
	float shadow = textureLod(tex, UV + vec2(0.0, -0.035), 4.0).a;
	shadow = smoothstep(0.05, 0.8, shadow) * 0.55;
	// Diagonal light streak across the metal (only where the logo is)
	// Broad, soft sheen (Gaussian profile) instead of a sharp streak
	float dx = (UV.x + UV.y * 0.35) - sweep * 1.35;
	float band = exp(-dx * dx / (2.0 * 0.11 * 0.11));
	vec3 col = c.rgb + vec3(1.0, 0.92, 0.75) * band * 0.28 * c.a * (0.6 + 0.4 * dot(c.rgb, vec3(0.33)));
	float logo_in = smoothstep(0.2, 1.0, reveal);
	float sh_in = smoothstep(0.0, 0.7, reveal);
	float a = c.a * logo_in + shadow * (1.0 - c.a * logo_in) * sh_in;
	vec3 outc = mix(vec3(0.0), col, clamp(c.a * logo_in / max(a, 0.001), 0.0, 1.0));
	COLOR = vec4(outc, a);
}
