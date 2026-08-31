namespace JunoSecondScreen
{
#if UNITY_EDITOR
    using System;
    using System.IO;
    using System.Threading;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// Editor helpers for copying the built mod package into Juno's live mods folder.
    /// </summary>
    internal static class DeployTools
    {
        private const string ModFileName = "Second Screen.sr2-mod";
        private const string ModInfoFileName = "Second Screen.sr2-mod-info";
        private const int CopyRetryCount = 8;
        private const int CopyRetryDelayMs = 300;

        [MenuItem("Tools/Second Screen/Deploy Built Mod To Juno")]
        private static void DeployBuiltMod()
        {
            string buildOutput = Path.Combine(ProjectRoot(), "ModAssetBundles");
            string builtMod = Path.Combine(buildOutput, ModFileName);
            string modsDirectory = JunoModsDirectory();

            if (!File.Exists(builtMod))
            {
                EditorUtility.DisplayDialog(
                    "Second Screen",
                    "No built mod package was found.\n\n" +
                    $"Build the mod first so that {Path.Combine("ModAssetBundles", ModFileName)} exists, then run this again.",
                    "OK");
                return;
            }

            Directory.CreateDirectory(modsDirectory);

            try
            {
                CopyWithRetries(builtMod, Path.Combine(modsDirectory, ModFileName));

                string builtInfo = Path.Combine(buildOutput, ModInfoFileName);
                if (File.Exists(builtInfo))
                {
                    CopyWithRetries(builtInfo, Path.Combine(modsDirectory, ModInfoFileName));
                }

                Debug.Log($"Second Screen deployed to {modsDirectory}");
                EditorUtility.DisplayDialog("Second Screen", "Deployed the mod into Juno's mods folder.", "OK");
            }
            catch (IOException ex)
            {
                Debug.LogError($"Second Screen deploy failed: {ex}");
                EditorUtility.DisplayDialog(
                    "Second Screen",
                    "Could not overwrite the installed mod because the file is in use.\n\n" +
                    "Close Juno: New Origins, then run this again.",
                    "OK");
            }
        }

        [MenuItem("Tools/Second Screen/Open Juno Mods Folder")]
        private static void OpenJunoModsFolder()
        {
            string modsDirectory = JunoModsDirectory();
            Directory.CreateDirectory(modsDirectory);
            EditorUtility.RevealInFinder(modsDirectory);
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Could not determine the Unity project root.");
        }

        private static string JunoModsDirectory()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Library",
                    "Application Support",
                    "Jundroo",
                    "SimpleRockets 2",
                    "Mods");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Low",
                "Jundroo",
                "SimpleRockets 2",
                "Mods");
        }

        private static void CopyWithRetries(string source, string destination)
        {
            Exception lastException = null;
            for (int attempt = 0; attempt < CopyRetryCount; attempt++)
            {
                try
                {
                    File.Copy(source, destination, overwrite: true);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                }

                Thread.Sleep(CopyRetryDelayMs);
            }

            throw new IOException($"Failed to copy {source} to {destination}.", lastException);
        }
    }
#endif
}
