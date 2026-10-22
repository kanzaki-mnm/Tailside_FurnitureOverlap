using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using TailsideFurnitureOverlap;

[BepInPlugin("s_evans.tailside.furnitureoverlap.probe", "Furniture Overlap Probe", "0.1.0")]
[BepInDependency(Plugin.Id)]
public sealed class Probe : BaseUnityPlugin
{
    private int count;
    private void Awake()
    {
        if (!File.Exists(Path.Combine(Paths.GameRootPath, ".modsetup-sandbox")) ||
            Array.IndexOf(Environment.GetCommandLineArgs(), "--overlap-probe") < 0) return;
        gameObject.hideFlags |= HideFlags.HideAndDontSave;
        DontDestroyOnLoad(gameObject);
        try { Run(); StartCoroutine(GuardProbe(RenderTieProbe())); }
        catch (Exception ex) { Logger.LogError("OVERLAP_PROBE_FAIL " + ex); Application.Quit(1); }
    }

    private void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        count++; Logger.LogInfo("PASS " + name);
    }

    private System.Collections.IEnumerator GuardProbe(System.Collections.IEnumerator probe)
    {
        while (true)
        {
            bool next;
            try { next = probe.MoveNext(); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Paths.GameRootPath, "overlap-render.report"), "FAIL " + ex);
                Logger.LogError("OVERLAP_PROBE_FAIL " + ex);
                Application.Quit(1);
                yield break;
            }
            if (!next) yield break;
            yield return probe.Current;
        }
    }

    private System.Collections.IEnumerator RenderTieProbe()
    {
        for(int warmup=0;warmup<30;warmup++)yield return null;
        var routePlugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        AccessTools.Method(typeof(Plugin),"InstallDrawOrder").Invoke(routePlugin,null);
        var routeObject=new GameObject("routing-test-manager");routeObject.SetActive(false);var routeManager=routeObject.AddComponent<ItemPlacement>();
        AccessTools.Field(typeof(ItemPlacement),"<Instance>k__BackingField").SetValue(null,routeManager);
        TestDpadRouting(routeManager);
        TestOrderHints(routeManager);
        var texture=new Texture2D(8,8);var pixels=new Color[64];for(int i=0;i<64;i++)pixels[i]=Color.white;
        texture.SetPixels(pixels);texture.Apply();
        var sprite=Sprite.Create(texture,new Rect(0,0,8,8),new Vector2(.5f,.5f),8);
        var shader=Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        var sharedMaterial=new Material(shader);
        Func<string,Color,SpriteRenderer> make=(name,color)=>{
            var go=new GameObject(name);go.SetActive(false);go.layer=31;go.transform.position=new Vector3(20000,0,0);
            var renderer=go.AddComponent<SpriteRenderer>();renderer.sprite=sprite;renderer.color=color;
            renderer.sharedMaterial=sharedMaterial;
            go.SetActive(true);DontDestroyOnLoad(go);return renderer;
        };
        var first=make("render-first",Color.green);var second=make("render-second",Color.red);
        var camera=new GameObject("tie-test-camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=1;
        DontDestroyOnLoad(camera.gameObject);
        camera.transform.position=new Vector3(20000,0,-10);camera.cullingMask=1<<31;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.blue;
        var target=new RenderTexture(32,32,24);camera.targetTexture=target;
        camera.Render();
        var previous=RenderTexture.active;RenderTexture.active=target;
        var capture=new Texture2D(32,32);capture.ReadPixels(new Rect(0,0,32,32),0,0);capture.Apply();RenderTexture.active=previous;
        var pixel=capture.GetPixel(16,16);
        Check(pixel.r>.9f && pixel.g<.1f && pixel.a>.9f,"GPU renders later identical-priority sprite in front");
        Check(second.GetInstanceID()<first.GetInstanceID(),"later renderer has lower native ID");
        first.enabled=false;second.enabled=false;
        string fixtures=Environment.GetEnvironmentVariable("OVERLAP_SPRITE_FIXTURES");
        Func<int,int,SpriteRenderer> herbRenderer=(id,width)=>{
            var tex=new Texture2D(width,34,TextureFormat.RGBA32,false);
            tex.LoadRawTextureData(File.ReadAllBytes(Path.Combine(fixtures,"sprite-"+id+".rgba")));tex.Apply();
            var go=new GameObject("gpu-herb-"+id);go.layer=31;go.transform.position=new Vector3(20000,1,0);
            var r=go.AddComponent<SpriteRenderer>();r.sprite=Sprite.Create(tex,new Rect(0,0,width,34),new Vector2(.5f,1),16);
            r.sharedMaterial=sharedMaterial;r.spriteSortPoint=SpriteSortPoint.Pivot;
            var copy=r.material; // Same per-item copy made by the real hover loop.
            return r;
        };
        sharedMaterial=new Material(Shader.Find("Shader Graphs/SpriteOutlineShader"));
        var green=herbRenderer(2124,18);var yellow=herbRenderer(2943,18);var brown=herbRenderer(1766,17);
        var wall=new GameObject("GPU-Wall");wall.AddComponent<SortingGroup>().sortingOrder=-1;green.transform.SetParent(wall.transform,true);yellow.transform.SetParent(wall.transform,true);brown.transform.SetParent(wall.transform,true);
        green.transform.position+=new Vector3(0,.1f,0);yellow.transform.position+=new Vector3(0,.2f,0);
        Func<Color> sample=()=>{
            SortingGroup.UpdateAllSortingGroups();camera.Render();var before=RenderTexture.active;RenderTexture.active=target;
            capture.ReadPixels(new Rect(0,0,32,32),0,0);capture.Apply();RenderTexture.active=before;
            return capture.GetPixel(16,16);
        };
        var combined=sample();var allPixels=capture.GetPixels32();green.enabled=false;yellow.enabled=false;var brownOnly=sample();var brownPixels=capture.GetPixels32();int visible=0,matched=0;for(int k=0;k<brownPixels.Length;k++){var q=brownPixels[k];if(q.b>250 && q.r<5 && q.g<5)continue;visible++;if(allPixels[k].Equals(q))matched++;}File.WriteAllText(Path.Combine(Paths.GameRootPath,"gpu-three-debug.report"),"combined="+combined+" brownOnly="+brownOnly+" visible="+visible+" matched="+matched);

        Logger.LogInfo("MIXED_TEXTURE_RENDER observed="+combined+" brownReference="+brownOnly+" matchedPixels="+matched+"/"+visible);
        Check(Mathf.Abs(combined.r-brownOnly.r)<.01f && Mathf.Abs(combined.g-brownOnly.g)<.01f && Mathf.Abs(combined.b-brownOnly.b)<.01f,
            "GPU shared wall group draws brown in front despite different herb heights");
        brown.enabled=false;
        Func<string,int,int,int,Vector2,Vector3,int,SpriteRenderer> makeDecor=(name,id,w,h,pivot,pos,order)=>{
            var tex=new Texture2D(w,h,TextureFormat.RGBA32,false);
            tex.LoadRawTextureData(File.ReadAllBytes(Path.Combine(fixtures,"sprite-"+id+".rgba")));tex.Apply();
            var go=new GameObject(name);go.layer=31;go.transform.SetParent(wall.transform,false);go.transform.position=pos;
            var renderer=go.AddComponent<SpriteRenderer>();renderer.sprite=Sprite.Create(tex,new Rect(0,0,w,h),pivot,16);
            renderer.sharedMaterial=new Material(sharedMaterial);renderer.sortingOrder=order;return renderer;
        };
        var board=makeDecor("gpu-board",1902,104,44,new Vector2(.5f,.5f),new Vector3(20000,0,0),1);
        var paper=makeDecor("gpu-paper",2211,18,18,new Vector2(.5f,0),new Vector3(20000,-.5f,0),0);
        var paperItem=paper.gameObject.AddComponent<Item>();var boardItem=board.gameObject.AddComponent<Item>();boardItem.isPlaced=true;
        var mo=new GameObject("gpu-order-manager");mo.SetActive(false);var manager=mo.AddComponent<ItemPlacement>();
        AccessTools.Field(typeof(ItemPlacement),"<Instance>k__BackingField").SetValue(null,manager);
        manager.inBuildMode=true;manager.currentItem=paperItem;
        paper.enabled=false;var boardReference=sample();paper.enabled=true;
        Func<Color,Color,bool> same=(x,y)=>Mathf.Abs(x.r-y.r)<.01f && Mathf.Abs(x.g-y.g)<.01f && Mathf.Abs(x.b-y.b)<.01f;
        Check(same(sample(),boardReference),"GPU standard paper renders behind blackboard");
        Plugin.ChangeOrder(paperItem,1);Plugin.ChangeOrder(paperItem,1);
        var frontPaper=sample();board.enabled=false;var paperReference=sample();board.enabled=true;
        Check(same(frontPaper,paperReference) && !same(frontPaper,boardReference),"GPU forward steps render paper in front of blackboard");
        var navObject=new GameObject("gpu-order-navigation");navObject.SetActive(false);var nav=navObject.AddComponent<BuildModeNavigation>();
        nav.placedItemsList=new System.Collections.Generic.List<Item>{boardItem,paperItem};
        AccessTools.Field(typeof(ItemPlacement),"buildModeNavigation").SetValue(manager,nav);
        var selectionHarmony=new Harmony("local.orderprobe.gpu-selection");
        selectionHarmony.Patch(AccessTools.Method(typeof(ItemSpriteHitTester),"CompareTopmost",new[]{typeof(Transform),typeof(Transform)}),
            postfix:new HarmonyMethod(AccessTools.Method(typeof(Plugin),"CompareVisibleOrder")));
        paperItem.isPlaced=true;manager.currentItem=null;
        Check(manager.GetGrabbableItemAtPosition(new Vector2(20000,0))==paperItem,"custom paper draw order and actual selection agree");
        selectionHarmony.UnpatchSelf();paperItem.isPlaced=false;manager.currentItem=paperItem;
        Plugin.ChangeOrder(paperItem,-1);Plugin.ChangeOrder(paperItem,-1);
        Check(same(sample(),boardReference),"GPU backward steps restore paper behind blackboard");
        Plugin.ChangeOrder(paperItem,1);Plugin.ChangeOrder(paperItem,1);Plugin.ChangeOrder(paperItem,0,true);
        Check(same(sample(),boardReference),"GPU reset restores original paper behind blackboard");
        var equipment = new GameObject("GPU-equipment-root");
        var equipmentGroup=equipment.AddComponent<SortingGroup>();
        var boardParent=board.transform.parent;board.transform.SetParent(equipment.transform,true);
        foreach(string layer in new[]{"Obstacles","Foreground"})
        {
            equipmentGroup.sortingLayerName=layer;equipmentGroup.sortingOrder=0;
            Check(same(sample(),boardReference),"GPU reproduces occlusion by equipment on "+layer);
            Plugin.ChangeOrder(paperItem,1);
            Check(same(sample(),paperReference),"GPU adjusted decor escapes parent group above "+layer+" equipment");
            Plugin.ChangeOrder(paperItem,-1);
            Check(same(sample(),boardReference),"GPU Down restores standard equipment occlusion on "+layer);
            Plugin.ChangeOrder(paperItem,1);Plugin.ChangeOrder(paperItem,0,true);
            Check(same(sample(),boardReference),"GPU reset restores inherited sorting scope on "+layer);
        }
        Plugin.ChangeOrder(paperItem,1);
        var ownGroup=paper.GetComponent<SortingGroup>();
        Check(ownGroup.enabled && ownGroup.sortAtRoot,"adjusted decor has independent root sorting group");
        object[] compareArgs={paper.transform,board.transform,0};
        paperItem.isPlaced=true;
        AccessTools.Method(typeof(Plugin),"CompareVisibleOrder").Invoke(null,compareArgs);
        Check((int)compareArgs[2]>0,"selection ignores escaped parent groups and follows visible front item");paperItem.isPlaced=false;
        var orderPlugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        var orderEnabled=orderPlugin.Settings.Bind("General","Enabled",true);
        orderEnabled.Value=false;AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(orderPlugin,null);
        Check(!ownGroup.enabled && same(sample(),boardReference),"disabling mod restores original GPU scope and visibility");
        orderEnabled.Value=true;AccessTools.Method(typeof(Plugin),"SynchronizeOrders").Invoke(null,null);
        Check(ownGroup.enabled && same(sample(),paperReference),"reenabling restores cross-layer front order");
        Plugin.ChangeOrder(paperItem,0,true);
        board.transform.SetParent(boardParent,true);equipment.SetActive(false);
        var nativeGroup=board.gameObject.AddComponent<SortingGroup>();nativeGroup.enabled=false;
        nativeGroup.sortingLayerName="Obstacles";nativeGroup.sortingOrder=17;nativeGroup.sortAtRoot=false;
        boardItem.isPlaced=false;manager.currentItem=boardItem;
        Plugin.ChangeOrder(boardItem,1);
        Check(board.GetComponent<SortingGroup>()==nativeGroup && nativeGroup.enabled && nativeGroup.sortAtRoot,"preexisting item group reused without duplicate components");
        Plugin.ChangeOrder(boardItem,0,true);
        Check(!nativeGroup.enabled && !nativeGroup.sortAtRoot && nativeGroup.sortingLayerName=="Obstacles" && nativeGroup.sortingOrder==17,"reset restores all original item group settings");
        Plugin.ChangeOrder(boardItem,1);
        orderEnabled.Value=false;AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(orderPlugin,null);
        Check(!nativeGroup.enabled && nativeGroup.sortingOrder==17,"mod disable restores preexisting disabled group");
        orderEnabled.Value=true;AccessTools.Method(typeof(Plugin),"SynchronizeOrders").Invoke(null,null);
        Check(nativeGroup.enabled && nativeGroup.sortingOrder==18,"reenable reapplies adjustment to original group");
        Plugin.ChangeOrder(boardItem,0,true);boardItem.isPlaced=true;manager.currentItem=paperItem;

        // Reproduce the world-space NPC bubble being covered by promoted furniture.
        var npcRoot=new GameObject("speech-test-npc");npcRoot.layer=31;
        npcRoot.transform.position=new Vector3(20000,0,0);
        var npc=npcRoot.AddComponent<NPCDialogueBubbleOnDistance>();npc.enabled=false;
        var speechObject=new GameObject("speech-test-canvas",typeof(RectTransform),typeof(Canvas));
        speechObject.layer=31;speechObject.transform.SetParent(npcRoot.transform,false);
        var speech=speechObject.GetComponent<Canvas>();speech.renderMode=RenderMode.WorldSpace;speech.worldCamera=camera;
        speech.sortingLayerName="Obstacles";speech.sortingOrder=3;
        speech.GetComponent<RectTransform>().sizeDelta=Vector2.one;
        var bubbleObject=new GameObject("speech-test-bubble",typeof(RectTransform),typeof(UnityEngine.UI.Image));
        bubbleObject.layer=31;bubbleObject.transform.SetParent(speechObject.transform,false);
        var bubbleRect=bubbleObject.GetComponent<RectTransform>();bubbleRect.sizeDelta=Vector2.one;
        bubbleObject.GetComponent<UnityEngine.UI.Image>().color=Color.magenta;
        AccessTools.Field(typeof(NPCDialogueBubbleOnDistance),"dialogueBubble").SetValue(npc,bubbleObject);
        var speechPosition=speech.transform.position;var bubblePosition=bubbleRect.position;
        Canvas.ForceUpdateCanvases();
        Plugin.ChangeOrder(paperItem,1);
        Check(same(sample(),paperReference),"GPU reproduces promoted furniture covering ordinary NPC bubble");
        // Invoke the real patched Start, not the protection helper, to verify routing.
        AccessTools.Method(typeof(NPCDialogueBubbleOnDistance),"Start").Invoke(npc,null);
        bubbleObject.SetActive(true);Canvas.ForceUpdateCanvases();
        var speechPixel=sample();
        Check(speechPixel.r>.9f && speechPixel.b>.9f && speechPixel.g<.1f,"GPU NPC bubble renders in front of promoted furniture");
        Check(speech.sortingLayerName=="BuildMode" && speech.sortingOrder==3 && (speech.isRootCanvas || speech.overrideSorting),"only speech canvas sorting is promoted layer="+speech.sortingLayerName+" order="+speech.sortingOrder+" override="+speech.overrideSorting);
        Check(speech.transform.position==speechPosition && bubbleRect.position==bubblePosition,"bubble screen position and NPC position stay unchanged");
        for(int step=0;step<10;step++)Plugin.ChangeOrder(paperItem,1);
        Check(sample().b>.9f,"NPC bubble stays in front after repeated furniture forward steps");
        orderEnabled.Value=false;AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(orderPlugin,null);
        Check(speech.sortingLayerName=="Obstacles" && speech.sortingOrder==3 && !speech.overrideSorting,"disabling mod restores original speech canvas settings");
        orderEnabled.Value=true;AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(orderPlugin,null);
        Check(speech.sortingLayerName=="BuildMode" && sample().b>.9f,"reenabling protects already-existing speech bubble");
        AccessTools.Method(typeof(Plugin),"RestoreSpeechOrder").Invoke(orderPlugin,new object[]{true});
        Check(speech.sortingLayerName=="Obstacles" && !speech.overrideSorting,"shutdown cleanup restores original speech order");
        speech.renderMode=RenderMode.ScreenSpaceOverlay;
        AccessTools.Method(typeof(Plugin),"ProtectSpeech").Invoke(null,new object[]{npc,bubbleObject});
        Check(speech.sortingLayerName=="Obstacles" && !speech.overrideSorting,"screen-space UI is excluded from speech adjustment");
        speech.renderMode=RenderMode.WorldSpace;speech.sortingLayerName="Items";
        AccessTools.Method(typeof(Plugin),"ProtectSpeech").Invoke(null,new object[]{npc,bubbleObject});
        Check(speech.sortingLayerName=="Items","existing higher-priority speech layer is preserved");
        UnityEngine.Object.Destroy(npcRoot);yield return null;
        AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(orderPlugin,null);
        Check(true,"destroyed speech canvases are safely retired");
        Plugin.ChangeOrder(paperItem,0,true);

        var originalKeyboard=UnityEngine.InputSystem.Keyboard.current;
        var keyboard=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
        var input=AccessTools.Method(typeof(Plugin),"ProcessOrderInput");
        var plugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        var f=plugin.Settings.Bind("DrawOrder","ForwardKey",KeyCode.PageUp);var b=plugin.Settings.Bind("DrawOrder","BackwardKey",KeyCode.PageDown);var r=plugin.Settings.Bind("DrawOrder","ResetKey",KeyCode.Home);
        var oldF=f.Value;var oldB=b.Value;var oldR=r.Value;f.Value=KeyCode.UpArrow;b.Value=KeyCode.DownArrow;r.Value=KeyCode.Backspace;
        bool wasPaused=PauseMenu.GameIsPaused;PauseMenu.GameIsPaused=false;
        // The plugin Behaviour is disabled by Run()'s unpatch test. The game
        // update callback must still receive keys independently of LateUpdate.
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.UpArrow));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==1,"actual Input System rebound UpArrow event reaches game-update callback");
        input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==1,"same-frame callback cannot double-apply key press");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());UnityEngine.InputSystem.InputSystem.Update();
        yield return null;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.DownArrow));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==0,"actual Input System rebound DownArrow event moves paper backward");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());UnityEngine.InputSystem.InputSystem.Update();
        yield return null;
        Plugin.ChangeOrder(paperItem,1);Plugin.ChangeOrder(paperItem,1);
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Backspace));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==0,"actual Input System rebound Backspace event resets paper order");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());UnityEngine.InputSystem.InputSystem.Update();
        yield return null;
        PauseMenu.GameIsPaused=true;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.UpArrow));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==0,"rebound UpArrow event is ignored while paused");PauseMenu.GameIsPaused=false;
        manager.inBuildMode=false;input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==0,"rebound UpArrow event is ignored outside build mode");manager.inBuildMode=true;
        manager.currentItem=null;input.Invoke(null,new object[]{manager});
        Check(paper.sortingOrder==0,"rebound UpArrow event is ignored without carried furniture");
        f.Value=oldF;b.Value=oldB;r.Value=oldR;
        PauseMenu.GameIsPaused=wasPaused;
        UnityEngine.InputSystem.InputSystem.RemoveDevice(keyboard);if(originalKeyboard!=null)originalKeyboard.MakeCurrent();
        var padTests=TestGamepadOrder(manager,paperItem,paper,input,plugin);
        while(padTests.MoveNext())yield return padTests.Current;
        TestCleanup(manager, paperItem, paper, plugin);
        File.WriteAllText(Path.Combine(Paths.GameRootPath,"overlap-render.report"),"RENDER_TIE firstId="+first.GetInstanceID()+" secondId="+second.GetInstanceID()+" pixel="+pixel+" checks="+count);
        Logger.LogInfo("RENDER_TIE firstId="+first.GetInstanceID()+" secondId="+second.GetInstanceID()+" firstRenderer="+first.GetInstanceID()+" secondRenderer="+second.GetInstanceID()+" pixel="+pixel);
        Logger.LogInfo("OVERLAP_PROBE_PASS checks="+count);
        Application.Quit(0);
        yield break;
    }

    private void Run()
    {
        Check(Plugin.TryOrderInputKey(KeyCode.Alpha1,out var digit) && digit==UnityEngine.InputSystem.Key.Digit1,"number row binding maps to Input System");
        Check(Plugin.TryOrderInputKey(KeyCode.Keypad9,out var numpad) && numpad==UnityEngine.InputSystem.Key.Numpad9,"numpad binding maps to Input System");
        Check(Plugin.TryOrderInputKey(KeyCode.Return,out var enter) && enter==UnityEngine.InputSystem.Key.Enter,"return binding maps to Input System");
        Check(Plugin.TryOrderInputKey(KeyCode.LeftControl,out var ctrl) && ctrl==UnityEngine.InputSystem.Key.LeftCtrl,"control binding maps to Input System");
        Check(Plugin.TryOrderInputKey(KeyCode.UpArrow,out var arrow) && arrow==UnityEngine.InputSystem.Key.UpArrow,"existing arrow binding unchanged");
        Check(!Plugin.TryOrderInputKey(KeyCode.None,out _) && !Plugin.TryOrderInputKey(KeyCode.JoystickButton0,out _),"nonkeyboard keys do not become invalid Input System indices");
        // Inactive components avoid unrelated game Awake/Update/save logic.
        var managerObject = new GameObject("probe-manager"); managerObject.SetActive(false);
        var manager = managerObject.AddComponent<ItemPlacement>();
        AccessTools.Field(typeof(ItemPlacement), "<Instance>k__BackingField").SetValue(null, manager);
        manager.inBuildMode = true;
        var previewObject = new GameObject("probe-preview"); previewObject.SetActive(false);
        previewObject.transform.position = new Vector3(10000, 0, 0);
        var item = previewObject.AddComponent<Item>();
        item.itemIndicator = previewObject.transform;
        item.placementLayer = 1; item.overlapLayer = 0; item.addLayer = 0;
        item.distance = 1; item.sideOffset = .2f;
        var renderer = previewObject.AddComponent<SpriteRenderer>();
        var box = previewObject.AddComponent<BoxCollider2D>(); box.size = new Vector2(.4f,.4f);
        AccessTools.Field(typeof(Item), "spriteRenderer").SetValue(item, renderer);
        AccessTools.Field(typeof(Item), "boxCollider2D").SetValue(item, box);
        manager.currentItem = item;
        var ground = new GameObject("probe-ground"); ground.transform.position = new Vector3(10000,-.6f,0);
        ground.AddComponent<BoxCollider2D>().size = new Vector2(4,.2f);
        var furniture = new GameObject("probe-furniture"); furniture.SetActive(false);
        furniture.layer = 8; furniture.transform.position = new Vector3(10000,-.15f,0);
        var owner = furniture.AddComponent<Item>(); owner.isPlaced = true;
        var child = new GameObject("probe-child-collider"); child.layer = 8; child.transform.SetParent(furniture.transform, false);
        child.AddComponent<BoxCollider2D>().size = new Vector2(1,.2f);
        // No root box/indicator is needed for the placed owner's Awake.
        furniture.SetActive(true);
        Physics2D.SyncTransforms();
        var configOwner = (Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        var config = configOwner.Settings;
        Check(Path.GetFileName(config.ConfigFilePath) == "FurnitureOverlap.cfg" && ((BaseUnityPlugin)configOwner).Config.Count == 0, "all settings use friendly config filename without legacy bindings");
        config.Save();
        Check(File.Exists(config.ConfigFilePath), "friendly config file is written to disk");
        var forward = config.Bind("DrawOrder", "ForwardKey", KeyCode.PageUp);
        var originalKey = forward.Value;
        forward.Value = KeyCode.F12; config.Save(); forward.Value = originalKey;
        bool autoSave = config.SaveOnConfigSet; config.SaveOnConfigSet = false;
        forward.Value = KeyCode.F11; config.Reload();
        Check(forward.Value == originalKey, "friendly config reload updates existing bindings");
        config.SaveOnConfigSet = autoSave;
        var enabled = (BepInEx.Configuration.ConfigEntry<bool>)config["General", "Enabled"];
        Func<bool> canPlace = () => {
            AccessTools.Field(typeof(Item), "lastPlacementCheckPosition").SetValue(item, new Vector3(float.NaN,0,0));
            Physics2D.SyncTransforms(); return item.CanBePlaced();
        };
        var anywhere = config.Bind("Placement", "Anywhere", true);
        anywhere.Value = false;
        enabled.Value = false;
        Check(!canPlace(), "vanilla rejects furniture overlap");
        enabled.Value = true;
        Check(canPlace(), "overlap enabled permits furniture overlap");
        Check(renderer.color == item.highlightGreen, "preview uses vanilla green highlight");
        manager.inBuildMode = false;
        Check(!canPlace(), "outside build mode retains vanilla rejection");
        manager.inBuildMode = true; manager.currentItem = null;
        Check(!canPlace(), "unselected item retains vanilla rejection");
        manager.currentItem = item; owner.isPlaced = false;
        Check(!canPlace(), "unplaced foreign item is not ignored"); owner.isPlaced = true;
        var world = new GameObject("probe-world-blocker"); world.layer = 8;
        world.transform.position = new Vector3(10000,-.3f,0);
        world.AddComponent<BoxCollider2D>().size = new Vector2(1,.1f);
        Check(!canPlace(), "world obstruction still blocks placement"); world.SetActive(false);
        ground.SetActive(false);
        Check(!canPlace(), "overlap cannot provide missing floor support"); ground.SetActive(true);
        child.layer = 0;
        Check(canPlace(), "allowed furniture support is retained"); child.layer = 8;
        ground.layer = 9;
        Check(!canPlace(), "wrong surface still blocks placement"); ground.layer = 0;
        anywhere.Value = true;
        Check(canPlace(), "anywhere placement accepts normally forbidden surface");
        Check(renderer.color == item.highlightGreen, "anywhere preview uses green highlight");
        ground.SetActive(false);
        Check(canPlace(), "anywhere placement accepts missing floor support");
        world.SetActive(true);
        Check(canPlace(), "anywhere placement accepts static counter-style obstruction");
        manager.inBuildMode=false;
        Check(!canPlace(), "anywhere placement does not apply outside build mode");manager.inBuildMode=true;
        manager.currentItem=null;
        Check(!canPlace(), "anywhere placement does not apply to unheld furniture");manager.currentItem=item;
        enabled.Value=false;
        Check(!canPlace(), "disabled mod restores original blocked verdict");enabled.Value=true;
        anywhere.Value=false;
        Check(!canPlace(), "turning anywhere off restores overlap-only world obstruction rule");
        world.SetActive(false);ground.SetActive(true);ground.layer=0;
        TestSelection(manager, enabled);
        TestRealDecor(manager, enabled);
        TestDrawOrder(manager, enabled);
        // Restore vanilla via actual Harmony removal, not a substitute algorithm.
        BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance.enabled = false;
        new Harmony(Plugin.Id).UnpatchSelf();
        Check(!canPlace(), "unpatch restores vanilla placement");
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Item), "CheckColliders")) == null ||
            !Harmony.GetPatchInfo(AccessTools.Method(typeof(Item), "CheckColliders")).Owners.Contains(Plugin.Id), "patch ownership removed");
    }

    private void TestDpadRouting(ItemPlacement manager)
    {
        var oldPad=UnityEngine.InputSystem.Gamepad.current;
        var pad=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
        var oldControls=UserControl.instance;
        var go=new GameObject("dpad-routing-control");go.SetActive(false);var controls=go.AddComponent<UserControl>();UserControl.instance=controls;
        var plugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        var setting=plugin.Settings.Bind("DrawOrder","GamepadEnabled",true);bool oldSetting=setting.Value;setting.Value=true;
        var prefix=AccessTools.Method(typeof(Plugin),"MaskNavigationDpad");var finish=AccessTools.Method(typeof(Plugin),"RestoreNavigationDpad");
        var itemObject=new GameObject("dpad-carried-test");itemObject.SetActive(false);var item=itemObject.AddComponent<Item>();
        manager.inBuildMode=true;manager.currentItem=item;controls.dPadMovement=Vector2.up;
        object[] arguments={null};prefix.Invoke(null,arguments);
        Check(controls.dPadMovement==Vector2.zero,"carried furniture masks inventory D-pad navigation");
        finish.Invoke(null,new object[]{null,arguments[0]});
        Check(controls.dPadMovement==Vector2.up,"navigation finalizer restores original D-pad vector");
        arguments[0]=null;prefix.Invoke(null,arguments);var fault=new Exception("test navigation fault");
        Check(ReferenceEquals(finish.Invoke(null,new object[]{fault,arguments[0]}),fault) && controls.dPadMovement==Vector2.up,"navigation fault preserves exception and restores input");
        manager.currentItem=null;arguments[0]=null;prefix.Invoke(null,arguments);
        Check(arguments[0]==null && controls.dPadMovement==Vector2.up,"normal inventory D-pad retained when not carrying");manager.currentItem=item;
        manager.inBuildMode=false;prefix.Invoke(null,arguments);
        Check(arguments[0]==null && controls.dPadMovement==Vector2.up,"outside build mode D-pad retained");manager.inBuildMode=true;
        setting.Value=false;prefix.Invoke(null,arguments);
        Check(arguments[0]==null && controls.dPadMovement==Vector2.up,"disabling gamepad feature retains original navigation");setting.Value=true;
        bool wasPaused=PauseMenu.GameIsPaused;PauseMenu.GameIsPaused=true;prefix.Invoke(null,arguments);
        Check(arguments[0]==null && controls.dPadMovement==Vector2.up,"pause-menu D-pad retained");PauseMenu.GameIsPaused=wasPaused;
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(BuildModeNavigation),"Update")).Owners.Contains(Plugin.Id),"actual navigation method has routing patch installed");
        UserControl.instance=oldControls;setting.Value=oldSetting;manager.currentItem=null;
        UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);if(oldPad!=null)oldPad.MakeCurrent();
    }

    private System.Collections.IEnumerator TestGamepadOrder(ItemPlacement manager,Item item,SpriteRenderer sprite,System.Reflection.MethodInfo input,Plugin plugin)
    {
        var original=UnityEngine.InputSystem.Gamepad.current;
        var pad=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
        var setting=plugin.Settings.Bind("DrawOrder","GamepadEnabled",true);bool prior=setting.Value;setting.Value=true;
        manager.inBuildMode=true;manager.currentItem=item;bool wasPaused=PauseMenu.GameIsPaused;PauseMenu.GameIsPaused=false;
        yield return null;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadUp));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==1,"D-pad Up input moves carried paper forward");
        yield return null;input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==1,"held D-pad Up does not repeat every frame");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());UnityEngine.InputSystem.InputSystem.Update();yield return null;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadDown));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"D-pad Down input moves carried paper backward");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());UnityEngine.InputSystem.InputSystem.Update();yield return null;
        Plugin.ChangeOrder(item,1);Plugin.ChangeOrder(item,1);
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadLeft));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"D-pad Left input resets carried paper to standard");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());UnityEngine.InputSystem.InputSystem.Update();yield return null;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadRight));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"D-pad Right does not change draw order");
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());UnityEngine.InputSystem.InputSystem.Update();yield return null;
        setting.Value=false;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadUp));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"disabled gamepad option ignores D-pad order input");setting.Value=true;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());UnityEngine.InputSystem.InputSystem.Update();yield return null;
        PauseMenu.GameIsPaused=true;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadUp));
        UnityEngine.InputSystem.InputSystem.Update();input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"paused game ignores D-pad order input");PauseMenu.GameIsPaused=false;
        manager.inBuildMode=false;input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"outside build mode ignores D-pad order input");manager.inBuildMode=true;
        manager.currentItem=null;input.Invoke(null,new object[]{manager});
        Check(sprite.sortingOrder==0,"D-pad without carried furniture does not change order");
        setting.Value=prior;PauseMenu.GameIsPaused=wasPaused;
        UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);if(original!=null)original.MakeCurrent();
    }

    private static bool SkipNavmesh() => false;
    private void TestDrawOrder(ItemPlacement manager, BepInEx.Configuration.ConfigEntry<bool> enabled)
    {
        var plugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        var go=new GameObject("order-test-paper");go.SetActive(false);
        var sprite=go.AddComponent<SpriteRenderer>();
        var box=go.AddComponent<BoxCollider2D>();
        var item=go.AddComponent<Item>();item.itemIndicator=go.transform;item.PrefabListIndex=0;item.Type=Item.ItemType.WallDecor;
        AccessTools.Field(typeof(Item),"spriteRenderer").SetValue(item,sprite);
        AccessTools.Field(typeof(Item),"boxCollider2D").SetValue(item,box);
        manager.inBuildMode=true;manager.currentItem=item;manager.currentPlacedItems=new System.Collections.Generic.List<SavedItem>();
        Check(Plugin.ChangeOrder(item,1) && sprite.sortingOrder==1,"carried paper moves one level forward");
        Check(Plugin.ChangeOrder(item,1) && sprite.sortingOrder==2,"second forward step crosses board order one");
        Check(Plugin.ChangeOrder(item,-1) && sprite.sortingOrder==1,"backward step changes exactly one level");
        Check(Plugin.ChangeOrder(item,0,true) && sprite.sortingOrder==0,"reset restores standard order");
        Check(Plugin.ChangeOrder(item,-1) && sprite.sortingOrder==-1,"negative order is supported");
        manager.inBuildMode=false;
        Check(!Plugin.ChangeOrder(item,1) && sprite.sortingOrder==-1,"draw-order keys cannot alter furniture outside build mode");manager.inBuildMode=true;
        manager.currentItem=null;
        Check(!Plugin.ChangeOrder(item,1),"draw-order changes require carried selection");manager.currentItem=item;
        item.isPlaced=true;
        Check(!Plugin.ChangeOrder(item,1),"placed furniture cannot be changed without picking up");item.isPlaced=false;
        Plugin.ChangeOrder(item,0,true);Plugin.ChangeOrder(item,1);Plugin.ChangeOrder(item,1);
        var sortObject=new GameObject("order-vanilla-sort");sortObject.SetActive(false);
        var sorter=sortObject.AddComponent<StackableItemPlacedSorting>();item.transform.SetParent(sortObject.transform,true);
        AccessTools.Method(typeof(StackableItemPlacedSorting),"UpdateSorting").Invoke(sorter,null);
        Check(sprite.sortingOrder==2,"vanilla sorting recalculation preserves requested custom order");
        var testHarmony=new Harmony("local.orderprobe.navmesh");
        testHarmony.Patch(AccessTools.Method(typeof(Item),"UpdateNavmeshWithThisItem"),prefix:new HarmonyMethod(typeof(Probe),nameof(SkipNavmesh)));
        var placedEvent=AccessTools.Field(typeof(BuildModeNavigation),"onItemPlaced");
        if(placedEvent.GetValue(null)==null)placedEvent.SetValue(null,Activator.CreateInstance(placedEvent.FieldType));
        try{item.PlaceItem();}finally{testHarmony.UnpatchSelf();}
        var orders=(System.Collections.Generic.Dictionary<SavedItem,int>)AccessTools.Field(typeof(Plugin),"savedOrders").GetValue(plugin);
        Check(item.isPlaced && orders[item.savedItem]==2 && manager.currentPlacedItems.Contains(item.savedItem),"actual PlaceItem commits custom order with new vanilla saved item");
        var saved=item.savedItem;
        var document=OrderStorage.Capture(manager.currentPlacedItems,orders);
        Check(document.Entries.Count==1 && document.Entries[0].Offset==2,"snapshot stores committed order");
        // Actual undo restore calls MarkTaken with the original SavedItem.
        var restoredObject=new GameObject("order-undo-restore");restoredObject.SetActive(false);
        var restoredRenderer=restoredObject.AddComponent<SpriteRenderer>();var restoredBox=restoredObject.AddComponent<BoxCollider2D>();
        var restored=restoredObject.AddComponent<Item>();restored.savedItem=saved;restored.itemIndicator=restored.transform;
        AccessTools.Field(typeof(Item),"spriteRenderer").SetValue(restored,restoredRenderer);
        AccessTools.Field(typeof(Item),"boxCollider2D").SetValue(restored,restoredBox);
        restored.MarkTaken();
        Check(restoredRenderer.sortingOrder==2 && restored.GetComponent<SortingGroup>().sortAtRoot && restored.GetComponent<SortingGroup>().sortingLayerName=="Foreground","MarkTaken restores persisted offset with cross-layer sorting group");
        enabled.Value=false;AccessTools.Method(typeof(Plugin),"LateUpdate").Invoke(plugin,null);
        Check(sprite.sortingOrder==0 && restoredRenderer.sortingOrder==0,"disabling mod restores standard draw order");
        enabled.Value=true;AccessTools.Method(typeof(Plugin),"SynchronizeOrders").Invoke(null,null);
        Check(sprite.sortingOrder==2,"re-enabling mod restores requested order");
        item.isPlaced=false;manager.currentItem=item;
        Plugin.ChangeOrder(item,0,true);
        Check(sprite.sortingOrder==0 && orders[saved]==0,"reset removes saved adjustment for carried item");
        var save=new SaveFile();save.allSavedItems=new System.Collections.Generic.List<SavedItem>{saved};
        var newObjects=Newtonsoft.Json.JsonConvert.DeserializeObject<SaveFile>(Newtonsoft.Json.JsonConvert.SerializeObject(save));
        var matched=OrderStorage.Match(document,newObjects.allSavedItems);
        Check(matched.Count==1 && matched[newObjects.allSavedItems[0]]==2,"saved order matches newly deserialized object identity");
        newObjects.allSavedItems[0].SavedPosition.x+=1;
        Check(OrderStorage.Match(document,newObjects.allSavedItems).Count==0,"changed layout cannot inherit stale order");
        document.Version=99;
        Check(OrderStorage.Match(document,save.allSavedItems).Count==0,"unknown storage version is rejected");document.Version=1;
        document.Entries[0].Offset=1001;
        Check(OrderStorage.Match(document,save.allSavedItems).Count==0,"out-of-range stored offset is rejected");document.Entries[0].Offset=2;
        string folder=Path.Combine(Environment.GetEnvironmentVariable("OVERLAP_SPRITE_FIXTURES"),"order-probe-"+Guid.NewGuid().ToString("N"));
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(save))));
        try
        {
            OrderStorage.Write(folder,bytes,document);
            var loaded=OrderStorage.Read(folder,bytes);
            Check(loaded.Entries[0].Offset==2,"mod-owned sidecar writes and reloads offset");
            Check(OrderStorage.Read(folder,new byte[]{1,2,3})==null,"different save bytes do not load sidecar");
            loaded.Entries.Clear();OrderStorage.Write(folder,bytes,loaded);
            Check(OrderStorage.Read(folder,bytes).Entries.Count==0,"reset atomically replaces existing sidecar");
            File.WriteAllText(Path.Combine(folder,OrderStorage.Hash(bytes)+".json"),"broken json");
            bool malformed=false;try{OrderStorage.Read(folder,bytes);}catch{malformed=true;}
            Check(malformed,"malformed sidecar is reported for caller fallback");
            save.allSavedItems[0].SavedPosition.y+=1;
            bool mismatch=false;try{OrderStorage.Write(folder,bytes,OrderStorage.Capture(save.allSavedItems,orders));}catch(InvalidDataException){mismatch=true;}
            Check(mismatch,"save completion layout mismatch refuses sidecar write");
            string gameFile=Path.Combine(folder,"game.sav");File.WriteAllBytes(gameFile,bytes);
            var store=AccessTools.Method(typeof(Plugin),"StoreAfterSave");
            bool warned=false;Action<string> warning=message=>warned=true;
            var falseTask=(System.Threading.Tasks.Task<bool>)store.Invoke(null,new object[]{System.Threading.Tasks.Task.FromResult(false),gameFile,folder,document,warning});
            Check(!falseTask.GetAwaiter().GetResult() && !warned,"failed vanilla save does not persist mod data");
            var trueTask=(System.Threading.Tasks.Task<bool>)store.Invoke(null,new object[]{System.Threading.Tasks.Task.FromResult(true),gameFile,folder,document,warning});
            Check(trueTask.GetAwaiter().GetResult() && OrderStorage.Read(folder,bytes).Entries.Count==1,"successful save completion stores captured order");
            var missingTask=(System.Threading.Tasks.Task<bool>)store.Invoke(null,new object[]{System.Threading.Tasks.Task.FromResult(true),gameFile+"-missing",folder,document,warning});
            Check(missingTask.GetAwaiter().GetResult() && warned,"sidecar I/O failure preserves successful vanilla save result");
        }
        finally
        {
            if(Directory.Exists(folder)){foreach(var f in Directory.GetFiles(folder))File.Delete(f);Directory.Delete(folder);}
        }
        TestLoadOrder(manager,plugin,enabled,bytes,document);
        manager.currentItem=null;
    }

    private void TestLoadOrder(ItemPlacement placement,Plugin plugin,BepInEx.Configuration.ConfigEntry<bool> enabled,byte[] bytes,OrderStorage.Document doc)
    {
        var singleton=AccessTools.Field(typeof(SaveManager),"<_instance>k__BackingField");
        var prior=singleton.GetValue(null);
        var saveObject=new GameObject("load-order-save-manager");saveObject.SetActive(false);var manager=saveObject.AddComponent<SaveManager>();
        var steamObject=new GameObject("load-order-steam");steamObject.SetActive(false);var steam=steamObject.AddComponent<SteamInitializer>();
        AccessTools.Field(typeof(SaveManager),"steamInitializer").SetValue(manager,steam);
        AccessTools.Field(typeof(SaveManager),"<currentSlotId>k__BackingField").SetValue(manager,2);
        var save=Newtonsoft.Json.JsonConvert.DeserializeObject<SaveFile>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(bytes))));
        AccessTools.Field(typeof(SaveManager),"CurrentSaveFile").SetValue(manager,save);singleton.SetValue(null,manager);
        placement.currentPlacedItems=save.allSavedItems;
        var folderField=AccessTools.Field(typeof(Plugin),"orderFolder");var priorFolder=(string)folderField.GetValue(plugin);
        string folder=Path.Combine(Environment.GetEnvironmentVariable("OVERLAP_SPRITE_FIXTURES"),"order-load-"+Guid.NewGuid().ToString("N"));
        string file=Path.Combine(manager.GetSaveFolderPathByID(),"SaveFile2.sav");
        byte[] original=File.Exists(file)?File.ReadAllBytes(file):null;
        var restoredObject=new GameObject("real-load-order-item");var renderer=restoredObject.AddComponent<SpriteRenderer>();
        var item=restoredObject.AddComponent<Item>();item.isPlaced=true;item.savedItem=save.allSavedItems[0];
        try
        {
            folderField.SetValue(plugin,folder);File.WriteAllBytes(file,bytes);OrderStorage.Write(folder,bytes,doc);
            var load=AccessTools.Method(typeof(Plugin),"LoadDrawOrders");
            load.Invoke(null,new object[]{placement});
            Check(renderer.sortingOrder==2,"game load hook applies sidecar to active loaded furniture");
            item.isPlaced=false;placement.currentItem=item;Plugin.ChangeOrder(item,0,true);item.isPlaced=true;placement.currentItem=null;
            var offsets=(System.Collections.Generic.Dictionary<SavedItem,int>)AccessTools.Field(typeof(Plugin),"savedOrders").GetValue(plugin);
            OrderStorage.Write(folder,bytes,OrderStorage.Capture(save.allSavedItems,offsets));
            load.Invoke(null,new object[]{placement});
            Check(renderer.sortingOrder==0,"reset remains standard after load hook runs again");
            OrderStorage.Write(folder,bytes,doc);
            enabled.Value=false;load.Invoke(null,new object[]{placement});
            Check(renderer.sortingOrder==0,"loading while mod disabled does not change render order");
            enabled.Value=true;AccessTools.Method(typeof(Plugin),"SynchronizeOrders").Invoke(null,null);
            Check(renderer.sortingOrder==2,"loaded stored order reapplies when mod is enabled");
            File.WriteAllText(file,"different save version");load.Invoke(null,new object[]{placement});
            Check(renderer.sortingOrder==0,"unmatched save version safely falls back to standard in game load hook");
        }
        finally
        {
            if(original==null)File.Delete(file);else File.WriteAllBytes(file,original);
            folderField.SetValue(plugin,priorFolder);singleton.SetValue(null,prior);enabled.Value=true;
            if(Directory.Exists(folder)){foreach(var f in Directory.GetFiles(folder))File.Delete(f);Directory.Delete(folder);}
        }
    }

    private void TestSelection(ItemPlacement manager, BepInEx.Configuration.ConfigEntry<bool> enabled)
    {
        var texture = new Texture2D(8,8);
        var pixels = new Color[64]; for(int i=0;i<64;i++) pixels[i]=Color.white;
        texture.SetPixels(pixels); texture.Apply();
        var sprite = Sprite.Create(texture,new Rect(0,0,8,8),new Vector2(.5f,.5f),8);
        var common = new GameObject("probe-common-sort-group");
        common.AddComponent<SortingGroup>().sortingOrder=10;
        Func<string,Item> make = name => {
            var go = new GameObject(name); go.SetActive(false);
            go.transform.SetParent(common.transform,false); go.transform.position=new Vector3(10000,4,0);
            go.AddComponent<SpriteRenderer>().sprite=sprite;
            var result=go.AddComponent<Item>(); result.isPlaced=true; go.SetActive(true);
            return result;
        };
        var a=make("probe-blackboard"); var b=make("probe-herb");
        var navObject=new GameObject("probe-navigation"); navObject.SetActive(false);
        var nav=navObject.AddComponent<BuildModeNavigation>();
        nav.placedItemsList=new System.Collections.Generic.List<Item>{a,b};
        AccessTools.Field(typeof(ItemPlacement),"buildModeNavigation").SetValue(manager,nav);
        manager.currentItem=null;
        enabled.Value=false;
        int vanilla=ItemSpriteHitTester.CompareTopmost(a,b);
        var front=vanilla>0 ? b : a; var back=front==a ? b : a;
        front.GetComponent<SpriteRenderer>().sortingOrder=20;
        back.GetComponent<SpriteRenderer>().sortingOrder=0;
        Vector2 point=new Vector2(10000,4);
        Check(manager.GetGrabbableItemAtPosition(point)==back,"reproduced vanilla shared-group selection mismatch");
        enabled.Value=true;
        Check(ItemSpriteHitTester.CompareTopmost(front,back)>0,"shared-group sprite order chooses visible front");
        Check(ItemSpriteHitTester.CompareTopmost(back,front)<0,"selection comparison is symmetric");
        Check(manager.GetGrabbableItemAtPosition(point)==front,"actual click and hover query selects front item");
        Check(manager.GetGrabbableItemAtPosition(new Vector2(10000.8f,4))==null,"outside sprite remains unselected");
        front.isPlaced=false;
        Check(manager.GetGrabbableItemAtPosition(point)==back,"unplaced front sprite excluded"); front.isPlaced=true;
        manager.currentItem=front;
        Check(manager.GetGrabbableItemAtPosition(point)==back,"carried front item excluded"); manager.currentItem=null;
        var inner=new GameObject("probe-inner-sort-group"); inner.transform.SetParent(common.transform,false);
        var innerSort=inner.AddComponent<SortingGroup>(); innerSort.sortingOrder=-10;
        front.transform.SetParent(inner.transform,true);
        Check(manager.GetGrabbableItemAtPosition(point)==back,"nested group order overrides child sprite order");
        innerSort.sortingOrder=30;
        Check(manager.GetGrabbableItemAtPosition(point)==front,"nested group above sibling selects front");
        innerSort.enabled=false;
        Check(manager.GetGrabbableItemAtPosition(point)==front,"disabled sorting group is ignored");
        front.transform.SetParent(common.transform,true);
        manager.inBuildMode=false;
        Check(manager.GetGrabbableItemAtPosition(point)==back,"outside build mode keeps original selection");
        manager.inBuildMode=true;
        enabled.Value=false;
        Check(manager.GetGrabbableItemAtPosition(point)==back,"config disabled keeps original selection");
        enabled.Value=true;
    }

    private void TestRealDecor(ItemPlacement manager, BepInEx.Configuration.ConfigEntry<bool> enabled)
    {
        string fixtures=Environment.GetEnvironmentVariable("OVERLAP_SPRITE_FIXTURES");
        if(string.IsNullOrEmpty(fixtures)) throw new Exception("Real game sprite fixtures required");
        Func<int,int,int,Vector2,Sprite> load=(id,w,h,pivot)=>{
            var texture=new Texture2D(w,h,TextureFormat.RGBA32,false);
            texture.LoadRawTextureData(File.ReadAllBytes(Path.Combine(fixtures,"sprite-"+id+".rgba"))); texture.Apply();
            return Sprite.Create(texture,new Rect(0,0,w,h),pivot,16,0,SpriteMeshType.FullRect,Vector4.zero,true);
        };
        // Actual game images, dimensions and pivots read from sharedassets2.
        var board=load(1902,104,44,new Vector2(.5f,.5f));
        var herb=load(2124,18,34,new Vector2(.5f,1));
        var group=new GameObject("real-decor-common"); group.AddComponent<SortingGroup>();
        Func<string,Sprite,Vector3,Item> make=(name,sprite,position)=>{
            var go=new GameObject(name);go.SetActive(false);go.transform.SetParent(group.transform,false);
            go.transform.position=position;var renderer=go.AddComponent<SpriteRenderer>();renderer.sprite=sprite;
            renderer.sortingOrder=1;renderer.spriteSortPoint=SpriteSortPoint.Pivot;
            var item=go.AddComponent<Item>();item.isPlaced=true;go.SetActive(true);return item;
        };
        var b=make("real-WallMenuSign",board,new Vector3(10000,10,0));
        var h=make("real-WallDecor_24_Dried_Herb",herb,new Vector3(9997.1f,11.1f,0));
        var nav=(BuildModeNavigation)AccessTools.Field(typeof(ItemPlacement),"buildModeNavigation").GetValue(manager);
        nav.placedItemsList=new System.Collections.Generic.List<Item>{b,h};manager.currentItem=null;
        Vector2 overlap=new Vector2(9997,10);
        Check(ItemSpriteHitTester.IsPointOverSprite(h.GetComponent<SpriteRenderer>(),overlap),"real herb overlap point is inside sprite hit shape");
        enabled.Value=false;
        Check(manager.GetGrabbableItemAtPosition(overlap)==b,"real top-pivot herb loses to centre-pivot board in vanilla");
        enabled.Value=true;
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"real herb selected within board overlap");
        nav.placedItemsList.Reverse();
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"real herb priority independent of list order");
        Check(manager.GetGrabbableItemAtPosition(new Vector2(10000,10))==b,"exposed board remains selectable");
        b.GetComponent<SpriteRenderer>().sortingOrder=2;
        Check(manager.GetGrabbableItemAtPosition(overlap)==b,"explicit higher drawing order overrides small-decoration preference");
        b.GetComponent<SpriteRenderer>().sortingOrder=1;
        h.transform.SetParent(null,true);b.transform.SetParent(null,true);
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"real herb priority also works without shared sorting group");
        enabled.Value=false;
        Check(manager.GetGrabbableItemAtPosition(overlap)==b,"disabling mod restores original top-pivot selection");
        enabled.Value=true;
        var secondHerb=make("real-herb-created-later",herb,h.transform.position+new Vector3(.1f,0,0));
        secondHerb.transform.SetParent(null,true);
        nav.placedItemsList=new System.Collections.Generic.List<Item>{b,h,secondHerb};
        enabled.Value=false;
        Check(ItemSpriteHitTester.CompareTopmost(h,secondHerb)>0,"vanilla equal herb tie picks older item");
        enabled.Value=true;
        Check(manager.GetGrabbableItemAtPosition(overlap)==secondHerb,"equal herb tie selects later renderer");
        nav.placedItemsList.Reverse();
        Check(manager.GetGrabbableItemAtPosition(overlap)==secondHerb,"equal herb tie independent of candidate order");
        manager.currentItem=secondHerb;
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"carrying later herb exposes earlier herb");manager.currentItem=null;
        secondHerb.GetComponent<SpriteRenderer>().sortingOrder=0;
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"explicit render priority still beats creation order");
        secondHerb.GetComponent<SpriteRenderer>().sortingOrder=1;
        Check(ItemSpriteHitTester.CompareTopmost(h,secondHerb)<0 && ItemSpriteHitTester.CompareTopmost(secondHerb,h)>0,"equal herb renderer tie is symmetric");
        // The real hover loop accesses .material for EVERY furniture renderer.
        // This instantiates one material per item, even before any highlight.
        AccessTools.Method(typeof(BuildModeNavigation),"TurnOffHighlights").Invoke(nav,null);
        Check(h.GetComponent<SpriteRenderer>().sharedMaterial!=secondHerb.GetComponent<SpriteRenderer>().sharedMaterial,"real hover loop creates distinct material instances");
        Check(manager.GetGrabbableItemAtPosition(overlap)==secondHerb,"equal herbs remain selectable after real highlight preparation");
        var yellow=make("real-yellow-herb",load(2943,18,34,new Vector2(.5f,1)),h.transform.position);
        var brown=make("real-brown-herb",load(1766,17,34,new Vector2(.5f,1)),h.transform.position);
        yellow.transform.SetParent(null,true);brown.transform.SetParent(null,true);
        nav.placedItemsList=new System.Collections.Generic.List<Item>{h,yellow,brown};
        AccessTools.Method(typeof(BuildModeNavigation),"TurnOffHighlights").Invoke(nav,null);
        Check(manager.GetGrabbableItemAtPosition(overlap)==brown,"real three-herb hover selects brown placed last");
        manager.currentItem=brown;
        Check(manager.GetGrabbableItemAtPosition(overlap)==yellow,"real three-herb hover next selects yellow");
        brown.isPlaced=false;manager.currentItem=yellow;
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"real three-herb hover finally selects green");
        brown.isPlaced=true;manager.currentItem=null;nav.placedItemsList.Reverse();
        Check(manager.GetGrabbableItemAtPosition(overlap)==brown,"real three-herb result independent of navigation list order");
        enabled.Value=false;
        Check(manager.GetGrabbableItemAtPosition(overlap)==h,"vanilla reproduces green-first selection for three herbs");
        enabled.Value=true;
        h.transform.SetParent(group.transform,true);yellow.transform.SetParent(group.transform,true);brown.transform.SetParent(group.transform,true);
        h.transform.position+=new Vector3(0,-.3f,0);yellow.transform.position+=new Vector3(0,-.1f,0);brown.transform.position+=new Vector3(0,.3f,0);
        Check(manager.GetGrabbableItemAtPosition(overlap)==brown,"shared wall group ignores individual herb height");
        h.transform.position+=new Vector3(0,.8f,0);
        Check(manager.GetGrabbableItemAtPosition(overlap)==brown,"moving green herb higher does not reorder shared-group selection");
        manager.currentItem=brown;
        Check(manager.GetGrabbableItemAtPosition(overlap)==yellow,"shared wall group exposes yellow regardless of individual height");manager.currentItem=null;
        h.transform.SetParent(null,true);yellow.transform.SetParent(null,true);brown.transform.SetParent(null,true);
        Check(manager.GetGrabbableItemAtPosition(overlap)==yellow,"outside sorting group individual distance still applies");
    }
    private void TestOrderHints(ItemPlacement manager)
    {
        var plugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.Id].Instance;
        Check(Plugin.OrderHintLabel(false,true,KeyCode.UpArrow,KeyCode.DownArrow,KeyCode.Backspace)=="[↑] 手前へ    [↓] 奥へ    [Backspace] 標準に戻す","Japanese keyboard hint follows configured keys");
        Check(Plugin.OrderHintLabel(true,true,KeyCode.Home,KeyCode.Home,KeyCode.Home).Contains("[←] 標準に戻す"),"controller hint describes reset direction");
        Check(Plugin.OrderHintLabel(false,false,KeyCode.PageUp,KeyCode.PageDown,KeyCode.Home).Contains("[PgUp] Bring forward"),"English hint and default binding labels");
        var root=new GameObject("hint-test-canvas",typeof(RectTransform),typeof(Canvas));
        var keys=new GameObject("BuildModeInstructionalButtonsKeyboardGroup",typeof(RectTransform));keys.transform.SetParent(root.transform,false);
        var pads=new GameObject("BuildModeInstructionalButtonsControllerGroup",typeof(RectTransform));pads.transform.SetParent(root.transform,false);
        var templateObject=new GameObject("Description",typeof(RectTransform),typeof(CanvasRenderer),typeof(TMPro.TextMeshProUGUI));templateObject.transform.SetParent(keys.transform,false);
        var template=templateObject.GetComponent<TMPro.TMP_Text>();template.fontSize=24;template.color=Color.black;
        var fonts=Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();Check(fonts.Length>0,"game fonts available for hints");template.font=fonts[0];
        AccessTools.Method(typeof(Plugin),"CreateOrderHints").Invoke(plugin,new object[]{template,keys.transform});
        var own=(GameObject)AccessTools.Field(typeof(Plugin),"orderHintRoot").GetValue(plugin);
        var text=(TMPro.TMP_Text)AccessTools.Field(typeof(Plugin),"orderHintText").GetValue(plugin);
        Check(own.transform.parent==root.transform && text.font==template.font,"hint owns separate UI and reuses game font");
        bool blocks=false;foreach(var graphic in own.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))blocks|=graphic.raycastTarget;
        Check(!blocks,"hint never intercepts mouse input");
        var oldScheme=TagTextReplacementManager.instance;
        var schemeObject=new GameObject("hint-test-scheme");schemeObject.SetActive(false);var scheme=schemeObject.AddComponent<TagTextReplacementManager>();TagTextReplacementManager.instance=scheme;
        var itemObject=new GameObject("hint-carried-item");itemObject.SetActive(false);manager.currentItem=itemObject.AddComponent<Item>();manager.inBuildMode=true;
        bool paused=PauseMenu.GameIsPaused;PauseMenu.GameIsPaused=false;
        var update=AccessTools.Method(typeof(Plugin),"UpdateOrderHints");
        scheme.detectedScheme="Mouse And Keyboard";update.Invoke(plugin,null);
        Check(own.activeSelf && !text.text.Contains("D-Pad"),"keyboard mode shows keyboard hints while carrying");
        scheme.detectedScheme="Gamepad Controller";update.Invoke(plugin,null);
        Check(own.activeSelf && text.text == Plugin.OrderHintLabel(true,text.text.Contains("手前へ"),KeyCode.Home,KeyCode.Home,KeyCode.Home) && !text.text.Contains("D-Pad"),"last used controller scheme switches hints");
        pads.SetActive(false);update.Invoke(plugin,null);Check(!own.activeSelf,"hidden vanilla hints also hide mod hints");pads.SetActive(true);
        PauseMenu.GameIsPaused=true;update.Invoke(plugin,null);Check(!own.activeSelf,"pause hides order hint");PauseMenu.GameIsPaused=false;
        manager.currentItem=null;update.Invoke(plugin,null);Check(!own.activeSelf,"inventory browsing hides order hint");
        manager.currentItem=itemObject.GetComponent<Item>();manager.inBuildMode=false;update.Invoke(plugin,null);Check(!own.activeSelf,"outside build mode hides order hint");
        manager.inBuildMode=true;manager.currentItem=null;PauseMenu.GameIsPaused=paused;TagTextReplacementManager.instance=oldScheme;
        AccessTools.Method(typeof(Plugin),"RemoveOrderHints").Invoke(plugin,null);
        Destroy(root);Destroy(schemeObject);Destroy(itemObject);
    }
    private void TestCleanup(ItemPlacement manager, Item carried, SpriteRenderer sprite, Plugin plugin)
    {
        var go = new GameObject("retired-order-test");
        go.SetActive(false);
        var deadItem = go.AddComponent<Item>();
        go.AddComponent<SpriteRenderer>();
        var saved = new SavedItem();
        deadItem.savedItem = saved;
        manager.inBuildMode = true;
        manager.currentItem = deadItem;
        Plugin.ChangeOrder(deadItem, 1);
        var states = (System.Collections.IDictionary)AccessTools.Field(typeof(Plugin), "orderStates").GetValue(plugin);
        DestroyImmediate(go);
        AccessTools.Method(typeof(Plugin), "SynchronizeOrders").Invoke(null, null);
        Check(!states.Contains(deadItem), "destroyed furniture releases its runtime draw-order state");
        var savedOrders = (System.Collections.Generic.Dictionary<SavedItem, int>)AccessTools.Field(typeof(Plugin), "savedOrders").GetValue(plugin);
        Check(savedOrders.ContainsKey(saved), "retiring renderer retains saved order for undo restoration");

        manager.currentItem = carried;
        carried.isPlaced = false;
        Plugin.ChangeOrder(carried, 0, true);
        Plugin.ChangeOrder(carried, 1);
        var group = sprite.GetComponent<SortingGroup>();
        var canvas = new GameObject("cleanup-test-canvas", typeof(RectTransform));
        var keys = new GameObject("BuildModeInstructionalButtonsKeyboardGroup", typeof(RectTransform));
        keys.transform.SetParent(canvas.transform, false);
        var pads = new GameObject("BuildModeInstructionalButtonsControllerGroup", typeof(RectTransform));
        pads.transform.SetParent(canvas.transform, false);
        var templateObject = new GameObject("Description", typeof(RectTransform), typeof(CanvasRenderer), typeof(TMPro.TextMeshProUGUI));
        templateObject.transform.SetParent(keys.transform, false);
        var template = templateObject.GetComponent<TMPro.TMP_Text>();
        template.font = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>()[0];
        AccessTools.Method(typeof(Plugin), "CreateOrderHints").Invoke(plugin, new object[] {template, keys.transform});
        var hint = (GameObject)AccessTools.Field(typeof(Plugin), "orderHintRoot").GetValue(plugin);
        AccessTools.Method(typeof(Plugin), "InstallOrderHints").Invoke(plugin, null);
        AccessTools.Method(typeof(Plugin), "InstallOrderHints").Invoke(plugin, null);
        var shutdown = AccessTools.Method(typeof(Plugin), "Shutdown");
        shutdown.Invoke(plugin, null);
        Check(sprite.sortingOrder == 0 && !group.enabled, "shutdown restores sprite order and disables owned group immediately");
        Check(!hint.activeSelf && AccessTools.Field(typeof(Plugin), "orderHintRoot").GetValue(plugin) == null,
            "shutdown hides UI immediately and releases its references");
        bool patchRemains = false;
        foreach (var method in Harmony.GetAllPatchedMethods())
            if (Harmony.GetPatchInfo(method).Owners.Contains(Plugin.Id)) patchRemains = true;
        Check(!patchRemains, "shutdown removes every patch owned by this Mod");
        AccessTools.Method(typeof(Plugin), "LateUpdate").Invoke(plugin, null);
        AccessTools.Method(typeof(Plugin), "RefreshOrderHints").Invoke(plugin, null);
        Check(!Plugin.ChangeOrder(carried, 1) && !hint.activeSelf, "callbacks remain inert after shutdown or startup rollback");
        shutdown.Invoke(plugin, null);
        Check(states.Count == 0, "repeated cleanup is harmless and releases runtime state");
        Destroy(canvas);
    }
}
