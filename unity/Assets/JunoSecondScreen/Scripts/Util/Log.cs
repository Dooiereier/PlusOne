namespace JunoSecondScreen.Util
{
    using UnityEngine;

    /// <summary>
    /// Prefixed logging so the mod's messages are easy to find in Juno's player log.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[SecondScreen] ";

        public static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}
