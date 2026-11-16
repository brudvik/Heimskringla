shader_type fog;

// Lav tåke som ligger i dalene og over fjorden og driver sakte med vinden (morgendis, tåkebanker).
// Brukes av et FogVolume som følger kameraet (DayNight.cs); støyen ligger fast i verden, så
// tåka ikke "svømmer" med kameraet. Tettheten følger høyden over bakken (world_map.g = høyde/100).

global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform vec2 wind_dir;

uniform float amount = 0.0;          // 0 = ingen tåke (settes per tid/vær)
uniform float thickness = 9.0;       // hvor høyt tåkelaget når (m, eksponentielt)
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
	// Tåka samler seg lavt: tykkest nede i dalene og over vannet, tynnere oppover i liene
	float low = 1.0 - smoothstep(15.0, 140.0, ground);
	float layer = exp(-max(above, 0.0) / (thickness * (0.6 + low * 0.8))) * step(-2.0, above);
	// Banker og drag: to oktaver som driver med vinden og bølger sakte
	vec3 q = vec3(wp.x, wp.y * 2.5, wp.z) * 0.025 - vec3(wind_dir.x, 0.0, wind_dir.y) * TIME * 0.04;
	float n = n3(q) * 0.65 + n3(q * 2.7 + 13.0) * 0.35;
	float banks = smoothstep(0.35, 0.75, n);
	// Myk kant mot sidene av volumet
	vec2 e = smoothstep(vec2(0.0), vec2(0.18), UVW.xz) * smoothstep(vec2(1.0), vec2(0.82), UVW.xz);
	DENSITY = amount * layer * banks * (0.4 + low * 0.6) * e.x * e.y;
	ALBEDO = tint;
}
