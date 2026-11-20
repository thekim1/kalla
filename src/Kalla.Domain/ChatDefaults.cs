namespace Kalla.Domain;

/// <summary>
/// Chat defaults the platform starts from (P1b item 39; admin design §5.5.4, chat design §5).
/// </summary>
public static class ChatDefaults
{
    /// <summary>
    /// The key in <c>Settings.Chat</c> that holds the instructions a new assistant's first draft starts from. It is edited on
    /// the Settings platform page. When the key is missing, <see cref="SystemPrompt"/> applies.
    /// </summary>
    public const string SystemPromptKey = "defaultSystemPrompt";

    /// <summary>
    /// The citizen-service instructions (Swedish, like the platform frame of chat design §7.3, which is prepended at run
    /// time and cannot be removed). They set tone, audience and answer shape only; the frame's rules on sources stay.
    /// </summary>
    public const string SystemPrompt = """
        Du svarar invånare, företagare och besökare som har frågor om Umeå kommuns tjänster.
        Svara vänligt, sakligt och kort, på enkel svenska utan förkortningar och fackord. Börja med det viktigaste.
        Använd en punktlista när svaret har flera steg, och berätta vad personen behöver göra härnäst.
        Be aldrig om personuppgifter. Om frågan gäller ett enskilt ärende, hänvisa till kommunens kontaktcenter.
        """;
}
