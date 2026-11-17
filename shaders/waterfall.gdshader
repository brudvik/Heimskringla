shader_type spatial;
render_mode cull_disabled, depth_draw_never, specular_schlick_ggx;

// Rapids and waterfalls: white, foaming water streaming down the ribbon.
// UV.x = across (0..1), UV.y = meters along the fall (increasing downwards). COLOR.r = steepness (0..1),
// COLOR.g = fade-in at the ends.

uniform vec3 water_color : source_color = vec3(0.55, 0.7, 0.75);
uniform vec3 foam_color : source_color = vec3(0.92, 0.95, 0.97);

float hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

varying float steep;
varying float endfade;

void vertex() {
	steep = COLOR.r;
	endfade = COLOR.g;
	// Slightly undulating surface
	VERTEX.y += sin(UV.y * 3.0 - TIME * 6.0 + UV.x * 5.0) * 0.06;
}

void fragment() {
	float speed = 4.0;   // constant speed: varying speed along the ribbon would stretch the pattern over time
	vec2 p = vec2(UV.x * 14.0, mod(UV.y, 4096.0) * 1.6 - TIME * speed * 2.0);
	// Long streaks along the fall + smaller clumps
	float streak = vnoise(vec2(p.x, p.y * 0.15)) * 0.5 + vnoise(p * vec2(2.3, 0.6)) * 0.3 + vnoise(p * vec2(5.1, 1.7)) * 0.2;
	float foam = smoothstep(0.35 - steep * 0.25, 0.75, streak);
	vec3 col = mix(water_color, foam_color, foam);
	float edge = smoothstep(0.0, 0.18, UV.x) * smoothstep(1.0, 0.82, UV.x);
	ALBEDO = col;
	ALPHA = clamp((0.55 + foam * 0.45) * edge * endfade, 0.0, 0.95);
	ROUGHNESS = mix(0.15, 0.7, foam);
	SPECULAR = 0.5;
}
