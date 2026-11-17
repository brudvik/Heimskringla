shader_type spatial;

// Tools (Kenney Survival Kit): the colormap palette is interpreted as materials.
//   bluish/grey colors -> forged steel (metallic, scratches, darker in recesses)
//   orange/pink/brown   -> wooden handle (brown, lengthwise grain, worn lighter where the hand grips)
// Details are generated in object space, so they follow the tool in the hand.

uniform sampler2D palette : source_color, filter_nearest;

varying vec3 obj_pos;

float hash(vec3 p) {
	uvec3 q = uvec3(ivec3(floor(p)) + ivec3(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u ^ q.z * 2654435761u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}
float vnoise(vec3 p) {
	vec3 i = floor(p), f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i), hash(i + vec3(1, 0, 0)), u.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), u.x), u.y),
	           mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), u.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), u.x), u.y), u.z);
}

void vertex() {
	obj_pos = VERTEX;
}

void fragment() {
	vec3 src = texture(palette, UV).rgb;
	float metal = smoothstep(-0.02, 0.06, src.b - src.r);
	vec3 p = obj_pos * 40.0;

	// Wood: grain along the handle (y), knots and wear
	float grain = vnoise(vec3(p.x * 3.0, p.y * 0.15, p.z * 3.0)) * 0.6 + vnoise(vec3(p.x * 9.0, p.y * 0.4, p.z * 9.0)) * 0.4;
	vec3 wood = mix(vec3(0.20, 0.11, 0.05), vec3(0.42, 0.26, 0.13), grain);
	wood *= 0.9 + vnoise(p * 0.3) * 0.2;

	// Steel: dark wrought iron, lighter worn edges, scratches
	float scratch = smoothstep(0.82, 0.9, vnoise(vec3(p.x * 0.6, p.y * 14.0, p.z * 0.6)));
	vec3 steel = vec3(0.32, 0.33, 0.35) * (0.8 + vnoise(p * 0.8) * 0.35) + scratch * 0.15;

	ALBEDO = mix(wood, steel, metal);
	METALLIC = metal * 0.55;
	ROUGHNESS = mix(0.78 - grain * 0.1, 0.58 - scratch * 0.15, metal);
	SPECULAR = 0.4;
}
