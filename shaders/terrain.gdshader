shader_type spatial;
render_mode cull_disabled;

// Bakkefarge kommer fra vertex color (klimasone/helning beregnet på CPU).
// Shaderen legger på støy i verdensrom for å bryte opp flatene.

varying vec3 world_pos;

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x),
	           mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 base = pow(COLOR.rgb, vec3(2.2));
	float n = vnoise(world_pos.xz * 0.45) * 0.45 + vnoise(world_pos.xz * 2.1) * 0.35 + vnoise(world_pos.xz * 0.06) * 0.20;
	float dist = length(VERTEX);
	float detail = mix(0.80 + n * 0.40, 1.0, smoothstep(150.0, 600.0, dist));
	ALBEDO = base * detail;
	ROUGHNESS = 0.95;
	SPECULAR = 0.15;
}
