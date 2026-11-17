# Lists classes matching a filter, with their properties (run: godot --headless --script scripts/godot/list_classes.gd -- <filter>)
extends SceneTree

func _init():
	var args := OS.get_cmdline_user_args()
	var filt: String = args[0] if args.size() > 0 else "IK"
	for c in ClassDB.get_class_list():
		if filt.to_lower() in c.to_lower():
			var props := []
			for p in ClassDB.class_get_property_list(c, true):
				props.append(p.name)
			print(c, " <- ", ClassDB.get_parent_class(c), " : ", props)
	quit()
