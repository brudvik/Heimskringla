shader_type fog;

// Low mist lying in the valleys and over the fjord, drifting slowly with the wind (morning haze, fog banks).
// Used by a FogVolume that follows the camera (DayNight.cs); the noise is fixed in the world, so
// the mist doesn't "swim" with the camera. Density follows height above ground (world_map.g = height/100).

global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform vec2 wind_dir;

uniform float amount = 0.0;          // 0 = no mist (set per time of day/weather)
uniform float thickness = 9.0;       // how high the mist layer reaches (m, exponential)
uniform vec3 tint = vec3(0.92, 0.94, 0.97);

float h3(vec3 p) {
	uvec3 q = uvec3(ivec3(floor(p)) + ivec3(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u ^ q.z * 2654435761u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

float n3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(h3(i), h3(i + vec3(1, 0, 0)), u.x), mix(h3(i + vec3(0, 1, 0)), h3(i + vec3(1, 1, 0)), u.x), u.y),
	           mix(mix(h3(i + vec3(0, 0, 1)), h3(i + vec3(1, 0, 1)), u.x), mix(h3(i + vec3(0, 1, 1)), h3(i + vec3(1, 1, 1)), u.x), u.y), u.z);
}

void fog() {
	vec3 wp = WORLD_POSITION;
	float ground = max(texture(world_map, (wp.xz - world_rect.xy) * world_rect.zw).g * 100.0, 0.0);
	float above = wp.y - ground;
	// Mist gathers low: thickest down in the valleys and over water, thinner up the hillsides
	float low = 1.0 - smoothstep(15.0, 140.0, ground);
	float layer = exp(-max(above, 0.0) / (thickness * (0.6 + low * 0.8))) * step(-2.0, above);
	// Banks and wisps: two octaves drifting with the wind and slowly undulating
	vec3 q = vec3(wp.x, wp.y * 2.5, wp.z) * 0.025 - vec3(wind_dir.x, 0.0, wind_dir.y) * TIME * 0.04;
	float n = n3(q) * 0.65 + n3(q * 2.7 + 13.0) * 0.35;
	float banks = smoothstep(0.35, 0.75, n);
	// Soft edge towards the sides of the volume
	vec2 e = smoothstep(vec2(0.0), vec2(0.18), UVW.xz) * smoothstep(vec2(1.0), vec2(0.82), UVW.xz);
	DENSITY = amount * layer * banks * (0.4 + low * 0.6) * e.x * e.y;
	ALBEDO = tint;
}
