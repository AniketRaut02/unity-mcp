using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP;
using UnityMCP.Security;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Group U of the tool catalog -- Terrain &amp; Environment. Every type here (Terrain, TerrainData, TerrainLayer,
    /// TreePrototype/TreeInstance, DetailPrototype, WindZone) is core Unity (com.unity.modules.terrain, always
    /// present) -- no optional package, no reflection, confirmed via live spike including one real gotcha:
    /// TerrainData.detailResolution is 0 on a freshly-created TerrainData and must be set via SetDetailResolution()
    /// before any detail-layer array access, or GetDetailLayer throws IndexOutOfRangeException. All brush-style
    /// tools (sculpt/paint/details/holes) share one circular-falloff grid helper, since height/alpha/detail maps
    /// each have their own independent resolution over the same world-space extents.
    /// </summary>
    public static class TerrainTools
    {
        [MCPTool(
            "create_terrain",
            "Creates a new TerrainData asset and a Terrain GameObject using it (flat, default height 0).",
            group: "terrain")]
        public static MCPResult CreateTerrain(
            MCPToolContext ctx,
            [MCPParam("Destination path relative to Assets/ for the TerrainData asset, e.g. 'Terrain/Ground.asset'.")] string assetPath,
            [MCPParam("Name for the new Terrain GameObject. Defaults to 'Terrain'.")] string name = "Terrain",
            [MCPParam("World-space size along X in meters. Defaults to 500.")] float width = 500f,
            [MCPParam("World-space size along Z in meters. Defaults to 500.")] float length = 500f,
            [MCPParam("World-space height range in meters. Defaults to 300.")] float height = 300f,
            [MCPParam("Heightmap resolution -- must be (power of two) + 1, e.g. 513, 1025. Defaults to 513.")] int heightmapResolution = 513,
            [MCPParam("World-space X position.")] float x = 0f,
            [MCPParam("World-space Y position.")] float y = 0f,
            [MCPParam("World-space Z position.")] float z = 0f)
        {
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("assetPath must end with '.asset'.");
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, assetPath, out var fullPath, out var guardError))
                return MCPResult.Fail(guardError);
            if (File.Exists(fullPath))
                return MCPResult.Fail($"'{assetPath}' already exists.");

            var data = new TerrainData();
            data.heightmapResolution = heightmapResolution;
            data.size = new Vector3(width, height, length);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var unityPath = "Assets/" + assetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.CreateAsset(data, unityPath);
            AssetDatabase.SaveAssets();

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Terrain");
            go.transform.position = new Vector3(x, y, z);

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go), dataAssetPath = unityPath });
        }

        private static MCPResult ResolveTerrain(string path, out Terrain terrain, out TerrainData data)
        {
            terrain = null;
            data = null;
            var go = MCPSceneUtil.ResolvePath(path);
            if (go == null) return MCPResult.Fail($"Path '{path}' not found.");
            terrain = go.GetComponent<Terrain>();
            if (terrain == null) return MCPResult.Fail($"GameObject at '{path}' has no Terrain component.");
            data = terrain.terrainData;
            if (data == null) return MCPResult.Fail($"Terrain at '{path}' has no TerrainData assigned.");
            return null;
        }

        /// <summary>
        /// Shared circular brush: converts a world-space center+radius into grid cells over a map of the given
        /// resolution (height/alpha/detail maps each have their own resolution over the same world extents), and
        /// invokes apply(gridX, gridZ, falloff01) for every cell inside the circle (1 at center, 0 at the edge).
        /// </summary>
        private static void ApplyCircularBrush(int resolution, Vector3 terrainSize, Vector3 terrainWorldPos, float worldX, float worldZ, float worldRadius, Action<int, int, float> apply)
        {
            float normX = (worldX - terrainWorldPos.x) / terrainSize.x;
            float normZ = (worldZ - terrainWorldPos.z) / terrainSize.z;
            int centerX = Mathf.RoundToInt(normX * (resolution - 1));
            int centerZ = Mathf.RoundToInt(normZ * (resolution - 1));
            float radiusCellsX = Mathf.Max(1f, (worldRadius / terrainSize.x) * (resolution - 1));
            float radiusCellsZ = Mathf.Max(1f, (worldRadius / terrainSize.z) * (resolution - 1));
            int sweep = Mathf.CeilToInt(Mathf.Max(radiusCellsX, radiusCellsZ));

            for (int dz = -sweep; dz <= sweep; dz++)
            {
                for (int dx = -sweep; dx <= sweep; dx++)
                {
                    int gx = centerX + dx;
                    int gz = centerZ + dz;
                    if (gx < 0 || gx >= resolution || gz < 0 || gz >= resolution) continue;

                    float distNorm = Mathf.Sqrt((dx * dx) / (radiusCellsX * radiusCellsX) + (dz * dz) / (radiusCellsZ * radiusCellsZ));
                    if (distNorm > 1f) continue;

                    apply(gx, gz, 1f - distNorm);
                }
            }
        }

        [MCPTool(
            "sculpt_terrain_height",
            "Raises, lowers, smooths, or flattens a circular region of a Terrain's heightmap, centered at a " +
            "world-space X/Z position with linear falloff to the edge of radius.",
            group: "terrain")]
        public static MCPResult SculptTerrainHeight(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("Sculpt mode: Raise, Lower, Smooth, or Flatten.")] string mode,
            [MCPParam("World-space X of the brush center.")] float centerX,
            [MCPParam("World-space Z of the brush center.")] float centerZ,
            [MCPParam("Brush radius in world units.")] float radius,
            [MCPParam("Effect strength (0-1 normalized height delta at the brush center per call). Defaults to 0.1.")] float strength = 0.1f,
            [MCPParam("Flatten mode only: target normalized height (0-1) to flatten toward. Defaults to 0.5.")] float targetHeight = 0.5f)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;

            int res = data.heightmapResolution;
            var heights = data.GetHeights(0, 0, res, res);

            switch (mode)
            {
                case "Raise":
                    ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                        heights[gz, gx] = Mathf.Clamp01(heights[gz, gx] + strength * falloff));
                    break;
                case "Lower":
                    ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                        heights[gz, gx] = Mathf.Clamp01(heights[gz, gx] - strength * falloff));
                    break;
                case "Flatten":
                    ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                        heights[gz, gx] = Mathf.Lerp(heights[gz, gx], targetHeight, strength * falloff));
                    break;
                case "Smooth":
                    var original = (float[,])heights.Clone();
                    ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                    {
                        float sum = 0f; int count = 0;
                        for (int nz = Mathf.Max(0, gz - 1); nz <= Mathf.Min(res - 1, gz + 1); nz++)
                            for (int nx = Mathf.Max(0, gx - 1); nx <= Mathf.Min(res - 1, gx + 1); nx++)
                            { sum += original[nz, nx]; count++; }
                        float average = sum / count;
                        heights[gz, gx] = Mathf.Lerp(heights[gz, gx], average, strength * falloff);
                    });
                    break;
                default:
                    return MCPResult.Fail($"Unknown mode '{mode}'. Valid values: Raise, Lower, Smooth, Flatten.");
            }

            data.SetHeights(0, 0, heights);
            return MCPResult.Success();
        }

        [MCPTool("add_terrain_layer", "Creates a TerrainLayer asset from a diffuse texture and registers it on a Terrain (for painting via paint_terrain_texture).", group: "terrain")]
        public static MCPResult AddTerrainLayer(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("Path relative to Assets/ of the diffuse texture.")] string diffuseTexturePath,
            [MCPParam("Destination path relative to Assets/ for the new TerrainLayer asset, e.g. 'Terrain/Layers/Grass.terrainlayer'.")] string layerAssetPath,
            [MCPParam("World-space tile size X in meters. Defaults to 15.")] float tileSizeX = 15f,
            [MCPParam("World-space tile size Y (Z on the ground) in meters. Defaults to 15.")] float tileSizeY = 15f)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, diffuseTexturePath, out var texFullPath, out var texGuardError))
                return MCPResult.Fail(texGuardError);
            if (!File.Exists(texFullPath)) return MCPResult.Fail($"'{diffuseTexturePath}' does not exist.");
            var texUnityPath = "Assets/" + diffuseTexturePath.Replace('\\', '/').TrimStart('/');
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texUnityPath);
            if (texture == null) return MCPResult.Fail($"Could not load a Texture2D at '{diffuseTexturePath}'.");

            if (!layerAssetPath.EndsWith(".terrainlayer", StringComparison.OrdinalIgnoreCase))
                return MCPResult.Fail("layerAssetPath must end with '.terrainlayer'.");
            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, layerAssetPath, out var layerFullPath, out var layerGuardError))
                return MCPResult.Fail(layerGuardError);
            if (File.Exists(layerFullPath)) return MCPResult.Fail($"'{layerAssetPath}' already exists.");

            var layer = new TerrainLayer { diffuseTexture = texture, tileSize = new Vector2(tileSizeX, tileSizeY) };
            Directory.CreateDirectory(Path.GetDirectoryName(layerFullPath));
            var layerUnityPath = "Assets/" + layerAssetPath.Replace('\\', '/').TrimStart('/');
            AssetDatabase.CreateAsset(layer, layerUnityPath);
            AssetDatabase.SaveAssets();

            var layers = data.terrainLayers ?? Array.Empty<TerrainLayer>();
            var newLayers = new TerrainLayer[layers.Length + 1];
            Array.Copy(layers, newLayers, layers.Length);
            newLayers[layers.Length] = layer;
            data.terrainLayers = newLayers;

            return MCPResult.Success(new { layerAssetPath = layerUnityPath, layerIndex = layers.Length });
        }

        [MCPTool(
            "paint_terrain_texture",
            "Paints a registered terrain layer's weight into a circular region of the alphamap/splatmap, centered " +
            "at a world-space X/Z position with linear falloff. Other layers' weights are proportionally reduced " +
            "so all layer weights at a pixel still sum to 1 (Unity's own splatmap requirement).",
            group: "terrain")]
        public static MCPResult PaintTerrainTexture(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("Index into the Terrain's terrainLayers array (see add_terrain_layer's returned layerIndex).")] int layerIndex,
            [MCPParam("World-space X of the brush center.")] float centerX,
            [MCPParam("World-space Z of the brush center.")] float centerZ,
            [MCPParam("Brush radius in world units.")] float radius,
            [MCPParam("Target weight at the brush center (0-1). Defaults to 1.")] float opacity = 1f)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;
            if (data.terrainLayers == null || layerIndex < 0 || layerIndex >= data.terrainLayers.Length)
                return MCPResult.Fail($"layerIndex {layerIndex} is out of range (terrain has {data.terrainLayers?.Length ?? 0} layer(s) -- add one via add_terrain_layer first).");

            int res = data.alphamapResolution;
            int layerCount = data.alphamapLayers;
            var alphamaps = data.GetAlphamaps(0, 0, res, res);

            ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
            {
                float targetWeight = Mathf.Clamp01(opacity * falloff);
                float currentWeight = alphamaps[gz, gx, layerIndex];
                float remainingOthers = 1f - currentWeight;
                float newRemainingOthers = 1f - targetWeight;
                float scale = remainingOthers > 0.0001f ? newRemainingOthers / remainingOthers : 0f;

                for (int l = 0; l < layerCount; l++)
                    alphamaps[gz, gx, l] *= scale;
                alphamaps[gz, gx, layerIndex] = targetWeight;
            });

            data.SetAlphamaps(0, 0, alphamaps);
            return MCPResult.Success();
        }

        [MCPTool(
            "place_terrain_trees",
            "Adds a tree prototype (from a prefab) if not already registered, then scatters tree instances at " +
            "the given normalized (0-1) terrain-space positions.",
            group: "terrain")]
        public static MCPResult PlaceTerrainTrees(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("Path relative to Assets/ of the tree prefab.")] string prefabPath,
            [MCPParam("Positions, each as \"normX,normZ\" in 0-1 terrain space, e.g. [\"0.2,0.3\", \"0.5,0.6\"].")] string[] positions,
            [MCPParam("Uniform width scale for each placed tree. Defaults to 1.")] float widthScale = 1f,
            [MCPParam("Uniform height scale for each placed tree. Defaults to 1.")] float heightScale = 1f)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;
            if (positions == null || positions.Length == 0) return MCPResult.Fail("positions must contain at least one \"normX,normZ\" entry.");

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, prefabPath, out var prefabFullPath, out var prefabGuardError))
                return MCPResult.Fail(prefabGuardError);
            if (!File.Exists(prefabFullPath)) return MCPResult.Fail($"'{prefabPath}' does not exist.");
            var prefabUnityPath = "Assets/" + prefabPath.Replace('\\', '/').TrimStart('/');
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabUnityPath);
            if (prefab == null) return MCPResult.Fail($"Could not load a prefab GameObject at '{prefabPath}'.");

            var prototypes = data.treePrototypes ?? Array.Empty<TreePrototype>();
            int prototypeIndex = Array.FindIndex(prototypes, p => p.prefab == prefab);
            if (prototypeIndex < 0)
            {
                var newPrototypes = new TreePrototype[prototypes.Length + 1];
                Array.Copy(prototypes, newPrototypes, prototypes.Length);
                newPrototypes[prototypes.Length] = new TreePrototype { prefab = prefab };
                data.treePrototypes = newPrototypes;
                prototypeIndex = prototypes.Length;
            }

            int placed = 0;
            foreach (var entry in positions)
            {
                var parts = entry.Split(',');
                if (parts.Length != 2 || !float.TryParse(parts[0], out var nx) || !float.TryParse(parts[1], out var nz))
                    return MCPResult.Fail($"Invalid position entry '{entry}' -- expected \"normX,normZ\".");

                terrain.AddTreeInstance(new TreeInstance
                {
                    position = new Vector3(nx, 0f, nz),
                    prototypeIndex = prototypeIndex,
                    widthScale = widthScale,
                    heightScale = heightScale,
                    color = Color.white,
                    lightmapColor = Color.white,
                });
                placed++;
            }

            return MCPResult.Success(new { prototypeIndex, placedCount = placed, totalTreeCount = data.treeInstanceCount });
        }

        [MCPTool(
            "place_terrain_details",
            "Adds a detail prototype (grass texture) if not already registered, then paints its density into a " +
            "circular region centered at a world-space X/Z position with linear falloff.",
            group: "terrain")]
        public static MCPResult PlaceTerrainDetails(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("Path relative to Assets/ of the detail billboard texture.")] string detailTexturePath,
            [MCPParam("World-space X of the brush center.")] float centerX,
            [MCPParam("World-space Z of the brush center.")] float centerZ,
            [MCPParam("Brush radius in world units.")] float radius,
            [MCPParam("Max instances per detail cell at the brush center (0-16 typical). Defaults to 8.")] int density = 8)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;

            if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, detailTexturePath, out var texFullPath, out var texGuardError))
                return MCPResult.Fail(texGuardError);
            if (!File.Exists(texFullPath)) return MCPResult.Fail($"'{detailTexturePath}' does not exist.");
            var texUnityPath = "Assets/" + detailTexturePath.Replace('\\', '/').TrimStart('/');
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texUnityPath);
            if (texture == null) return MCPResult.Fail($"Could not load a Texture2D at '{detailTexturePath}'.");

            if (data.detailResolution == 0) data.SetDetailResolution(512, 16);

            var prototypes = data.detailPrototypes ?? Array.Empty<DetailPrototype>();
            int prototypeIndex = Array.FindIndex(prototypes, p => p.prototypeTexture == texture);
            if (prototypeIndex < 0)
            {
                var newPrototypes = new DetailPrototype[prototypes.Length + 1];
                Array.Copy(prototypes, newPrototypes, prototypes.Length);
                newPrototypes[prototypes.Length] = new DetailPrototype { usePrototypeMesh = false, prototypeTexture = texture };
                data.detailPrototypes = newPrototypes;
                prototypeIndex = prototypes.Length;
            }

            int res = data.detailResolution;
            var layer = data.GetDetailLayer(0, 0, res, res, prototypeIndex);
            ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                layer[gz, gx] = Mathf.RoundToInt(density * falloff));
            data.SetDetailLayer(0, 0, prototypeIndex, layer);

            return MCPResult.Success(new { prototypeIndex });
        }

        [MCPTool(
            "paint_terrain_holes",
            "Carves (or fills back in) a circular hole in the terrain mesh/collider, for caves or building " +
            "entrances -- a hole map cell is boolean (no falloff), set true for \"solid\" and false for \"hole\".",
            group: "terrain")]
        public static MCPResult PaintTerrainHoles(
            MCPToolContext ctx,
            [MCPParam("Hierarchy path of the GameObject with the Terrain component.")] string path,
            [MCPParam("World-space X of the brush center.")] float centerX,
            [MCPParam("World-space Z of the brush center.")] float centerZ,
            [MCPParam("Brush radius in world units.")] float radius,
            [MCPParam("True carves a hole; false fills it back in (restores solid terrain). Defaults to true.")] bool carveHole = true)
        {
            var fail = ResolveTerrain(path, out var terrain, out var data);
            if (fail != null) return fail;

            int res = data.holesResolution;
            var holes = data.GetHoles(0, 0, res, res);
            ApplyCircularBrush(res, data.size, terrain.transform.position, centerX, centerZ, radius, (gx, gz, falloff) =>
                holes[gz, gx] = !carveHole);
            data.SetHoles(0, 0, holes);

            return MCPResult.Success();
        }

        [MCPTool("create_wind_zone", "Creates a GameObject with a WindZone (Directional or Spherical), for foliage motion.", group: "terrain")]
        public static MCPResult CreateWindZone(
            MCPToolContext ctx,
            [MCPParam("Name for the new GameObject. Defaults to 'WindZone'.")] string name = "WindZone",
            [MCPParam("Directional (uniform, affects whole scene) or Spherical (falls off with distance/radius).")] WindZoneMode mode = WindZoneMode.Directional,
            [MCPParam("World-space X position (Spherical only affects placement).")] float x = 0f,
            [MCPParam("World-space Y position.")] float y = 0f,
            [MCPParam("World-space Z position.")] float z = 0f,
            [MCPParam("Sphere radius, Spherical mode only. Defaults to 50.")] float radius = 50f,
            [MCPParam("Steady wind strength. Defaults to 1.")] float windMain = 1f,
            [MCPParam("Randomized gust strength added on top of windMain. Defaults to 1.")] float windTurbulence = 1f,
            [MCPParam("How fast wind strength pulses between gusts. Defaults to 0.5.")] float windPulseFrequency = 0.5f)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Create Wind Zone");
            go.transform.position = new Vector3(x, y, z);

            var wind = go.AddComponent<WindZone>();
            wind.mode = mode;
            wind.radius = radius;
            wind.windMain = windMain;
            wind.windTurbulence = windTurbulence;
            wind.windPulseFrequency = windPulseFrequency;

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }
    }
}
