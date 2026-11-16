shader_type canvas_item;

// Stemning over menybildet: tåke som driver sakte, svake lysstråler ovenfra, mørk vignett.

uniform float strength = 1.0;
uniform float time_offset = 0.0;

float h(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(4096));
	uint x = q.x * 1597334677u ^ q.y * 3812015801u;
	x ^= x >> 16u; x *= 2246822519u; x ^= x >> 13u; x *= 3266489917u; x ^= x >> 16u;
	return float(x) * (1.0 / 4294967296.0);
}
float n(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(h(i), h(i + vec2(1, 0)), u.x), mix(h(i + vec2(0, 1)), h(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	mat2 r = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
	for (int i = 0; i < 5; i++) { s += n(p) * a; p = r * p * 2.0 + 3.1; a *= 0.5; }
	return s;
}

void fragment() {
	float t = TIME + time_offset;
	vec2 uv = UV;
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 p = vec2(uv.x * aspect, uv.y) * 3.0;
	// To lag tåke som driver i hver sin fart (dybde)
	float f1 = fbm(p * 0.8 + vec2(t * 0.025, t * 0.004));
	float f2 = fbm(p * 1.7 + vec2(-t * 0.04, t * 0.01) + 7.0);
	float mist = smoothstep(0.45, 0.85, f1 * 0.6 + f2 * 0.5) * smoothstep(0.25, 0.9, uv.y);
	vec3 col = vec3(0.82, 0.86, 0.92) * mist * 0.32;
	// Lysstråler skrått ovenfra (fra øvre venstre)
	vec2 src = vec2(0.28, -0.25);
	vec2 d = uv - src;
	float ang = atan(d.x, d.y);
	float rays = n(vec2(ang * 22.0, t * 0.08)) * 0.6 + n(vec2(ang * 47.0, -t * 0.05)) * 0.4;
	rays = smoothstep(0.55, 0.95, rays) * smoothstep(1.3, 0.2, length(d)) * 0.16;
	col += vec3(1.0, 0.9, 0.7) * rays;
	float a = clamp(mist * 0.38 + rays, 0.0, 1.0);
	// Vignett
	float vig = smoothstep(0.35, 1.05, length((uv - 0.5) * vec2(1.25, 1.0)));
	col = mix(col, vec3(0.0), vig);
	a = max(a, vig * 0.75);
	COLOR = vec4(col / max(a, 0.001), a * strength);
}
