// Felles skymodell for himmelen (sky.gdshader) og speilingen i vannet (water.gdshader), så skyene
// i vannet er de samme som på himmelen.
//
//  - Gradientstøy med heltalls-hash, roterte oktaver og vridd domene: ingen rutenett/skjøter
//    ("limte" firkanter) som verdistøy med fract-hash ga.
//  - Lave haugskyer/flak (bulete, lyse mot sola, mørkere under) + høye fjærskyer (cirrus) som
//    strekkes langs vinden.
//  - Skydekket varierer over himmelen (skybanker og åpninger), ikke likt overalt.

float cl_hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(1 << 20));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

// Gradientstøy, ca. -0.7..0.7
float cl_gnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a0 = cl_hash(i) * 6.2831853, a1 = cl_hash(i + vec2(1, 0)) * 6.2831853;
	float a2 = cl_hash(i + vec2(0, 1)) * 6.2831853, a3 = cl_hash(i + vec2(1, 1)) * 6.2831853;
	float va = dot(vec2(cos(a0), sin(a0)), f);
	float vb = dot(vec2(cos(a1), sin(a1)), f - vec2(1, 0));
	float vc = dot(vec2(cos(a2), sin(a2)), f - vec2(0, 1));
	float vd = dot(vec2(cos(a3), sin(a3)), f - vec2(1, 1));
	return mix(mix(va, vb, u.x), mix(vc, vd, u.x), u.y);
}

// fbm 0..1 med roterte oktaver (skjuler rutenettet)
float cl_fbm(vec2 p, int oct) {
	const mat2 R = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
	float s = 0.0, a = 0.5, n = 0.0;
	for (int i = 0; i < oct; i++) {
		s += cl_gnoise(p) * a;
		n += a;
		p = R * p * 2.03 + vec2(11.3, 7.1);
		a *= 0.5;
	}
	return s / n * 0.75 + 0.5;
}

// Lave skyer: tetthet 0..1 i punktet (cov = lokalt skydekke)
float cl_low(vec2 uv, float cov, int oct) {
	vec2 w = vec2(cl_gnoise(uv * 0.45 + 3.7), cl_gnoise(uv * 0.45 - 8.1));
	float n = cl_fbm(uv + w * 0.55, oct);
	// "Bulete" topper: forsterk de øvre delene av støyen
	n = n * 0.75 + (1.0 - abs(cl_gnoise(uv * 2.6 + w) * 1.4)) * 0.25 * n;
	return smoothstep(1.0 - cov - 0.04, 1.0 - cov + 0.3, n);
}

/// Skyfarge (rgb) og dekning (a) i retning d.
/// offset = vindforskyvning, coverage/dark/night fra været, sundir mot sola, lys-/skyggefarge lineært.
vec4 cl_sky(vec3 d, vec2 offset, float cl_cov, float cl_dark, float cl_night, vec3 sundir, vec3 light_col, vec3 shadow_col, int oct) {
	if (d.y <= 0.0) return vec4(0.0);
	vec2 uv = d.xz / (d.y + 0.1) * 1.1;
	vec2 p = uv + offset;
	vec3 sd = normalize(sundir);

	// Skydekket varierer over himmelen (store banker og åpninger)
	float region = cl_gnoise(p * 0.11 + vec2(4.1, -2.3)) * 0.42;
	float cov = clamp(cl_cov + region * (1.0 - smoothstep(0.7, 1.0, cl_cov)), 0.0, 1.0);
	float dens = cl_low(p, cov, oct);
	dens = max(dens, smoothstep(0.78, 1.0, cl_cov) * (0.85 + 0.15 * cl_fbm(p * 1.7, 3)));

	// Belysning: sammenlign tettheten et stykke mot sola. Tynnere mot sola = lys kant, tykkere = skygge.
	vec2 toward = sd.xz / max(length(sd.xz), 0.001) * 0.12;
	float dens2 = cl_low(p + toward, cov, max(oct - 2, 2));
	float lit = clamp(0.62 + (dens - dens2) * 1.6 - dens * 0.25, 0.0, 1.0);
	float sunfac = pow(max(dot(d, sd), 0.0), 5.0);
	vec3 col = mix(shadow_col, light_col, lit);
	// Sølvkant: tynne kanter rundt sola lyser opp
	col += light_col * sunfac * (1.0 - dens) * 1.2 * (1.0 - cl_dark);
	// Tunge regnskyer: mørke, blågrå bunner
	col *= mix(1.0, 0.42 + lit * 0.35, cl_dark);

	// Høye fjærskyer: tynne striper langs vinden, mest i klart/lettskyet vær
	vec2 wdir = normalize(offset + vec2(0.37, 0.11));
	vec2 cp = vec2(dot(p, wdir) * 0.35, dot(p, vec2(-wdir.y, wdir.x)) * 2.2) * 0.6 + 21.0;
	float ci = smoothstep(0.58, 0.82, cl_fbm(cp, max(oct - 1, 2))) * smoothstep(0.62, 0.3, cl_fbm(p * 0.07 + 9.0, 2));
	ci *= 0.55 * (1.0 - smoothstep(0.6, 0.9, cl_cov));
	vec3 ccol = mix(light_col, shadow_col, 0.15) + light_col * sunfac * 0.6;

	vec3 outc = mix(ccol, col, dens);
	float a = clamp(dens + ci * (1.0 - dens), 0.0, 1.0);
	outc = mix(outc, outc * vec3(0.12, 0.13, 0.18), cl_night);
	// Ut mot horisonten: skyene tones inn i disen
	a *= smoothstep(0.0, 0.12, d.y) * 0.95;
	return vec4(outc, a);
}
