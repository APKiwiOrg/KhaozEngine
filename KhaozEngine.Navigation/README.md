# KhaozEngine.Navigation

Engine-owned NPC navigation: clearance-grid walkability (one bake serves every agent radius), grid A* pathfinding behind an IPathPlanner seam with string-pulled waypoints, node budgets and partial paths, and a PathFollower that turns a moving goal into a per-tick steering direction for CharacterMovement.StepTowards.
