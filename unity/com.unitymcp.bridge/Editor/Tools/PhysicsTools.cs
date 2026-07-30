using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    public enum MCPColliderType
    {
        Box,
        Sphere,
        Capsule
    }

    public enum MCPForceMode
    {
        Force,
        Impulse,
        VelocityChange,
        Acceleration
    }

    public enum MCPJointType
    {
        Fixed,
        Hinge,
        Spring,
        Configurable
    }

    public enum MCPOverlapShape
    {
        Sphere,
        Box
    }

    public static class PhysicsTools
    {
        [MCPTool(
            "add_collider",
            "Adds a collider to a GameObject by path (Box/Sphere/Capsule). Shape parameters not relevant to the chosen type " +
            "are ignored (e.g. radius/height for Box). Omitted shape parameters keep Unity's default for a freshly added " +
            "collider of that type. GameObjects can have multiple colliders (compound colliders) — this always adds a new " +
            "one rather than replacing an existing one.",
            group: "physics")]
        public static MCPResult AddCollider(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Collider shape to add.")] MCPColliderType type,
            [MCPParam("Box size X. Box only; omit to keep Unity's default (1).")] float? sizeX = null,
            [MCPParam("Box size Y. Box only; omit to keep Unity's default (1).")] float? sizeY = null,
            [MCPParam("Box size Z. Box only; omit to keep Unity's default (1).")] float? sizeZ = null,
            [MCPParam("Collider center offset X. Omit to keep Unity's default (0).")] float? centerX = null,
            [MCPParam("Collider center offset Y. Omit to keep Unity's default (0).")] float? centerY = null,
            [MCPParam("Collider center offset Z. Omit to keep Unity's default (0).")] float? centerZ = null,
            [MCPParam("Radius. Sphere/Capsule only; omit to keep Unity's default (0.5).")] float? radius = null,
            [MCPParam("Height. Capsule only; omit to keep Unity's default (2).")] float? height = null,
            [MCPParam("Whether this collider is a trigger (no physical collision, only overlap events).")] bool isTrigger = false)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            switch (type)
            {
                case MCPColliderType.Box:
                {
                    var collider = Undo.AddComponent<BoxCollider>(go);
                    var size = collider.size;
                    ApplyVector3Overrides(ref size, sizeX, sizeY, sizeZ);
                    collider.size = size;

                    var center = collider.center;
                    ApplyVector3Overrides(ref center, centerX, centerY, centerZ);
                    collider.center = center;

                    collider.isTrigger = isTrigger;

                    return MCPResult.Success(new
                    {
                        path,
                        type = "Box",
                        size = Vec3ToAnon(collider.size),
                        center = Vec3ToAnon(collider.center),
                        isTrigger = collider.isTrigger
                    });
                }

                case MCPColliderType.Sphere:
                {
                    var collider = Undo.AddComponent<SphereCollider>(go);
                    if (radius.HasValue) collider.radius = radius.Value;

                    var center = collider.center;
                    ApplyVector3Overrides(ref center, centerX, centerY, centerZ);
                    collider.center = center;

                    collider.isTrigger = isTrigger;

                    return MCPResult.Success(new
                    {
                        path,
                        type = "Sphere",
                        radius = collider.radius,
                        center = Vec3ToAnon(collider.center),
                        isTrigger = collider.isTrigger
                    });
                }

                case MCPColliderType.Capsule:
                {
                    var collider = Undo.AddComponent<CapsuleCollider>(go);
                    if (radius.HasValue) collider.radius = radius.Value;
                    if (height.HasValue) collider.height = height.Value;

                    var center = collider.center;
                    ApplyVector3Overrides(ref center, centerX, centerY, centerZ);
                    collider.center = center;

                    collider.isTrigger = isTrigger;

                    return MCPResult.Success(new
                    {
                        path,
                        type = "Capsule",
                        radius = collider.radius,
                        height = collider.height,
                        center = Vec3ToAnon(collider.center),
                        isTrigger = collider.isTrigger
                    });
                }

                default:
                    return MCPResult.Fail($"Unsupported collider type '{type}'.");
            }
        }

        [MCPTool(
            "configure_rigidbody",
            "Adds a Rigidbody to a GameObject if it doesn't already have one, then sets the given properties. Omitted " +
            "properties are left at their current value (or Unity's default, for a newly added Rigidbody). Note: on newer " +
            "Unity versions 'drag'/'angularDrag' are the obsolete-but-functional names for what's now called " +
            "linearDamping/angularDamping — check your Editor version if these stop applying.",
            group: "physics")]
        public static MCPResult ConfigureRigidbody(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Mass in kg. Omit to leave unchanged.")] float? mass = null,
            [MCPParam("Linear drag. Omit to leave unchanged.")] float? drag = null,
            [MCPParam("Angular drag. Omit to leave unchanged.")] float? angularDrag = null,
            [MCPParam("Whether gravity affects this body. Omit to leave unchanged.")] bool? useGravity = null,
            [MCPParam("Whether physics forces are disabled (object is moved only by script/animation). Omit to leave unchanged.")] bool? isKinematic = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = Undo.AddComponent<Rigidbody>(go);
            else Undo.RecordObject(rb, "MCP: Configure Rigidbody");

            if (mass.HasValue) rb.mass = mass.Value;
            if (drag.HasValue) rb.linearDamping = drag.Value;
            if (angularDrag.HasValue) rb.angularDamping = angularDrag.Value;
            if (useGravity.HasValue) rb.useGravity = useGravity.Value;
            if (isKinematic.HasValue) rb.isKinematic = isKinematic.Value;

            return MCPResult.Success(RigidbodyStateAnon(rb));
        }

        [MCPTool(
            "set_velocity",
            "Sets linear and/or angular velocity on a GameObject's Rigidbody. Fails if it has no Rigidbody — call " +
            "configure_rigidbody first. Omitted axes are left unchanged.",
            group: "physics")]
        public static MCPResult SetVelocity(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Linear velocity X. Omit to leave unchanged.")] float? velX = null,
            [MCPParam("Linear velocity Y. Omit to leave unchanged.")] float? velY = null,
            [MCPParam("Linear velocity Z. Omit to leave unchanged.")] float? velZ = null,
            [MCPParam("Angular velocity X (degrees/sec). Omit to leave unchanged.")] float? angVelX = null,
            [MCPParam("Angular velocity Y (degrees/sec). Omit to leave unchanged.")] float? angVelY = null,
            [MCPParam("Angular velocity Z (degrees/sec). Omit to leave unchanged.")] float? angVelZ = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return MCPResult.Fail($"GameObject at '{path}' has no Rigidbody. Call configure_rigidbody first.");

            var vel = rb.linearVelocity;
            ApplyVector3Overrides(ref vel, velX, velY, velZ);
            rb.linearVelocity = vel;

            var angVel = rb.angularVelocity;
            ApplyVector3Overrides(ref angVel, angVelX, angVelY, angVelZ);
            rb.angularVelocity = angVel;

            return MCPResult.Success(RigidbodyStateAnon(rb));
        }

        [MCPTool(
            "apply_force",
            "Applies a force or impulse to a GameObject's Rigidbody. Fails if it has no Rigidbody. Unlike set_velocity, " +
            "x/y/z are all required — a force with no direction isn't meaningful.",
            group: "physics")]
        public static MCPResult ApplyForce(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path,
            [MCPParam("Force X component.")] float x,
            [MCPParam("Force Y component.")] float y,
            [MCPParam("Force Z component.")] float z,
            [MCPParam("How the force is applied — Force (continuous, mass-dependent), Impulse (instant, mass-dependent), VelocityChange (instant, ignores mass), or Acceleration (continuous, ignores mass).")] MCPForceMode mode = MCPForceMode.Force)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return MCPResult.Fail($"GameObject at '{path}' has no Rigidbody. Call configure_rigidbody first.");

            rb.AddForce(new Vector3(x, y, z), MapForceMode(mode));

            return MCPResult.Success();
        }

        [MCPTool(
            "get_rigidbody_state",
            "Reads back a GameObject's Rigidbody properties: mass, drag, angularDrag, useGravity, isKinematic, velocity, " +
            "angularVelocity. Fails if it has no Rigidbody.",
            group: "physics", readOnly: true)]
        public static MCPResult GetRigidbodyState(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the target GameObject.")] string path)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return MCPResult.Fail($"GameObject at '{path}' has no Rigidbody.");

            return MCPResult.Success(RigidbodyStateAnon(rb));
        }

        [MCPTool(
            "raycast",
            "Casts a ray and returns the first hit, if any. Provide either fromPath (casts from that GameObject's world " +
            "position) or explicit originX/Y/Z — not both. Direction defaults to straight down (0,-1,0), the common " +
            "'what's beneath this object' case. Pure query — never modifies the scene.",
            group: "physics")]
        public static MCPResult Raycast(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of a GameObject to cast from (uses its world position). Omit if using originX/Y/Z instead.")] string fromPath = null,
            [MCPParam("Explicit world-space origin X. Requires originY and originZ too. Ignored if fromPath is given.")] float? originX = null,
            [MCPParam("Explicit world-space origin Y. Requires originX and originZ too. Ignored if fromPath is given.")] float? originY = null,
            [MCPParam("Explicit world-space origin Z. Requires originX and originY too. Ignored if fromPath is given.")] float? originZ = null,
            [MCPParam("Ray direction X component. Defaults to straight down.")] float dirX = 0f,
            [MCPParam("Ray direction Y component. Defaults to straight down.")] float dirY = -1f,
            [MCPParam("Ray direction Z component. Defaults to straight down.")] float dirZ = 0f,
            [MCPParam("Maximum ray distance.")] float maxDistance = 100f,
            [MCPParam("Unity physics layer mask to restrict the raycast to. Omit to use all layers.")] int? layerMask = null)
        {
            Vector3 origin;
            if (!string.IsNullOrEmpty(fromPath))
            {
                var go = MCPSceneUtil.ResolvePath(fromPath);
                if (go == null) return MCPResult.Fail($"Path '{fromPath}' not found.");
                origin = go.transform.position;
            }
            else if (originX.HasValue && originY.HasValue && originZ.HasValue)
            {
                origin = new Vector3(originX.Value, originY.Value, originZ.Value);
            }
            else
            {
                return MCPResult.Fail("Provide either fromPath, or all three of originX/originY/originZ.");
            }

            var direction = new Vector3(dirX, dirY, dirZ);
            int mask = layerMask ?? Physics.DefaultRaycastLayers;

            bool didHit = Physics.Raycast(origin, direction, out var hitInfo, maxDistance, mask);
            if (!didHit) return MCPResult.Success(new { hit = false });

            return MCPResult.Success(new
            {
                hit = true,
                point = Vec3ToAnon(hitInfo.point),
                normal = Vec3ToAnon(hitInfo.normal),
                distance = hitInfo.distance,
                colliderPath = hitInfo.collider != null ? MCPSceneUtil.GetPath(hitInfo.collider.gameObject) : null
            });
        }

        [MCPTool(
            "spherecast",
            "Casts a sphere (a 'thick' raycast) and returns the first hit, if any -- use where a thin ray would miss " +
            "(AI perception cones, aim assist). Same origin/direction conventions as raycast.",
            group: "physics")]
        public static MCPResult SphereCast(
            MCPToolContext ctx,
            [MCPParam("Sphere radius.")] float radius,
            [MCPParam("Hierarchy path of a GameObject to cast from (uses its world position). Omit if using originX/Y/Z instead.")] string fromPath = null,
            [MCPParam("Explicit world-space origin X. Requires originY and originZ too. Ignored if fromPath is given.")] float? originX = null,
            [MCPParam("Explicit world-space origin Y. Requires originX and originZ too. Ignored if fromPath is given.")] float? originY = null,
            [MCPParam("Explicit world-space origin Z. Requires originX and originY too. Ignored if fromPath is given.")] float? originZ = null,
            [MCPParam("Cast direction X component. Defaults to straight down.")] float dirX = 0f,
            [MCPParam("Cast direction Y component. Defaults to straight down.")] float dirY = -1f,
            [MCPParam("Cast direction Z component. Defaults to straight down.")] float dirZ = 0f,
            [MCPParam("Maximum cast distance.")] float maxDistance = 100f,
            [MCPParam("Unity physics layer mask to restrict the cast to. Omit to use all layers.")] int? layerMask = null)
        {
            Vector3 origin;
            if (!string.IsNullOrEmpty(fromPath))
            {
                var go = MCPSceneUtil.ResolvePath(fromPath);
                if (go == null) return MCPResult.Fail($"Path '{fromPath}' not found.");
                origin = go.transform.position;
            }
            else if (originX.HasValue && originY.HasValue && originZ.HasValue)
            {
                origin = new Vector3(originX.Value, originY.Value, originZ.Value);
            }
            else
            {
                return MCPResult.Fail("Provide either fromPath, or all three of originX/originY/originZ.");
            }

            var direction = new Vector3(dirX, dirY, dirZ);
            int mask = layerMask ?? Physics.DefaultRaycastLayers;

            bool didHit = Physics.SphereCast(origin, radius, direction, out var hitInfo, maxDistance, mask);
            if (!didHit) return MCPResult.Success(new { hit = false });

            return MCPResult.Success(new
            {
                hit = true,
                point = Vec3ToAnon(hitInfo.point),
                normal = Vec3ToAnon(hitInfo.normal),
                distance = hitInfo.distance,
                colliderPath = hitInfo.collider != null ? MCPSceneUtil.GetPath(hitInfo.collider.gameObject) : null
            });
        }

        [MCPTool(
            "overlap_query",
            "Finds every collider within a sphere or box region, without casting a ray -- use for area-of-effect / " +
            "proximity checks (e.g. 'what's within melee range').",
            group: "physics")]
        public static MCPResult OverlapQuery(
            MCPToolContext ctx,
            [MCPParam("Region shape.")] MCPOverlapShape shape,
            [MCPParam("Region center X.")] float centerX,
            [MCPParam("Region center Y.")] float centerY,
            [MCPParam("Region center Z.")] float centerZ,
            [MCPParam("Sphere radius. Sphere only.")] float radius = 1f,
            [MCPParam("Box half-extents X. Box only.")] float halfExtentX = 0.5f,
            [MCPParam("Box half-extents Y. Box only.")] float halfExtentY = 0.5f,
            [MCPParam("Box half-extents Z. Box only.")] float halfExtentZ = 0.5f,
            [MCPParam("Box rotation around Y in degrees. Box only. Defaults to 0.")] float boxRotationY = 0f,
            [MCPParam("Unity physics layer mask to restrict the query to. Omit to use all layers.")] int? layerMask = null)
        {
            var center = new Vector3(centerX, centerY, centerZ);
            int mask = layerMask ?? Physics.DefaultRaycastLayers;

            Collider[] hits = shape switch
            {
                MCPOverlapShape.Sphere => Physics.OverlapSphere(center, radius, mask),
                MCPOverlapShape.Box => Physics.OverlapBox(center, new Vector3(halfExtentX, halfExtentY, halfExtentZ), Quaternion.Euler(0, boxRotationY, 0), mask),
                _ => Array.Empty<Collider>()
            };

            var paths = hits.Select(c => MCPSceneUtil.GetPath(c.gameObject)).ToList();
            return MCPResult.Success(new { count = paths.Count, paths });
        }

        [MCPTool(
            "add_joint",
            "Adds a physics joint to a GameObject, optionally connecting it to another Rigidbody. Adding a joint " +
            "automatically adds a Rigidbody to this GameObject first if it doesn't already have one (Unity's own " +
            "RequireComponent enforcement). type: Fixed, Hinge, Spring, or Configurable.",
            group: "physics")]
        public static MCPResult AddJoint(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject to add the joint to.")] string path,
            [MCPParam("Joint type to add.")] MCPJointType type,
            [MCPParam("Hierarchy path of another GameObject with a Rigidbody to connect to. Omit to anchor in world space instead.")] string connectedPath = null,
            [MCPParam("Force above which the joint breaks. Omit for unbreakable (infinite).")] float? breakForce = null,
            [MCPParam("Torque above which the joint breaks. Omit for unbreakable (infinite).")] float? breakTorque = null)
        {
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");

            Rigidbody connectedBody = null;
            if (!string.IsNullOrEmpty(connectedPath))
            {
                var connectedGo = MCPSceneUtil.ResolvePath(connectedPath);
                if (connectedGo == null) return MCPResult.Fail($"connectedPath '{connectedPath}' not found.");
                connectedBody = connectedGo.GetComponent<Rigidbody>();
                if (connectedBody == null) return MCPResult.Fail($"GameObject at '{connectedPath}' has no Rigidbody.");
            }

            Joint joint = type switch
            {
                MCPJointType.Fixed => Undo.AddComponent<FixedJoint>(go),
                MCPJointType.Hinge => Undo.AddComponent<HingeJoint>(go),
                MCPJointType.Spring => Undo.AddComponent<SpringJoint>(go),
                MCPJointType.Configurable => Undo.AddComponent<ConfigurableJoint>(go),
                _ => null
            };
            if (joint == null) return MCPResult.Fail($"Unhandled joint type '{type}'.");

            joint.connectedBody = connectedBody;
            if (breakForce.HasValue) joint.breakForce = breakForce.Value;
            if (breakTorque.HasValue) joint.breakTorque = breakTorque.Value;

            return MCPResult.Success(new { path, jointType = type.ToString() });
        }

        [MCPTool(
            "set_layer_collision_matrix",
            "Enables or disables collision detection between two physics layers, by name -- affects every collider " +
            "on those layers project-wide, not just one GameObject.",
            group: "physics")]
        public static MCPResult SetLayerCollisionMatrix(
            MCPToolContext ctx,
            [MCPParam("First layer name, e.g. 'Default'.")] string layer1,
            [MCPParam("Second layer name, e.g. 'Player'. May be the same as layer1.")] string layer2,
            [MCPParam("true if the two layers should collide with each other, false to ignore collisions between them.")] bool collide)
        {
            int index1 = LayerMask.NameToLayer(layer1);
            if (index1 < 0) return MCPResult.Fail($"Layer '{layer1}' does not exist.");

            int index2 = LayerMask.NameToLayer(layer2);
            if (index2 < 0) return MCPResult.Fail($"Layer '{layer2}' does not exist.");

            Physics.IgnoreLayerCollision(index1, index2, !collide);
            return MCPResult.Success(new { layer1, layer2, collide });
        }

        [MCPTool("create_physics_material", "Creates a PhysicsMaterial asset with the given friction/bounce settings.", group: "physics")]
        public static MCPResult CreatePhysicsMaterial(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/, e.g. 'Physics/Ice.physicMaterial'.")] string assetPath,
            [MCPParam("Dynamic friction (0-1 typical). Omit for Unity's default.")] float? dynamicFriction = null,
            [MCPParam("Static friction (0-1 typical). Omit for Unity's default.")] float? staticFriction = null,
            [MCPParam("Bounciness (0-1). Omit for Unity's default.")] float? bounciness = null)
        {
            // Unity's asset importer only recognizes the legacy ".physicMaterial" extension for this
            // type even though the runtime class was renamed PhysicMaterial -> PhysicsMaterial. An asset
            // saved as ".physicsMaterial" silently fails to import as a loadable PhysicsMaterial, so we
            // normalize the extension here rather than trusting the caller's spelling.
            if (assetPath.EndsWith(".physicsMaterial", StringComparison.OrdinalIgnoreCase))
                assetPath = assetPath.Substring(0, assetPath.Length - ".physicsMaterial".Length) + ".physicMaterial";
            else if (!assetPath.EndsWith(".physicMaterial", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.physicMaterial'.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);

            if (System.IO.File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var material = new PhysicsMaterial();
            if (dynamicFriction.HasValue) material.dynamicFriction = dynamicFriction.Value;
            if (staticFriction.HasValue) material.staticFriction = staticFriction.Value;
            if (bounciness.HasValue) material.bounciness = bounciness.Value;

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            var unityAssetPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.CreateAsset(material, unityAssetPath);

            return MCPResult.Success(new { assetPath = unityAssetPath });
        }

        [MCPTool(
            "configure_physics_settings",
            "Configures project-wide physics settings: gravity and solver iteration counts. Omitted parameters are " +
            "left unchanged.",
            group: "physics")]
        public static MCPResult ConfigurePhysicsSettings(
            MCPToolContext ctx,
            [MCPParam("Gravity X. Omit to leave unchanged.")] float? gravityX = null,
            [MCPParam("Gravity Y. Omit to leave unchanged (Unity's default is -9.81).")] float? gravityY = null,
            [MCPParam("Gravity Z. Omit to leave unchanged.")] float? gravityZ = null,
            [MCPParam("Default solver iteration count. Omit to leave unchanged.")] int? solverIterations = null,
            [MCPParam("Default solver velocity iteration count. Omit to leave unchanged.")] int? solverVelocityIterations = null)
        {
            if (gravityX.HasValue || gravityY.HasValue || gravityZ.HasValue)
            {
                var gravity = Physics.gravity;
                if (gravityX.HasValue) gravity.x = gravityX.Value;
                if (gravityY.HasValue) gravity.y = gravityY.Value;
                if (gravityZ.HasValue) gravity.z = gravityZ.Value;
                Physics.gravity = gravity;
            }

            if (solverIterations.HasValue) Physics.defaultSolverIterations = solverIterations.Value;
            if (solverVelocityIterations.HasValue) Physics.defaultSolverVelocityIterations = solverVelocityIterations.Value;

            return MCPResult.Success(new
            {
                gravity = Vec3ToAnon(Physics.gravity),
                solverIterations = Physics.defaultSolverIterations,
                solverVelocityIterations = Physics.defaultSolverVelocityIterations
            });
        }

        internal static void ApplyVector3Overrides(ref Vector3 vec, float? x, float? y, float? z)
        {
            if (x.HasValue) vec.x = x.Value;
            if (y.HasValue) vec.y = y.Value;
            if (z.HasValue) vec.z = z.Value;
        }

        private static object Vec3ToAnon(Vector3 v) => new { x = v.x, y = v.y, z = v.z };

        private static object RigidbodyStateAnon(Rigidbody rb) => new
        {
            mass = rb.mass,
            drag = rb.linearDamping,
            angularDrag = rb.angularDamping,
            useGravity = rb.useGravity,
            isKinematic = rb.isKinematic,
            velocity = Vec3ToAnon(rb.linearVelocity),
            angularVelocity = Vec3ToAnon(rb.angularVelocity)
        };

        private static ForceMode MapForceMode(MCPForceMode mode)
        {
            switch (mode)
            {
                case MCPForceMode.Impulse: return ForceMode.Impulse;
                case MCPForceMode.VelocityChange: return ForceMode.VelocityChange;
                case MCPForceMode.Acceleration: return ForceMode.Acceleration;
                default: return ForceMode.Force;
            }
        }
    }
}
