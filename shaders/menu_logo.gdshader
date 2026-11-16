shader_type canvas_item;

// Logoen: myk, mørk skygge bak (fra en uskarp mipmap av alfakanalen), og et lys som sveiper
// over metallet ved avsløringen og med jevne mellomrom etterpå.

uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float reveal = 1.0;        // 0..1 inntoning
uniform float sweep = -1.0;        // posisjon for lysstripen (-0.3..1.3), < -0.5 = av
uniform vec3 glow_color : source_color = vec3(1.0, 0.62, 0.3);

void fragment() {
	vec4 c = texture(tex, UV);
	// Bildefilen er beskåret helt nederst (knuteverket går ut i kanten): ton den mykt ut
	c.a *= smoothstep(1.0, 0.9, UV.y);
	// Myk, mørk skygge litt under logoen (fra en uskarp mipmap av alfakanalen)
	float shadow = textureLod(tex, UV + vec2(0.0, -0.035), 4.0).a;
	shadow = smoothstep(0.05, 0.8, shadow) * 0.55;
	// Lysstripe på skrå over metallet (bare der logoen er)
	// Bred, myk glans (gaussisk profil) i stedet for en skarp stripe
	float dx = (UV.x + UV.y * 0.35) - sweep * 1.35;
	float band = exp(-dx * dx / (2.0 * 0.11 * 0.11));
	vec3 col = c.rgb + vec3(1.0, 0.92, 0.75) * band * 0.28 * c.a * (0.6 + 0.4 * dot(c.rgb, vec3(0.33)));
	float logo_in = smoothstep(0.2, 1.0, reveal);
	float sh_in = smoothstep(0.0, 0.7, reveal);
	float a = c.a * logo_in + shadow * (1.0 - c.a * logo_in) * sh_in;
	vec3 outc = mix(vec3(0.0), col, clamp(c.a * logo_in / max(a, 0.001), 0.0, 1.0));
	COLOR = vec4(outc, a);
}
