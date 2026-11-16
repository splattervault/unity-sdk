// GAME CLIENT CODE. No API key anywhere: a game build must never hold one. Players sign in as a guest on this
// device or with a SplatterVault email account, and the SDK keeps the refresh token and the guest secret in
// the device's secure storage.
//
// Set up first: game console > your game > Sign-in tab: switch on "Guest accounts" and "Email accounts".
// Put this on a GameObject, set Game Id (the number on the Sign-in tab; it is not a secret) and press Play.
using System;
using System.Threading.Tasks;
using SplatterVault.Core;
using SplatterVault.Players;
using UnityEngine;

public class PlayerAccountsExample : MonoBehaviour
{
    [Tooltip("Your game id, from the Sign-in tab of the game console")]
    [SerializeField] private int gameId = 123;

    private SvPlayers players;
    private string status = "Starting...";
    private string email = "";
    private string code = "";
    private string password = "";
    private string displayName = "";
    private bool codeSent;
    private bool busy;
    private bool guestLost;

    private async void Start()
    {
        // One SvPlayers per game, kept for the whole session.
        players = new SvPlayers(gameId);
        players.Auth.SignInRequired += info => status = "Please sign in again (" + info.Reason + ")";
        players.Auth.StorageFailed += failure => Debug.LogWarning("Secure storage: " + failure.Operation + " of " + failure.Item + " failed");
        await Run("Signing you back in", async () =>
        {
            // Restores the LAST way the player signed in on this device: an email sign-in (refresh token) or this
            // device's guest. Never a different player: after a Meta/Steam/... sign-in or a sign-out it answers false
            // even with a guest stored, and the player chooses again (or you sign them in with that platform).
            if (await players.Auth.RestoreAsync())
            {
                status = "Welcome back";
                return;
            }
            var last = await players.Auth.GetLastSignInMethodAsync();
            status = last == null ? "Not signed in" : "Last time you signed in with " + last;
        });
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 440, Screen.height - 40));
        GUILayout.Label(status);
        GUI.enabled = !busy && players != null;
        if (players != null && players.Auth.IsSignedIn) SignedInGui();
        else SignedOutGui();
        GUI.enabled = true;
        GUILayout.EndArea();
    }

    private void SignedOutGui()
    {
        if (GUILayout.Button("Play as a guest"))
            _ = Run("Signing in as a guest", async () =>
            {
                var result = await players.Auth.SignInAsGuestAsync();
                status = result.IsNewPlayer ? "New guest made on this device" : "Signed in as this device's guest";
                // The device could not save the guest: it lasts until the game quits unless an email is added.
                if (!result.SavedToDevice) status += " (not saved on this device: add an email to keep it)";
            });
        if (guestLost && GUILayout.Button("Start over as a new guest"))
            _ = Run("Making a new guest", async () =>
            {
                // The SDK never retries a refused guest secret by itself; replacing it is the player's choice.
                await players.Auth.CreateGuestAsync(replaceExistingGuest: true);
                guestLost = false;
                status = "New guest made on this device";
            });

        GUILayout.Space(12);
        GUILayout.Label("Email");
        email = GUILayout.TextField(email);
        if (!codeSent)
        {
            if (GUILayout.Button("Email me a code (sign in, sign up or reset)"))
                _ = Run("Sending a code", async () =>
                {
                    var sent = await players.Auth.RequestEmailCodeAsync(email);
                    codeSent = true;
                    status = "Check your email: the code lasts " + sent.ExpiresInSeconds / 60 + " minutes";
                });
            GUILayout.Label("Password");
            password = GUILayout.PasswordField(password, '*');
            if (GUILayout.Button("Sign in with password"))
                _ = Run("Signing in", async () =>
                {
                    await players.Auth.SignInWithPasswordAsync(email, password);
                    password = "";
                    status = "Signed in";
                });
        }
        else
        {
            GUILayout.Label("Code from the email");
            code = GUILayout.TextField(code);
            GUILayout.Label("New password (optional: sets or resets it)");
            password = GUILayout.PasswordField(password, '*');
            GUILayout.Label("Display name (optional, for a new account)");
            displayName = GUILayout.TextField(displayName);
            if (GUILayout.Button("Sign in"))
                _ = Run("Checking the code", async () =>
                {
                    var result = await players.Auth.VerifyEmailCodeAsync(email, code, Optional(password), Optional(displayName));
                    codeSent = false;
                    code = password = "";
                    status = result.IsNewPlayer ? "Account made" : "Signed in";
                });
            if (GUILayout.Button("Back")) codeSent = false;
        }
    }

    private void SignedInGui()
    {
        GUILayout.Label("Player " + players.Auth.PlayerId + " (" + players.Auth.Method + ")");

        if (players.Auth.Method == SvProviders.Device)
        {
            // A guest lives on this device only: adding an email keeps the same player on any device.
            GUILayout.Space(8);
            GUILayout.Label("Add an email to keep this guest");
            email = GUILayout.TextField(email);
            if (!codeSent)
            {
                if (GUILayout.Button("Email me a code"))
                    _ = Run("Sending a code", async () =>
                    {
                        await players.Auth.RequestLinkEmailCodeAsync(email);
                        codeSent = true;
                        status = "Check your email";
                    });
            }
            else
            {
                GUILayout.Label("Code");
                code = GUILayout.TextField(code);
                GUILayout.Label("Password (optional)");
                password = GUILayout.PasswordField(password, '*');
                if (GUILayout.Button("Add the email"))
                    _ = Run("Adding the email", async () =>
                    {
                        await players.Auth.AddEmailAsync(email, code, Optional(password));
                        codeSent = false;
                        code = password = "";
                        status = "Email added: sign in with it on any device";
                    });
            }
        }

        GUILayout.Space(12);
        if (GUILayout.Button("Sign out")) _ = Run("Signing out", async () =>
        {
            await players.Auth.SignOutAsync();
            status = "Signed out";
        });
        if (GUILayout.Button("Delete my account and all my data"))
            _ = Run("Deleting", async () =>
            {
                await players.Auth.EraseMeAsync();
                status = "Account deleted";
            });
    }

    private async Task Run(string what, Func<Task> action)
    {
        busy = true;
        status = what + "...";
        try
        {
            await action();
        }
        catch (SvApiException e)
        {
            if (e.ErrorCode == "proof_invalid" || e.ErrorCode == SvApiException.StorageUnreadableCode) guestLost = true;
            status = Describe(e);
        }
        catch (Exception e)
        {
            status = e.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private static string Optional(string text)
    {
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // Act on ErrorCode, never on the message text.
    private static string Describe(SvApiException e)
    {
        switch (e.ErrorCode)
        {
            case SvApiException.NetworkErrorCode:
            case SvApiException.TimeoutCode:
                return "No connection. Try again.";
            case "rate_limited":
                return "Too many tries. Wait " + Math.Ceiling((e.RetryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds) + " s.";
            case "code_invalid":
                return "That code is not right or has expired. Ask for a new one.";
            case "invalid_credentials":
                return "The email or the password is not right.";
            case "weak_password":
                return "Use 8 to 72 characters, and not your email.";
            case "provider_not_enabled":
                return "This way of signing in is off for the game (Sign-in tab).";
            case "identity_in_use":
                return "Another player already has that email.";
            case "email_unavailable":
                return "Email can't be sent right now. Try the password, or later.";
            case "proof_invalid":
                return "This device's guest is gone. Play as a new guest or sign in with email.";
            case SvApiException.StorageUnreadableCode:
                // Nothing was changed. Locked storage clears up (try again); storage restored from another device
                // never does: then offer "Start over as a new guest" (CreateGuestAsync(replaceExistingGuest: true)).
                return "This device's saved sign-in can't be read right now. Try again.";
            default:
                return "Something went wrong (" + (e.ErrorCode ?? "HTTP " + e.Status) + ").";
        }
    }
}
