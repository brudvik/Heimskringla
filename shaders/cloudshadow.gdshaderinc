// Skyskygger som driver over landskapet med vinden: lysflekker og skygger som vandrer over lia
// og fjorden (det norske "lyset som bryter gjennom"). Felles for terreng, gress, trær, stein og vann.
//
// Skylaget ligger ~700 m over bakken; skyggen forskyves langs solretningen, og driver med
// cloud_offset (samme forskyvning som skyene på himmelen, se DayNight.cs). Kun direkte sollys
// skal skygges, men shaderne har ikke egen light()-funksjon, så albedo dempes litt i stedet.
// Det virker mest overbevisende når himmellyset er svakt i forhold til sola (slik det er satt nå).

global uniform vec4 cloud_params;     // x = skydekke, y = mørke (gråvær), z = natt
global uniform vec2 cloud_offset;
global uniform vec3 sun_direction;    // mot sola (DayNight.cs)

float cs_hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

float cs_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(cs_hash(i), cs_hash(i + vec2(1, 0)), u.x), mix(cs_hash(i + vec2(0, 1)), cs_hash(i + vec2(1, 1)), u.x), u.y);
}

// 1 = sol, < 1 = i skyskygge. view_pos = VERTEX (fragment), inv_view = INV_VIEW_MATRIX.
float cloud_shade(vec3 view_pos, mat4 inv_view) {
	vec3 sd = normalize(sun_direction);
	float strength = 0.42 * (1.0 - cloud_params.y) * (1.0 - cloud_params.z) * smoothstep(0.03, 0.2, sd.y);
	if (strength < 0.01) return 1.0;
	vec3 wp = (inv_view * vec4(view_pos, 1.0)).xyz;
	vec2 p = wp.xz + sd.xz / max(sd.y, 0.1) * 700.0 - cloud_offset * 1000.0;
	p *= 1.0 / 650.0;
	float n = cs_noise(p) * 0.55 + cs_noise(p * 2.3 + 17.0) * 0.3 + cs_noise(p * 5.1 + 41.0) * 0.15;
	// Mer skydekke = større og flere skygger; klar himmel = bare noen få
	float th = mix(0.78, 0.3, clamp(cloud_params.x, 0.0, 1.0));
	float shade = smoothstep(th - 0.06, th + 0.1, n);
	return 1.0 - shade * strength;
}
