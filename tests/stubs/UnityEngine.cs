// Stand-in declarations for the Unity engine assemblies, used only to type check
// the mod outside Unity. No behaviour, just the surface the mod compiles against.
namespace UnityEngine
{
    using System;
    using System.Collections;

    public class Object
    {
        public string name;
        public static void DontDestroyOnLoad(Object target) { }
        public static void Destroy(Object obj) { }
        public static implicit operator bool(Object exists) => true;
    }

    public class Component : Object { public GameObject gameObject; public Transform transform; }
    public class Behaviour : Component { public bool enabled; }
    public class Transform : Component { }
    public class Texture : Object { public int width; public int height; public FilterMode filterMode; }
    public class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipChain) { }
        public bool ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps) => true;
        public void Apply() { }
        public byte[] GetRawTextureData() => null;
    }

    public class RenderTexture : Texture
    {
        public RenderTexture(int width, int height, int depth, RenderTextureFormat format) { }
        public static RenderTexture active { get; set; }
        public bool Create() => true;
        public void Release() { }
    }

    public class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : Component => null;
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null;
        public void StopAllCoroutines() { }
    }

    public sealed class Coroutine { }
    public sealed class WaitForEndOfFrame { }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f;
        public static float Clamp01(float value) => value;
        public static int Clamp(int value, int min, int max) => value;
        public static float Clamp(float value, float min, float max) => value;
        public static int RoundToInt(float value) => (int)value;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
    }

    public static class Time
    {
        public static float time => 0f;
        public static float deltaTime => 0f;
        public static float unscaledTime => 0f;
    }

    public static class Application
    {
        public static string persistentDataPath => string.Empty;
        public static string dataPath => string.Empty;
        public static RuntimePlatform platform => RuntimePlatform.WindowsPlayer;
    }

    public static class Screen { public static int width => 0; public static int height => 0; }

    public static class SystemInfo { public static bool supportsAsyncGPUReadback => true; }

    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture dest) { }
        public static void Blit(Texture source, RenderTexture dest, Vector2 scale, Vector2 offset) { }
    }

    public static class ScreenCapture
    {
        public static void CaptureScreenshotIntoRenderTexture(RenderTexture renderTexture) { }
    }

    public enum RuntimePlatform { WindowsPlayer, OSXEditor, OSXPlayer, LinuxPlayer, WindowsEditor }
    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum RenderTextureFormat { ARGB32 }
    public enum TextureFormat { RGBA32 }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Quaternion { public float x, y, z, w; }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    }

    public struct Color { public float r, g, b, a; }
}

namespace UnityEngine.Rendering
{
    using System;
    using Unity.Collections;

    public struct AsyncGPUReadbackRequest
    {
        public bool hasError => false;
        public int width => 0;
        public int height => 0;
        public NativeArray<T> GetData<T>(int mipIndex = 0) where T : struct => default(NativeArray<T>);
    }

    public static class AsyncGPUReadback
    {
        public static AsyncGPUReadbackRequest Request(Texture src, int mipIndex, TextureFormat dstFormat, Action<AsyncGPUReadbackRequest> callback) => new AsyncGPUReadbackRequest();
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R8G8B8A8_UNorm }
}

namespace UnityEngine
{
    using UnityEngine.Experimental.Rendering;

    public static class ImageConversion
    {
        public static byte[] EncodeArrayToJPG(System.Array array, GraphicsFormat format, uint width, uint height, uint rowBytes = 0, int quality = 75) => null;
    }
}
