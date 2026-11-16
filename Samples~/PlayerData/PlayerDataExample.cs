// GAME CLIENT CODE. No API key anywhere: a game build must never hold one.
// Saves and loads the signed-in player's settings with SplatterVault player data, and reads the game's public data.
//
// Set up first: game console > your game > Player data tab: switch player data on; Sign-in tab: switch on
// "Guest accounts" (this sample signs in as a guest when there is no stored sign-in).
//
// Remember: whatever a player writes is untrusted (a modified client can write anything to its player store).
// Keep currency, XP and unlocks in the readonly store and change them from your backend.
using System;
using System.Threading.Tasks;
using SplatterVault.Core;
using SplatterVault.Players;
using UnityEngine;

public class PlayerDataExample : MonoBehaviour
{
    [Serializable]
    public class GameSettings
    {
        public int fov = 90;
        public bool invertY;
        public float volume = 0.8f;
    }

    [Tooltip("Your game id, from the Sign-in tab of the game console")]
    [SerializeField] private int gameId = 123;

    private SvPlayers players;
    private GameSettings settings = new GameSettings();
    private long settingsVersion; // 0: never saved ("must not exist yet" for ifVersion)
    private long matchesPlayed;
    private string messageOfTheDay = "";
    private string status = "Starting...";

    private async void Start()
    {
        players = new SvPlayers(gameId);
        try
        {
            if (!await players.Auth.RestoreAsync())
            {
                // Nothing restored: this sample plays as the device's guest. A real game shows its sign-in screen here,
                // above all when GetLastSignInMethodAsync() names a platform (the player expects that account, not a guest).
                await players.Auth.SignInAsGuestAsync();
            }
            await LoadAsync();
            await LoadNewsAsync();
            status = "Loaded";
        }
        catch (SvApiException e)
        {
            status = "Could not load (" + (e.ErrorCode ?? "HTTP " + e.Status) + ")";
        }
    }

    private async Task LoadAsync()
    {
        var data = await players.Data.GetMineAsync(new[] { "settings", "matchesPlayed" });
        settings = data.Player.Get("settings", new GameSettings());
        settingsVersion = data.Player.VersionOf("settings");
        matchesPlayed = data.Player.Get<long>("matchesPlayed");
    }

    // The game's public data, as an ETag-cached read: asking again answers 304 and the kept copy when nothing
    // changed. Call it when it matters (a menu opens), never on a timer: a player has 60 requests a minute.
    private async Task LoadNewsAsync()
    {
        var news = await players.Data.GetGameDataAsync(new[] { "motd" });
        messageOfTheDay = news.Data.Get("motd", "");
    }

    /// <summary>
    /// Saves with ifVersion: a newer save from another device is never overwritten. The SDK sends it again by itself
    /// only after an answer that proves nothing was saved (408, 429, 503); after a lost answer it may have been saved
    /// (IsOutcomeUnknown), so the save is never just sent again: what is stored is read back first.
    /// </summary>
    public async Task SaveSettingsAsync()
    {
        try
        {
            var result = await players.Data.WriteAsync(new SvDataOps().Set("settings", settings, ifVersion: settingsVersion));
            settingsVersion = result.Results[0].Version ?? 0;
            status = "Saved";
        }
        catch (SvApiException e) when (e.IsOutcomeUnknown)
        {
            // No answer (or a repeat met a conflict): the save MAY have landed. Never just send it again: read what is
            // stored. When it is what we saved, it landed; otherwise the player saves again (with the new version).
            var mine = SvJson.FromObject(settings);
            await LoadAsync();
            status = Newtonsoft.Json.Linq.JToken.DeepEquals(mine, SvJson.FromObject(settings))
                ? "Saved"
                : "Not sure it saved: loaded what is stored, save again if needed";
        }
        catch (SvApiException e) when (e.ErrorCode == "version_conflict")
        {
            // Changed since we read it (another device): read it again and let the player decide. Here: keep what is
            // stored. Never re-apply the change on top of it.
            await LoadAsync();
            status = "Your settings changed elsewhere: loaded the newest";
        }
        catch (SvApiException e)
        {
            status = "Not saved (" + (e.ErrorCode ?? "HTTP " + e.Status) + ")";
        }
    }

    /// <summary>
    /// A counter: inc adds on the server (never read, add and write back). No ifVersion here, so the SDK does not
    /// send it again by itself after a lost answer (that could count twice). With IsOutcomeUnknown, read the counter
    /// before counting again.
    /// </summary>
    public async Task CountMatchAsync()
    {
        try
        {
            var result = await players.Data.WriteAsync(new SvDataOps().Inc("matchesPlayed", 1, min: 0));
            var value = result.Results[0].Value;
            if (value != null) matchesPlayed = (long)value;
        }
        catch (SvApiException e) when (e.IsOutcomeUnknown)
        {
            await LoadAsync(); // it may have counted: show what is stored, never count again blindly
            status = "Not sure it counted: showing the stored count";
        }
        catch (SvApiException e)
        {
            status = "Not counted (" + (e.ErrorCode ?? "HTTP " + e.Status) + ")";
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 420, Screen.height - 40));
        GUILayout.Label(status);
        if (!string.IsNullOrEmpty(messageOfTheDay)) GUILayout.Label("News: " + messageOfTheDay);
        GUILayout.Label("Field of view: " + settings.fov);
        if (GUILayout.Button("FOV +5")) settings.fov = Math.Min(120, settings.fov + 5);
        if (GUILayout.Button("FOV -5")) settings.fov = Math.Max(60, settings.fov - 5);
        settings.invertY = GUILayout.Toggle(settings.invertY, "Invert Y");
        if (GUILayout.Button("Save settings")) _ = SaveSettingsAsync();
        GUILayout.Space(12);
        GUILayout.Label("Matches played: " + matchesPlayed);
        if (GUILayout.Button("Count a match")) _ = CountMatchAsync();
        GUILayout.EndArea();
    }
}
