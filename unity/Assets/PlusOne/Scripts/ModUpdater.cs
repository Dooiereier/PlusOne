namespace PlusOne
{
    using System;
    using System.Collections;
    using System.Text.RegularExpressions;
    using PlusOne.Util;
    using ModApi.Common;
    using ModApi.Ui;
    using UnityEngine;
    using UnityEngine.Networking;

    /// <summary>
    /// Generic mod update-check + reminder popup system. Ported from this
    /// author's Vizzy Exporter mod (itself ported from SatelliteTorifune/
    /// Volken2's ModUpdater.cs), adapted for PlusOne's own GitHub repo.
    ///
    /// What it does:
    ///   1. Reads the local version (Mod.Instance.ModInfo.Version).
    ///   2. Fetches the latest version via two channels:
    ///      channel 1: GitHub Releases API's tag_name (primary);
    ///      channel 2: a raw version.txt fallback (used if the API is rate-limited, offline,
    ///      or no release exists yet).
    ///   3. If the latest version is newer than the local one, and the player hasn't
    ///      dismissed that version already, shows a three-button dialog once the main menu
    ///      loads (Download / Later / Don't remind me).
    ///   4. "Don't remind me" is remembered per-version via PlayerPrefs, so a *newer* release
    ///      still prompts again later.
    ///
    /// Non-blocking: all network waiting happens in a coroutine (UnityWebRequest + an overall
    /// watchdog timeout), so the main thread is never blocked. Worst case (offline/rate
    /// limited), it gives up after 15s rather than hanging.
    ///
    /// Checked at most once per game session (guarded by the static _startedThisSession flag).
    /// </summary>
    public class ModUpdater
    {
        // Channel 1: GitHub Releases API - returns JSON, "tag_name" is the latest version
        // (e.g. "0.8" or "v0.9.1" - a leading "v" is stripped).
        // To release: create a GitHub release, tag it (e.g. "0.9"), attach the .sr2-mod file.
        public const string LatestVersionUrl =
            "https://api.github.com/repos/Dooiereier/PlusOne/releases/latest";

        // Opened when the player clicks "Download" - the repo's GitHub releases page, since
        // that's where players actually get the mod from.
        public const string DownloadUrl =
            "https://github.com/Dooiereier/PlusOne/releases/latest";

        // Channel 2 (fallback): a raw version.txt at the repo root, containing just the
        // version number (e.g. "0.8"). Used automatically if channel 1 fails. Points at
        // main - remember to keep version.txt in sync there on release. Leave empty ("") to
        // disable this fallback entirely.
        public const string VersionFileUrl =
            "https://raw.githubusercontent.com/Dooiereier/PlusOne/main/version.txt";

        // Where the player's "don't remind me" choice is stored, keyed to this mod so it
        // doesn't collide with any other mod's own update reminder.
        private const string SkippedVersionPrefKey = "PlusOne.UpdateReminder.SkippedVersion";

        // At most one check per game session, shared across instances.
        private static bool _startedThisSession;

        private Version _localVersion;
        private ModUpdaterHost _host;

        /// <summary>
        /// Starts one update check (a no-op if already run this session). Call this at the
        /// end of the mod's OnModInitialized, after the local version is available.
        ///
        /// This method itself never makes or waits on a network request - it just registers
        /// a coroutine host and returns immediately. The actual HTTP request happens in a
        /// coroutine (UnityWebRequest, polled once per frame, with an overall watchdog), so
        /// the main thread stays free the whole time; it gives up after 15s even if the
        /// network is down.
        /// </summary>
        public void CheckForUpdate()
        {
            try
            {
                if (_startedThisSession) return;
                _startedThisSession = true;

                _localVersion = Mod.Instance.ModInfo.Version;
                if (_localVersion == null)
                {
                    Log.Info("Update check skipped - ModVersion is null.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(LatestVersionUrl))
                {
                    Log.Info($"Update check - LatestVersionUrl not configured, current version {_localVersion}.");
                    return;
                }

                if (_host == null)
                {
                    var go = new GameObject("PlusOneUpdateReminder");
                    GameObject.DontDestroyOnLoad(go);
                    _host = go.AddComponent<ModUpdaterHost>();
                    _host.Owner = this;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Update check failed to start: {ex}");
            }
        }

        /// <summary>
        /// Coroutine body: fetch the latest version via both channels, compare against the
        /// local version, and - if newer - wait for the main menu and show the dialog.
        /// </summary>
        public IEnumerator FetchRoutine()
        {
            // Overall watchdog: no matter how slow/broken the network is, the whole "wait
            // for the latest version" process must end by this deadline
            // (Time.realtimeSinceStartup, unaffected by pause/hitches). UnityWebRequest's own
            // timeout only covers a single request - this covers the whole attempt, so
            // waiting always has an upper bound and never hangs indefinitely.
            const float totalTimeoutSeconds = 15f;
            var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;

            Version latest = null;
            var got = false;

            // Channel 1: GitHub Releases API.
            yield return TryFetchVersion(LatestVersionUrl, deadline, v => { latest = v; got = true; }, () => { });

            // Channel 2: fall back to version.txt if the API failed (rate limit, offline, no
            // release yet) and the watchdog deadline hasn't already passed.
            if (!got && Time.realtimeSinceStartup < deadline)
            {
                Log.Info("Update check - API channel unavailable, falling back to version.txt.");
                yield return TryFetchVersion(VersionFileUrl, deadline, v => { latest = v; got = true; }, () => { });
            }

            if (!got || latest == null)
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Log.Info($"Update check - timed out waiting for a version number (>{totalTimeoutSeconds}s), skipping this session.");
                else
                    Log.Info("Update check - both channels failed, skipping this session.");
                yield break;
            }

            Log.Info($"Update check - current {_localVersion}, latest available {latest}.");
            if (latest <= _localVersion) yield break; // already up to date

            // Already dismissed this exact version (or older)?
            if (Version.TryParse(PlayerPrefs.GetString(SkippedVersionPrefKey, ""), out var skipped)
                && latest <= skipped)
            {
                Log.Info($"Update check - version {latest} was already dismissed by the player.");
                yield break;
            }

            // Wait for the main menu so this doesn't interrupt an active flight/design scene.
            while (Game.Instance == null || !Game.Instance.SceneManager.InMenuScene)
            {
                yield return null;
            }

            ShowUpdateDialog(latest);
        }

        /// <summary>
        /// Fetches the given URL and parses a version out of it. Calls onSuccess(version) on
        /// success, onFail() otherwise. deadline is the shared watchdog deadline
        /// (Time.realtimeSinceStartup) - past it, the request is aborted and treated as a
        /// failure. Shared download+parse primitive for both channels.
        /// </summary>
        private IEnumerator TryFetchVersion(string url, float deadline, Action<Version> onSuccess, Action onFail)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                // Both GitHub URLs (API and raw) require a non-empty User-Agent, or they
                // return 403.
                request.SetRequestHeader("User-Agent", "PlusOneModUpdater/1.0");

                // Purely asynchronous waiting - the main thread stays completely free. Polled
                // once per frame (rather than `yield return request.SendWebRequest()`
                // directly) specifically so the watchdog deadline gets checked every frame
                // too - past it, the request is aborted, turning "wait for the version
                // number" into a process that's guaranteed to have an upper bound and
                // guaranteed to end.
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                    {
                        request.Abort(); // released along with `using` right after
                        Log.Info($"Update check - overall wait timed out, aborted request to {url}.");
                        onFail?.Invoke();
                        yield break;
                    }
                    yield return null; // one cheap bool comparison per frame
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Info($"Update check - request to {url} failed: {request.error}");
                    onFail?.Invoke();
                    yield break;
                }

                if (TryParseLatestVersion(request.downloadHandler.text, out var version))
                {
                    onSuccess?.Invoke(version);
                }
                else
                {
                    Log.Info($"Update check - couldn't parse a version from {url}, raw text: {request.downloadHandler.text}");
                    onFail?.Invoke();
                }
            }
        }

        /// <summary>
        /// Parses a version number out of a response body. Handles: plain text ("0.8" /
        /// "v0.8.1"), GitHub Releases JSON ("tag_name":"v0.8.1"), or {"version":"0.8"}.
        /// </summary>
        private static bool TryParseLatestVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();

            // JSON: prefer the "tag_name" (GitHub Releases) or "version" field's value.
            if (s.StartsWith("{") || s.StartsWith("["))
            {
                var match = Regex.Match(s, "\"(?:tag_name|version)\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) s = match.Groups[1].Value.Trim();
            }

            // Strip a leading v/V, then take the first "number.number..." looking span
            // (resilient to surrounding HTML/whitespace noise).
            s = Regex.Replace(s, "^[vV]", "");
            s = Regex.Match(s, @"\d+(?:\.\d+){1,3}").Value;

            return Version.TryParse(s, out version);
        }

        /// <summary>
        /// Shows the three-button dialog: Download / Later / Don't remind me.
        /// </summary>
        private void ShowUpdateDialog(Version latest)
        {
            try
            {
                if (Game.Instance?.UserInterface == null) return;

                var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.ThreeButtons, null, true);
                if (dialog == null) return;

                dialog.MessageText = string.Format(
                    "A new version of PlusOne is available.\n\nLatest version: {0}\nCurrent version: {1}",
                    latest, _localVersion);
                dialog.OkayButtonText = "Download";
                dialog.MiddleButtonText = "Later";
                dialog.CancelButtonText = "Don't remind me";

                dialog.OkayClicked += d =>
                {
                    d.Close();
                    if (!string.IsNullOrEmpty(DownloadUrl))
                        Application.OpenURL(DownloadUrl);
                    else
                        Log.Info("DownloadUrl is not configured.");
                };

                // Later: just closes - still reminds again next launch.
                dialog.MiddleClicked += d => d.Close();

                // Don't remind me: remembers the skipped version; won't prompt again until a
                // newer one is released.
                dialog.CancelClicked += d =>
                {
                    PlayerPrefs.SetString(SkippedVersionPrefKey, latest.ToString());
                    PlayerPrefs.Save();
                    d.Close();
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Update reminder dialog failed: {ex}");
            }
        }

        /// <summary>
        /// Coroutine host: gives the non-MonoBehaviour ModUpdater a place to run its
        /// UnityWebRequest coroutine.
        /// </summary>
        public class ModUpdaterHost : MonoBehaviour
        {
            public ModUpdater Owner;

            private void Start()
            {
                if (Owner != null)
                {
                    StartCoroutine(Owner.FetchRoutine());
                }
            }

            // Safety net: if this host object is ever destroyed (an unexpected scene switch,
            // reload, etc.), stop the coroutine immediately so no network wait is left
            // dangling.
            private void OnDestroy()
            {
                StopAllCoroutines();
            }
        }
    }
}
