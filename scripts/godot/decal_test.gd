# Isolert test: virker decals på standardmateriale vs. terrengshaderen?
# Kjør: godot --path . --script scripts/godot/decal_test.gd -- <utfil.png>
extends SceneTree

var frames := 0
var out := "decal_test.png"

func _init():
	var args := OS.get_cmdline_user_args()
	if args.size() > 0: out = args[0]
	var root3 := Node3D.new()
	get_root().add_child(root3)
	var cam := Camera3D.new()
	root3.add_child(cam)
	cam.look_at_from_position(Vector3(0, 6, 6), Vector3.ZERO)
	var light := DirectionalLight3D.new()
	root3.add_child(light)
	light.rotation_degrees = Vector3(-60, 30, 0)
	for i in 2:
		var mi := MeshInstance3D.new()
		var pm := PlaneMesh.new()
		pm.size = Vector2(3, 3)
		mi.mesh = pm
		if i == 0:
			var m := StandardMaterial3D.new()
			m.albedo_color = Color(0.8, 0.75, 0.6)
			mi.material_override = m
		else:
			var sm := ShaderMaterial.new()
			sm.shader = load("res://shaders/terrain.gdshader")
			mi.material_override = sm
		mi.position = Vector3(-1.6 + i * 3.2, 0, 0)
		root3.add_child(mi)
	var d := Decal.new()
	d.size = Vector3(6, 2, 1.0)
	var img := Image.create_empty(8, 8, false, Image.FORMAT_RGBA8)
	img.fill(Color.WHITE)
	d.texture_albedo = ImageTexture.create_from_image(img)
	d.modulate = Color(1, 0, 0)
	root3.add_child(d)

func _process(_d):
	frames += 1
	if frames == 10:
		get_root().get_texture().get_image().save_png(out)
		quit()
	return false
