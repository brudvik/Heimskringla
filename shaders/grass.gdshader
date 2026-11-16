shader_type spatial;
render_mode cull_disabled;

// Gresstuster i MultiMesh. INSTANCE_CUSTOM.rgb = bakkefargen der tusten står (sRGB).
// UV.y = 0 i toppen, 1 ved roten.

uniform float fade_start = 45.0;
uniform float fade_end = 65.0;

varying vec3 base_col;
varying float tip;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	float d = distance(wp.xz, cam.xz);
	float s = 1.0 - smoothstep(fade_start, fade_end, d);
	tip = 1.0 - UV.y;
	float sway = sin(TIME * 1.7 + wp.x * 0.35 + wp.z * 0.27) * 0.5 + sin(TIME * 3.1 + wp.x * 1.3) * 0.15;
	VERTEX.y *= s;
	VERTEX.x += sway * 0.12 * tip;
	VERTEX.z += sway * 0.07 * tip;
	base_col = pow(INSTANCE_CUSTOM.rgb, vec3(2.2));
}

void fragment() {
	vec3 col = mix(base_col * 0.75, base_col * 1.35 + vec3(0.03, 0.04, 0.0), tip);
	ALBEDO = col;
	ROUGHNESS = 0.9;
	SPECULAR = 0.1;
	BACKLIGHT = vec3(0.15, 0.2, 0.05);
}
