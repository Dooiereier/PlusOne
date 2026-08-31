// Minimal stand-ins so the protocol code can be exercised outside Unity.
namespace UnityEngine
{
    using System;

    public static class Debug
    {
        public static void Log(string message) => Console.WriteLine("[log] " + message);

        public static void LogWarning(string message) => Console.WriteLine("[warn] " + message);

        public static void LogError(string message) => Console.WriteLine("[error] " + message);
    }
}
