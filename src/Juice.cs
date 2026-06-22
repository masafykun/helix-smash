using UnityEngine;

// Reusable "game feel" helper. Auto-bootstraps (no setup needed).
// Procedural BGM + SFX + visual pops + screen shake — all self-contained (no asset files).
// Games should call: Juice.Score(pos)/Juice.Hit()/Juice.Pop(pos,color)/Juice.Blip(freq) on events.
public static class Juice
{
    static AudioSource _sfx, _bgm;
    static Camera _cam;
    static Vector3 _appliedOffset;
    static float _shake;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("__Juice");
        Object.DontDestroyOnLoad(go);
        _sfx = go.AddComponent<AudioSource>(); _sfx.playOnAwake = false;
        _bgm = go.AddComponent<AudioSource>(); _bgm.loop = true; _bgm.volume = 0.30f; _bgm.playOnAwake = false;
        _bgm.clip = BuildBgm();
        _bgm.Play();                       // WebGL: resumes on first user input automatically
        go.AddComponent<JuiceRunner>();
    }

    public static void Blip(float freq, float dur = 0.08f, float vol = 0.4f)
    {
        if (_sfx != null) _sfx.PlayOneShot(Tone(freq, dur), vol);
    }
    public static void Score() { Blip(880f, 0.07f, 0.45f); Blip(1320f, 0.06f, 0.3f); }
    public static void Hit()   { Blip(150f, 0.2f, 0.5f); Shake(0.25f); }
    public static void Lose()  { Blip(110f, 0.45f, 0.5f); Shake(0.4f); }
    public static void Score(Vector3 pos) { Score(); Pop(pos, new Color(1f, 0.85f, 0.2f)); }

    public static void Shake(float amount) { _shake = Mathf.Max(_shake, amount); }

    public static void Pop(Vector3 worldPos, Color color, int count = 10)
    {
        var sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        for (int i = 0; i < count; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var col = q.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
            q.transform.position = worldPos;
            q.transform.localScale = Vector3.one * 0.18f;
            var mr = q.GetComponent<MeshRenderer>();
            mr.material = new Material(sh) { color = color };
            float ang = i * Mathf.PI * 2f / count;
            var vel = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * Random.Range(2f, 4f);
            q.AddComponent<JuiceParticle>().Init(vel, mr);
        }
    }

    static AudioClip Tone(float freq, float dur)
    {
        int sr = 44100; int n = Mathf.Max(1, (int)(sr * dur));
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 12f);
        }
        var c = AudioClip.Create("sfx", n, 1, sr, false); c.SetData(data, 0); return c;
    }

    // ---- POP background track: I-V-vi-IV in C major, 128 BPM, looping ----
    // Layers: sine chord pad + square bass (8th pulse) + triangle arpeggio lead
    //         + 4-on-the-floor kick + hi-hats + snare on 2 & 4.
    static AudioClip BuildBgm()
    {
        int sr = 44100;
        const float bpm = 128f;
        float beat = 60f / bpm;
        int bars = 4, bpb = 4;
        int total = (int)(sr * beat * bars * bpb);
        var data = new float[total];

        float[][] chord = {
            new float[]{261.63f, 329.63f, 392.00f},  // C  major
            new float[]{196.00f, 246.94f, 293.66f},  // G  major
            new float[]{220.00f, 261.63f, 329.63f},  // A  minor
            new float[]{174.61f, 220.00f, 261.63f},  // F  major
        };
        float[] bassF = { 65.41f, 98.00f, 110.00f, 87.31f };  // C2 G2 A2 F2
        float eighth = beat / 2f;

        for (int bar = 0; bar < bars; bar++)
        {
            var ch = chord[bar];
            int barStart = (int)(sr * beat * bar * bpb);

            // soft sustained chord pad
            for (int n = 0; n < 3; n++)
                AddTone(data, sr, barStart, beat * bpb, ch[n], 0.09f, 0, 0.5f);

            // 8th-note bass + arpeggio lead + hats
            for (int e = 0; e < bpb * 2; e++)
            {
                int es = barStart + (int)(sr * eighth * e);
                AddTone(data, sr, es, eighth * 0.92f, bassF[bar], 0.34f, 1, 7f);   // square bass
                AddTone(data, sr, es, eighth * 0.85f, ch[e % 3] * 2f, 0.17f, 3, 6f); // triangle arp (octave up)
                AddNoise(data, sr, es, 0.045f, (e % 2 == 1) ? 0.11f : 0.06f, 65f);   // hat
            }

            // drums on the beat
            for (int b = 0; b < bpb; b++)
            {
                int bs = barStart + (int)(sr * beat * b);
                AddKick(data, sr, bs, 0.6f);
                if (b % 2 == 1) { AddNoise(data, sr, bs, 0.13f, 0.24f, 26f); AddTone(data, sr, bs, 0.09f, 190f, 0.14f, 0, 32f); } // snare 2&4
            }
        }

        for (int i = 0; i < total; i++) data[i] = Mathf.Clamp(data[i] * 0.85f, -0.97f, 0.97f);
        var c = AudioClip.Create("bgm", total, 1, sr, false); c.SetData(data, 0); return c;
    }

    // wave: 0 sine, 1 square, 2 saw, 3 triangle
    static void AddTone(float[] buf, int sr, int start, float dur, float freq, float amp, int wave, float decay)
    {
        int len = (int)(sr * dur);
        for (int i = 0; i < len && start + i < buf.Length; i++)
        {
            float t = (float)i / sr;
            float ph = freq * t;
            float s;
            if (wave == 1)      s = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * ph));
            else if (wave == 2) s = 2f * (ph - Mathf.Floor(ph + 0.5f));
            else if (wave == 3) s = Mathf.Abs(4f * (ph - Mathf.Floor(ph + 0.5f))) - 1f;
            else                s = Mathf.Sin(2f * Mathf.PI * ph);
            buf[start + i] += s * Mathf.Exp(-t * decay) * amp;
        }
    }

    static void AddKick(float[] buf, int sr, int start, float amp)
    {
        int len = (int)(sr * 0.18f);
        for (int i = 0; i < len && start + i < buf.Length; i++)
        {
            float t = (float)i / sr;
            float freq = Mathf.Lerp(130f, 45f, Mathf.Clamp01(t / 0.09f));
            buf[start + i] += Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 16f) * amp;
        }
    }

    static void AddNoise(float[] buf, int sr, int start, float dur, float amp, float decay)
    {
        int len = (int)(sr * dur);
        for (int i = 0; i < len && start + i < buf.Length; i++)
        {
            float t = (float)i / sr;
            buf[start + i] += (Random.value * 2f - 1f) * Mathf.Exp(-t * decay) * amp;
        }
    }

    class JuiceRunner : MonoBehaviour
    {
        void LateUpdate()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            _cam.transform.localPosition -= _appliedOffset;     // undo last frame's shake (no drift)
            if (_shake > 0.001f)
            {
                _appliedOffset = (Vector3)(Random.insideUnitCircle * _shake);
                _shake = Mathf.Lerp(_shake, 0f, Time.deltaTime * 8f);
            }
            else _appliedOffset = Vector3.zero;
            _cam.transform.localPosition += _appliedOffset;
        }
    }

    class JuiceParticle : MonoBehaviour
    {
        Vector3 _vel; MeshRenderer _mr; float _age, _life = 0.5f;
        public void Init(Vector3 vel, MeshRenderer mr) { _vel = vel; _mr = mr; }
        void Update()
        {
            _age += Time.deltaTime;
            transform.position += _vel * Time.deltaTime;
            transform.localScale *= 1f + Time.deltaTime * 1.5f;
            if (_mr != null) { var c = _mr.material.color; c.a = Mathf.Clamp01(1f - _age / _life); _mr.material.color = c; }
            if (_age >= _life) Destroy(gameObject);
        }
    }
}
