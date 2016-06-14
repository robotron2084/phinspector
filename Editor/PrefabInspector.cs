using UnityEngine;
using UnityEditor;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;


public class PrefabInspector : EditorWindow
{

  // DATA CLASSES

  /**
   * PrefabInfo describes the view information for a prefab in addition to 
   * information to allow it to re-persist from disk.
   * This mainly consists of the guid of the prefab, the currently selected
   * view for the prefab, and the a list of view model information.
   * We never 'crawl' hierarchies, they could be huge. We just display the views opened
   * by the user.
   */
  [System.Serializable]
  public class PrefabInfo
  {
    //The unique guid of the Prefab which can be serialized and used to retrieve
    // the game object if necessary.
    public string guid;

    // Used when showing the info to the user.
    public string name;

    // The id of the current view.
    public long currentID;

    // A priority value for recently used.
    public int priority;

    //Serialized version of the view information.
    public List<ObjectView> views;

    // A hash of view data against game objects that have been seen by the 
    // inspector.
    [System.NonSerialized]
    public Dictionary<GameObject, ObjectView> viewHash;

    // When we deserialize we'll have this cached view information but instead of 
    // crawling the hierarchy to map game objects (which might have changed, 
    // who knows what they are?) we create a localID hash and then when we 
    // come across a GO we'll create/hash a view then.
    [System.NonSerialized]
    public Dictionary<long, ObjectView> viewIDHash;

    public PrefabInfo(GameObject go)
    {
      name = go.name;
      guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(go));
      views = new List<ObjectView>();
      viewHash = new Dictionary<GameObject, ObjectView>();
      viewIDHash = new Dictionary<long, ObjectView>();
      ObjectView ov = GetView(go, true); //default to open for the first object.
      currentID = ov.localID;
    }

    public void Deselect()
    {
      foreach(ObjectView view in views)
      {
        view.obj = null; // keep your nose clean, kid.
      }
    }

    /**
     * Returns a view for the given game object. If no view exists one is created.
     * and added to the info to be persisted to disk. isOpen is only used when
     * a view is being created, otherwise the given view is returned.
     */
    public ObjectView GetView(GameObject go, bool isOpen)
    {
      ObjectView ov = null;
      if(!viewHash.TryGetValue(go, out ov))
      {
        //We haven't encountered this view. Perhaps we have one persisted to disk?
        long localID = PrefabInspector.GetLocalID(go);
        if(viewIDHash.TryGetValue(localID, out ov))
        {
          //its in the view id hash but not in the view hash!
          // Add it.
          viewHash[go] = ov;
        }else{
          //we haven't encountered this object make a new view model and add 
          // to our collections.
          ov = new ObjectView(go, isOpen);
          viewHash[go] = ov;
          viewIDHash[localID] = ov;
          views.Add(ov);
        }
      }
      ov.obj = go;
      return ov;
    }

    // Gets a view..but expects it to exist! (it can't make a view with just an id...)
    public ObjectView GetView(long localID)
    {
      return viewIDHash[localID];
    }

    public bool TryGetValue(GameObject go, out ObjectView ov)
    {
      return viewHash.TryGetValue(go, out ov);
    }

    public void OnDeserialization()
    {
      // Debug.Log("[PrefabInfo] OnDeserialization!");
      if(viewHash == null)
      {
        viewHash = new Dictionary<GameObject, ObjectView>();
      }
      if(viewIDHash == null)
      {
        viewIDHash = new Dictionary<long, ObjectView>();
      }
      if(name == null)
      {
        name = guid;
      }
      foreach(ObjectView view in views)
      {
        viewIDHash[view.localID] = view;
      }
    }
  }

  /**
   * A view model for an object. Is it 'open' so we can see its children? etc.
   */
  [System.Serializable]
  public class ObjectView
  {
    // The localIdentfierInFile id which is the internally unique value for the
    // object. Using getInstanceID() will not persist across re-starts.
    public long localID;

    // Whether or not the view is 'open' so that we can view the child objects
    // of this object.
    public bool isOpen;

    // A run-time only reference to the object we are viewing. This is nulled out
    // in PrefabInfo.Deselect() when we change prefabs.
    [System.NonSerialized]
    public GameObject obj;

    // The parent view. Really only used when we press the left-arrow to close 
    // the hierarchy and go up one level.
    [System.NonSerialized]
    public ObjectView parent;

    public ObjectView(GameObject go, bool io)
    {
      localID = PrefabInspector.GetLocalID(go);
      isOpen = io;
    }

    public ObjectView(long lid, bool io)
    {
      localID = lid;
      isOpen = io;
    }
  }

  /**
   * The overall data model for this inspector. Contains the above view models
   * along with information about the current selection. This is the top-most
   * object that is serialized to disk.
   */
  [System.Serializable]
  public class PrefabSet
  {
    // The list of prefabs that the inspector has seen.
    public List<PrefabInfo> knownPrefabs = new List<PrefabInfo>();

    // The guid of the current prefab. Used at initialization to find the prefab.
    public string currentGuid;

    // The current selection data.
    [System.NonSerialized]
    public PrefabInfo currentInfo;

    [System.NonSerialized]
    public GameObject currentPrefab;

    [System.NonSerialized]
    public List<PrefabInfo> recentlyUsed = new List<PrefabInfo>();

    //For the UI only.
    [System.NonSerialized]
    public string[] recentlyUsedNames;

    [System.NonSerialized]
    public Dictionary<string, PrefabInfo> prefabHash;

    [System.NonSerialized]
    public int maxRecent = 10;

    // Adds the given Prefab Does not check if we have added it already.
    // Select() is probably the better function and maybe this should be private.
    public PrefabInfo Add(GameObject prefab)
    {
      PrefabInfo prefabInfo = new PrefabInfo(prefab);
      prefabInfo.OnDeserialization();
      Add(prefabInfo);
      return prefabInfo;
    }

    public void Add(PrefabInfo pi)
    {
      knownPrefabs.Add(pi);
      prefabHash[pi.guid] = pi;
    } 

    // In general prefab data is kept around forever, but if an object is in 
    // recently used that was deleted this is called to remove it.
    // It would be possible to do some kind of sweep that would remove old crap....
    public void Remove(PrefabInfo pi)
    {
      knownPrefabs.Remove(pi);
      prefabHash.Remove(pi.guid);
      bool removedFromRecentlyUsed = recentlyUsed.Remove(pi);
      if(removedFromRecentlyUsed)
      {
        updateRecentlyUsed();
      }
    }

    // Selects the prefab if it is known.
    // Adds the prefab then selects it if is not known.
    public void Select(GameObject prefab)
    {
      if(prefab == null)
      {
        currentGuid = null;
        currentInfo = null;
        currentPrefab = null;
      }else{
        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
        PrefabInfo pi = null;
        if(prefabHash.TryGetValue(guid, out pi))
        {
          if(currentInfo != null)
          {
            //we are deselecting an info.
            currentInfo.Deselect();
          }
          currentGuid = guid;
          currentInfo = pi;
          currentInfo.name = prefab.name;
          currentPrefab = prefab;
          setPrefabAsRecentlyUsed(currentInfo);
        }else{
          Add(prefab);
          Select(prefab); // We must go deeper...BWOOOM
        }

      }
    }

    void setPrefabAsRecentlyUsed(PrefabInfo info)
    {
      recentlyUsed.Remove(info);//remove it if its already in there....
      recentlyUsed.Insert(0, info); // re-insert at the beginning.
      updateRecentlyUsed();
    }
 
    void updateRecentlyUsed()
    {
      //remove items at the back.
      while(recentlyUsed.Count > maxRecent)
      {
        PrefabInfo removed = recentlyUsed[recentlyUsed.Count - 1];
        recentlyUsed.RemoveAt(recentlyUsed.Count -1);
        removed.priority = -1;
      }

      //reset values and create array of names.
      recentlyUsedNames = new string[recentlyUsed.Count + 1];

      recentlyUsedNames[0] = "Recently Used";
      for(int i = 0; i < recentlyUsed.Count; i++)
      {
        PrefabInfo pi = recentlyUsed[i];
        pi.priority = i;
        recentlyUsedNames[i + 1] = pi.name;
      }
    }

    public void OnDeserialization()
    {
      if(recentlyUsed == null)
      {
        recentlyUsed = new List<PrefabInfo>();
      }
      maxRecent = 10;
      // Debug.Log("[PrefabSet] OnDeserialization");
      prefabHash = new Dictionary<string, PrefabInfo>();
      foreach(PrefabInfo info in knownPrefabs)
      {
        info.OnDeserialization();
        prefabHash[info.guid] = info;
        if(info.guid == currentGuid)
        {
          //this is similar to the Select() logic, probably should refactor to use that...
          currentInfo = info;
          string assetPath = AssetDatabase.GUIDToAssetPath(currentInfo.guid);
          currentPrefab = (GameObject)AssetDatabase.LoadAssetAtPath(assetPath, typeof(GameObject));
        }
        if(info.priority > -1)
        {
          recentlyUsed.Add(info);
        }
      }

      //Sort the recentlyUsed based on priority.
      recentlyUsed.Sort((info1, info2) => info1.priority.CompareTo(info2.priority));

      //Process our content and generate recently used data.
      updateRecentlyUsed();
    }
  }

  // THE INSPECTOR WINDOW 

  // DATA
  public PrefabSet prefabSet;

  // The mouse event is looked at while we draw the object views in the hierarchy.
  Event mouseEvent;

  // set in OnEnable to false so that when we recompile/etc we re-initialize.
  bool _initialized = false;

  //The scroll position of the scroll view for the object hierarchy.
  Vector2 scrollPosition;

  //The list of currently displaying views. Views may be hidden or shown at any
  // time and this is the linear list of views currently shown for purposes of
  // navigating via keyboard.
  List<ObjectView> views;

  // STYLES
  GUIStyle selectedObject; //uses a texture generated for a background.
  GUIStyle unselectedObject;

  GUIStyle foldoutOpen;
  GUIStyle foldoutClosed;

  //TODO: kb shortcut?
  [MenuItem ("Window/Prefab Inspector")]
  static void Init () 
  {
    // Get existing open window or if none, make a new one:
    PrefabInspector window = (PrefabInspector)EditorWindow.GetWindow (typeof (PrefabInspector));
    window.titleContent = new GUIContent("Prefabs");
    window.Show();
  }

  void OnEnable()
  {
    _initialized = false;
  }

  void OnDisable()
  {
    //GC our texture, since we have to set HideAndDontSave
    DestroyImmediate(selectedObject.normal.background);
    Persist(); //save to disk all our shtuff.
  }

  void init()
  {
    // Why the flag? Frequently initialization stuff has to happen in OnGui.
    if(!_initialized)
    {
      _initialized = true;

      selectedObject = new GUIStyle();
      selectedObject.normal.background = MakeTex(8,8, new Color32(75,96, 145, 255));
      unselectedObject = new GUIStyle();

      foldoutClosed = new GUIStyle(EditorStyles.foldout);
      foldoutClosed.margin = new RectOffset();
      foldoutOpen = new GUIStyle(foldoutClosed);
      foldoutOpen.normal.background  = foldoutOpen.onNormal.background;
      foldoutOpen.active.background  = foldoutOpen.onActive.background;
      foldoutOpen.hover.background  = foldoutOpen.onHover.background;

      views = new List<ObjectView>();

      prefabSet = PopulateFromDisk();
    }
  }

  void OnGUI () 
  {
    //always init from OnGUI
    init();

    // Get our event info.
    Event e = Event.current;
    if(e.type == EventType.MouseDown)
    {
      mouseEvent = e;
    }else{
      mouseEvent = null;
    }

    if(e.type == EventType.MouseDrag)
    {
      ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
      if(currentView.obj){
        DragAndDrop.PrepareStartDrag();
        DragAndDrop.objectReferences = new UnityEngine.Object[]{ currentView.obj };
        DragAndDrop.StartDrag(currentView.obj.ToString());
        e.Use();
      }
    }

    /**
     * If we select a new prefab this frame (such as through Recently Used or 
     * dragging a new one into the object field) then this will be set and we 
     * will look at our known views and attempt to set it to the active object.
     */
    bool setNewObject = false;


    // Draw recently used;
    if(prefabSet.recentlyUsed.Count > 0)
    {
      int selectedIndex = EditorGUILayout.Popup(0, prefabSet.recentlyUsedNames);
      if(selectedIndex > 0)
      {
        // Debug.Log("[PrefabSet] Selected:"+prefabSet.recentlyUsedNames[selectedIndex]);
        PrefabInfo info = prefabSet.recentlyUsed[selectedIndex - 1];
        string assetPath = AssetDatabase.GUIDToAssetPath(info.guid);
        if(assetPath != null )
        {
          GameObject prefab = (GameObject)AssetDatabase.LoadAssetAtPath(assetPath, typeof(GameObject));
          if(prefab != null)
          {
            prefabSet.Select(prefab);
            setNewObject = true;
          }else{
            // no prefab found! not cool.
            Debug.Log("[Prefab Inspector] Looks like that prefab doesn't exist. Perhaps it was deleted or moved. Removing from our list....");
            prefabSet.Remove(info);
          }
        }
      }
    }

    //Display Select Prefab Field
    float lw = EditorGUIUtility.labelWidth;
    EditorGUIUtility.labelWidth = 50;
    GameObject newObj = (GameObject)EditorGUILayout.ObjectField("Prefab", prefabSet.currentPrefab, typeof(GameObject), false);
    EditorGUIUtility.labelWidth = lw;

    if(newObj != prefabSet.currentPrefab)
    {
      if(newObj is GameObject)
      { 
        GameObject root = PrefabUtility.FindPrefabRoot(newObj);
        prefabSet.Select(root);
        setNewObject = true;
      }else{
        prefabSet.Select(null);
      }
    }
    GUI.enabled = prefabSet.currentPrefab != null;
    if(GUILayout.Button("Open In New Scene"))
    {
      if(EditorApplication.isSceneDirty)
      {
        EditorApplication.SaveCurrentSceneIfUserWantsTo();
      } 
      EditorApplication.NewScene();
      GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefabSet.currentPrefab);
      ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
      if(currentView != null && currentView.obj != null)
      {
        UnityEngine.Object prefabParent = currentView.obj;
        GameObject selection = FindSelection(go, prefabParent);
        Selection.activeTransform = selection.transform;
      }
    }
    GUI.enabled = true;

    // Draw prefab hierarchy.
    views.Clear();
    if(prefabSet.currentPrefab != null)
    {
      scrollPosition = GUILayout.BeginScrollView(scrollPosition);
      displayObject(prefabSet.currentPrefab, 0, null);
      if(setNewObject)
      {
        //we have now drawn the display and have wired up our object views for 
        // known objects. We have also set a new object this frame.
        // Lets traverse our views, looking for an object that may correspond to
        // a selected object.
        if(prefabSet.currentInfo.currentID > 0)
        {
          foreach(ObjectView view in prefabSet.currentInfo.views)
          {
            if(view.localID == prefabSet.currentInfo.currentID)
            {
              Selection.activeObject = view.obj;
            }
          }
        }

      }
      GUILayout.EndScrollView();
    }

    // Handle Keyboard input.
    if (e.type == EventType.KeyDown) {
      if(e.keyCode == KeyCode.DownArrow)
      {
        e.Use();
        ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
        int index = views.IndexOf(currentView);
        index++;
        if(index < views.Count)
        {
          currentView = views[index];
          Selection.activeObject = currentView.obj;
          prefabSet.currentInfo.currentID = currentView.localID;
        }
      }
      if(e.keyCode == KeyCode.UpArrow)
      {
        e.Use();
        ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
        int index = views.IndexOf(currentView);
        index--;
        if(index >= 0)
        {
          currentView = views[index];
          Selection.activeObject = currentView.obj;
          prefabSet.currentInfo.currentID = currentView.localID;
        }
      }
      if(e.keyCode == KeyCode.LeftArrow)
      {
        e.Use();
        ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
        if(currentView.isOpen)
        {
          currentView.isOpen = false;
        }else{
          //its already closed, let's get the parent and close that.
          currentView = currentView.parent;
          if(currentView != null)
          {
            currentView.isOpen = false;
            Selection.activeObject = currentView.obj;
            prefabSet.currentInfo.currentID = currentView.localID;
          }
        }
      }
      if(e.keyCode == KeyCode.RightArrow)
      {
        e.Use();
        ObjectView currentView = prefabSet.currentInfo.GetView(prefabSet.currentInfo.currentID);
        currentView.isOpen = true;
      }
    }
  }

  // Iterates over a prefab instance hierarchy and attempts to find the instance
  // gameobject that corresponds to the currently selected gameobject inside 
  // the prefab.
  GameObject FindSelection(GameObject go, UnityEngine.Object prefabParent)
  {
    if(PrefabUtility.GetPrefabParent(go) == prefabParent)
    {
      return go;
    }
    foreach(Transform t in go.transform)
    {
      GameObject sel = FindSelection(t.gameObject, prefabParent);
      if(sel != null)
      {
        return sel;
      }
    }
    return null;
  }

  // Recursively walks the object hierarchy and uses the view model information
  // inside the PrefabInfo object to show foldouts (not EditorGUI.Foldout but
  // our own that works better for our needs).
  void displayObject(GameObject obj, int depth, ObjectView parentView)
  {
    float buttonWidth = 20.0f;

    ObjectView view = null;
    PrefabInfo info = prefabSet.currentInfo;
    view = info.GetView(obj, false);
    views.Add(view);
    view.parent = parentView;

    bool viewSelected = info.currentID == view.localID;
    GUIStyle selectedStyle = viewSelected ? selectedObject : unselectedObject;

    if(obj.transform.childCount > 0)
    {
      Rect r = EditorGUILayout.BeginHorizontal(selectedStyle);
      GUILayout.Space(depth * 10); //indent
      GUIStyle arrowStyle = view.isOpen ? foldoutOpen : foldoutClosed;
      bool arrowClicked = GUILayout.Button(GUIContent.none, arrowStyle, GUILayout.Width(  buttonWidth));
      GUILayout.Label(obj.name);
      EditorGUILayout.EndHorizontal();
      if(mouseEvent != null && r.Contains(mouseEvent.mousePosition))
      {
        Selection.activeObject = obj;
        info.currentID = view.localID;
        Repaint();
      }

      if(arrowClicked)
      {
        view.isOpen = !view.isOpen;
      }

      if(view.isOpen)
      {
        //draw another one.
        foreach(Transform child in obj.transform)
        {
          displayObject(child.gameObject, depth + 1, view);
        }
      }
    }else{
      Rect r = EditorGUILayout.BeginHorizontal(selectedStyle);
      GUILayout.Space((depth * 10) + buttonWidth); //indent
      GUILayout.Label(obj.name);
      EditorGUILayout.EndHorizontal();
      if(mouseEvent != null && r.Contains(mouseEvent.mousePosition))
      {
        Selection.activeObject = obj;
        info.currentID = view.localID;
        Repaint();
      }
    }
  }

  // UTIL
  static PropertyInfo debugModeInspectorThing;  

  // A hack to allow for SerializedObject to contain the m_LocalIdentfierInFile
  // SerializedProperty, which is required for the localID to be retrieved.
  public static long GetLocalID(GameObject go)
  {
    initDebugMode();
    SerializedObject so = new SerializedObject(go);
    debugModeInspectorThing.SetValue(so, InspectorMode.Debug, null);
    SerializedProperty localIDProp = so.FindProperty("m_LocalIdentfierInFile");
    return localIDProp.longValue;
  }

  static void initDebugMode()
  {
    if(debugModeInspectorThing == null)
    {
      debugModeInspectorThing = typeof(SerializedObject).GetProperty("inspectorMode", BindingFlags.NonPublic | BindingFlags.Instance);
    }
  }

  // Creates a texture with HideAndDontSave (so it doesn't get cleaned up with
  // various editor-time sweeps). Must be manually DestroyImmediate() in order to
  // not leak.
  private Texture2D MakeTex(int width, int height, Color col)
  {
      Color[] pix = new Color[width*height];
  
      for(int i = 0; i < pix.Length; i++)
          pix[i] = col;
  
      Texture2D result = new Texture2D(width, height);
      result.SetPixels(pix);
      result.Apply();
      result.hideFlags = HideFlags.HideAndDontSave;
  
      return result; 
  }

  // SERIALIZATION 

  // Code that de/serializes to disk in persistentDataPath. This 
  // serializes all view models using guids and localIDs so that we're saving 
  // very little information to disk. Currently using the BinaryFormatter class.

  static string serializationPath = "/prefabinspector/data.bin";
  public void Persist()
  {
    string path = Application.persistentDataPath + serializationPath;
    SerializationUtil.Serialize(path, prefabSet);
  }

  public static PrefabSet PopulateFromDisk()
  {
    string path = Application.persistentDataPath + serializationPath;
    PrefabSet prefabSet = null;
    try{

      prefabSet = (PrefabSet)SerializationUtil.Deserialize(path);
      if(prefabSet != null)
      {
        prefabSet.OnDeserialization();
      }
    }catch(System.Exception ex)
    {
      Debug.LogError("Issue deserializing:"+ex);
    }
    if(prefabSet == null)
    {
      prefabSet = new PrefabSet();
      prefabSet.OnDeserialization();
      return prefabSet;
    }else{
      return prefabSet;
    }
  }

  public class SerializationUtil
  {
    public static void Serialize(string path, System.Object o)
    {
      string dirPath = Path.GetDirectoryName(path);
      if(!Directory.Exists(dirPath))
      {
        Directory.CreateDirectory(dirPath);
      }
      FileStream fs = new FileStream(path, FileMode.Create);
      BinaryFormatter formatter = new BinaryFormatter();
      try
      {
        formatter.Serialize(fs, o);
      }
      catch(SerializationException e)
      {
        UnityEngine.Debug.Log("Failed to serialize:"+ e.Message);
        throw;
      }
      finally
      {
        fs.Close();
      }
    }

    public static System.Object Deserialize(string path)
    {
      if(File.Exists(path))
      {
        FileStream fs = new FileStream(path, FileMode.Open);
        try
        {
          BinaryFormatter formatter = new BinaryFormatter();
          return formatter.Deserialize(fs);
        }
        catch(SerializationException e)
        {
          UnityEngine.Debug.Log("[SerializationUtil] failed to deserialize:"+e.Message);
          throw;
        }
        finally
        {
          fs.Close();
        }
      }else{
        return null;
      }
    }
  }


}