// Felles vind for trær, busker og gress. Styres av globale shader-parametre fra Weather.cs.
//   wind_dir      : retning vinden blåser MOT, i xz (normalisert)
//   wind_strength : 0 = stille, ~0.3 = bris, 1 = storm

global uniform vec2 wind_dir;
global uniform float wind_strength;

// Vindkast som vandrer over landskapet i vindretningen (0..1)
float wind_gust(vec2 wp, float t) {
	float s = dot(wp, wind_dir) * 0.045 - t * (0.5 + wind_strength * 1.4);
	float c = dot(wp, vec2(-wind_dir.y, wind_dir.x)) * 0.021;
	return clamp(0.55 + 0.45 * sin(s) * sin(s * 0.41 + c + 1.3), 0.0, 1.0);
}

// Forskyvning i objektrom for et punkt v (objektrom, origo ved roten).
//   bend    : hvor mye hele planten bøyer seg (stive grantrær < 1, bjørk > 1)
//   flutter : rask risting av løv/kvister (lengre fra stammen = mer)
vec3 wind_displace(vec3 v, mat4 model, float t, float bend, float flutter) {
	mat3 m = mat3(model);
	float s2 = dot(m[1], m[1]);
	float sy = sqrt(s2);
	vec3 wp = (model * vec4(v, 1.0)).xyz;
	vec3 root = model[3].xyz;
	float h = max(v.y, 0.0) * sy;                 // høyde over roten (m)
	float phase = dot(root.xz, vec2(0.113, 0.071));

	float gust = wind_gust(root.xz, t);
	float sway = sin(t * (0.9 + wind_strength * 0.9) + phase) * 0.35 + sin(t * 2.3 + phase * 1.7) * 0.12;
	float amt = wind_strength * (0.35 + gust * 0.9 + sway) * bend;
	float hf = h * h * 0.0022;                    // 10 m -> 0.22, 18 m -> 0.7
	vec3 dir = vec3(wind_dir.x, 0.0, wind_dir.y);
	vec3 off = dir * amt * hf;
	// Litt sideveis vugging, så trærne ikke bare nikker
	off += vec3(-dir.z, 0.0, dir.x) * sin(t * 1.3 + phase * 2.1) * wind_strength * 0.25 * hf * bend;
	off.y -= abs(amt) * hf * hf * 0.15;           // tuppen synker litt når den bøyes

	// Løvrisling
	float r = length(v.xz) * sy;
	float fl = sin(t * (6.0 + wind_strength * 5.0) + dot(wp, vec3(2.3, 1.9, 2.9)))
	         + 0.6 * sin(t * 10.7 + dot(wp, vec3(-3.1, 2.2, 1.7)));
	float fa = flutter * (0.012 + 0.05 * wind_strength * (0.4 + gust)) * clamp(r * 0.6, 0.0, 1.0) * clamp(h * 0.4, 0.0, 1.0);
	off += vec3(0.6, 0.35, -0.5) * fl * fa;

	return transpose(m) * off / s2;               // tilbake til objektrom (uniform skala)
}
