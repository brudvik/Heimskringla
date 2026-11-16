shader_type spatial;

// Bjørkebark: hvit med mørke, horisontale streker. Brukes på CommonTree-modellene for å lage bjørk.

uniform sampler2D bark_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D bark_normal : hint_normal, filter_linear_mipmap, repeat_enable;

varying vec3 obj_pos;

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	obj_pos = VERTEX;
}

void fragment() {
	vec3 tex = texture(bark_tex, UV).rgb;
	float lum = dot(tex, vec3(0.333));
	vec3 white = vec3(0.86, 0.85, 0.80) * (0.75 + lum * 0.5);
	float ang = atan(obj_pos.z, obj_pos.x);
	float n = vnoise(vec2(ang * 3.0, obj_pos.y * 9.0));
	float stripe = smoothstep(0.72, 0.8, n);
	float base_dark = 1.0 - smoothstep(0.0, 0.9, obj_pos.y);   // mørkere nederst
	vec3 col = mix(white, vec3(0.12, 0.11, 0.10), max(stripe, base_dark * 0.7));
	ALBEDO = col;
	NORMAL_MAP = texture(bark_normal, UV).rgb;
	ROUGHNESS = 0.85;
}
