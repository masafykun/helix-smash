using System.Collections.Generic;
using UnityEngine;

// HELIX SMASH — one-finger helix drop (Helix Jump-style). Rotate the spiral tower so the
// bouncing ball threads the GAP in each ring and drops to the next level. Chain 3+ clean
// drops in a single fall and the ball ignites into a FIREBALL that SMASHES straight through
// platforms (and danger arcs) for a cascade of score. Land on a dark DANGER arc while not on
// fire = wipeout. Endless descent; the deeper you go the tighter the gaps and the more danger.
//
// Built 100% from code so it renders reliably in WebGL with engine-code stripping disabled:
//   * NO Rigidbody / colliders. The ball is pure Transform-driven (custom gravity/bounce
//     integration) and every hit-test is a discrete slot lookup at the ring it reaches.
//   * Rings are procedural annular-sector meshes (ArcMesh) parented under one rotating tower.
//   * Particles/feedback go through Juice (CreatePrimitive-based, strip-safe). Coexists with
//     Juice (sfx/bgm) and AutoShot.
public class HelixSmash : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__HelixSmash");
        go.AddComponent<HelixSmash>();
        DontDestroyOnLoad(go);
    }

    // ---- geometry constants ----
    const int   NSEG = 12;                 // slots per ring (30° each)
    const float SLOT = 360f / NSEG;
    const float INNER = 0.62f, OUTER = 2.6f, THICK = 0.34f;
    const float LEVEL_GAP = 2.35f;         // vertical spacing between rings
    const float BALL_R = 0.42f;            // ball radius
    const float BALL_Z = -2.05f;           // ball sits on the front (camera side) of the rings
    const float HORIZON = 30f;             // generate rings this far below the ball
    const float CULL_ABOVE = 6f;           // remove rings this far above the ball
    const float BOUNCE_V = 7.4f;           // bounce launch speed
    const float GRAV = 22f;                // gravity
    const float TERMINAL = -19f;           // max fall speed (keeps cascades readable)
    const float FIRE_DUR = 1.05f;          // fireball duration after a 3-chain
    const float REQ_HALFW = 3.25f;         // keep the whole tower visible horizontally

    enum Cell { Gap, Normal, Danger }

    class Ring
    {
        public int depth;                  // 0 = top, increases downward
        public float y;                    // local Y of this ring's mid-plane
        public Cell[] cell = new Cell[NSEG];
        public Transform root;             // parent of this ring's slot meshes (child of tower)
        public readonly List<Mesh> meshes = new List<Mesh>();  // owned procedural meshes (freed on removal)
        public bool alive = true;
        public int gapCenterSlot;          // for the attract autopilot
        public float TopY => y + THICK * 0.5f;
    }

    // ---- scene refs ----
    Transform tower;       // rotates around Y; parents pillar + every ring
    Transform pillar;
    Transform ball, ballVisual;
    Transform cam; Camera camComp;
    TextMesh hudScore, hudDepth, comboText, banner, dbg;

    readonly List<Ring> rings = new List<Ring>();
    int targetIdx;         // index in `rings` of the ring the ball is currently testing against
    int genIndex;          // next ring depth to generate
    float genY;            // local Y of the last generated ring

    // ---- run state ----
    enum State { Playing, Over }
    State state = State.Playing;
    float towerAngle, angVel;              // tower rotation + keyboard inertia
    float ballY, velY;                     // ball vertical position/velocity
    int score, best, depth, combo, bestDepth;
    int fallChain;                         // rings passed since last bounce (drives fire)
    float fireTime;                        // >0 => fireball / smash mode
    float squash;                          // ball squash on bounce
    float comboFlash, bannerTimer;
    int milestone;                         // last LEVEL milestone reached
    bool attract = true;

    // HUD layout adapts to aspect (Unity vertical FOV is fixed => portrait is much narrower)
    float hudScale = 1f, halfH = 3f, halfW = 4.6f;
    const float HUD_Z = 6.5f, FOV = 52f;
    float camDist = 8.5f;

    // pointer-drag steering
    bool ptrActive; float lastPtrX;

    // debug
    bool showDbg; int dbgSmash;

    // palette (vivid, cool->warm cycling for depth gradient)
    static readonly Color[] PAL = {
        new Color(0.16f,0.62f,0.96f), new Color(0.20f,0.80f,0.74f),
        new Color(0.55f,0.78f,0.30f), new Color(0.98f,0.74f,0.22f),
        new Color(0.97f,0.45f,0.34f), new Color(0.85f,0.34f,0.78f),
    };
    Material dangerMat, dangerEdgeMat, ballMat, pillarMat;

    // ===================================================================== boot
    void Start()
    {
        // strip the default scene camera/light so we don't double-light or shoot the wrong camera
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("helix_best", 0);
        bestDepth = PlayerPrefs.GetInt("helix_bestdepth", 0);

        BuildEnvironment();
        BuildTower();
        BuildBall();
        BuildCamera();
        BuildHud();

        ballY = 1.8f;                // start above ring 0 (at y=0) so the first drop is a bounce
        genIndex = 0; genY = 0f; targetIdx = 0;
        while (genY > ballY - HORIZON) GenerateRing();
        UpdateCamera(0f, true);
        RefreshHud();
    }

    // ===================================================================== materials / mesh
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.35f, bool emissive = false, float emi = 0.55f, bool noCull = false)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (noCull && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // double-sided => winding-proof
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emi);
        }
        return m;
    }

    // cache normal-slot materials (palette index + shade) so each ring doesn't allocate fresh ones
    readonly Dictionary<int, Material> normalMatCache = new Dictionary<int, Material>();
    Material NormalMat(int palIdx, bool dark)
    {
        int key = palIdx * 2 + (dark ? 1 : 0);
        if (!normalMatCache.TryGetValue(key, out var m))
        {
            Color baseCol = PAL[palIdx] * (dark ? 0.82f : 1f);
            m = Mat(baseCol, 0.05f, 0.4f, true, 0.18f, true);
            normalMatCache[key] = m;
        }
        return m;
    }

    // Annular-sector slab built around the Y axis: radius [INNER,OUTER], extruded ±THICK/2,
    // spanning [startDeg, startDeg+sweepDeg]. Vertices live around the axis so a slot mesh just
    // needs localPosition Y; rotating the parent tower spins it correctly.
    static Mesh ArcMesh(float startDeg, float sweepDeg)
    {
        int seg = Mathf.Max(2, Mathf.RoundToInt(sweepDeg / 7f));
        float a0 = startDeg * Mathf.Deg2Rad, da = sweepDeg * Mathf.Deg2Rad / seg;
        float hy = THICK * 0.5f;
        var v = new List<Vector3>(); var tri = new List<int>();
        // ring of 4 verts per angular step: outerTop, innerTop, outerBot, innerBot
        for (int i = 0; i <= seg; i++)
        {
            float a = a0 + da * i; float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
            v.Add(new Vector3(cx * OUTER, hy, cz * OUTER));
            v.Add(new Vector3(cx * INNER, hy, cz * INNER));
            v.Add(new Vector3(cx * OUTER, -hy, cz * OUTER));
            v.Add(new Vector3(cx * INNER, -hy, cz * INNER));
        }
        for (int i = 0; i < seg; i++)
        {
            int b = i * 4, n = b + 4;
            // top face (oT,iT,iT+1,oT+1)
            tri.Add(b + 0); tri.Add(b + 1); tri.Add(n + 1); tri.Add(b + 0); tri.Add(n + 1); tri.Add(n + 0);
            // bottom face (reversed)
            tri.Add(b + 2); tri.Add(n + 2); tri.Add(n + 3); tri.Add(b + 2); tri.Add(n + 3); tri.Add(b + 3);
            // outer wall (oT,oB,oB+1,oT+1)
            tri.Add(b + 0); tri.Add(n + 0); tri.Add(n + 2); tri.Add(b + 0); tri.Add(n + 2); tri.Add(b + 2);
            // inner wall
            tri.Add(b + 1); tri.Add(b + 3); tri.Add(n + 3); tri.Add(b + 1); tri.Add(n + 3); tri.Add(n + 1);
        }
        // two end caps (oT,iT,iB,oB) at i=0 and i=seg
        AddCap(tri, 0, true);
        AddCap(tri, seg * 4, false);
        var m = new Mesh();
        m.SetVertices(v); m.SetTriangles(tri, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }
    static void AddCap(List<int> tri, int b, bool front)
    {
        // verts at index b: oT(0) iT(1) oB(2) iB(3)
        if (front) { tri.Add(b + 0); tri.Add(b + 2); tri.Add(b + 3); tri.Add(b + 0); tri.Add(b + 3); tri.Add(b + 1); }
        else       { tri.Add(b + 0); tri.Add(b + 3); tri.Add(b + 2); tri.Add(b + 0); tri.Add(b + 1); tri.Add(b + 3); }
    }

    GameObject SlotObj(Mesh m, Transform parent, Material mat)
    {
        var g = new GameObject("slot");
        g.transform.SetParent(parent, false);
        g.AddComponent<MeshFilter>().sharedMesh = m;
        g.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return g;
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.97f, 0.9f);
        sun.intensity = 1.25f;
        sun.transform.rotation = Quaternion.Euler(48f, -28f, 0f);
        sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.42f, 0.50f, 0.66f);
        RenderSettings.ambientEquatorColor = new Color(0.30f, 0.34f, 0.46f);
        RenderSettings.ambientGroundColor  = new Color(0.12f, 0.14f, 0.20f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.10f, 0.12f, 0.20f);
        RenderSettings.fogStartDistance = 14f;
        RenderSettings.fogEndDistance = 34f;

        dangerMat     = Mat(new Color(0.10f, 0.11f, 0.16f), 0.1f, 0.25f, false, 0.55f, true);
        dangerEdgeMat = Mat(new Color(1f, 0.18f, 0.22f), 0f, 0.5f, true, 0.9f, true);
        ballMat       = Mat(new Color(1f, 0.93f, 0.30f), 0.1f, 0.7f, true, 0.55f);
        pillarMat     = Mat(new Color(0.20f, 0.22f, 0.30f), 0.2f, 0.4f);
    }

    void BuildTower()
    {
        tower = new GameObject("Tower").transform;
        // central pillar (radially symmetric => its rotation is invisible). Follows the ball in Y.
        var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        var col = p.GetComponent<Collider>(); if (col) Destroy(col);
        p.transform.SetParent(tower, false);
        p.transform.localScale = new Vector3(INNER * 1.7f, 60f, INNER * 1.7f);
        p.GetComponent<Renderer>().sharedMaterial = pillarMat;
        pillar = p.transform;
    }

    void BuildBall()
    {
        ball = new GameObject("Ball").transform;
        ballVisual = new GameObject("BallVisual").transform;
        ballVisual.SetParent(ball, false);
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var col = s.GetComponent<Collider>(); if (col) Destroy(col);
        s.transform.SetParent(ballVisual, false);
        s.transform.localScale = Vector3.one * (BALL_R * 2f);
        s.GetComponent<Renderer>().sharedMaterial = ballMat;
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.07f, 0.08f, 0.14f);
        camComp.fieldOfView = FOV;
        camComp.farClipPlane = 80f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudScore = MakeText(0.085f, Color.white, TextAnchor.UpperLeft);
        hudDepth = MakeText(0.060f, new Color(0.8f, 0.9f, 1f), TextAnchor.UpperRight);
        comboText = MakeText(0.12f, new Color(1f, 0.9f, 0.3f), TextAnchor.MiddleCenter);
        banner = MakeText(0.15f, Color.white, TextAnchor.MiddleCenter);
        dbg = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; banner.text = "";
        AdjustHud(); RefreshHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;
        hudScore.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudScore.characterSize = 0.085f * hudScale;
        hudDepth.transform.localPosition = new Vector3( ix, iy, HUD_Z); hudDepth.characterSize = 0.060f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -iy * 0.55f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        comboText.transform.localPosition = new Vector3(0, halfH * 0.46f, HUD_Z);
        if (comboFlash <= 0f) comboText.characterSize = 0.12f * hudScale;
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudDepth) hudDepth.text = "DEPTH  " + depth + "\nBEST  " + best;
    }

    // ===================================================================== generation
    void GenerateRing()
    {
        int d = genIndex;
        var r = new Ring { depth = d, y = genY };
        r.root = new GameObject("ring" + d).transform;
        r.root.SetParent(tower, false);
        r.root.localPosition = new Vector3(0, r.y, 0);

        // difficulty: gap narrows, danger arcs appear deeper
        int gapLen = Mathf.Clamp(4 - d / 14, 2, 4);          // slots forming the gap (60°..120°)
        int dangerCount = Mathf.Clamp((d - 5) / 7, 0, 4);
        int gapStart = Random.Range(0, NSEG);
        for (int i = 0; i < NSEG; i++) r.cell[i] = Cell.Normal;
        for (int i = 0; i < gapLen; i++) r.cell[(gapStart + i) % NSEG] = Cell.Gap;
        r.gapCenterSlot = (gapStart + gapLen / 2) % NSEG;

        // scatter danger arcs among the solid (non-gap) slots
        var solid = new List<int>();
        for (int i = 0; i < NSEG; i++) if (r.cell[i] == Cell.Normal) solid.Add(i);
        for (int k = 0; k < dangerCount && solid.Count > 1; k++)
        {
            int pick = Random.Range(0, solid.Count);
            r.cell[solid[pick]] = Cell.Danger; solid.RemoveAt(pick);
        }

        // build slot meshes: merge consecutive same-kind solid slots into one arc for a clean look
        int s = 0;
        while (s < NSEG)
        {
            if (r.cell[s] == Cell.Gap) { s++; continue; }
            Cell kind = r.cell[s];
            int run = 1;
            while (s + run < NSEG && r.cell[s + run] == kind) run++;
            float start = s * SLOT + 1.2f;                    // tiny pad => faceted seams
            float sweep = run * SLOT - 2.4f;
            var mesh = ArcMesh(start, sweep); r.meshes.Add(mesh);
            if (kind == Cell.Danger)
            {
                SlotObj(mesh, r.root, dangerMat);
                var edge = ArcMesh(start, sweep); r.meshes.Add(edge);
                SlotObj(edge, r.root, dangerEdgeMat).transform.localScale = new Vector3(1f, 1.06f, 1f);
            }
            else
            {
                // alternate light/dark shade per slot-run for readability (cached materials)
                SlotObj(mesh, r.root, NormalMat(d % PAL.Length, (s % 2) != 0));
            }
            s += run;
        }
        rings.Add(r);
        genIndex++; genY -= LEVEL_GAP;
    }

    // free a ring's GameObjects AND its procedural meshes (Unity won't auto-free meshes => OOM)
    void DestroyRing(Ring r)
    {
        if (r == null) return;
        if (r.root) Destroy(r.root.gameObject);
        for (int i = 0; i < r.meshes.Count; i++) if (r.meshes[i]) Destroy(r.meshes[i]);
        r.meshes.Clear();
    }

    void CullAbove()
    {
        float cutoff = ballY + CULL_ABOVE;
        while (rings.Count > 0 && rings[0].y > cutoff && targetIdx > 0)
        {
            DestroyRing(rings[0]);
            rings.RemoveAt(0); targetIdx--;
        }
    }

    int SlotUnderBall()
    {
        float local = Mathf.Repeat(270f - towerAngle, 360f);   // ball world angle is fixed at 270°
        return Mathf.FloorToInt(local / SLOT) % NSEG;
    }

    // ===================================================================== input
    void GatherInput()
    {
        // keyboard: eased angular velocity
        float key = Input.GetAxisRaw("Horizontal");
        float targetAng = key * 230f;
        angVel = Mathf.MoveTowards(angVel, targetAng, 1400f * Time.deltaTime);
        float delta = angVel * Time.deltaTime;

        // pointer / touch: 1:1 drag rotate (classic helix swipe)
        bool down = Input.GetMouseButton(0) || Input.touchCount > 0;
        float px = Input.touchCount > 0 ? Input.GetTouch(0).position.x : Input.mousePosition.x;
        if (down)
        {
            if (!ptrActive) { ptrActive = true; lastPtrX = px; }
            float dx = px - lastPtrX; lastPtrX = px;
            delta += (dx / Mathf.Max(1f, Screen.width)) * 360f * 0.9f;
        }
        else ptrActive = false;

        if (Mathf.Abs(key) > 0.01f || Mathf.Abs(delta) > 0.001f && down || Input.anyKeyDown) attract = false;

        if (attract) delta = AttractDelta();
        towerAngle = Mathf.Repeat(towerAngle + delta, 360f);
        tower.localRotation = Quaternion.Euler(0, towerAngle, 0);
    }

    // gently auto-aligns the next ring's gap under the ball (keeps the in-engine demo alive).
    // After ATTRACT_MAX it parks on a SOLID slot so the idle demo bounces in place instead of
    // descending forever (the in-engine screenshot/demo only needs ~a few seconds of motion).
    const int ATTRACT_MAX = 14;
    float AttractDelta()
    {
        if (targetIdx >= rings.Count) return 60f * Time.deltaTime;
        var r = rings[targetIdx];
        int slot = r.gapCenterSlot;
        if (depth >= ATTRACT_MAX)
        {
            slot = -1;
            for (int i = 0; i < NSEG; i++) if (r.cell[i] == Cell.Normal) { slot = i; break; }
            if (slot < 0) slot = r.gapCenterSlot;   // (shouldn't happen) fall back to the gap
        }
        float wantLocal = (slot + 0.5f) * SLOT;
        float wantTower = Mathf.Repeat(270f - wantLocal, 360f);
        float diff = Mathf.DeltaAngle(towerAngle, wantTower);
        return Mathf.Clamp(diff, -180f, 180f) * Mathf.Min(1f, 6f * Time.deltaTime);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        if (state == State.Over)
        {
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began))
            { attract = false; Restart(); }
            UpdateBallVisual(dt);
            UpdateCamera(dt, false);
            return;
        }

        GatherInput();

        if (fireTime > 0f) fireTime -= dt;

        // ---- vertical integration (terminal-capped so fast drops stay readable) ----
        velY -= GRAV * dt;
        if (velY < TERMINAL) velY = TERMINAL;
        ballY += velY * dt;

        // ---- collisions (while-loop handles multi-ring frames safely) ----
        if (velY <= 0f) ResolveDescent();

        ball.position = new Vector3(0, ballY, BALL_Z);

        Generate();
        CullAbove();
        UpdateBallVisual(dt);
        UpdateCamera(dt, false);
        TickHud(dt);
        if (showDbg) UpdateDbg();
    }

    void Generate()
    {
        while (genY > ballY - HORIZON) GenerateRing();
    }

    void ResolveDescent()
    {
        float ballBottom = ballY - BALL_R;
        int guard = 0;
        while (targetIdx < rings.Count && guard++ < 40)
        {
            var r = rings[targetIdx];
            if (r == null || !r.alive) { targetIdx++; continue; }
            if (ballBottom > r.TopY) break;            // not reached this ring yet
            int slot = SlotUnderBall();
            Cell c = r.cell[slot];

            if (fireTime > 0f && c != Cell.Gap) { Smash(r); targetIdx++; continue; }
            if (c == Cell.Gap)    { PassGap(r); targetIdx++; continue; }
            if (c == Cell.Danger) { Die(r); return; }
            Bounce(r); return;                          // solid -> bounce, stop here
        }
    }

    // ===================================================================== events
    void PassGap(Ring r)
    {
        depth++; combo++; fallChain++;
        int gain = 10 + combo * 5;
        score += gain;
        Juice.Blip(540f + Mathf.Min(combo, 14) * 70f, 0.06f, 0.4f);   // ascending chime
        Juice.Pop(new Vector3(0, r.y, BALL_Z), new Color(0.7f, 0.95f, 1f), 5);
        if (combo >= 2) { comboText.text = "x" + combo; comboFlash = 1f; FlashCombo(); }
        if (fallChain == 3) IgniteFire();
        Milestones();
        RefreshHud();
    }

    void IgniteFire()
    {
        fireTime = FIRE_DUR;
        Juice.Blip(220f, 0.18f, 0.5f); Juice.Blip(330f, 0.18f, 0.4f);
        Juice.Shake(0.18f);
        FloatText("FIRE!", new Color(1f, 0.55f, 0.15f));
    }

    void Smash(Ring r)
    {
        depth++; combo++; fallChain++;
        score += 25 + combo * 4;
        dbgSmash++;
        Color cc = PAL[r.depth % PAL.Length];
        Juice.Pop(new Vector3(0, r.y, BALL_Z), cc, 12);
        Juice.Pop(new Vector3(0, r.y, BALL_Z + 0.6f), new Color(1f, 0.6f, 0.15f), 8);
        Juice.Blip(300f + Mathf.Min(combo, 16) * 30f, 0.07f, 0.45f);
        Juice.Shake(0.16f);
        r.alive = false;
        DestroyRing(r);
        // NOTE: do NOT refresh fireTime here — fire is a bounded ~1s burst from the 3-chain,
        // otherwise a perfect run smashes forever and free-falls into an unreadable blur.
        comboText.text = "x" + combo; comboFlash = 1f; FlashCombo();
        Milestones();
        RefreshHud();
    }

    void Bounce(Ring r)
    {
        velY = BOUNCE_V;
        squash = 1f;
        if (combo >= 3) FloatText("COMBO " + combo, new Color(0.8f, 0.9f, 1f));
        combo = 0; fallChain = 0; comboFlash = 0f; comboText.text = "";
        Juice.Blip(330f + Random.Range(-25f, 45f), 0.06f, 0.32f);   // plop, varied pitch
        Juice.Pop(new Vector3(0, r.TopY, BALL_Z), PAL[r.depth % PAL.Length] * 1.1f, 5);
    }

    void Die(Ring r)
    {
        state = State.Over;
        velY = 0f;
        Juice.Lose(); Juice.Shake(0.55f);
        Juice.Pop(ball.position, new Color(1f, 0.25f, 0.2f), 18);
        bool nb = score > best, nd = depth > bestDepth;
        if (nb) { best = score; PlayerPrefs.SetInt("helix_best", best); }
        if (nd) { bestDepth = depth; PlayerPrefs.SetInt("helix_bestdepth", bestDepth); }
        PlayerPrefs.Save();
        comboText.text = "";
        banner.transform.localPosition = new Vector3(0, 0, HUD_Z);
        banner.characterSize = 0.088f * hudScale;
        banner.color = Color.white;
        banner.text = "WIPEOUT\n\nSCORE  " + score + "\nDEPTH  " + depth + (nb ? "\nNEW BEST!" : "\nBEST  " + best) + "\n\nTAP TO DROP AGAIN";
        bannerTimer = 9999f;
        RefreshHud();
    }

    void Milestones()
    {
        int m = depth / 10;
        if (m > milestone)
        {
            milestone = m;
            score += 100;
            FloatText("LEVEL " + (m + 1), new Color(0.5f, 1f, 0.7f));
            Juice.Score(ball.position + Vector3.up * 0.5f);
        }
    }

    void Restart()
    {
        foreach (var r in rings) DestroyRing(r);
        rings.Clear();
        state = State.Playing;
        towerAngle = 0; angVel = 0; tower.localRotation = Quaternion.identity;
        ballY = 1.8f; velY = 0f; squash = 0f; fireTime = 0f;
        score = 0; depth = 0; combo = 0; fallChain = 0; milestone = 0;
        genIndex = 0; genY = 0f; targetIdx = 0;
        banner.text = ""; banner.color = Color.white; comboText.text = "";
        while (genY > ballY - HORIZON) GenerateRing();
        UpdateCamera(0f, true);
        RefreshHud();
    }

    // ===================================================================== visual / camera
    void FlashCombo()
    {
        comboText.color = combo >= 8 ? new Color(1f, 0.35f, 0.5f)
                        : combo >= 5 ? new Color(1f, 0.6f, 0.2f)
                                     : new Color(1f, 0.9f, 0.3f);
    }

    void FloatText(string s, Color c)
    {
        banner.transform.localPosition = new Vector3(0, -halfH * 0.4f, HUD_Z);
        banner.characterSize = 0.14f * hudScale;
        banner.text = s; banner.color = c; bannerTimer = 0.7f;
    }

    void UpdateBallVisual(float dt)
    {
        squash = Mathf.MoveTowards(squash, 0f, dt * 4.5f);
        // squash on impact + slight stretch when falling fast
        float fall = Mathf.Clamp01(-velY / 12f);
        float sy = (1f - squash * 0.45f) * (1f + fall * 0.25f);
        float sxz = (1f + squash * 0.35f) * (1f - fall * 0.12f);
        ballVisual.localScale = new Vector3(sxz, sy, sxz);
        // fire tint
        var col = fireTime > 0f ? new Color(1f, 0.5f, 0.12f) : new Color(1f, 0.93f, 0.30f);
        if (ballMat.HasProperty("_BaseColor")) ballMat.SetColor("_BaseColor", col);
        if (ballMat.HasProperty("_Color")) ballMat.SetColor("_Color", col);
        if (ballMat.HasProperty("_EmissionColor")) ballMat.SetColor("_EmissionColor", col * (fireTime > 0f ? 1.1f : 0.55f));
    }

    void UpdateCamera(float dt, bool snap)
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float t = Mathf.Tan(FOV * 0.5f * Mathf.Deg2Rad);
        // pull the camera back far enough that the whole tower fits the (narrow) portrait width
        camDist = Mathf.Max(8.5f, REQ_HALFW / Mathf.Max(0.05f, aspect * t));
        Vector3 want = new Vector3(0, ballY + 3.6f, -camDist);
        Vector3 look = new Vector3(0, ballY - 1.6f, 0);
        if (snap) cam.position = want;
        else cam.position = Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-9f * dt));
        cam.rotation = Quaternion.LookRotation(look - cam.position, Vector3.up);
        camComp.fieldOfView = FOV;
        if (pillar) pillar.position = new Vector3(0, ballY - 6f, 0);
        AdjustHud();
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.2f;
            comboText.characterSize = 0.12f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.6f);
        }
        if (bannerTimer > 0f && bannerTimer < 9000f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { banner.text = ""; banner.color = Color.white; }
        }
    }

    void UpdateDbg()
    {
        dbg.text = string.Format(
            "state {0}  fire {1:0.00}\nballY {2:0.0} velY {3:0.0} sq {4:0.0}\nang {5:0} slot {6}  tIdx {7}\nscore {8} depth {9} combo {10} chain {11}\nrings {12} smash {13}  fps {14:0}\nasp {15:0.00} camD {16:0.0} hW {17:0.0}",
            state, fireTime, ballY, velY, squash, towerAngle, SlotUnderBall(), targetIdx,
            score, depth, combo, fallChain, rings.Count, dbgSmash,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), camComp.aspect, camDist, halfW);
    }
}
