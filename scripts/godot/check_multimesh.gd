# Sjekker MultiMesh-bufferformatet med custom data (kjør: godot --headless --script scripts/godot/check_multimesh.gd)
extends SceneTree

func _init():
	var mm := MultiMesh.new()
	mm.transform_format = MultiMesh.TRANSFORM_3D
	mm.use_custom_data = true
	mm.instance_count = 2
	print("buffer len for 2 instances: ", mm.buffer.size())
	var t := Transform3D(Basis().scaled(Vector3(2, 3, 4)), Vector3(10, 20, 30))
	mm.set_instance_transform(1, t)
	mm.set_instance_custom_data(1, Color(0.1, 0.2, 0.3, 0.4))
	print("buffer: ", mm.buffer)
	quit()
