shader_type spatial;
render_mode cull_disabled, depth_draw_always, specular_schlick_ggx;

uniform vec3 shallow_color : source_color = vec3(0.20, 0.50, 0.55);
uniform vec3 deep_color : source_color = vec3(0.03, 0.14, 0.24);
uniform float wave_strength = 1.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec2 wave_grad(vec2 p, float t) {
	vec2 g = vec2(0.0);
	g += vec2(0.8, 0.6) * cos(dot(p, vec2(0.8, 0.6)) * 0.35 + t * 1.1) * 0.35;
	g += vec2(-0.5, 0.85) * cos(dot(p, vec2(-0.5, 0.85)) * 0.6 + t * 1.6) * 0.22;
	g += vec2(0.95, -0.3) * cos(dot(p, vec2(0.95, -0.3)) * 1.3 + t * 2.3) * 0.12;
	g += vec2(0.2, 0.98) * cos(dot(p, vec2(0.2, 0.98)) * 2.7 + t * 3.1) * 0.07;
	return g;
}

void fragment() {
	float dist = length(VERTEX);
	float fade = 1.0 - smoothstep(80.0, 900.0, dist);
	vec2 g = wave_grad(world_pos.xz, TIME) * wave_strength * (0.25 + 0.75 * fade);
	vec3 n_world = normalize(vec3(-g.x * 0.25, 1.0, -g.y * 0.25));
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	vec3 pixel_pos = upos.xyz / upos.w;
	float water_depth = max(0.0, VERTEX.z - pixel_pos.z);

	float dt = clamp(water_depth / 9.0, 0.0, 1.0);
	vec3 col = mix(shallow_color, deep_color, dt);
	float foam = (1.0 - smoothstep(0.0, 0.6, water_depth)) * 0.6;
	col = mix(col, vec3(0.9, 0.95, 1.0), foam);

	ALBEDO = col;
	ALPHA = clamp(0.45 + water_depth * 0.22, 0.45, 0.95);
	ROUGHNESS = 0.06;
	METALLIC = 0.0;
	SPECULAR = 0.6;
}
