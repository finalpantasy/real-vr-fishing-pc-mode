#if TRAILER
using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // Trailer builds only (compiled with TRAILER): plays the game on its own through the mod's virtual keys and mouse,
    // and records each shot as a numbered JPEG sequence at a locked 30 fps (game time steps exactly 1/30 s per frame,
    // so slow frames never stutter the footage). Never part of a release build.
    partial class Director : MonoBehaviour
    {
        const int Fps = 30;
        string root, clipDir;
        int frame, pending;
        bool recording;

        Driver D => Driver.I;

        void Start()
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RealVRFishingTrailer", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(root);
            Application.runInBackground = true;
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            Win.Scripted = true;
            StartCoroutine(Capture());
            StartCoroutine(RunAll());
            Log("recording to " + root);
        }

        static void Log(string s) => Plugin.Log.LogInfo("director: " + s);

        IEnumerator RunAll()
        {
            IEnumerator shots = null;
            try { shots = Shots(); }
            catch (Exception e) { Log("shots failed to start: " + e); }
            if (shots != null) yield return Guard(shots);
            StopClip();
            while (pending > 0) yield return null;
            Log("done");
            Win.Scripted = false;
            Application.Quit();
        }

        // runs a shot list but logs and stops instead of throwing, so a failed step never leaves keys held
        IEnumerator Guard(IEnumerator e)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!e.MoveNext()) break;
                    cur = e.Current;
                }
                catch (Exception ex) { Log("shot error: " + ex); break; }
                if (cur is IEnumerator nested) yield return Guard(nested);
                else yield return cur;
            }
            Win.Virtual.Clear();
            Win.VirtualMouse = Vector2.zero;
            Win.VirtualWheel = 0f;
        }

        // ---- recording ----

        void StartClip(string name)
        {
            StopClip();
            clipDir = Path.Combine(root, name);
            Directory.CreateDirectory(clipDir);
            frame = 0;
            Time.captureFramerate = Fps;
            recording = true;
            Log("clip " + name);
        }

        void StopClip()
        {
            if (!recording) return;
            recording = false;
            Time.captureFramerate = 0;
            Log($"clip done, {frame} frames");
        }

        IEnumerator Capture()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (!recording) continue;
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                byte[] jpg = tex.EncodeToJPG(93);
                Destroy(tex);
                string path = Path.Combine(clipDir, $"f{++frame:000000}.jpg");
                Interlocked.Increment(ref pending);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { File.WriteAllBytes(path, jpg); }
                    finally { Interlocked.Decrement(ref pending); }
                });
            }
        }

        // ---- input helpers (game time; under capture that is exactly 1/30 s per frame) ----

        static IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        static IEnumerator Hold(int vk, float seconds)
        {
            Win.Virtual.Add(vk);
            yield return Wait(seconds);
            Win.Virtual.Remove(vk);
        }

        static IEnumerator Tap(int vk)
        {
            Win.Virtual.Add(vk);
            yield return null;
            yield return null;
            Win.Virtual.Remove(vk);
            yield return null;
        }

        static IEnumerator WaitUntil(Func<bool> done, float timeout, string what)
        {
            for (float t = 0f; t < timeout; t += Time.deltaTime)
            {
                bool ok = false;
                try { ok = done(); } catch { }
                if (ok) yield break;
                yield return null;
            }
            Log("timed out waiting for " + what);
        }

        // eased turn of the head to an absolute yaw / pitch
        IEnumerator Look(float yaw, float pitch, float seconds)
        {
            float y0 = D.Yaw, p0 = D.Pitch;
            float dy = Mathf.DeltaAngle(y0, yaw);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / seconds);
                D.Yaw = y0 + dy * k;
                D.Pitch = Mathf.Lerp(p0, pitch, k);
                yield return null;
            }
            D.Yaw = y0 + dy;
            D.Pitch = pitch;
        }

        // middle-mouse drag, spread over the given time
        IEnumerator Drag(Vector2 pixels, float seconds)
        {
            Win.Virtual.Add(0x04);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                Win.VirtualMouse = pixels * (Time.deltaTime / seconds);
                yield return null;
            }
            Win.Virtual.Remove(0x04);
        }
    }
}
#endif
