using Verse;

namespace IsoRim
{
    // Log lines for working on the mod. Players never see them: warnings only appear with the
    // game's development mode on, and notes (what was hooked, timings, the light per hour) only
    // with the -isorimdiag switch as well (the screenshot harness's -isorim3d switch counts too).
    // Real errors still go to the log as errors.
    public static class ModLog
    {
        public static readonly bool Diag = Prefs.DevMode
            && (GenCommandLine.CommandLineArgPassed("isorimdiag") || GenCommandLine.CommandLineArgPassed("isorim3d"));

        public static void Note(string text)
        {
            if (Diag)
            {
                Log.Message(text);
            }
        }

        public static void Warn(string text)
        {
            if (Prefs.DevMode)
            {
                Log.Warning(text);
            }
        }
    }
}
