using System;
using System.IO;
using System.Collections;
using UnityEngine;

public class AutoShot : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__AutoShot");
        go.AddComponent<AutoShot>();
        DontDestroyOnLoad(go);
        Debug.Log("AutoShot: bootstrap fired");
    }

    void Start()
    {
        string path = null; float delay = 4f; bool quit = false;
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "-autoshot" && i + 1 < a.Length) path = a[i + 1];
            else if (a[i] == "-autoshotdelay" && i + 1 < a.Length) float.TryParse(a[i + 1], out delay);
            else if (a[i] == "-quitafter") quit = true;
        }
        Debug.Log("AutoShot: path=" + path + " delay=" + delay + " quit=" + quit);
        if (!string.IsNullOrEmpty(path)) StartCoroutine(Cap(path, delay, quit));
    }

    IEnumerator Cap(string path, float delay, bool quit)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            int w = 1280, h = 720;
            var cam = Camera.main;
            if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0];
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var rt = new RenderTexture(w, h, 24);
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            if (cam != null)
            {
                var prev = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prev;
            }
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("AutoShot: wrote -> " + path);
        }
        catch (Exception e) { Debug.LogError("AutoShot: capture failed: " + e); }
        yield return new WaitForSeconds(0.5f);
        if (quit) Application.Quit();
    }
}
