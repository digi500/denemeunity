using UnityEngine;
using UnityEditor;
using System.Net;
using System.Threading;
using System.IO;
using System;
using System.Collections.Generic;

[InitializeOnLoad]
public static class AgentIntegrationServer
{
    private static HttpListener listener;
    private static Thread listenerThread;
    private static readonly Queue<Action> mainThreadQueue = new Queue<Action>();
    private static readonly object queueLock = new object();
    
    private static readonly List<LogEntry> consoleLogs = new List<LogEntry>();
    private const int Port = 52424;

    [System.Serializable]
    public struct LogEntry
    {
        public string condition;
        public string stackTrace;
        public string type;
        public string time;
    }

    static AgentIntegrationServer()
    {
        // Subscribe to update to run actions on main thread
        EditorApplication.update += Update;
        // Subscribe to log messages
        Application.logMessageReceived += HandleLog;
        
        // Clean up before assembly reload to avoid port conflict issues
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        
        StartServer();
    }
    
    private static void OnBeforeAssemblyReload()
    {
        StopServer();
    }

    private static void HandleLog(string condition, string stackTrace, LogType type)
    {
        lock (consoleLogs)
        {
            consoleLogs.Add(new LogEntry {
                condition = condition,
                stackTrace = stackTrace,
                type = type.ToString(),
                time = DateTime.Now.ToString("HH:mm:ss")
            });
            // Keep last 100 logs
            if (consoleLogs.Count > 100)
                consoleLogs.RemoveAt(0);
        }
    }

    private static void StartServer()
    {
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            
            listenerThread = new Thread(Listen);
            listenerThread.Start();
            Debug.Log($"Agent Integration Server started on http://127.0.0.1:{Port}/");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to start Agent Integration Server on port {Port}: " + e.Message);
        }
    }

    private static void StopServer()
    {
        try
        {
            if (listener != null)
            {
                listener.Stop();
                listener.Close();
                listener = null;
            }
            if (listenerThread != null)
            {
                if (!listenerThread.Join(500))
                {
                    listenerThread.Abort();
                }
                listenerThread = null;
            }
            Debug.Log("Agent Integration Server stopped.");
        }
        catch (Exception e)
        {
            Debug.LogError("Error stopping Agent Integration Server: " + e.Message);
        }
    }

    private static void Listen()
    {
        while (listener != null && listener.IsListening)
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                ProcessRequest(context);
            }
            catch (Exception)
            {
                // Listener might have been stopped
                break;
            }
        }
    }
    
    private static void ProcessRequest(HttpListenerContext context)
    {
        string path = context.Request.Url.AbsolutePath.ToLower();
        
        if (path == "/play")
        {
            QueueOnMainThread(() => {
                EditorApplication.isPlaying = true;
                SendJsonResponse(context, "{\"status\":\"success\", \"isPlaying\": true}");
            });
        }
        else if (path == "/stop")
        {
            QueueOnMainThread(() => {
                EditorApplication.isPlaying = false;
                SendJsonResponse(context, "{\"status\":\"success\", \"isPlaying\": false}");
            });
        }
        else if (path == "/status")
        {
            QueueOnMainThread(() => {
                string statusJson = string.Format(
                    "{{\n  \"isPlaying\": {0},\n  \"isPaused\": {1},\n  \"activeScene\": \"{2}\"\n}}",
                    EditorApplication.isPlaying.ToString().ToLower(),
                    EditorApplication.isPaused.ToString().ToLower(),
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                );
                SendJsonResponse(context, statusJson);
            });
        }
        else if (path == "/hierarchy")
        {
            QueueOnMainThread(() => {
                var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                List<string> objNames = new List<string>();
                foreach (var obj in rootObjects)
                {
                    objNames.Add(GetGameObjectInfo(obj));
                }
                string hierarchyJson = "[" + string.Join(",", objNames) + "]";
                SendJsonResponse(context, hierarchyJson);
            });
        }
        else if (path == "/logs")
        {
            lock (consoleLogs)
            {
                string json = JsonUtility.ToJson(new LogListWrapper { logs = consoleLogs });
                SendJsonResponse(context, json);
            }
        }
        else if (path == "/screenshot")
        {
            QueueOnMainThread(() => {
                try
                {
                    Debug.Log("Screenshot: Started in-memory capture process.");
                    Camera cam = Camera.main;
                    if (cam == null) cam = UnityEngine.Object.FindObjectOfType<Camera>();
                    
                    if (cam != null)
                    {
                        int width = 1024;
                        int height = 768;
                        RenderTexture rt = new RenderTexture(width, height, 24);
                        RenderTexture previous = cam.targetTexture;
                        cam.targetTexture = rt;
                        
                        Texture2D screenShot = new Texture2D(width, height, TextureFormat.RGB24, false);
                        cam.Render();
                        
                        RenderTexture.active = rt;
                        screenShot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        
                        cam.targetTexture = previous;
                        RenderTexture.active = null;
                        
                        byte[] bytes = screenShot.EncodeToPNG();
                        
                        UnityEngine.Object.DestroyImmediate(rt);
                        UnityEngine.Object.DestroyImmediate(screenShot);
                        
                        context.Response.ContentType = "image/png";
                        context.Response.ContentLength64 = bytes.Length;
                        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        context.Response.OutputStream.Close();
                        Debug.Log("Screenshot: Successfully sent in-memory PNG.");
                    }
                    else
                    {
                        Debug.LogWarning("Screenshot: No camera found to render.");
                        SendTextResponse(context, "No camera found to render", 500);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("Error in screenshot capture: " + e.Message + "\n" + e.StackTrace);
                    SendTextResponse(context, "Error capturing screenshot: " + e.Message, 500);
                }
            });
        }
        else if (path == "/checkskybox")
        {
            QueueOnMainThread(() => {
                try
                {
                    Texture2D skyboxTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/skybox.png");
                    Material skyMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/SunsetSkybox.mat");
                    
                    Camera cam = Camera.main;
                    if (cam == null) cam = UnityEngine.Object.FindObjectOfType<Camera>();
                    CameraFollow follow = cam != null ? cam.GetComponent<CameraFollow>() : null;
                    bool followTargetNull = follow != null ? (follow.target == null) : true;
                    string targetName = (follow != null && follow.target != null) ? follow.target.name : "null";
                    
                    GameObject vehicle = GameObject.Find("VehiclePlaceholder");
                    bool vehicleFound = vehicle != null;
                    
                    List<string> texProps = new List<string>();
                    if (skyMat != null)
                    {
                        var names = skyMat.GetTexturePropertyNames();
                        foreach (var n in names) texProps.Add("\"" + n + "\"");
                    }
                    
                    string response = string.Format(
                        "{{\n  \"skyboxTexNull\": {0},\n  \"skyMatNull\": {1},\n  \"skyMatShader\": \"{2}\",\n  \"textureProperties\": [{3}],\n  \"followComponentNull\": {4},\n  \"followTargetNull\": {5},\n  \"followTargetName\": \"{6}\",\n  \"vehicleFound\": {7}\n}}",
                        (skyboxTex == null).ToString().ToLower(),
                        (skyMat == null).ToString().ToLower(),
                        (skyMat != null && skyMat.shader != null) ? skyMat.shader.name : "null",
                        string.Join(",", texProps),
                        (follow == null).ToString().ToLower(),
                        followTargetNull.ToString().ToLower(),
                        targetName,
                        vehicleFound.ToString().ToLower()
                    );
                    SendJsonResponse(context, response);
                }
                catch (Exception e)
                {
                    SendTextResponse(context, "Error checking skybox: " + e.Message, 500);
                }
            });
        }
        else if (path == "/buildscene")
        {
            QueueOnMainThread(() => {
                try
                {
                    BuildGameplayScene();
                    SendJsonResponse(context, "{\"status\":\"success\", \"message\":\"Gameplay scene built successfully\"}");
                }
                catch (Exception e)
                {
                    SendTextResponse(context, "Error building scene: " + e.Message + "\n" + e.StackTrace, 500);
                }
            });
        }
        else if (path == "/setupgameplay")
        {
            QueueOnMainThread(() => {
                try
                {
                    SetupGameplayScene();
                    SendJsonResponse(context, "{\"status\":\"success\", \"message\":\"Gameplay scene components and UI setup successfully\"}");
                }
                catch (Exception e)
                {
                    SendTextResponse(context, "Error setting up scene: " + e.Message + "\n" + e.StackTrace, 500);
                }
            });
        }
        else if (path == "/buildwebgl")
        {
            QueueOnMainThread(() => {
                try
                {
                    BuildWebGL();
                    SendJsonResponse(context, "{\"status\":\"success\", \"message\":\"WebGL build completed successfully\"}");
                }
                catch (Exception e)
                {
                    SendTextResponse(context, "Error building WebGL: " + e.Message + "\n" + e.StackTrace, 500);
                }
            });
        }
        else
        {
            SendTextResponse(context, "Not Found", 404);
        }
    }
    
    [System.Serializable]
    private class LogListWrapper
    {
        public List<LogEntry> logs;
    }
    
    private static string GetGameObjectInfo(GameObject obj)
    {
        List<string> childInfos = new List<string>();
        for (int i = 0; i < obj.transform.childCount; i++)
        {
            childInfos.Add(GetGameObjectInfo(obj.transform.GetChild(i).gameObject));
        }
        
        List<string> componentNames = new List<string>();
        foreach (var comp in obj.GetComponents<Component>())
        {
            if (comp != null)
                componentNames.Add("\"" + comp.GetType().Name + "\"");
        }
        
        return string.Format(
            "{{\n  \"name\": \"{0}\",\n  \"active\": {1},\n  \"components\": [{2}],\n  \"children\": [{3}]\n}}",
            obj.name.Replace("\"", "\\\""),
            obj.activeSelf.ToString().ToLower(),
            string.Join(",", componentNames),
            string.Join(",", childInfos)
        );
    }

    private static void QueueOnMainThread(Action action)
    {
        lock (queueLock)
        {
            mainThreadQueue.Enqueue(action);
        }
        Debug.Log("QueueOnMainThread: Action queued. Count: " + mainThreadQueue.Count);
    }

    private static void Update()
    {
        while (true)
        {
            Action action = null;
            lock (queueLock)
            {
                if (mainThreadQueue.Count > 0)
                {
                    action = mainThreadQueue.Dequeue();
                }
            }
            
            if (action != null)
            {
                Debug.Log("Update: Dequeued action. Executing...");
                try
                {
                    action();
                    Debug.Log("Update: Action executed successfully.");
                }
                catch (Exception e)
                {
                    Debug.LogError("Error executing action on main thread: " + e.Message + "\n" + e.StackTrace);
                }
            }
            else
            {
                break;
            }
        }
    }
    
    private static void SendJsonResponse(HttpListenerContext context, string json)
    {
        try
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception) {}
    }
    
    private static void SendTextResponse(HttpListenerContext context, string text, int statusCode = 200)
    {
        try
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception) {}
    }

    private static void BuildGameplayScene()
    {
        // 1. Create folders
        if (!AssetDatabase.IsValidFolder("Assets/Scripts"))
            AssetDatabase.CreateFolder("Assets", "Scripts");
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder("Assets/Textures"))
            AssetDatabase.CreateFolder("Assets", "Textures");
            
        // 2. Create new scene
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects, 
            UnityEditor.SceneManagement.NewSceneMode.Single
        );
        
        // 3. Load textures and create materials
        Material roadMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (roadMat.shader == null) roadMat.shader = Shader.Find("Standard");
        roadMat.color = new Color(0.15f, 0.15f, 0.15f); // fallback color
        Texture2D asphaltTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/asphalt.png");
        if (asphaltTex != null)
        {
            roadMat.mainTexture = asphaltTex;
            roadMat.mainTextureScale = new Vector2(1f, 5f);
        }
        AssetDatabase.CreateAsset(roadMat, "Assets/Materials/Road.mat");
        
        Material carMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (carMat.shader == null) carMat.shader = Shader.Find("Standard");
        carMat.color = Color.white; // use white color so colormap texture isn't tinted
        Texture2D carTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/colormap.png");
        if (carTex != null)
        {
            carMat.mainTexture = carTex;
        }
        AssetDatabase.CreateAsset(carMat, "Assets/Materials/Vehicle.mat");
        
        // 4. Create materials for barriers (curbs)
        Material curbRed = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (curbRed.shader == null) curbRed.shader = Shader.Find("Standard");
        curbRed.color = new Color(0.8f, 0.1f, 0.1f);
        AssetDatabase.CreateAsset(curbRed, "Assets/Materials/CurbRed.mat");
        
        Material curbWhite = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (curbWhite.shader == null) curbWhite.shader = Shader.Find("Standard");
        curbWhite.color = new Color(0.9f, 0.9f, 0.9f);
        AssetDatabase.CreateAsset(curbWhite, "Assets/Materials/CurbWhite.mat");
        
        // 5. Create Racetrack and Walls
        GameObject trackParent = new GameObject("Racetrack");
        int segments = 40;
        float radiusX = 25f;
        float radiusZ = 18f;
        float width = 8f;
        
        for (int i = 0; i < segments; i++)
        {
            float angle = i * 2f * Mathf.PI / segments;
            float nextAngle = (i + 1) * 2f * Mathf.PI / segments;
            
            Vector3 pos1 = new Vector3(Mathf.Cos(angle) * radiusX, 0f, Mathf.Sin(angle) * radiusZ);
            Vector3 pos2 = new Vector3(Mathf.Cos(nextAngle) * radiusX, 0f, Mathf.Sin(nextAngle) * radiusZ);
            
            Vector3 center = (pos1 + pos2) / 2f;
            Vector3 dir = pos2 - pos1;
            float length = dir.magnitude;
            
            // Road segment (made 2m thick downwards to prevent WebGL physics tunneling)
            GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
            segment.name = "TrackSegment_" + i;
            segment.transform.parent = trackParent.transform;
            segment.transform.position = new Vector3(center.x, center.y - 1f, center.z);
            segment.transform.localScale = new Vector3(width, 2f, length + 0.2f);
            segment.transform.rotation = Quaternion.LookRotation(dir);
            segment.GetComponent<Renderer>().sharedMaterial = roadMat;
            
            // Outer/Inner Offset for barriers
            Vector3 sideOffset = Vector3.Cross(dir.normalized, Vector3.up).normalized * (width / 2f);
            Material curbMat = (i % 2 == 0) ? curbRed : curbWhite;
            
            // Outer wall
            GameObject outerWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            outerWall.name = "OuterWall_" + i;
            outerWall.transform.parent = trackParent.transform;
            outerWall.transform.position = center + sideOffset + Vector3.up * 0.4f; // sticks up by 0.4m
            outerWall.transform.localScale = new Vector3(0.3f, 0.8f, length + 0.2f);
            outerWall.transform.rotation = Quaternion.LookRotation(dir);
            outerWall.GetComponent<Renderer>().sharedMaterial = curbMat;
            
            // Inner wall
            GameObject innerWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            innerWall.name = "InnerWall_" + i;
            innerWall.transform.parent = trackParent.transform;
            innerWall.transform.position = center - sideOffset + Vector3.up * 0.4f;
            innerWall.transform.localScale = new Vector3(0.3f, 0.8f, length + 0.2f);
            innerWall.transform.rotation = Quaternion.LookRotation(dir);
            innerWall.GetComponent<Renderer>().sharedMaterial = curbMat;
        }
        
        // 6. Create Vehicle (positioned on road segment 0)
        GameObject vehicle = new GameObject("VehiclePlaceholder");
        vehicle.transform.position = new Vector3(25f, 1.5f, 0f); // spawned higher to prevent clipping/tunneling on WebGL startup
        
        // Add Box Collider matching the sedan dimensions
        BoxCollider boxCollider = vehicle.AddComponent<BoxCollider>();
        boxCollider.center = new Vector3(0f, 0.35f, 0f);
        boxCollider.size = new Vector3(1.4f, 0.8f, 2.4f);
        
        // Load Car Model Prefab
        GameObject carPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/sedan-sports.fbx");
        if (carPrefab != null)
        {
            GameObject carModel = GameObject.Instantiate(carPrefab, vehicle.transform);
            carModel.name = "Model";
            carModel.transform.localPosition = Vector3.zero;
            carModel.transform.localRotation = Quaternion.identity;
            
            // Assign material to all renderers in the car body
            foreach (var renderer in carModel.GetComponentsInChildren<Renderer>())
            {
                renderer.sharedMaterial = carMat;
            }
        }
        else
        {
            Debug.LogError("Failed to load car prefab Assets/Models/sedan-sports.fbx");
        }

        // Load Wheel Model Prefab
        GameObject wheelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/wheel-racing.fbx");
        if (wheelPrefab != null)
        {
            // Position offsets for the 4 wheels
            Vector3[] wheelOffsets = new Vector3[]
            {
                new Vector3(-0.55f, 0.15f, 0.65f),  // Front Left
                new Vector3(0.55f, 0.15f, 0.65f),   // Front Right
                new Vector3(-0.55f, 0.15f, -0.65f), // Rear Left
                new Vector3(0.55f, 0.15f, -0.65f)   // Rear Right
            };
            
            string[] wheelNames = new string[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" };
            
            for (int w = 0; w < 4; w++)
            {
                GameObject wheel = GameObject.Instantiate(wheelPrefab, vehicle.transform);
                wheel.name = wheelNames[w];
                wheel.transform.localPosition = wheelOffsets[w];
                // Mirror the right side wheels
                if (w % 2 == 1)
                {
                    wheel.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                }
                else
                {
                    wheel.transform.localRotation = Quaternion.identity;
                }
                
                // Assign material to wheel renderers
                foreach (var renderer in wheel.GetComponentsInChildren<Renderer>())
                {
                    renderer.sharedMaterial = carMat;
                }
            }
        }
        else
        {
            Debug.LogError("Failed to load wheel prefab Assets/Models/wheel-racing.fbx");
        }
        
        // 7. Adjust Camera Position and Rotation for a nice overview
        Camera mainCam = Camera.main;
        if (mainCam == null)
        {
            mainCam = UnityEngine.Object.FindObjectOfType<Camera>();
        }
        if (mainCam != null)
        {
            mainCam.transform.position = new Vector3(0f, 30f, -40f);
            mainCam.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
            Debug.Log("Camera positioned at: " + mainCam.transform.position);
        }
        
        // 8. Adjust Directional Light
        Light dirLight = UnityEngine.Object.FindObjectOfType<Light>();
        if (dirLight != null && dirLight.type == LightType.Directional)
        {
            dirLight.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
        
        // 9. Load Skybox texture and create Skybox material
        Texture2D skyboxTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/skybox.png");
        if (skyboxTex != null)
        {
            Material skyMat = new Material(Shader.Find("Skybox/Panoramic"));
            if (skyMat.shader == null) skyMat.shader = Shader.Find("Standard");
            skyMat.SetTexture("_MainTex", skyboxTex);
            AssetDatabase.CreateAsset(skyMat, "Assets/Materials/SunsetSkybox.mat");
            RenderSettings.skybox = skyMat;
            DynamicGI.UpdateEnvironment();
        }
        
        // 10. Save scene
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/Gameplay.unity");
        Debug.Log("Gameplay scene built, barriers created, textures/skybox mapped, and saved successfully.");
    }

    private static void SetupGameplayScene()
    {
        // 0. Rebuild scene geometry
        BuildGameplayScene();

        // 1. Open the Gameplay scene
        string scenePath = "Assets/Scenes/Gameplay.unity";
        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
        
        // 2. Find the vehicle and camera
        GameObject vehicle = GameObject.Find("VehiclePlaceholder");
        if (vehicle == null)
            throw new Exception("VehiclePlaceholder GameObject not found in the scene! Build the scene first.");
            
        Camera mainCamObj = Camera.main;
        if (mainCamObj == null)
            mainCamObj = UnityEngine.Object.FindObjectOfType<Camera>();
        if (mainCamObj == null)
            throw new Exception("Main Camera not found in the scene!");
            
        // Configure Camera clear flags and culling mask to render skybox and UI
        mainCamObj.clearFlags = CameraClearFlags.Skybox;
        mainCamObj.cullingMask = -1; // Render everything
            
        // 3. Attach Rigidbody and MobileDriftController to vehicle
        Rigidbody rb = vehicle.GetComponent<Rigidbody>();
        if (rb == null) rb = vehicle.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0.5f;
        rb.angularDamping = 1.0f;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        
        MobileDriftController drift = vehicle.GetComponent<MobileDriftController>();
        if (drift == null) drift = vehicle.AddComponent<MobileDriftController>();
        drift.acceleration = 25f;
        drift.maxSpeed = 20f;
        drift.turnSpeed = 160f;
        drift.driftFactor = 0.95f;
        
        // 4. Attach CameraFollow to Main Camera
        CameraFollow follow = mainCamObj.GetComponent<CameraFollow>();
        if (follow == null) follow = mainCamObj.gameObject.AddComponent<CameraFollow>();
        follow.target = vehicle.transform;
        follow.offset = new Vector3(0f, 6f, -10f);
        follow.followSpeed = 8f;
        follow.lookAtOffset = 1.5f;
        follow.rotationSpeed = 5f;
        
        // 5. Create UI Canvas with TextMeshPro
        GameObject canvasObj = GameObject.Find("DriftCanvas");
        if (canvasObj != null)
        {
            UnityEngine.Object.DestroyImmediate(canvasObj);
        }
        
        canvasObj = new GameObject("DriftCanvas");
        canvasObj.layer = LayerMask.NameToLayer("UI");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = mainCamObj;
        canvas.planeDistance = 5f;
        canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
        canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        
        // Create TextMeshProUGUI Score Text using reflection to avoid compile-time dependencies
        GameObject textObj = new GameObject("ScoreText");
        textObj.layer = LayerMask.NameToLayer("UI");
        textObj.transform.parent = canvasObj.transform;
        
        System.Type tmproType = System.Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
        if (tmproType == null)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                tmproType = assembly.GetType("TMPro.TextMeshProUGUI");
                if (tmproType != null) break;
            }
        }
        
        Component scoreText = null;
        if (tmproType != null)
        {
            scoreText = textObj.AddComponent(tmproType);
            
            var textProp = tmproType.GetProperty("text");
            if (textProp != null) textProp.SetValue(scoreText, "SCORE: 0");
            
            var fontSizeProp = tmproType.GetProperty("fontSize");
            if (fontSizeProp != null) fontSizeProp.SetValue(scoreText, 32f);
            
            var colorProp = tmproType.GetProperty("color");
            if (colorProp != null) colorProp.SetValue(scoreText, Color.white);
            
            var alignmentProp = tmproType.GetProperty("alignment");
            if (alignmentProp != null)
            {
                System.Type alignType = System.Type.GetType("TMPro.TextAlignmentOptions, Unity.TextMeshPro");
                if (alignType == null)
                {
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        alignType = assembly.GetType("TMPro.TextAlignmentOptions");
                        if (alignType != null) break;
                    }
                }
                if (alignType != null)
                {
                    object topLeftVal = System.Enum.Parse(alignType, "TopLeft");
                    alignmentProp.SetValue(scoreText, topLeftVal);
                }
            }
        }
        else
        {
            Debug.LogWarning("TextMeshProUGUI class could not be found via reflection.");
        }
        
        // Margins/Anchors for Top-Left positioning
        RectTransform rect = textObj.GetComponent<RectTransform>();
        if (rect == null) rect = textObj.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(30f, -30f);
        rect.sizeDelta = new Vector2(500f, 150f);
        
        // Create VersionText at bottom-right
        GameObject versionTextObj = new GameObject("VersionText");
        versionTextObj.layer = LayerMask.NameToLayer("UI");
        versionTextObj.transform.parent = canvasObj.transform;
        
        if (tmproType != null)
        {
            Component versionTextComp = versionTextObj.AddComponent(tmproType);
            
            var textProp = tmproType.GetProperty("text");
            if (textProp != null) textProp.SetValue(versionTextComp, "v1.0.0");
            
            var fontSizeProp = tmproType.GetProperty("fontSize");
            if (fontSizeProp != null) fontSizeProp.SetValue(versionTextComp, 18f);
            
            var colorProp = tmproType.GetProperty("color");
            if (colorProp != null) colorProp.SetValue(versionTextComp, new Color(1f, 1f, 1f, 0.7f)); // semi-transparent white
            
            var alignmentProp = tmproType.GetProperty("alignment");
            if (alignmentProp != null)
            {
                System.Type alignType = System.Type.GetType("TMPro.TextAlignmentOptions, Unity.TextMeshPro");
                if (alignType == null)
                {
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        alignType = assembly.GetType("TMPro.TextAlignmentOptions");
                        if (alignType != null) break;
                    }
                }
                if (alignType != null)
                {
                    object bottomRightVal = System.Enum.Parse(alignType, "BottomRight");
                    alignmentProp.SetValue(versionTextComp, bottomRightVal);
                }
            }
        }
        
        RectTransform versionRect = versionTextObj.GetComponent<RectTransform>();
        if (versionRect == null) versionRect = versionTextObj.AddComponent<RectTransform>();
        versionRect.anchorMin = new Vector2(1f, 0f);
        versionRect.anchorMax = new Vector2(1f, 0f);
        versionRect.pivot = new Vector2(1f, 0f);
        versionRect.anchoredPosition = new Vector2(-20f, 20f);
        versionRect.sizeDelta = new Vector2(200f, 50f);
        
        versionTextObj.AddComponent<VersionDisplay>();
        
        // 6. Attach DriftScoreSystem to Canvas and hook up references
        DriftScoreSystem scoreSystem = canvasObj.AddComponent<DriftScoreSystem>();
        scoreSystem.vehicleRb = rb;
        
        var scoreTextRefField = typeof(DriftScoreSystem).GetField("scoreText");
        if (scoreTextRefField != null && scoreText != null)
        {
            scoreTextRefField.SetValue(scoreSystem, scoreText);
        }
        scoreSystem.minDriftAngle = 15f;
        scoreSystem.minSpeed = 5f;
        scoreSystem.scoreMultiplier = 10f;
        
        // 7. Configure Skybox material texture references
        Texture2D skyboxTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/skybox.png");
        Material skyMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/SunsetSkybox.mat");
        if (skyMat != null && skyboxTex != null)
        {
            skyMat.SetTexture("_MainTex", skyboxTex);
            EditorUtility.SetDirty(skyMat);
            AssetDatabase.SaveAssets();
            RenderSettings.skybox = skyMat;
            DynamicGI.UpdateEnvironment();
        }
        else
        {
            Debug.LogWarning("SunsetSkybox.mat or skybox.png could not be loaded to assign texture reference.");
        }
        
        // 8. Save scene
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("Gameplay scene components and Canvas UI configured and saved successfully.");
    }

    private static void BuildWebGL()
    {
        // 1. Re-generate scene to ensure everything is latest and saved
        SetupGameplayScene();

        // 2. Increment Version Number in Resources/version.txt
        string resourcesDir = "Assets/Resources";
        if (!Directory.Exists(resourcesDir))
        {
            Directory.CreateDirectory(resourcesDir);
        }
        
        string versionFilePath = Path.Combine(resourcesDir, "version.txt");
        int buildNumber = 0;
        if (File.Exists(versionFilePath))
        {
            string currentVersion = File.ReadAllText(versionFilePath).Trim();
            string[] parts = currentVersion.Split('.');
            if (parts.Length > 0)
            {
                int.TryParse(parts[parts.Length - 1], out buildNumber);
            }
        }
        buildNumber++;
        string newVersion = "1.0." + buildNumber;
        File.WriteAllText(versionFilePath, newVersion);
        Debug.Log("Incremented Version to: " + newVersion);
        AssetDatabase.ImportAsset(versionFilePath);

        // 3. Build WebGL Player
        string buildPath = "WebGLBuild";
        if (Directory.Exists(buildPath))
        {
            try
            {
                Directory.Delete(buildPath, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to delete existing WebGLBuild folder: " + e.Message);
            }
        }
        Directory.CreateDirectory(buildPath);

        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = new[] { "Assets/Scenes/Gameplay.unity" };
        buildPlayerOptions.locationPathName = buildPath;
        buildPlayerOptions.target = BuildTarget.WebGL;
        buildPlayerOptions.options = BuildOptions.None;

        Debug.Log("Starting WebGL Build...");
        var buildReport = BuildPipeline.BuildPlayer(buildPlayerOptions);
        if (buildReport.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log("WebGL Build Succeeded!");
            
            // 4. Create vercel.json in the WebGLBuild directory with headers for compression mapping
            string vercelJsonPath = Path.Combine(buildPath, "vercel.json");
            string vercelJsonContent = @"{
  ""headers"": [
    {
      ""source"": ""Build/(.*)\\.wasm\\.br"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""br""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/wasm""
        }
      ]
    },
    {
      ""source"": ""Build/(.*)\\.js\\.br"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""br""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/javascript""
        }
      ]
    },
    {
      ""source"": ""Build/(.*)\\.data\\.br"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""br""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/octet-stream""
        }
      ]
    },
    {
      ""source"": ""Build/(.*)\\.wasm\\.gz"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""gzip""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/wasm""
        }
      ]
    },
    {
      ""source"": ""Build/(.*)\\.js\\.gz"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""gzip""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/javascript""
        }
      ]
    },
    {
      ""source"": ""Build/(.*)\\.data\\.gz"",
      ""headers"": [
        {
          ""key"": ""Content-Encoding"",
          ""value"": ""gzip""
        },
        {
          ""key"": ""Content-Type"",
          ""value"": ""application/octet-stream""
        }
      ]
    }
  ]
}";
            File.WriteAllText(vercelJsonPath, vercelJsonContent);
            Debug.Log("Created vercel.json decompression headers successfully.");
        }
        else
        {
            throw new Exception("Unity WebGL build failed. Check editor log/console for compilation details.");
        }
    }
}
