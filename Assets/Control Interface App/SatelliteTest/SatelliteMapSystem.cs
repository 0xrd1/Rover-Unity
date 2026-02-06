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
public class Waypoint
{
    public double latitude;
    public double longitude;
    public Waypoint(double lat, double lon)
    {
        latitude = lat;
        longitude = lon;
    }
}

public class MissionConfig
{
    public enum SearchType { None, Spiral, Lawnmower, }
    public enum SearchObject { Aruco, Hammer, Mallet, Bottle }
    public class SearchConfig
    {
        // Spiral: center gps + radius (meters) + lane spacing (meters)
        // Lawnmower: 4 gps corners for lawnmower + lane spacing (meters)
    }

    public List<Waypoint> waypoints;
    public SearchType searchType;
    public SearchObject searchObject;
    public SearchConfig searchConfig;

    public MissionConfig(SearchType type, SearchObject obj, SearchConfig config, List<Waypoint> points)
    {
        searchType = type;
        searchObject = obj;
        searchConfig = config;
        waypoints = points;
    }
}


// ---------------------------------------------------------
// 2. MAIN MAP CONTROLLER
// ---------------------------------------------------------

public class SatelliteMapSystem : MonoBehaviour
{
    public static SatelliteMapSystem Instance; // Singleton for coroutine hosting

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

    [Header("Camera Control")]
    public float panSpeed = 1.0f;
    public float zoomSensitivity = 10.0f;
    public float minCamHeight = 5.0f;
    public float maxCamHeight = 200.0f;

    // Internal State
    private ITileLoader _loader;
    private Camera _cam;
    private Vector3 _lastMousePos;
    private double _originGlobalTileX;
    private double _originGlobalTileY;

    public enum LoadMode { Local, Web }

    private void Awake()
    {
        Instance = this;
        _cam = Camera.main;

        // 1. Initialize Loader
        if (mode == LoadMode.Local)
            _loader = new LocalTileLoader(localTileFolder);
        else
            _loader = new WebTileLoader(webUrlTemplate);

        // 2. Pre-calculate Global Web Mercator Coordinates for the Origin (Lat/Lon)
        // This makes the click-to-GPS math ultra-fast later.
        _originGlobalTileX = LongitudeToTileX(originLon, zoomLevel);
        _originGlobalTileY = LatitudeToTileY(originLat, zoomLevel);
    }

    private void Start()
    {
        GenerateMap();
        
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
        HandleClickForGPS();
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
        r.material = baseMaterial != null ? new Material(baseMaterial) : new Material(Shader.Find("Unlit/Texture"));
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


    // --- INTERACTION & GPS ---
    void HandleClickForGPS()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero); // Infinite ground plane at Y=0

            if (groundPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                GetGPSFromUnityPosition(hitPoint, out double lat, out double lon);
                Debug.Log($"<color=cyan>CLICK:</color> Unity({hitPoint}) => GPS({lat:F7}, {lon:F7})");

                SpawnMarkerAtGPS(lat, lon, $"Marker_{lat:F4}_{lon:F4}");
            }
        }
    }

    void HandleCameraInput()
    {
        // Panning (Right Mouse Drag)
        if (Input.GetMouseButtonDown(1)) _lastMousePos = Input.mousePosition;
        
        if (Input.GetMouseButton(1))
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
