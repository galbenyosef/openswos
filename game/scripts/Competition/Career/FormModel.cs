namespace OpenSwos.Competition.Career;

/// <summary>
/// Deterministic, short-lived player form used as a small match-time skill nudge.
/// </summary>
public static class FormModel
{
    /// <summary>
    /// Updates a player's form after a match. A result is +1 for a win, 0 for a
    /// draw, or -1 for a loss; zero minutes means the player only decays.
    /// </summary>
    public static void UpdateFormAfterMatch(CareerPlayer p, int result, int minutes, uint seed)
    {
        // A decisive result the player featured in nudges form and lets a streak
        // build toward the cap; a draw or a match the player sat out lets form
        // fade back toward neutral (form is temporary).
        if (minutes > 0 && result != 0)
            p.Form = Math.Clamp(p.Form + Math.Sign(result), -3, 3);
        else
            DecayTowardZero(p);
    }

    /// <summary>
    /// Returns the small quantized-skill nudge contributed by current form.
    /// </summary>
    public static int FormSkillDelta(CareerPlayer p)
    {
        int form = Math.Clamp(p.Form, -3, 3);
        return form >= 2 ? 1 : form <= -2 ? -1 : 0;
    }

    /// <summary>
    /// Moves form one step toward neutral and preserves the valid form range.
    /// </summary>
    public static void DecayTowardZero(CareerPlayer p)
    {
        int form = Math.Clamp(p.Form, -3, 3);
        p.Form = form > 0 ? form - 1 : form < 0 ? form + 1 : 0;
    }
}
