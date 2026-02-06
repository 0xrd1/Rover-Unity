using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;

// ---------------------------------------------------------
// 1. LOADER INTERFACES & CLASSES
// ---------------------------------------------------------

public interface ITileLoader
{
    // We use a callback (Action<Texture2D>) to handle both instantaneous local loads 
    // and asynchronous web downloads.
    void LoadTile(int x, int y, int zoom, Action<Texture2D> onLoaded);
}

// Loads from Assets folder (Editor Only) or Resources (Build)
public class LocalTileLoader : ITileLoader
{
    private string _baseFolder;

    public LocalTileLoader(string folder)
    {
        _baseFolder = folder;
    }

    public void LoadTile(int x, int y, int zoom, Action<Texture2D> onLoaded)
    {
#if UNITY_EDITOR
        // Matches your requested format: Assets/Folder/~x,y~.png
        string path = $"Assets/{_baseFolder}/~{x},{y}~.png";
        Texture2D tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        
        if (tex != null)
        {
            onLoaded?.Invoke(tex);
        }
        else
        {
            Debug.LogWarning($"[LocalLoader] Tile not found: {path}");
        }
#else
        Debug.LogError("AssetDatabase loading is Editor-only. Use Resources.Load for builds.");
#endif
    }
}

// Loads from a Web API (e.g., OpenStreetMap, Mapbox, or custom server)
public class WebTileLoader : ITileLoader
{
    private string _urlTemplate; // e.g. "https://tile.openstreetmap.org/{z}/{x}/{y}.png"

    public WebTileLoader(string urlTemplate)
    {
        _urlTemplate = urlTemplate;
    }

    public void LoadTile(int x, int y, int zoom, Action<Texture2D> onLoaded)
    {
        // Web requests must be run via a Coroutine. 
        // We need a MonoBehaviour to run coroutines, so we find a host.
        SatelliteMapSystem.Instance.StartCoroutine(DownloadTile(x, y, zoom, onLoaded));
    }

    private IEnumerator DownloadTile(int x, int y, int zoom, Action<Texture2D> onLoaded)
    {
        // Calculate the Global Web Mercator index if needed. 
        // NOTE: Your local -18 to 17 indices are RELATIVE. 
        // If using a real API, you must convert relative X/Y to Global X/Y here.
        // For this example, we assume the URL expects the relative index or you have a custom server.
        
        string url = _urlTemplate
            .Replace("{x}", x.ToString())
            .Replace("{y}", y.ToString())
            .Replace("{z}", zoom.ToString());

        using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(url))
        {
            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[WebLoader] Error: {uwr.error} ({url})");
            }
            else
            {
                onLoaded?.Invoke(DownloadHandlerTexture.GetContent(uwr));
            }
        }
    }
}


// Mission Classes
[Serializable]
public class Waypoint
{
    public double latitude;
    public double longitude;
    public Waypoint() { }
    public Waypoint(double lat, double lon)
    {
        latitude = lat;
        longitude = lon;
    }
}

[Serializable]
public class MissionConfig
{
    public enum SearchType { None, Spiral, Lawnmower }
    public enum SearchObject { Aruco, Hammer, Mallet, Bottle }

    [Serializable]
    public class SearchConfig
    {
        public SearchConfig() { }
        // Spiral: center gps + radius (meters) + lane spacing (meters)
        // Lawnmower: 4 gps corners for lawnmower + lane spacing (meters)
    }

    public List<Waypoint> waypoints = new List<Waypoint>();
    public SearchType searchType;
    public SearchObject searchObject;
    public SearchConfig searchConfig = new SearchConfig();

    public MissionConfig(SearchType type, SearchObject obj, List<Waypoint> points)
    {
        searchType = type;
        searchObject = obj;
        waypoints = points;
    }
}




// Mission Visualization

public class WaypointHoverEffect : MonoBehaviour
{
    private Color _baseColor;
    private Material _mat;

    void Start()
    {
        _mat = GetComponent<Renderer>().material;
        // Enable emission keyword for standard shaders
        _mat.EnableKeyword("_EMISSION");
    }

    void OnMouseEnter()
    {
        _baseColor = _mat.color;
        // Make it glow by multiplying the color or setting an emission map
        _mat.SetColor("_EmissionColor", _baseColor * 2.0f); 
        transform.localScale *= 1.2f; // Slight pop effect
    }

    void OnMouseExit()
    {
        _mat.SetColor("_EmissionColor", Color.black); // Turn off glow
        transform.localScale /= 1.2f;
    }
}

// ---------------------------------------------------------
// 2. MAIN MAP CONTROLLER
// ---------------------------------------------------------

[RequireComponent(typeof(LineRenderer))]
public class SatelliteMapSystem : MonoBehaviour
{
    public static SatelliteMapSystem Instance; // Singleton for coroutine hosting

    public enum LoadMode { Local, Web }

    [ContextMenu("Export Mission to Console")]
    public void PrintMissionToConsole()
    {
        if (currentMission == null) return;
        string json = JsonUtility.ToJson(currentMission, true);
        Debug.Log($"<color=lime><b>[MISSION EXPORT]</b></color>\n{json}");
    }

    [Header("Configuration")]
    public LoadMode mode = LoadMode.Local;
    public string localTileFolder = "Control Interface App/Maps/Merryfield";
    [Tooltip("Use {x}, {y}, {z} as placeholders")]
    public string webUrlTemplate = "https://myserver.com/tiles/{z}/{x}/{y}.png";

    [Header("Grid Settings")]
    public int minX = -18;
    public int maxX = 17;
    public int minY = -18;
    public int maxY = 17;
    
    [Header("Geospatial Settings")]
    public int zoomLevel = 19;
    public double originLat = 44.56469;
    public double originLon = -123.27415;
    
    [Header("Unity Settings")]
    public float tileSize = 256.0f; // Size of the tile in Unity Units
    public Material baseMaterial;  // Material to apply texture to
    public GameObject markerPrefab;
    [Range(0.1f, 5.0f)]
    public float markerScale = 2.0f;
    private float _previousMarkerScale = 2.0f;
    public Color waypointColor = Color.white;

    [Header("Camera Control")]
    public float panSpeed = 1.0f;
    public float zoomSensitivity = 10.0f;
    public float minCamHeight = 5.0f;
    public float maxCamHeight = 500.0f;

    // Internal State
    private ITileLoader _loader;
    private Camera _cam;
    private Vector3 _lastMousePos;
    private double _originGlobalTileX;
    private double _originGlobalTileY;

    private MissionConfig currentMission;

    private GameObject _markers;
    private LineRenderer _pathRenderer;
    private List<GameObject> _waypointMarkers = new List<GameObject>();
    private int _lastWaypointCount = 0;

    private void Awake()
    {
        Instance = this;
        _cam = Camera.main;
        _pathRenderer = GetComponent<LineRenderer>();
        _pathRenderer.material = new Material(Shader.Find("Sprites/Default"));
        _pathRenderer.startColor = Color.white;
        _pathRenderer.endColor = Color.white;
        _pathRenderer.startWidth = markerScale * 0.5f;
        _pathRenderer.endWidth = markerScale * 0.5f;
        _pathRenderer.alignment = LineAlignment.View;
        _pathRenderer.numCornerVertices = 5;         
        _pathRenderer.numCapVertices = 5;
        _pathRenderer.positionCount = 0;
        _pathRenderer.useWorldSpace = true;
        baseMaterial = new Material(Shader.Find("Unlit/Texture"));

        // 1. Initialize Loader
        if (mode == LoadMode.Local)
            _loader = new LocalTileLoader(localTileFolder);
        else
            _loader = new WebTileLoader(webUrlTemplate);

        // 2. Pre-calculate Global Web Mercator Coordinates for the Origin (Lat/Lon)
        // This makes the click-to-GPS math ultra-fast later.
        _originGlobalTileX = LongitudeToTileX(originLon, zoomLevel);
        _originGlobalTileY = LatitudeToTileY(originLat, zoomLevel);

        currentMission = new MissionConfig(
            MissionConfig.SearchType.None,
            MissionConfig.SearchObject.Aruco,
            new List<Waypoint>()
        );
    }

    private void Start()
    {
        GenerateMap();

        _markers = new GameObject("Markers");
        _markers.transform.parent = this.transform;

        // Center Camera
        if (_cam != null)
        {
            _cam.transform.position = new Vector3(0, 50, 0);
            _cam.transform.LookAt(Vector3.zero);
        }
    }

    private void Update()
    {
        HandleCameraInput();
        HandleInteraction();

        if (markerScale != _previousMarkerScale || currentMission.waypoints.Count != _lastWaypointCount)
        {
            RenderWaypoints();
            _pathRenderer.startWidth = markerScale * 0.5f;
            _pathRenderer.endWidth = markerScale * 0.5f;
        }
    }

    // --- MAP GENERATION ---

    void GenerateMap()
    {
        GameObject mapOrigin = new GameObject("MapOrigin");
        mapOrigin.transform.parent = this.transform;
        mapOrigin.transform.localPosition = Vector3.zero;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                int currentX = x;
                int currentY = y;

                _loader.LoadTile(x, y, zoomLevel, (texture) => { CreateTileObject(currentX, currentY, texture, mapOrigin.transform); });
            }
        }
    }

    void CreateTileObject(int x, int y, Texture2D tex, Transform parent)
    {
        GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
        tile.name = $"({x}, {y})";
        tile.transform.parent = parent;

        // Position on X/Z Plane (Ground)
        // Note: Map Y (North/South) corresponds to Unity Z. 
        // In Tile grids, Positive Y is usually South (down). In Unity, Positive Z is North.
        // We map Tile Y to Unity -Z.
        tile.transform.localPosition = new Vector3(x * tileSize, 0, -y * tileSize);
        
        // Rotate Quad to lay flat
        tile.transform.localRotation = Quaternion.Euler(90, 0, 0);
        tile.transform.localScale = Vector3.one * tileSize;

        // Apply Texture
        Renderer r = tile.GetComponent<Renderer>();
        r.material = baseMaterial != null ? baseMaterial : new Material(Shader.Find("Unlit/Texture"));
        r.material.mainTexture = tex;
        
        // Remove collider if you want raycasts to pass through (optional)
        // keeping it helps for clicking specific tiles, but for GPS we raycast against a math plane
        Destroy(tile.GetComponent<Collider>());
    }

    // Mission Visualization
    public GameObject SpawnMarkerAtGPS(double lat, double lon, string markerName = "GPS_Marker")
    {
        // 1. Convert GPS to Global Tile Coords
        double targetGlobalX = LongitudeToTileX(lon, zoomLevel);
        double targetGlobalY = LatitudeToTileY(lat, zoomLevel);

        // 2. Find difference from origin
        double offsetX = targetGlobalX - _originGlobalTileX;
        double offsetY = targetGlobalY - _originGlobalTileY;

        // 3. Convert to Unity Units (X is East, Z is North)
        // Note: Global Y increases South, so we invert it for Unity's Z
        float unityX = (float)(offsetX * tileSize);
        float unityZ = (float)(-offsetY * tileSize);

        GameObject marker = markerPrefab != null ? Instantiate(markerPrefab) : GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = markerName;
        marker.transform.SetParent(this.transform);
        marker.transform.localPosition = new Vector3(unityX, 1.0f, unityZ); // Lifted slightly off ground
        
        return marker;
    }

    void RenderWaypoints()
    {
        // Clean up markers if we deleted points
        while (_waypointMarkers.Count > currentMission.waypoints.Count)
        {
            int lastIndex = _waypointMarkers.Count - 1;
            Destroy(_waypointMarkers[lastIndex]);
            _waypointMarkers.RemoveAt(lastIndex);
        }

        _pathRenderer.positionCount = currentMission.waypoints.Count;

        for (int i = 0; i < currentMission.waypoints.Count; i++)
        {
            Vector3 worldPos = GetUnityPositionFromGPS(currentMission.waypoints[i].latitude, currentMission.waypoints[i].longitude);
            worldPos.y = 1.0f; 
            
            _pathRenderer.SetPosition(i, worldPos);

            if (i >= _waypointMarkers.Count)
            {
                GameObject marker = markerPrefab != null ? Instantiate(markerPrefab) : GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.transform.SetParent(_markers.transform);
                marker.AddComponent<WaypointHoverEffect>();
                _waypointMarkers.Add(marker);
            }
            
            _waypointMarkers[i].name = $"WP_{i}";
            _waypointMarkers[i].transform.position = worldPos;
            _waypointMarkers[i].transform.localScale = Vector3.one * markerScale;

            Renderer r = _waypointMarkers[i].GetComponent<Renderer>();
            if (i == 0) r.material.color = Color.green;
            else if (i == currentMission.waypoints.Count - 1) r.material.color = Color.red;
            else r.material.color = waypointColor;
        }

        _previousMarkerScale = markerScale;
        _lastWaypointCount = currentMission.waypoints.Count;
    }

    // --- INTERACTION  ---
    void HandleInteraction()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero); // Infinite ground plane at Y=0

            if (groundPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                GetGPSFromUnityPosition(hitPoint, out double lat, out double lon);
                currentMission.waypoints.Add(new Waypoint(lat, lon));
                Debug.Log($"<color=cyan>CLICK:</color> Unity({hitPoint}) => GPS({lat:F7}, {lon:F7})");
            }
        }

        if (Input.GetMouseButtonDown(1))
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;

            // Ensure your markerPrefab has a Collider!
            if (Physics.Raycast(ray, out hit))
            {
                GameObject hitObj = hit.collider.gameObject;
                int markerIndex = _waypointMarkers.IndexOf(hitObj);

                if (markerIndex != -1)
                {
                    currentMission.waypoints.RemoveAt(markerIndex);
                    Debug.Log($"Removed Waypoint {markerIndex}");
                }
            }
        }
    }

    void HandleCameraInput()
    {
        // Panning (Right Mouse Drag)
        if (Input.GetMouseButtonDown(2)) _lastMousePos = Input.mousePosition;
        
        if (Input.GetMouseButton(2))
        {
            Vector3 delta = Input.mousePosition - _lastMousePos;
            
            // Adjust pan speed based on height (pan faster when higher up)
            float heightMult = _cam.transform.position.y / 10.0f;
            Vector3 move = new Vector3(-delta.x, 0, -delta.y) * panSpeed * heightMult * Time.deltaTime;
            
            // Transform movement relative to camera rotation
            move = Quaternion.Euler(0, _cam.transform.eulerAngles.y, 0) * move;
            
            _cam.transform.Translate(move, Space.World);
            _lastMousePos = Input.mousePosition;
        }

        // Zooming (Scroll Wheel)
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0)
        {
            Vector3 zoomDir = _cam.transform.forward * scroll * zoomSensitivity;
            Vector3 newPos = _cam.transform.position + zoomDir;

            // Clamp Height
            if (newPos.y < minCamHeight) newPos = _cam.transform.position + (Vector3.down * (_cam.transform.position.y - minCamHeight));
            if (newPos.y > maxCamHeight) newPos.y = maxCamHeight; // Simple cap

            _cam.transform.position = newPos;
        }
    }

    // --- MATH CORE (Web Mercator) ---

    public void GetGPSFromUnityPosition(Vector3 pos, out double lat, out double lon)
    {
        // 1. Normalize Unity units to Tile units
        double xOffsetTiles = pos.x / tileSize;
        double yOffsetTiles = pos.z / tileSize; // Unity Z is Map Y (North/South)

        // 2. Apply to Origin Global Coordinate
        // Tile X increases East (Unity +X)
        double targetGlobalX = _originGlobalTileX + xOffsetTiles;
        
        // Tile Y increases SOUTH. Unity Z increases NORTH.
        // So we SUBTRACT the Unity Z offset.
        double targetGlobalY = _originGlobalTileY - yOffsetTiles;

        // 3. Convert back to Lat/Lon
        lon = TileXToLongitude(targetGlobalX, zoomLevel);
        lat = TileYToLatitude(targetGlobalY, zoomLevel);
    }

    public Vector3 GetUnityPositionFromGPS(double lat, double lon)
    {
        // 1. Convert Global GPS to Global Web Mercator tile coordinates
        double targetGlobalX = LongitudeToTileX(lon, zoomLevel);
        double targetGlobalY = LatitudeToTileY(lat, zoomLevel);

        // 2. Subtract the origin to get the offset in tiles
        double offsetX = targetGlobalX - _originGlobalTileX;
        double offsetY = targetGlobalY - _originGlobalTileY;

        // 3. Scale by tileSize to get Unity Units
        // Unity X+ is East (same as tile X)
        // Unity Z+ is North (opposite of tile Y which increases South)
        float x = (float)(offsetX * tileSize);
        float z = (float)(-offsetY * tileSize);

        return new Vector3(x, 0, z);
    }

    // Standard Web Mercator Formulas
    private static double LongitudeToTileX(double lon, int zoom)
    {
        return (lon + 180.0) / 360.0 * Math.Pow(2.0, zoom);
    }

    private static double LatitudeToTileY(double lat, int zoom)
    {
        double latRad = lat * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(latRad) + (1.0 / Math.Cos(latRad))) / Math.PI) / 2.0 * Math.Pow(2.0, zoom);
    }

    private static double TileXToLongitude(double tileX, int zoom)
    {
        return tileX / Math.Pow(2.0, zoom) * 360.0 - 180.0;
    }

    private static double TileYToLatitude(double tileY, int zoom)
    {
        double tileYNorm = Math.PI - 2.0 * Math.PI * tileY / Math.Pow(2.0, zoom);
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(tileYNorm) - Math.Exp(-tileYNorm)));
    }
}
