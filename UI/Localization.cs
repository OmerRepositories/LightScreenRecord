namespace LiteRec.UI;

internal enum Language
{
    English,
    Hebrew,
}

/// <summary>
/// UI strings in English and Hebrew. A plain dictionary keeps the app free of resource assemblies
/// and lets the language be switched instantly at runtime.
/// </summary>
internal static class Loc
{
    public static Language Current { get; set; } = Language.English;

    public static bool IsRightToLeft => Current == Language.Hebrew;

    /// <summary>Returns the string for <paramref name="key"/> in the current language.</summary>
    public static string T(string key) =>
        Strings.TryGetValue(key, out var pair) ? (Current == Language.Hebrew ? pair.He : pair.En) : key;

    /// <summary>Returns a formatted string in the current language.</summary>
    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>
    /// Keeps a left-to-right fragment (sizes like "1920 x 1080", coordinates, file names) intact when
    /// it is shown inside Hebrew text; without the embedding marks the bidi algorithm reorders it.
    /// </summary>
    public static string Ltr(string text) => IsRightToLeft ? $"‪{text}‬" : text;

    private static readonly Dictionary<string, (string En, string He)> Strings = new()
    {
        ["Title"] = ("LiteRec - Screen Recorder", "LiteRec - מקליט מסך"),

        // Capture area
        ["GroupCapture"] = ("Capture area", "אזור הקלטה"),
        ["ModeScreen"] = ("Full screen", "מסך מלא"),
        ["ModeWindow"] = ("Window", "חלון"),
        ["ModeRegion"] = ("Custom region", "אזור מותאם"),
        ["SelectRegion"] = ("Select region...", "בחירת אזור..."),
        ["NoRegion"] = ("No region selected", "לא נבחר אזור"),
        ["ScreenItem"] = ("Screen {0} ({1})", "מסך {0} ({1})"),
        ["PrimarySuffix"] = (" - primary", " - ראשי"),
        ["RegionHint"] = ("Drag to select the recording area  -  Esc to cancel", "גררו לבחירת אזור ההקלטה  -  Esc לביטול"),

        // Audio
        ["GroupAudio"] = ("Audio", "שמע"),
        ["SystemAudio"] = ("System audio", "שמע מערכת"),
        ["Microphone"] = ("Microphone", "מיקרופון"),
        ["DefaultDevice"] = ("Default device", "התקן ברירת מחדל"),

        // Video
        ["GroupVideo"] = ("Video", "וידאו"),
        ["Resolution"] = ("Resolution", "רזולוציה"),
        ["ResOriginal"] = ("Original size", "גודל מקורי"),
        ["Quality"] = ("Quality", "איכות"),
        ["QualitySmall"] = ("Small file", "קובץ קטן"),
        ["QualityBalanced"] = ("Balanced", "מאוזנת"),
        ["QualityHigh"] = ("High", "גבוהה"),
        ["Motion"] = ("Motion", "תנועה"),
        ["MotionStandard"] = ("Standard - recommended", "רגילה - מומלץ"),
        ["MotionSmooth"] = ("Extra smooth - for games and video", "חלקה במיוחד - למשחקים ולווידאו"),
        ["MotionLight"] = ("Light - for slow computers", "קלה - למחשבים חלשים"),
        ["CaptureCursor"] = ("Record the mouse cursor", "הקלטת סמן העכבר"),
        ["HidePanel"] = ("Hide this panel from the recording", "הסתרת חלון זה מההקלטה"),

        // Output
        ["GroupOutput"] = ("Save to", "תיקיית שמירה"),
        ["Browse"] = ("Browse...", "עיון..."),
        ["OpenFolder"] = ("Open", "פתיחה"),
        ["BrowseTitle"] = ("Choose where recordings are saved", "בחירת תיקייה לשמירת ההקלטות"),

        // Controls and status
        ["Start"] = ("Start (F9)", "התחלה (F9)"),
        ["Pause"] = ("Pause (F9)", "השהיה (F9)"),
        ["Resume"] = ("Resume (F9)", "המשך (F9)"),
        ["Stop"] = ("Stop && save (F10)", "עצירה ושמירה (F10)"),
        ["StatusReady"] = ("Ready", "מוכן"),
        ["StatusStarting"] = ("Starting...", "מתחיל..."),
        ["StatusRecording"] = ("Recording", "מקליט"),
        ["StatusPaused"] = ("Paused", "מושהה"),
        ["StatusSaving"] = ("Saving...", "שומר..."),
        ["Saved"] = ("Saved: {0}", "נשמר: {0}"),
        ["Stats"] = ("Frames: {0:N0}   Dropped: {1:N0}", "פריימים: {0:N0}   נשמטו: {1:N0}"),
        ["HotkeyHint"] = ("F9 start / pause    F10 stop and save", "F9 התחלה / השהיה    F10 עצירה ושמירה"),

        // Errors
        ["ErrorTitle"] = ("LiteRec", "LiteRec"),
        ["ErrNoWindow"] = ("Choose a window to record.", "יש לבחור חלון להקלטה."),
        ["ErrWindowGone"] = ("The selected window is no longer open.", "החלון שנבחר כבר אינו פתוח."),
        ["ErrWindowMinimized"] = ("The selected window is minimized. Restore it and try again.", "החלון שנבחר ממוזער. יש לשחזר אותו ולנסות שוב."),
        ["ErrNoRegion"] = ("Select a region first.", "יש לבחור אזור תחילה."),
        ["ErrNoScreen"] = ("Choose a screen to record.", "יש לבחור מסך להקלטה."),
        ["ErrStart"] = ("Could not start recording.\n\n{0}", "לא ניתן להתחיל הקלטה.\n\n{0}"),
        ["ErrRecording"] = ("The recording stopped because of an error.\n\n{0}", "ההקלטה נעצרה עקב שגיאה.\n\n{0}"),
        ["ErrHotkey"] = ("Hotkey {0} is used by another program", "מקש הקיצור {0} תפוס על ידי תוכנה אחרת"),
        ["TargetClosed"] = ("The recorded window was closed - recording saved.", "החלון המוקלט נסגר - ההקלטה נשמרה."),
    };
}
